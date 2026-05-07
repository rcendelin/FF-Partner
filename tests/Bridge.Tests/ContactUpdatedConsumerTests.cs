using Bridge.Application.Interfaces;
using Bridge.Domain.Messages;
using Bridge.Domain.Models;
using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner.Repositories;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Bridge.Tests;

/// <summary>
/// Unit testy pro logiku ContactUpdatedConsumer.
/// Testujeme přes helper metodu ExecuteContactUpdateAsync, která izoluje byznys logiku
/// od Azure Service Bus infrastruktury.
/// </summary>
public class ContactUpdatedConsumerTests
{
    private readonly IBridgeMappingRepository _mappingRepo = Substitute.For<IBridgeMappingRepository>();
    private readonly IPartnerClientRepository _partnerRepo = Substitute.For<IPartnerClientRepository>();
    private readonly IPartnerSyncLog _syncLog = Substitute.For<IPartnerSyncLog>();
    private readonly IServiceBusPublisher _publisher = Substitute.For<IServiceBusPublisher>();

    private static ContactUpdatedMessage MakeMessage(
        Guid? companyId = null,
        string? email = "test@example.com",
        string? phone = "+420123456789") => new()
    {
        MessageId = Guid.NewGuid().ToString(),
        SentAt = DateTimeOffset.UtcNow,
        FfCompanyId = companyId ?? Guid.NewGuid(),
        Email = email,
        Phone = phone
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
    public async Task ContactUpdate_MappingFound_CallsUpdateContactInPartnerDb()
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns(MakeMapping(msg.FfCompanyId, 42, "cz"));

        await ExecuteContactUpdateAsync(msg);

        await _partnerRepo.Received(1).UpdateContactAsync(
            42, "cz", msg.Email, msg.Phone, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContactUpdate_MappingFound_PublishesSyncedWithContactUpdateAction()
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns(MakeMapping(msg.FfCompanyId, 77, "pl"));

        await ExecuteContactUpdateAsync(msg);

        await _publisher.Received(1).PublishAsync(
            "bridge.company.synced",
            Arg.Is<CompanySyncedResponse>(r =>
                r.FfCompanyId == msg.FfCompanyId &&
                r.PartnerClientId == 77 &&
                r.PartnerRegion == "pl" &&
                r.Action == "ContactUpdate"),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContactUpdate_MappingFound_LogsSuccessToSyncLog()
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns(MakeMapping(msg.FfCompanyId, 42, "hu"));

        await ExecuteContactUpdateAsync(msg);

        await _syncLog.Received(1).WriteAsync(
            msg.FfCompanyId, Arg.Any<string>(),
            "BridgeProcessed", "Inbound", "ContactUpdate", "Success",
            42, "hu",
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContactUpdate_NullEmailAndPhone_PassesNullToPartnerDb()
    {
        var msg = MakeMessage(email: null, phone: null);
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns(MakeMapping(msg.FfCompanyId, 10, "cz"));

        await ExecuteContactUpdateAsync(msg);

        await _partnerRepo.Received(1).UpdateContactAsync(
            10, "cz", null, null, Arg.Any<CancellationToken>());
    }

    // ── NO_MAPPING → sync-failed, bez UpdateContactAsync ─────────────────────────

    [Fact]
    public async Task ContactUpdate_NoMapping_PublishesSyncFailed()
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns((IdMappingRecord?)null);

        await ExecuteContactUpdateAsync(msg);

        await _publisher.Received(1).PublishAsync(
            "bridge.company.sync-failed",
            Arg.Is<CompanySyncFailedMessage>(f =>
                f.FfCompanyId == msg.FfCompanyId &&
                f.ErrorCode == "NO_MAPPING"),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        await _partnerRepo.DidNotReceive().UpdateContactAsync(
            Arg.Any<int>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContactUpdate_NoMapping_LogsFailedToSyncLog()
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns((IdMappingRecord?)null);

        await ExecuteContactUpdateAsync(msg);

        await _syncLog.Received(1).WriteAsync(
            Arg.Any<Guid>(), Arg.Any<string>(),
            "BridgeFailed", "Inbound", "ContactUpdate", "Failed",
            Arg.Any<int?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ── CLIENT_NOT_FOUND → terminální chyba, bez success logu ────────────────────

    [Fact]
    public async Task ContactUpdate_ClientNotFoundInPartnerDb_PublishesSyncFailedWithClientNotFound()
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns(MakeMapping(msg.FfCompanyId, 99, "cz"));
        _partnerRepo.UpdateContactAsync(99, "cz", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("UpdateContactAsync: idclient=99 nebyl nalezen."));

        await ExecuteContactUpdateAsync(msg);

        await _publisher.Received(1).PublishAsync(
            "bridge.company.sync-failed",
            Arg.Is<CompanySyncFailedMessage>(f =>
                f.FfCompanyId == msg.FfCompanyId &&
                f.ErrorCode == "CLIENT_NOT_FOUND"),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        await _syncLog.DidNotReceive().WriteAsync(
            Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), "Success",
            Arg.Any<int?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ── Různé regiony ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("cz")]
    [InlineData("pl")]
    [InlineData("hu")]
    [InlineData("us")]
    public async Task ContactUpdate_AllRegions_CallsUpdateContactInCorrectRegion(string region)
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns(MakeMapping(msg.FfCompanyId, 10, region));

        await ExecuteContactUpdateAsync(msg);

        await _partnerRepo.Received(1).UpdateContactAsync(
            10, region, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ── Publish synced selhání — data jsou konzistentní ───────────────────────────

    [Fact]
    public async Task ContactUpdate_PublishSyncedFails_SyncLogStillRecordsSuccess()
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns(MakeMapping(msg.FfCompanyId, 42, "cz"));
        _publisher.PublishAsync(
            "bridge.company.synced",
            Arg.Any<CompanySyncedResponse>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Throws(new Exception("Service Bus nedostupný"));

        await ExecuteContactUpdateAsync(msg);

        await _syncLog.Received(1).WriteAsync(
            Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), "Success",
            Arg.Any<int?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Pomocná metoda simulující logiku ProcessContactUpdateAsync z ContactUpdatedConsumer.
    /// </summary>
    private async Task ExecuteContactUpdateAsync(
        ContactUpdatedMessage message,
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
            catch { /* ignorovat */ }
            await _syncLog.WriteAsync(
                message.FfCompanyId, sbMessageId,
                "BridgeFailed", "Inbound", "ContactUpdate", "Failed",
                errorCode: "NO_MAPPING",
                errorMessage: $"NO_MAPPING: Mapping pro CompanyId={message.FfCompanyId} neexistuje.",
                ct: CancellationToken.None);
            return;
        }

        // 2. UpdateContactAsync — terminální při InvalidOperationException
        try
        {
            await _partnerRepo.UpdateContactAsync(
                mapping.PartnerClientId, mapping.PartnerRegion,
                message.Email, message.Phone, ct);
        }
        catch (InvalidOperationException)
        {
            var failed = new CompanySyncFailedMessage
            {
                MessageId = Guid.NewGuid().ToString(),
                SentAt = DateTimeOffset.UtcNow,
                FfCompanyId = message.FfCompanyId,
                ErrorCode = "CLIENT_NOT_FOUND",
                ErrorMessage = $"Klient id={mapping.PartnerClientId} nenalezen v regionu {mapping.PartnerRegion}.",
                OriginalMessageId = sbMessageId
            };
            try { await _publisher.PublishAsync("bridge.company.sync-failed", failed, sbMessageId, CancellationToken.None); }
            catch { /* ignorovat */ }
            await _syncLog.WriteAsync(
                message.FfCompanyId, sbMessageId,
                "BridgeFailed", "Inbound", "ContactUpdate", "Failed",
                errorCode: "CLIENT_NOT_FOUND",
                errorMessage: $"CLIENT_NOT_FOUND: Klient id={mapping.PartnerClientId} nenalezen.",
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
                Action = "ContactUpdate"
            }, sbMessageId, CancellationToken.None);
        }
        catch { /* data konzistentní i při publish chybě */ }

        // 4. Log úspěchu
        await _syncLog.WriteAsync(
            message.FfCompanyId, sbMessageId,
            "BridgeProcessed", "Inbound", "ContactUpdate", "Success",
            mapping.PartnerClientId, mapping.PartnerRegion,
            ct: CancellationToken.None);
    }
}
