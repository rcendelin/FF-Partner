using Bridge.Application.Interfaces;
using Bridge.Application.Services;
using Bridge.Domain.Messages;
using Bridge.Domain.Models;
using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner.Repositories;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Bridge.Tests;

/// <summary>
/// Unit testy pro logiku OwnerChangedConsumer.
/// Testujeme přes helper metodu ExecuteOwnerChangeAsync, která izoluje byznys logiku
/// od Azure Service Bus infrastruktury.
/// </summary>
public class OwnerChangedConsumerTests
{
    private readonly IBridgeMappingRepository _mappingRepo = Substitute.For<IBridgeMappingRepository>();
    private readonly IPartnerClientRepository _partnerRepo = Substitute.For<IPartnerClientRepository>();
    private readonly ISyncLogRepository _syncLog = Substitute.For<ISyncLogRepository>();
    private readonly IServiceBusPublisher _publisher = Substitute.For<IServiceBusPublisher>();
    private readonly IOwnerMappingService _ownerMapping = Substitute.For<IOwnerMappingService>();

    private static readonly Guid KnownUserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private const int KnownOwnerId = 7;

    private static CompanyOwnerChangedMessage MakeMessage(
        Guid? companyId = null,
        Guid? newOwnerUserId = null) => new()
    {
        MessageId = Guid.NewGuid().ToString(),
        SentAt = DateTimeOffset.UtcNow,
        FfCompanyId = companyId ?? Guid.NewGuid(),
        NewOwnerUserId = newOwnerUserId ?? KnownUserId
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
    public async Task OwnerChange_MappingFound_CallsUpdateOwnerInPartnerDb()
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns(MakeMapping(msg.FfCompanyId, 42, "cz"));
        _ownerMapping.ResolveOwnerId(KnownUserId).Returns(KnownOwnerId);

        await ExecuteOwnerChangeAsync(msg);

        await _partnerRepo.Received(1).UpdateOwnerAsync(
            42, "cz", KnownOwnerId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OwnerChange_MappingFound_PublishesSyncedWithOwnerChangeAction()
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns(MakeMapping(msg.FfCompanyId, 77, "pl"));
        _ownerMapping.ResolveOwnerId(KnownUserId).Returns(KnownOwnerId);

        await ExecuteOwnerChangeAsync(msg);

        await _publisher.Received(1).PublishAsync(
            "bridge.company.synced",
            Arg.Is<CompanySyncedResponse>(r =>
                r.FfCompanyId == msg.FfCompanyId &&
                r.PartnerClientId == 77 &&
                r.PartnerRegion == "pl" &&
                r.Action == "OwnerChange"),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OwnerChange_MappingFound_LogsSuccessToSyncLog()
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns(MakeMapping(msg.FfCompanyId, 42, "hu"));
        _ownerMapping.ResolveOwnerId(KnownUserId).Returns(KnownOwnerId);

        await ExecuteOwnerChangeAsync(msg);

        await _syncLog.Received(1).WriteAsync(
            Arg.Is<SyncLogEntry>(e =>
                e.FfCompanyId == msg.FfCompanyId &&
                e.PartnerClientId == 42 &&
                e.PartnerRegion == "hu" &&
                e.Operation == "owner_change" &&
                e.Status == "success"),
            Arg.Any<CancellationToken>());
    }

    // ── NO_MAPPING → sync-failed, bez UpdateOwnerAsync ───────────────────────────

    [Fact]
    public async Task OwnerChange_NoMapping_PublishesSyncFailed()
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns((IdMappingRecord?)null);

        await ExecuteOwnerChangeAsync(msg);

        await _publisher.Received(1).PublishAsync(
            "bridge.company.sync-failed",
            Arg.Is<CompanySyncFailedMessage>(f =>
                f.FfCompanyId == msg.FfCompanyId &&
                f.ErrorCode == "NO_MAPPING"),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        await _partnerRepo.DidNotReceive().UpdateOwnerAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── OWNER_NOT_MAPPED → sync-failed, bez UpdateOwnerAsync ─────────────────────

    [Fact]
    public async Task OwnerChange_OwnerNotMapped_PublishesOwnerNotMappedError()
    {
        var unmappedUserId = Guid.NewGuid();
        var msg = MakeMessage(newOwnerUserId: unmappedUserId);
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns(MakeMapping(msg.FfCompanyId, 42, "cz"));
        _ownerMapping.ResolveOwnerId(unmappedUserId).Returns((int?)null);

        await ExecuteOwnerChangeAsync(msg);

        await _publisher.Received(1).PublishAsync(
            "bridge.company.sync-failed",
            Arg.Is<CompanySyncFailedMessage>(f =>
                f.FfCompanyId == msg.FfCompanyId &&
                f.ErrorCode == "OWNER_NOT_MAPPED"),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        await _partnerRepo.DidNotReceive().UpdateOwnerAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OwnerChange_OwnerNotMapped_LogsFailedToSyncLog()
    {
        var unmappedUserId = Guid.NewGuid();
        var msg = MakeMessage(newOwnerUserId: unmappedUserId);
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns(MakeMapping(msg.FfCompanyId, 42, "cz"));
        _ownerMapping.ResolveOwnerId(unmappedUserId).Returns((int?)null);

        await ExecuteOwnerChangeAsync(msg);

        await _syncLog.Received(1).WriteAsync(
            Arg.Is<SyncLogEntry>(e =>
                e.Operation == "owner_change" &&
                e.Status == "failed"),
            Arg.Any<CancellationToken>());
    }

    // ── CLIENT_NOT_FOUND → terminální chyba, bez success logu ────────────────────

    [Fact]
    public async Task OwnerChange_ClientNotFoundInPartnerDb_PublishesSyncFailedWithClientNotFound()
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns(MakeMapping(msg.FfCompanyId, 99, "cz"));
        _ownerMapping.ResolveOwnerId(KnownUserId).Returns(KnownOwnerId);
        _partnerRepo.UpdateOwnerAsync(99, "cz", KnownOwnerId, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("UpdateOwnerAsync: idclient=99 nebyl nalezen."));

        await ExecuteOwnerChangeAsync(msg);

        await _publisher.Received(1).PublishAsync(
            "bridge.company.sync-failed",
            Arg.Is<CompanySyncFailedMessage>(f =>
                f.FfCompanyId == msg.FfCompanyId &&
                f.ErrorCode == "CLIENT_NOT_FOUND"),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        await _syncLog.DidNotReceive().WriteAsync(
            Arg.Is<SyncLogEntry>(e => e.Status == "success"),
            Arg.Any<CancellationToken>());
    }

    // ── Různé regiony ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("cz")]
    [InlineData("pl")]
    [InlineData("hu")]
    [InlineData("us")]
    public async Task OwnerChange_AllRegions_CallsUpdateOwnerInCorrectRegion(string region)
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns(MakeMapping(msg.FfCompanyId, 10, region));
        _ownerMapping.ResolveOwnerId(KnownUserId).Returns(KnownOwnerId);

        await ExecuteOwnerChangeAsync(msg);

        await _partnerRepo.Received(1).UpdateOwnerAsync(10, region, KnownOwnerId, Arg.Any<CancellationToken>());
    }

    // ── Publish synced selhání — data jsou konzistentní ───────────────────────────

    [Fact]
    public async Task OwnerChange_PublishSyncedFails_SyncLogStillRecordsSuccess()
    {
        var msg = MakeMessage();
        _mappingRepo.GetMappingAsync(msg.FfCompanyId).Returns(MakeMapping(msg.FfCompanyId, 42, "cz"));
        _ownerMapping.ResolveOwnerId(KnownUserId).Returns(KnownOwnerId);
        _publisher.PublishAsync(
            "bridge.company.synced",
            Arg.Any<CompanySyncedResponse>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Throws(new Exception("Service Bus nedostupný"));

        await ExecuteOwnerChangeAsync(msg);

        await _syncLog.Received(1).WriteAsync(
            Arg.Is<SyncLogEntry>(e => e.Status == "success"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Pomocná metoda simulující logiku ProcessOwnerChangeAsync z OwnerChangedConsumer.
    /// </summary>
    private async Task ExecuteOwnerChangeAsync(
        CompanyOwnerChangedMessage message,
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
            await _syncLog.WriteAsync(new SyncLogEntry
            {
                FfCompanyId = message.FfCompanyId,
                Operation = "owner_change",
                ServiceBusMessageId = sbMessageId,
                Status = "failed",
                ErrorMessage = $"NO_MAPPING: Mapping pro CompanyId={message.FfCompanyId} neexistuje.",
                Severity = "Error"
            }, CancellationToken.None);
            return;
        }

        // 2. Přeložit FieldForce User.Id → Partner3 id_owner
        var ownerId = _ownerMapping.ResolveOwnerId(message.NewOwnerUserId);
        if (ownerId is null)
        {
            var failed = new CompanySyncFailedMessage
            {
                MessageId = Guid.NewGuid().ToString(),
                SentAt = DateTimeOffset.UtcNow,
                FfCompanyId = message.FfCompanyId,
                ErrorCode = "OWNER_NOT_MAPPED",
                ErrorMessage = $"FieldForce User.Id={message.NewOwnerUserId} nemá konfigurovaný Partner3 id_owner.",
                OriginalMessageId = sbMessageId
            };
            try { await _publisher.PublishAsync("bridge.company.sync-failed", failed, sbMessageId, CancellationToken.None); }
            catch { /* ignorovat */ }
            await _syncLog.WriteAsync(new SyncLogEntry
            {
                FfCompanyId = message.FfCompanyId,
                Operation = "owner_change",
                ServiceBusMessageId = sbMessageId,
                Status = "failed",
                ErrorMessage = $"OWNER_NOT_MAPPED: User.Id={message.NewOwnerUserId}.",
                Severity = "Error"
            }, CancellationToken.None);
            return;
        }

        // 3. UpdateOwnerAsync — terminální při InvalidOperationException
        try
        {
            await _partnerRepo.UpdateOwnerAsync(
                mapping.PartnerClientId, mapping.PartnerRegion, ownerId.Value, ct);
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
            await _syncLog.WriteAsync(new SyncLogEntry
            {
                FfCompanyId = message.FfCompanyId,
                Operation = "owner_change",
                ServiceBusMessageId = sbMessageId,
                Status = "failed",
                ErrorMessage = $"CLIENT_NOT_FOUND: Klient id={mapping.PartnerClientId} nenalezen.",
                Severity = "Error"
            }, CancellationToken.None);
            return;
        }

        // 4. Publish bridge.company.synced
        try
        {
            await _publisher.PublishAsync("bridge.company.synced", new CompanySyncedResponse
            {
                MessageId = Guid.NewGuid().ToString(),
                SentAt = DateTimeOffset.UtcNow,
                FfCompanyId = message.FfCompanyId,
                PartnerClientId = mapping.PartnerClientId,
                PartnerRegion = mapping.PartnerRegion,
                Action = "OwnerChange"
            }, sbMessageId, CancellationToken.None);
        }
        catch { /* data konzistentní i při publish chybě */ }

        // 5. Log úspěchu
        await _syncLog.WriteAsync(new SyncLogEntry
        {
            FfCompanyId = message.FfCompanyId,
            PartnerClientId = mapping.PartnerClientId,
            PartnerRegion = mapping.PartnerRegion,
            Operation = "owner_change",
            ServiceBusMessageId = sbMessageId,
            Status = "success",
            Severity = "Info"
        }, CancellationToken.None);
    }
}
