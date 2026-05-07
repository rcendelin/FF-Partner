using Bridge.Api.Sagas;
using Bridge.Application.Interfaces;
using Bridge.Domain.Enums;
using Bridge.Domain.Messages;
using Bridge.Domain.Models;
using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Bridge.Tests;

/// <summary>
/// Unit testy pro MoveClientToRegionSaga.
/// Testuje 5-krokovou sekvenci přesunu klienta + kompenzační logiku.
/// </summary>
public class MoveClientToRegionSagaTests
{
    private readonly IPartnerClientRepository _partnerRepo = Substitute.For<IPartnerClientRepository>();
    private readonly IBridgeMappingRepository _mappingRepo = Substitute.For<IBridgeMappingRepository>();
    private readonly IPartnerSyncLog _syncLog = Substitute.For<IPartnerSyncLog>();
    private readonly IServiceBusPublisher _publisher = Substitute.For<IServiceBusPublisher>();

    private MoveClientToRegionSaga CreateSaga() => new(
        _partnerRepo, _mappingRepo, _syncLog, _publisher,
        NullLogger<MoveClientToRegionSaga>.Instance);

    private static PartnerClient MakeClient(Guid? ffCompanyId = null) => new()
    {
        IdClient = 100,
        ClientFirm = "Test Firma s.r.o.",
        ClientIc = "12345678",
        ClientCountryId = 1,
        ClientCountryShort = "PL",
        ClientDisable = 0,
        FfCompanyId = ffCompanyId ?? Guid.NewGuid(),
        FfSyncSource = "FF",
        DataOwner = DataOwner.FieldForce,
        LastFfSyncAt = DateTime.UtcNow
    };

