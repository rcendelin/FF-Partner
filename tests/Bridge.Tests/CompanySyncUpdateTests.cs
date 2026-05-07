using Bridge.Application.Interfaces;
using Bridge.Application.Services;
using Bridge.Domain.Enums;
using Bridge.Domain.Exceptions;
using Bridge.Domain.Messages;
using Bridge.Domain.Models;
using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner;
using Bridge.Infrastructure.Partner.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Bridge.Tests;

/// <summary>
/// Testy pro logiku UPDATE + conflict detection v CompanySyncConsumer.
/// Testujeme přes pomocnou metodu ExecuteUpdateAsync která izoluje
/// byznys logiku od Azure Service Bus infrastruktury.
/// </summary>
public class CompanySyncUpdateTests
{
    private readonly IGaiaCountryRepository _countryRepo = Substitute.For<IGaiaCountryRepository>();
    private readonly IGaiaZipRepository _zipRepo = Substitute.For<IGaiaZipRepository>();
    private readonly IGaiaStateRepository _stateRepo = Substitute.For<IGaiaStateRepository>();
    private readonly IGaiaCountyRepository _countyRepo = Substitute.For<IGaiaCountyRepository>();
    private readonly IPartnerSyncLog _syncLog = Substitute.For<IPartnerSyncLog>();
    private readonly IPartnerClientRepository _partnerRepo = Substitute.For<IPartnerClientRepository>();
    private readonly IBridgeMappingRepository _mappingRepo = Substitute.For<IBridgeMappingRepository>();
    private readonly IServiceBusPublisher _publisher = Substitute.For<IServiceBusPublisher>();
    private readonly IOwnerMappingService _ownerMapping = Substitute.For<IOwnerMappingService>();

    private GeoValidationService CreateGeoService() => new(
        _countryRepo, _zipRepo, _stateRepo, _countyRepo, _syncLog,
        NullLogger<GeoValidationService>.Instance);

    private static IdMappingRecord MakeMapping(Guid companyId, int partnerId = 10, string region = "cz") => new()
    {
        FfCompanyId = companyId,
        PartnerClientId = partnerId,
        PartnerRegion = region,
        EntityType = "client",
        LastSyncAt = DateTime.UtcNow.AddHours(-1),
        LastSyncDirection = "ff_to_partner",
        CreatedAt = DateTime.UtcNow.AddDays(-1),
        UpdatedAt = DateTime.UtcNow.AddHours(-1)
    };

    private static PartnerClient MakeExistingClient(int id, DateTime? lastFfSyncAt = null) => new()
    {
        IdClient = id,
        ClientFirm = "Stará firma s.r.o.",
        ClientCountryId = 1,
        ClientCountryShort = "CZ",
        ClientRight = 0,
        DataOwner = DataOwner.FieldForce,
        LastFfSyncAt = lastFfSyncAt ?? DateTime.UtcNow.AddHours(-2)
    };

    private static CompanySyncMessage MakeUpdateMessage(
        Guid? companyId = null,
        string countryCode = "CZ",
        DateTimeOffset? sentAt = null) => new()
    {
        MessageId = Guid.NewGuid().ToString(),
        SentAt = sentAt ?? DateTimeOffset.UtcNow,
        Action = "Update",
        CompanyId = companyId ?? Guid.NewGuid(),
        CompanyName = "Nová firma s.r.o.",
        CountryCode = countryCode,
        PostalCode = "11000",
        City = "Praha",
        CompanyRole = "Customer"
    };

    // ---- Žádný mapping → sync-failed ----

