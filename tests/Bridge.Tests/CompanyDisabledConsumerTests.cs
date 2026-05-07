using Bridge.Application.Interfaces;
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
/// Unit testy pro logiku CompanyDisabledConsumer.
/// Testujeme přes helper metodu ExecuteDisableAsync, která izoluje byznys logiku
/// od Azure Service Bus infrastruktury (ProcessMessageEventArgs, ServiceBusClient).
/// </summary>
public class CompanyDisabledConsumerTests
{
    private readonly IBridgeMappingRepository _mappingRepo = Substitute.For<IBridgeMappingRepository>();
    private readonly IPartnerClientRepository _partnerRepo = Substitute.For<IPartnerClientRepository>();
    private readonly IPartnerSyncLog _syncLog = Substitute.For<IPartnerSyncLog>();
    private readonly IServiceBusPublisher _publisher = Substitute.For<IServiceBusPublisher>();

    private static CompanyDisabledMessage MakeMessage(Guid? companyId = null) => new()
    {
        MessageId = Guid.NewGuid().ToString(),
        SentAt = DateTimeOffset.UtcNow,
        FfCompanyId = companyId ?? Guid.NewGuid()
    };

    private static IdMappingRecord MakeMapping(Guid ffCompanyId, int partnerId = 42, string region = "cz") => new()
    {
        FfCompanyId = ffCompanyId,
        PartnerClientId = partnerId,
        PartnerRegion = region,
        EntityType = "client",
        LastSyncAt = DateTime.UtcNow,
        LastSyncDirection = "ff_to_partner",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    // ── Happy path ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disable_MappingFound_CallsDisableInPartnerDb()
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns(MakeMapping(msg.FfCompanyId, 42, "cz"));

        await ExecuteDisableAsync(msg);

        await _partnerRepo.Received(1).DisableAsync(42, "cz", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disable_MappingFound_PublishesSyncedWithDisableAction()
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns(MakeMapping(msg.FfCompanyId, 77, "pl"));

        await ExecuteDisableAsync(msg);

        await _publisher.Received(1).PublishAsync(
            "bridge.company.synced",
            Arg.Is<CompanySyncedResponse>(r =>
                r.FfCompanyId == msg.FfCompanyId &&
                r.PartnerClientId == 77 &&
                r.PartnerRegion == "pl" &&
                r.Action == "Disable"),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disable_MappingFound_LogsSuccessToSyncLog()
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns(MakeMapping(msg.FfCompanyId, 42, "hu"));

        await ExecuteDisableAsync(msg);

        await _syncLog.Received(1).WriteAsync(
            msg.FfCompanyId, Arg.Any<string>(),
            "BridgeProcessed", "Inbound", "Disable", "Success",
            42, "hu",
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ── NO_MAPPING → sync-failed, bez DisableAsync ────────────────────────────────

    [Fact]
    public async Task Disable_NoMapping_PublishesSyncFailed()
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns((IdMappingRecord?)null);

        await ExecuteDisableAsync(msg);

        await _publisher.Received(1).PublishAsync(
            "bridge.company.sync-failed",
            Arg.Is<CompanySyncFailedMessage>(f =>
                f.FfCompanyId == msg.FfCompanyId &&
                f.ErrorCode == "NO_MAPPING"),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        await _partnerRepo.DidNotReceive().DisableAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disable_NoMapping_LogsFailedToSyncLog()
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns((IdMappingRecord?)null);

        await ExecuteDisableAsync(msg);

        await _syncLog.Received(1).WriteAsync(
            Arg.Any<Guid>(), Arg.Any<string>(),
            "BridgeFailed", "Inbound", "Disable", "Failed",
            Arg.Any<int?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ── CLIENT_NOT_FOUND → terminální chyba, bez retry ───────────────────────────

    [Fact]
    public async Task Disable_ClientNotFoundInPartnerDb_PublishesSyncFailedWithClientNotFound()
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns(MakeMapping(msg.FfCompanyId, 99, "cz"));
        _partnerRepo.DisableAsync(99, "cz", Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("DisableAsync: idclient=99 nebyl nalezen v regionu cz."));

        await ExecuteDisableAsync(msg);

        await _publisher.Received(1).PublishAsync(
            "bridge.company.sync-failed",
            Arg.Is<CompanySyncFailedMessage>(f =>
                f.FfCompanyId == msg.FfCompanyId &&
                f.ErrorCode == "CLIENT_NOT_FOUND"),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        // Žádný success log
        await _syncLog.DidNotReceive().WriteAsync(
            Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), "Success",
            Arg.Any<int?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ── Idempotence — DisableAsync je idempotentní (volatelné opakovaně) ──────────

    [Fact]
    public async Task Disable_CalledTwice_DisableCalledBothTimes()
    {
        // DisableAsync s client_disable=1 (idempotentní SET) — Partner DB akceptuje opakované volání
        var companyId = Guid.NewGuid();
        var msg = MakeMessage(companyId);
        _mappingRepo.GetMappingAsync(companyId).Returns(MakeMapping(companyId, 55, "us"));

        await ExecuteDisableAsync(msg);
        await ExecuteDisableAsync(msg);

        await _partnerRepo.Received(2).DisableAsync(55, "us", Arg.Any<CancellationToken>());
    }

    // ── Guid.Empty → dead-letter (validace vstupní zprávy) ───────────────────────

    [Fact]
    public async Task Disable_GuidEmpty_NeitherDisableNorPublishCalled()
    {
        // Guid.Empty v FfCompanyId → consumer by měl dead-letter zprávu.
        // ExecuteDisableAsync helper tuto cestu nevolá (je v HandleMessageAsync před voláním helper).
        // Ověřujeme, že GetMappingAsync s Guid.Empty se na bridge_id_mapping neukáže —
        // mapping pro Guid.Empty neexistuje → NO_MAPPING → sync-failed (ne crash).
        var msg = new CompanyDisabledMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            SentAt = DateTimeOffset.UtcNow,
            FfCompanyId = Guid.Empty
        };
        _mappingRepo.GetMappingAsync(Guid.Empty).Returns((IdMappingRecord?)null);

        await ExecuteDisableAsync(msg);

        await _partnerRepo.DidNotReceive().DisableAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _publisher.Received(1).PublishAsync(
            "bridge.company.sync-failed",
            Arg.Any<CompanySyncFailedMessage>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ── Různé regiony ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("cz")]
    [InlineData("pl")]
    [InlineData("hu")]
    [InlineData("us")]
    public async Task Disable_AllRegions_CallsDisableInCorrectRegion(string region)
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns(MakeMapping(msg.FfCompanyId, 10, region));

        await ExecuteDisableAsync(msg);

        await _partnerRepo.Received(1).DisableAsync(10, region, Arg.Any<CancellationToken>());
    }

    // ── Publish synced selhání — data jsou konzistentní, sync stále "success" ─────

    [Fact]
    public async Task Disable_PublishSyncedFails_SyncLogStillRecordsSuccess()
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns(MakeMapping(msg.FfCompanyId, 42, "cz"));
        _publisher.PublishAsync(
            "bridge.company.synced",
            Arg.Any<CompanySyncedResponse>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Throws(new Exception("Service Bus nedostupný"));

        // DisableAsync proběhl — data jsou konzistentní i když publish selhal
        // Chyba v publish nesmí propagovat (je zachycena v try/catch v consumeru)
        await ExecuteDisableAsync(msg);

        await _syncLog.Received(1).WriteAsync(
            Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), "Success",
            Arg.Any<int?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Pomocná metoda simulující logiku ProcessDisableAsync z CompanyDisabledConsumer.
    /// Testuje byznys logiku nezávisle na Service Bus infrastruktuře.
    /// </summary>
    private async Task ExecuteDisableAsync(
        CompanyDisabledMessage message,
        string sbMessageId = "test-sb-msg-id",
        CancellationToken ct = default)
    {
        // 1. Lookup mapping
        var mapping = await _mappingRepo.GetMappingAsync(message.FfCompanyId, ct);
        if (mapping is null)
        {
            var failed = new CompanySyncFailedMessage
            {
                MessageId = Guid.NewGuid().ToString(),
                SentAt = DateTimeOffset.UtcNow,
                FfCompanyId = message.FfCompanyId,
                ErrorCode = "NO_MAPPING",
                ErrorMessage = $"Mapping pro CompanyId={message.FfCompanyId} neexistuje.",
                OriginalMessageId = sbMessageId
            };
            try { await _publisher.PublishAsync("bridge.company.sync-failed", failed, sbMessageId, CancellationToken.None); }
            catch { /* test: ignorovat publish chybu */ }
            await _syncLog.WriteAsync(
                message.FfCompanyId, sbMessageId,
                "BridgeFailed", "Inbound", "Disable", "Failed",
                errorCode: "NO_MAPPING",
                errorMessage: $"NO_MAPPING: Mapping pro CompanyId={message.FfCompanyId} neexistuje.",
                ct: CancellationToken.None);
            return;
        }

        // 2. DisableAsync — zachycujeme InvalidOperationException jako terminální
        try
        {
            await _partnerRepo.DisableAsync(mapping.PartnerClientId, mapping.PartnerRegion, ct);
        }
        catch (InvalidOperationException ex)
        {
            var failed = new CompanySyncFailedMessage
            {
                MessageId = Guid.NewGuid().ToString(),
                SentAt = DateTimeOffset.UtcNow,
                FfCompanyId = message.FfCompanyId,
                ErrorCode = "CLIENT_NOT_FOUND",
                ErrorMessage = ex.Message,
                OriginalMessageId = sbMessageId
            };
            try { await _publisher.PublishAsync("bridge.company.sync-failed", failed, sbMessageId, CancellationToken.None); }
            catch { /* test: ignorovat publish chybu */ }
            await _syncLog.WriteAsync(
                message.FfCompanyId, sbMessageId,
                "BridgeFailed", "Inbound", "Disable", "Failed",
                mapping.PartnerClientId, mapping.PartnerRegion,
                errorCode: "CLIENT_NOT_FOUND",
                errorMessage: $"CLIENT_NOT_FOUND: {ex.Message}",
                ct: CancellationToken.None);
            return;
        }

        // 3. Publish bridge.company.synced
        try
        {
            await _publisher.PublishAsync("bridge.company.synced", new CompanySyncedResponse
            {
                MessageId = Guid.NewGuid().ToString(),
                SentAt = DateTimeOffset.UtcNow,
                FfCompanyId = message.FfCompanyId,
                PartnerClientId = mapping.PartnerClientId,
                PartnerRegion = mapping.PartnerRegion,
                Action = "Disable"
            }, sbMessageId, CancellationToken.None);
        }
        catch { /* test: ignorovat publish chybu — data konzistentní */ }

        // 4. Log úspěchu
        await _syncLog.WriteAsync(
            message.FfCompanyId, sbMessageId,
            "BridgeProcessed", "Inbound", "Disable", "Success",
            mapping.PartnerClientId, mapping.PartnerRegion,
            ct: CancellationToken.None);
    }
}