    private static IdMappingRecord MakeMapping(Guid? ffCompanyId = null, int sourcePartnerId = 100, string region = "cz") => new()
    {
        FfCompanyId = ffCompanyId ?? Guid.NewGuid(),
        PartnerClientId = sourcePartnerId,
        PartnerRegion = region,
        EntityType = "client",
        LastSyncAt = DateTime.UtcNow,
        LastSyncDirection = "ff_to_partner",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    // ── Happy path ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_AllStepsSucceed_ReturnsSuccess()
    {
        // Arrange
        var ffId = Guid.NewGuid();
        var client = MakeClient(ffId);
        var mapping = MakeMapping(ffId, sourcePartnerId: 100, region: "cz");

        _partnerRepo.InsertAsync(Arg.Any<PartnerClient>(), "pl", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(200));

        var saga = CreateSaga();

        // Act
        var result = await saga.ExecuteAsync(client, "cz", "pl", mapping, "msg-1", CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(SagaOutcome.Success, result.Outcome);

        // Ověřit pořadí operací
        await _partnerRepo.Received(1).InsertAsync(Arg.Any<PartnerClient>(), "pl", Arg.Any<CancellationToken>());
        await _partnerRepo.Received(1).DisableAsync(100, "cz", Arg.Any<CancellationToken>());
        await _mappingRepo.Received(1).UpdateMappingAsync(
            Arg.Is<IdMappingRecord>(m => m.PartnerClientId == 200 && m.PartnerRegion == "pl"),
            Arg.Any<CancellationToken>());
        await _publisher.Received(1).PublishAsync(
            "bridge.company.synced",
            Arg.Is<CompanySyncedResponse>(r => r.Action == "RegionChange" && r.PartnerClientId == 200),
            "msg-1", CancellationToken.None);
    }

    [Fact]
    public async Task Execute_Success_LogsPendingAndSuccessToSyncLog()
    {
        // Arrange
        var ffId = Guid.NewGuid();
        var client = MakeClient(ffId);
        var mapping = MakeMapping(ffId);

        _partnerRepo.InsertAsync(Arg.Any<PartnerClient>(), "pl", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(200));

        var saga = CreateSaga();

        // Act
        await saga.ExecuteAsync(client, "cz", "pl", mapping, "msg-1", CancellationToken.None);

        // Musí být zapsán SagaPending/InProgress i SagaCompleted/Success do PartnerSyncLog
        await _syncLog.Received(1).WriteAsync(
            Arg.Any<Guid>(), Arg.Any<string>(),
            "SagaPending", "Internal", "region_change", "InProgress",
            Arg.Any<int?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            CancellationToken.None);
        await _syncLog.Received(1).WriteAsync(
            Arg.Any<Guid>(), Arg.Any<string>(),
            "SagaCompleted", "Internal", "region_change", "Success",
            Arg.Any<int?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            CancellationToken.None);
    }

    // ── Krok 1 selhal ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_Step1InsertFails_ReturnsFailedAtStep1_NoChanges()
    {
        // Arrange
        var client = MakeClient();
        var mapping = MakeMapping();

        _partnerRepo.InsertAsync(Arg.Any<PartnerClient>(), "pl", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("MySQL nedostupné"));

        var saga = CreateSaga();

        // Act
        var result = await saga.ExecuteAsync(client, "cz", "pl", mapping, "msg-1", CancellationToken.None);

        // Assert
        Assert.Equal(SagaOutcome.FailedAtStep1_NoChanges, result.Outcome);
        Assert.True(result.HasNoSideEffects);
        Assert.False(result.IsSuccess);

        // DISABLE a UpdateMapping nesmí být volány
        await _partnerRepo.DidNotReceive().DisableAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _mappingRepo.DidNotReceive().UpdateMappingAsync(Arg.Any<IdMappingRecord>(), Arg.Any<CancellationToken>());
    }

    // ── Krok 3 selhal ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_Step3DisableFails_CompensatesDeleteTargetAndReturnsCompensatedAtStep3()
    {
        // Arrange
        var client = MakeClient();
        var mapping = MakeMapping(sourcePartnerId: 100, region: "cz");

        _partnerRepo.InsertAsync(Arg.Any<PartnerClient>(), "pl", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(200));
        _partnerRepo.DisableAsync(100, "cz", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Source DB nedostupná"));

        var saga = CreateSaga();

        // Act
        var result = await saga.ExecuteAsync(client, "cz", "pl", mapping, "msg-1", CancellationToken.None);

        // Assert
        Assert.Equal(SagaOutcome.CompensatedAtStep3, result.Outcome);

        // Kompenzace: DELETE z cílové DB
        await _partnerRepo.Received(1).DeleteAsync(200, "pl", CancellationToken.None);

        // UpdateMappingAsync nesmí být volán (mapping zůstane nezměněn)
        await _mappingRepo.DidNotReceive().UpdateMappingAsync(Arg.Any<IdMappingRecord>(), Arg.Any<CancellationToken>());
    }

    // ── Krok 4 selhal ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_Step4UpdateMappingFails_CompensatesEnableSourceDeleteTargetAndReturnsCompensatedAtStep4()
    {
        // Arrange
        var client = MakeClient();
        var mapping = MakeMapping(sourcePartnerId: 100, region: "cz");

        _partnerRepo.InsertAsync(Arg.Any<PartnerClient>(), "pl", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(200));
        _mappingRepo.UpdateMappingAsync(Arg.Any<IdMappingRecord>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Azure SQL nedostupný"));

        var saga = CreateSaga();

        // Act
        var result = await saga.ExecuteAsync(client, "cz", "pl", mapping, "msg-1", CancellationToken.None);

        // Assert
        Assert.Equal(SagaOutcome.CompensatedAtStep4, result.Outcome);

        // Kompenzace: EnableAsync původní + DeleteAsync cílové
        await _partnerRepo.Received(1).EnableAsync(100, "cz", CancellationToken.None);
        await _partnerRepo.Received(1).DeleteAsync(200, "pl", CancellationToken.None);
    }

    // ── Krok 5 publish selhal ────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_Step5PublishFails_DataIsConsistentReturnSuccess()
    {
        // Arrange: publish selže, ale data v DB jsou konzistentní → saga vrátí Success
        var client = MakeClient();
        var mapping = MakeMapping();

        _partnerRepo.InsertAsync(Arg.Any<PartnerClient>(), "pl", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(200));
        _publisher.PublishAsync("bridge.company.synced", Arg.Any<CompanySyncedResponse>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Service Bus nedostupný"));

        var saga = CreateSaga();

        // Act
        var result = await saga.ExecuteAsync(client, "cz", "pl", mapping, "msg-1", CancellationToken.None);

        // Assert: publish selhal ale data jsou OK → Success (publish failure je Warning, ne Error)
        Assert.True(result.IsSuccess);
        Assert.Equal(SagaOutcome.Success, result.Outcome);
    }

    // ── Target client fields ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_InsertedClientHasCorrectFieldsForTargetRegion()
    {
        // Arrange
        var ffId = Guid.NewGuid();
        var client = MakeClient(ffId);
        client.ClientFirm = "XYZ Trade";
        client.ClientIc = "99887766";
        client.ClientRight = 2;  // Dealer

        PartnerClient? insertedClient = null;
        _partnerRepo.InsertAsync(Arg.Do<PartnerClient>(c => insertedClient = c), "pl", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(999));

        var saga = CreateSaga();

        // Act
        await saga.ExecuteAsync(client, "cz", "pl", MakeMapping(ffId), "msg-1", CancellationToken.None);

        // Assert: nový client pro target region
        Assert.NotNull(insertedClient);
        Assert.Equal(0, insertedClient.IdClient);    // nový INSERT — IdClient = 0
        Assert.Equal("XYZ Trade", insertedClient.ClientFirm);
        Assert.Equal("99887766", insertedClient.ClientIc);
        Assert.Equal(2, insertedClient.ClientRight);
        Assert.Equal(ffId, insertedClient.FfCompanyId);
        Assert.Equal("FF", insertedClient.FfSyncSource);
        Assert.Equal(DataOwner.FieldForce, insertedClient.DataOwner);
        Assert.Equal(0, insertedClient.ClientDisable);  // nový client je aktivní
    }

    // ── Mapping po úspěchu ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_Success_MappingUpdatedToTargetRegionAndPartnerId()
    {
        // Arrange
        var ffId = Guid.NewGuid();
        var mapping = MakeMapping(ffId, sourcePartnerId: 100, region: "cz");

        _partnerRepo.InsertAsync(Arg.Any<PartnerClient>(), "pl", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(777));

        IdMappingRecord? updatedMapping = null;
        _mappingRepo.UpdateMappingAsync(Arg.Do<IdMappingRecord>(m => updatedMapping = m), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var saga = CreateSaga();

        // Act
        await saga.ExecuteAsync(MakeClient(ffId), "cz", "pl", mapping, "msg-1", CancellationToken.None);

        // Assert
        Assert.NotNull(updatedMapping);
        Assert.Equal(ffId, updatedMapping.FfCompanyId);
        Assert.Equal(777, updatedMapping.PartnerClientId);
        Assert.Equal("pl", updatedMapping.PartnerRegion);
        Assert.Equal("ff_to_partner", updatedMapping.LastSyncDirection);
    }
}