    [Fact]
    public async Task ProcessUpdate_NoMapping_PublishesSyncFailed()
    {
        var companyId = Guid.NewGuid();
        _mappingRepo.GetMappingAsync(companyId).Returns((IdMappingRecord?)null);

        var geoSvc = CreateGeoService();
        var msg = MakeUpdateMessage(companyId: companyId);

        await ExecuteUpdateAsync(geoSvc, msg);

        await _partnerRepo.DidNotReceive().UpdateAsync(Arg.Any<PartnerClient>(), Arg.Any<string>());
        await _publisher.Received(1).PublishAsync(
            "bridge.company.sync-failed",
            Arg.Is<CompanySyncFailedMessage>(m => m.ErrorCode == "NO_MAPPING"),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- Stale mapping (tbl_client neexistuje) → sync-failed ----

    [Fact]
    public async Task ProcessUpdate_StaleMappingNoClient_PublishesSyncFailed()
    {
        var companyId = Guid.NewGuid();
        _mappingRepo.GetMappingAsync(companyId)
            .Returns(MakeMapping(companyId, partnerId: 999));
        _partnerRepo.GetByPartnerIdAsync(999, "cz")
            .Returns((PartnerClient?)null);

        var geoSvc = CreateGeoService();
        var msg = MakeUpdateMessage(companyId: companyId);

        await ExecuteUpdateAsync(geoSvc, msg);

        await _partnerRepo.DidNotReceive().UpdateAsync(Arg.Any<PartnerClient>(), Arg.Any<string>());
        await _publisher.Received(1).PublishAsync(
            "bridge.company.sync-failed",
            Arg.Is<CompanySyncFailedMessage>(m => m.ErrorCode == "ORPHANED_MAPPING"),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- Conflict detection — záznam novější než zpráva + 5min tolerance ----

    [Fact]
    public async Task ProcessUpdate_ConflictDetected_PublishesConflict_SkipsUpdate()
    {
        var companyId = Guid.NewGuid();
        // Zpráva odeslána 20 min nazpátek, DB synced před 2 min → lastSync > sentAt + 5min → conflict
        var sentAt = DateTimeOffset.UtcNow.AddMinutes(-20);  // zpráva stará 20 min
        var lastSyncAt = DateTime.UtcNow.AddMinutes(-2);      // DB sync před 2 min (novější než sentAt+5min)

        _mappingRepo.GetMappingAsync(companyId)
            .Returns(MakeMapping(companyId, partnerId: 10));
        _partnerRepo.GetByPartnerIdAsync(10, "cz")
            .Returns(MakeExistingClient(10, lastFfSyncAt: lastSyncAt));

        var geoSvc = CreateGeoService();
        var msg = MakeUpdateMessage(companyId: companyId, sentAt: sentAt);

        await ExecuteUpdateAsync(geoSvc, msg);

        // UPDATE nesmí proběhnout
        await _partnerRepo.DidNotReceive().UpdateAsync(Arg.Any<PartnerClient>(), Arg.Any<string>());
        // Konflikt musí být publikován
        await _publisher.Received(1).PublishAsync(
            "bridge.company.conflict",
            Arg.Is<CompanyConflictMessage>(m =>
                m.FfCompanyId == companyId &&
                m.PartnerClientId == 10),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        // Sync log se zápisem conflict
        await _syncLog.Received().WriteAsync(
            Arg.Any<Guid>(), Arg.Any<string>(),
            "BridgeProcessed", "Inbound", "Update", "Conflict",
            Arg.Any<int?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ---- Žádný konflikt — záznam starší než zpráva ----

    [Fact]
    public async Task ProcessUpdate_NoConflict_PerformsUpdateAndPublishesSynced()
    {
        var companyId = Guid.NewGuid();
        var lastSyncAt = DateTime.UtcNow.AddMinutes(-30);  // DB sync před 30 min = STARŠÍ

        _countryRepo.FindByIsoCodeAsync("CZ")
            .Returns(new CfgCountry { Id = 1, Short = "CZ" });
        _zipRepo.FindBestMatchAsync(Arg.Any<string?>(), Arg.Any<int>())
            .Returns((CfgZip?)null);
        _stateRepo.FindBestMatchAsync(Arg.Any<string?>(), Arg.Any<int>())
            .Returns((CfgState?)null);

        _mappingRepo.GetMappingAsync(companyId)
            .Returns(MakeMapping(companyId, partnerId: 10));
        _partnerRepo.GetByPartnerIdAsync(10, "cz")
            .Returns(MakeExistingClient(10, lastFfSyncAt: lastSyncAt));

        var geoSvc = CreateGeoService();
        var msg = new CompanySyncMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            SentAt = DateTimeOffset.UtcNow,
            Action = "Update",
            CompanyId = companyId,
            CompanyName = "Nová firma s.r.o.",
            CountryCode = "CZ",
            PostalCode = "11000",
            City = "Praha",
            CompanyRole = "Dealer"
        };

        await ExecuteUpdateAsync(geoSvc, msg);

        await _partnerRepo.Received(1).UpdateAsync(
            Arg.Is<PartnerClient>(c =>
                c.ClientFirm == "Nová firma s.r.o." &&
                c.ClientRight == 2 &&          // Dealer = 2
                c.DataOwner == DataOwner.FieldForce),
            "cz");

        await _publisher.Received(1).PublishAsync(
            "bridge.company.synced",
            Arg.Is<CompanySyncedResponse>(r =>
                r.FfCompanyId == companyId &&
                r.Action == "Update" &&
                r.PartnerClientId == 10),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());

        await _syncLog.Received().WriteAsync(
            Arg.Any<Guid>(), Arg.Any<string>(),
            "BridgeProcessed", "Inbound", "Update", "Success",
            Arg.Any<int?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ---- Conflict tolerance edge case: záznam je starý přesně 5 minut ----

    [Fact]
    public async Task ProcessUpdate_SyncAtExactlyFiveMinutesOld_NoConflict()
    {
        var companyId = Guid.NewGuid();
        var sentAt = DateTimeOffset.UtcNow;
        // lastFfSyncAt = SentAt - 5 min → prázdná tolerance (>= tolerance, ne <=)
        var lastSyncAt = sentAt.AddMinutes(-5).UtcDateTime;

        _countryRepo.FindByIsoCodeAsync("CZ")
            .Returns(new CfgCountry { Id = 1, Short = "CZ" });
        _zipRepo.FindBestMatchAsync(Arg.Any<string?>(), Arg.Any<int>())
            .Returns((CfgZip?)null);
        _stateRepo.FindBestMatchAsync(Arg.Any<string?>(), Arg.Any<int>())
            .Returns((CfgState?)null);

        _mappingRepo.GetMappingAsync(companyId)
            .Returns(MakeMapping(companyId, partnerId: 5));
        _partnerRepo.GetByPartnerIdAsync(5, "cz")
            .Returns(MakeExistingClient(5, lastFfSyncAt: lastSyncAt));

        var geoSvc = CreateGeoService();
        var msg = MakeUpdateMessage(companyId: companyId, sentAt: sentAt);

        await ExecuteUpdateAsync(geoSvc, msg);

        // Žádný conflict — UPDATE proběhne
        await _partnerRepo.Received(1).UpdateAsync(Arg.Any<PartnerClient>(), Arg.Any<string>());
        await _publisher.DidNotReceive().PublishAsync(
            "bridge.company.conflict",
            Arg.Any<CompanyConflictMessage>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- Neznámá země → GeoValidationException ----

    [Fact]
    public async Task ProcessUpdate_UnknownCountryInGaia_ThrowsGeoValidationException()
    {
        var companyId = Guid.NewGuid();
        _mappingRepo.GetMappingAsync(companyId)
            .Returns(MakeMapping(companyId, partnerId: 10));
        _partnerRepo.GetByPartnerIdAsync(10, "cz")
            .Returns(MakeExistingClient(10, lastFfSyncAt: DateTime.UtcNow.AddHours(-1)));
        // CZ má region ale není v GAIA
        _countryRepo.FindByIsoCodeAsync("CZ").Returns((CfgCountry?)null);

        var geoSvc = CreateGeoService();
        var msg = MakeUpdateMessage(companyId: companyId, countryCode: "CZ");

        await Assert.ThrowsAsync<GeoValidationException>(
            () => ExecuteUpdateAsync(geoSvc, msg));

        await _partnerRepo.DidNotReceive().UpdateAsync(Arg.Any<PartnerClient>(), Arg.Any<string>());
    }

    // ---- mapping.UpdateMappingAsync voláno po úspěšném UPDATE ----

    [Fact]
    public async Task ProcessUpdate_Success_UpdatesMappingWithNewOwner()
    {
        var companyId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();

        _countryRepo.FindByIsoCodeAsync("PL")
            .Returns(new CfgCountry { Id = 2, Short = "PL" });
        _zipRepo.FindBestMatchAsync(Arg.Any<string?>(), Arg.Any<int>())
            .Returns((CfgZip?)null);
        _stateRepo.FindBestMatchAsync(Arg.Any<string?>(), Arg.Any<int>())
            .Returns((CfgState?)null);

        _mappingRepo.GetMappingAsync(companyId)
            .Returns(MakeMapping(companyId, partnerId: 20, region: "pl"));
        _partnerRepo.GetByPartnerIdAsync(20, "pl")
            .Returns(MakeExistingClient(20, lastFfSyncAt: DateTime.UtcNow.AddHours(-1)));
        _ownerMapping.ResolveOwnerId(ownerId).Returns(99);

        var geoSvc = CreateGeoService();
        var msg = new CompanySyncMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            SentAt = DateTimeOffset.UtcNow,
            Action = "Update",
            CompanyId = companyId,
            CompanyName = "Polska Sp. z o.o.",
            CountryCode = "PL",
            CompanyRole = "Customer",
            AssignedUserId = ownerId
        };

        await ExecuteUpdateAsync(geoSvc, msg);

        await _mappingRepo.Received(1).UpdateMappingAsync(
            Arg.Is<IdMappingRecord>(m =>
                m.FfCompanyId == companyId &&
                m.PartnerOwnerId == 99 &&
                m.FfUserId == ownerId),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Pomocná metoda simulující logiku ProcessUpdateAsync z CompanySyncConsumer.
    /// </summary>
    private async Task ExecuteUpdateAsync(
        GeoValidationService geoSvc,
        CompanySyncMessage message,
        CancellationToken ct = default)
    {
        var mapping = await _mappingRepo.GetMappingAsync(message.CompanyId, ct);
        if (mapping is null)
        {
            await PublishSyncFailedAsync(message, message.MessageId, "NO_MAPPING",
                $"Mapping pro CompanyId={message.CompanyId} neexistuje.", ct);
            return;
        }

        var region = mapping.PartnerRegion;
        var existingClient = await _partnerRepo.GetByPartnerIdAsync(mapping.PartnerClientId, region, ct);
        if (existingClient is null)
        {
            await PublishSyncFailedAsync(message, message.MessageId, "ORPHANED_MAPPING",
                $"tbl_client id={mapping.PartnerClientId} nenalezen.", ct);
            return;
        }

        // Conflict detection — stale message guard (lastFfSyncAt > sentAt + 5min)
        if (existingClient.LastFfSyncAt.HasValue &&
            existingClient.LastFfSyncAt.Value > message.SentAt.UtcDateTime.AddMinutes(5))
        {
            await _publisher.PublishAsync("bridge.company.conflict", new CompanyConflictMessage
            {
                MessageId = Guid.NewGuid().ToString(),
                SentAt = DateTimeOffset.UtcNow,
                FfCompanyId = message.CompanyId,
                PartnerClientId = mapping.PartnerClientId,
                PartnerRegion = region,
                ExistingLastSyncAt = new DateTimeOffset(existingClient.LastFfSyncAt.Value, TimeSpan.Zero),
                IncomingMessageSentAt = message.SentAt
            }, message.MessageId, ct);

            await _syncLog.WriteAsync(
                message.CompanyId, message.MessageId,
                "BridgeProcessed", "Inbound", "Update", "Conflict",
                mapping.PartnerClientId, region,
                ct: ct);
            return;
        }

        var address = new AddressDto
        {
            CountryCode = message.CountryCode,
            PostalCode = message.PostalCode,
            City = message.City,
            State = message.State,
            County = message.County
        };
        var geo = await geoSvc.ValidateAsync(address, ct);

        var clientRight = Enum.TryParse<CompanyRole>(message.CompanyRole, ignoreCase: true, out var role)
            ? role switch { CompanyRole.Customer => 0, CompanyRole.Dealer => 2, CompanyRole.Oem => 1, _ => existingClient.ClientRight }
            : existingClient.ClientRight;

        var ownerId = _ownerMapping.ResolveOwnerId(message.AssignedUserId)
            ?? existingClient.IdOwner;

        var now = DateTime.UtcNow;
        existingClient.ClientFirm = message.CompanyName;
        existingClient.ClientIc = message.Ico;
        existingClient.ClientDic = message.Dic;
        existingClient.ClientStreet = message.Street;
        existingClient.ClientCity = geo.City;
        existingClient.ClientPsc = message.PostalCode;
        existingClient.ClientCountryId = geo.CountryId;
        existingClient.ClientCountryShort = geo.CountryShort;
        existingClient.ClientState = geo.State;
        existingClient.ClientStateId = geo.StateId;
        existingClient.ClientCounty = geo.County;
        existingClient.ClientCountyId = geo.CountyId;
        existingClient.ClientZipId = geo.ZipId;
        existingClient.ClientPhone = message.PrimaryContactPhone;
        existingClient.ClientMail = message.PrimaryContactEmail;
        existingClient.ClientRight = clientRight;
        existingClient.IdOwner = ownerId;
        existingClient.FfSyncSource = "FF";
        existingClient.DataOwner = DataOwner.FieldForce;
        existingClient.LastFfSyncAt = now;

        await _partnerRepo.UpdateAsync(existingClient, region, ct);

        var updatedMapping = new IdMappingRecord
        {
            FfCompanyId = mapping.FfCompanyId,
            PartnerClientId = mapping.PartnerClientId,
            PartnerRegion = region,
            EntityType = mapping.EntityType,
            PipedriveId = mapping.PipedriveId,
            FfUserId = message.AssignedUserId,
            PartnerOwnerId = ownerId,
            LastSyncAt = now,
            LastSyncDirection = "ff_to_partner",
            CreatedAt = mapping.CreatedAt,
            UpdatedAt = now
        };
        await _mappingRepo.UpdateMappingAsync(updatedMapping, ct);

        await _publisher.PublishAsync("bridge.company.synced", new CompanySyncedResponse
        {
            MessageId = Guid.NewGuid().ToString(),
            SentAt = DateTimeOffset.UtcNow,
            FfCompanyId = message.CompanyId,
            PartnerClientId = mapping.PartnerClientId,
            PartnerRegion = region,
            Action = "Update"
        }, message.MessageId, ct);

        await _syncLog.WriteAsync(
            message.CompanyId, message.MessageId,
            "BridgeProcessed", "Inbound", "Update", "Success",
            mapping.PartnerClientId, region,
            ct: ct);
    }

    private async Task PublishSyncFailedAsync(
        CompanySyncMessage message, string sbMessageId,
        string errorCode, string errorMsg, CancellationToken ct)
    {
        var failed = new CompanySyncFailedMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            SentAt = DateTimeOffset.UtcNow,
            FfCompanyId = message.CompanyId,
            ErrorCode = errorCode,
            ErrorMessage = errorMsg,
            OriginalMessageId = sbMessageId
        };
        await _publisher.PublishAsync("bridge.company.sync-failed", failed, sbMessageId, ct);
    }
}
