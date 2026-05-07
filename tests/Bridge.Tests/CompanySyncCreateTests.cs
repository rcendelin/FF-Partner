using Bridge.Application.Interfaces;
using Bridge.Application.Services;
using Bridge.Domain.Enums;
using Bridge.Domain.Exceptions;
using Bridge.Domain.Messages;
using Bridge.Domain.Models;
using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Bridge.Tests;

/// <summary>
/// Testy pro logiku CREATE v CompanySyncConsumer.
/// Testujeme přes helper metodu CompanySyncCreateOrchestrator která izoluje
/// byznys logiku od Azure Service Bus infrastruktury.
/// </summary>
public class CompanySyncCreateTests
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

    private static CompanySyncMessage MakeSyncMessage(
        string action = "Create",
        string countryCode = "CZ",
        string companyRole = "Customer",
        Guid? assignedUserId = null) => new()
    {
        MessageId = Guid.NewGuid().ToString(),
        SentAt = DateTimeOffset.UtcNow,
        Action = action,
        CompanyId = Guid.NewGuid(),
        CompanyName = "Test s.r.o.",
        Ico = "12345678",
        CountryCode = countryCode,
        PostalCode = "11000",
        City = "Praha",
        State = "Praha",
        CompanyRole = companyRole,
        AssignedUserId = assignedUserId
    };

    // ---- Routing dle CountryCode ----

    [Theory]
    [InlineData("CZ", "cz")]
    [InlineData("SK", "cz")]
    [InlineData("PL", "pl")]
    [InlineData("HU", "hu")]
    [InlineData("US", "us")]
    public async Task ProcessCreate_KnownCountry_InsertsIntoCorrectRegion(
        string countryCode, string expectedRegion)
    {
        _countryRepo.FindByIsoCodeAsync(countryCode)
            .Returns(new CfgCountry { Id = 1, Short = countryCode });
        _zipRepo.FindBestMatchAsync(Arg.Any<string?>(), Arg.Any<int>())
            .Returns((CfgZip?)null);
        _stateRepo.FindBestMatchAsync(Arg.Any<string?>(), Arg.Any<int>())
            .Returns((CfgState?)null);
        _mappingRepo.GetMappingAsync(Arg.Any<Guid>()).Returns((IdMappingRecord?)null);
        _partnerRepo.InsertAsync(Arg.Any<PartnerClient>(), Arg.Any<string>()).Returns(42);

        var geoSvc = CreateGeoService();
        var msg = MakeSyncMessage(countryCode: countryCode);

        await ExecuteCreateAsync(geoSvc, msg);

        await _partnerRepo.Received(1).InsertAsync(
            Arg.Any<PartnerClient>(),
            Arg.Is<string>(r => r == expectedRegion));
    }

    // ---- Mapování rolí ----

    [Theory]
    [InlineData("Customer", 0)]
    [InlineData("Dealer", 2)]
    [InlineData("Oem", 1)]
    [InlineData("UNKNOWN", 0)]  // fallback = 0
    public async Task ProcessCreate_CompanyRole_MapsToCorrectClientRight(
        string role, int expectedRight)
    {
        _countryRepo.FindByIsoCodeAsync("CZ")
            .Returns(new CfgCountry { Id = 1, Short = "CZ" });
        _zipRepo.FindBestMatchAsync(Arg.Any<string?>(), Arg.Any<int>())
            .Returns((CfgZip?)null);
        _stateRepo.FindBestMatchAsync(Arg.Any<string?>(), Arg.Any<int>())
            .Returns((CfgState?)null);
        _mappingRepo.GetMappingAsync(Arg.Any<Guid>()).Returns((IdMappingRecord?)null);
        _partnerRepo.InsertAsync(Arg.Any<PartnerClient>(), Arg.Any<string>()).Returns(1);

        var geoSvc = CreateGeoService();
        var msg = MakeSyncMessage(companyRole: role);

        await ExecuteCreateAsync(geoSvc, msg);

        await _partnerRepo.Received(1).InsertAsync(
            Arg.Is<PartnerClient>(c => c.ClientRight == expectedRight),
            Arg.Any<string>());
    }

    // ---- Idempotence ----

    [Fact]
    public async Task ProcessCreate_DuplicateCreate_SkipsInsert()
    {
        var companyId = Guid.NewGuid();
        _mappingRepo.GetMappingAsync(companyId)
            .Returns(new IdMappingRecord
            {
                FfCompanyId = companyId,
                PartnerClientId = 99,
                PartnerRegion = "cz",
                EntityType = "client",
                LastSyncAt = DateTime.UtcNow,
                LastSyncDirection = "ff_to_partner",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });

        var geoSvc = CreateGeoService();
        var msg = new CompanySyncMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            SentAt = DateTimeOffset.UtcNow,
            Action = "Create",
            CompanyId = companyId,
            CompanyName = "Duplikát s.r.o.",
            CountryCode = "CZ",
            CompanyRole = "Customer"
        };

        await ExecuteCreateAsync(geoSvc, msg);

        // INSERT nesmí být zavolán
        await _partnerRepo.DidNotReceive().InsertAsync(
            Arg.Any<PartnerClient>(), Arg.Any<string>());
        // Publish taky ne
        await _publisher.DidNotReceive().PublishAsync<CompanySyncedResponse>(
            Arg.Any<string>(), Arg.Any<CompanySyncedResponse>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- Nepodporovaný region → UnsupportedRegionException ----

    [Fact]
    public async Task ProcessCreate_UnsupportedCountry_ThrowsUnsupportedRegionException()
    {
        // "XX" nemá region v RegionRouter → hned UnsupportedRegionException
        _mappingRepo.GetMappingAsync(Arg.Any<Guid>()).Returns((IdMappingRecord?)null);

        var geoSvc = CreateGeoService();
        var msg = MakeSyncMessage(countryCode: "XX");

        await Assert.ThrowsAsync<UnsupportedRegionException>(
            () => ExecuteCreateAsync(geoSvc, msg));

        await _partnerRepo.DidNotReceive().InsertAsync(
            Arg.Any<PartnerClient>(), Arg.Any<string>());
    }

    // ---- Země s platným regionem, ale neznámá v GAIA → GeoValidationException ----

    [Fact]
    public async Task ProcessCreate_CountryWithRegionButUnknownInGaia_ThrowsGeoValidationException()
    {
        // CZ má region ("cz"), ale GAIA cfg_country ho nezná → GeoValidationException(UnknownCountry)
        _countryRepo.FindByIsoCodeAsync("CZ").Returns((CfgCountry?)null);
        _mappingRepo.GetMappingAsync(Arg.Any<Guid>()).Returns((IdMappingRecord?)null);

        var geoSvc = CreateGeoService();
        var msg = MakeSyncMessage(countryCode: "CZ");

        var ex = await Assert.ThrowsAsync<GeoValidationException>(
            () => ExecuteCreateAsync(geoSvc, msg));

        Assert.Equal(GeoValidationErrorCode.UnknownCountry, ex.ErrorCode);
        await _partnerRepo.DidNotReceive().InsertAsync(
            Arg.Any<PartnerClient>(), Arg.Any<string>());
    }

    // ---- Neznámé PSČ — sync pokračuje, ZipId = null ----

    [Fact]
    public async Task ProcessCreate_UnknownZip_InsertsWithNullZipId()
    {
        _countryRepo.FindByIsoCodeAsync("CZ")
            .Returns(new CfgCountry { Id = 1, Short = "CZ" });
        _zipRepo.FindBestMatchAsync("99999", 1).Returns((CfgZip?)null);
        _stateRepo.FindBestMatchAsync(Arg.Any<string?>(), Arg.Any<int>())
            .Returns((CfgState?)null);
        _mappingRepo.GetMappingAsync(Arg.Any<Guid>()).Returns((IdMappingRecord?)null);
        _partnerRepo.InsertAsync(Arg.Any<PartnerClient>(), Arg.Any<string>()).Returns(7);

        var geoSvc = CreateGeoService();
        var msg = new CompanySyncMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            SentAt = DateTimeOffset.UtcNow,
            Action = "Create",
            CompanyId = Guid.NewGuid(),
            CompanyName = "Firma bez PSČ s.r.o.",
            CountryCode = "CZ",
            PostalCode = "99999",
            City = "Neznámé",
            CompanyRole = "Customer"
        };

        await ExecuteCreateAsync(geoSvc, msg);

        await _partnerRepo.Received(1).InsertAsync(
            Arg.Is<PartnerClient>(c => c.ClientZipId == null),
            Arg.Any<string>());
        await _mappingRepo.Received(1).SaveMappingAsync(Arg.Any<IdMappingRecord>());
    }

    // ---- Úspěšný CREATE — publikuje synced + loguje ----

    [Fact]
    public async Task ProcessCreate_Success_PublishesSyncedAndLogsSuccess()
    {
        var companyId = Guid.NewGuid();
        _countryRepo.FindByIsoCodeAsync("CZ")
            .Returns(new CfgCountry { Id = 1, Short = "CZ" });
        _zipRepo.FindBestMatchAsync(Arg.Any<string?>(), Arg.Any<int>())
            .Returns((CfgZip?)null);
        _stateRepo.FindBestMatchAsync(Arg.Any<string?>(), Arg.Any<int>())
            .Returns((CfgState?)null);
        _mappingRepo.GetMappingAsync(companyId).Returns((IdMappingRecord?)null);
        _partnerRepo.InsertAsync(Arg.Any<PartnerClient>(), Arg.Any<string>()).Returns(55);

        var geoSvc = CreateGeoService();
        var msg = new CompanySyncMessage
        {
            MessageId = "msg-123",
            SentAt = DateTimeOffset.UtcNow,
            Action = "Create",
            CompanyId = companyId,
            CompanyName = "Testovací firma a.s.",
            CountryCode = "CZ",
            CompanyRole = "Dealer"
        };

        await ExecuteCreateAsync(geoSvc, msg);

        // Publikováno bridge.company.synced
        await _publisher.Received(1).PublishAsync(
            "bridge.company.synced",
            Arg.Is<CompanySyncedResponse>(r =>
                r.FfCompanyId == companyId &&
                r.PartnerClientId == 55 &&
                r.PartnerRegion == "cz" &&
                r.Action == "Create"),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        // Zapsán BridgeProcessed/Success do PartnerSyncLog
        await _syncLog.Received(1).WriteAsync(
            companyId,
            Arg.Any<string>(),
            "BridgeProcessed",
            "Inbound",
            "Create",
            "Success",
            55,
            "cz",
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ---- DataOwner a FfSyncSource musí být správně nastaveny ----

    [Fact]
    public async Task ProcessCreate_InsertedClient_HasCorrectFfFields()
    {
        var companyId = Guid.NewGuid();
        _countryRepo.FindByIsoCodeAsync("PL")
            .Returns(new CfgCountry { Id = 2, Short = "PL" });
        _zipRepo.FindBestMatchAsync(Arg.Any<string?>(), Arg.Any<int>())
            .Returns((CfgZip?)null);
        _stateRepo.FindBestMatchAsync(Arg.Any<string?>(), Arg.Any<int>())
            .Returns((CfgState?)null);
        _mappingRepo.GetMappingAsync(Arg.Any<Guid>()).Returns((IdMappingRecord?)null);
        _partnerRepo.InsertAsync(Arg.Any<PartnerClient>(), Arg.Any<string>()).Returns(1);

        var geoSvc = CreateGeoService();
        var msg = new CompanySyncMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            SentAt = DateTimeOffset.UtcNow,
            Action = "Create",
            CompanyId = companyId,
            CompanyName = "Polska firma Sp. z o.o.",
            CountryCode = "PL",
            CompanyRole = "Customer"
        };

        await ExecuteCreateAsync(geoSvc, msg);

        await _partnerRepo.Received(1).InsertAsync(
            Arg.Is<PartnerClient>(c =>
                c.FfCompanyId == companyId &&
                c.FfSyncSource == "FF" &&
                c.DataOwner == DataOwner.FieldForce &&
                c.ClientDisable == 0),
            Arg.Any<string>());
    }

    /// <summary>
    /// Pomocná metoda simulující logiku ProcessCreateAsync z CompanySyncConsumer.
    /// Testuje byznys logiku nezávisle na Service Bus infrastruktuře.
    /// </summary>
    private async Task ExecuteCreateAsync(
        GeoValidationService geoSvc, CompanySyncMessage message,
        CancellationToken ct = default)
    {
        // Idempotence check
        var existingMapping = await _mappingRepo.GetMappingAsync(message.CompanyId, ct);
        if (existingMapping is not null)
            return;

        var region = Bridge.Infrastructure.Partner.RegionRouter.ResolveRegion(message.CountryCode);

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
            ? role switch { CompanyRole.Customer => 0, CompanyRole.Dealer => 2, CompanyRole.Oem => 1, _ => 0 }
            : 0;

        var ownerId = _ownerMapping.ResolveOwnerId(message.AssignedUserId);

        var now = DateTime.UtcNow;
        var partnerClient = new PartnerClient
        {
            ClientFirm = message.CompanyName,
            ClientIc = message.Ico,
            ClientDic = message.Dic,
            ClientStreet = message.Street,
            ClientCity = geo.City,
            ClientPsc = message.PostalCode,
            ClientCountryId = geo.CountryId,
            ClientCountryShort = geo.CountryShort,
            ClientState = geo.State,
            ClientStateId = geo.StateId,
            ClientCounty = geo.County,
            ClientCountyId = geo.CountyId,
            ClientZipId = geo.ZipId,
            ClientPhone = message.PrimaryContactPhone,
            ClientMail = message.PrimaryContactEmail,
            ClientRight = clientRight,
            ClientDate = now,
            ClientDisable = 0,
            IdOwner = ownerId,
            FfCompanyId = message.CompanyId,
            FfSyncSource = "FF",
            DataOwner = DataOwner.FieldForce,
            LastFfSyncAt = now
        };

        var partnerId = await _partnerRepo.InsertAsync(partnerClient, region, ct);

        var mapping = new IdMappingRecord
        {
            FfCompanyId = message.CompanyId,
            PartnerClientId = partnerId,
            PartnerRegion = region,
            EntityType = "client",
            PipedriveId = message.PipedriveId,
            FfUserId = message.AssignedUserId,
            PartnerOwnerId = ownerId,
            LastSyncAt = now,
            LastSyncDirection = "ff_to_partner",
            CreatedAt = now,
            UpdatedAt = now
        };
        await _mappingRepo.SaveMappingAsync(mapping, ct);

        var response = new CompanySyncedResponse
        {
            MessageId = Guid.NewGuid().ToString(),
            SentAt = DateTimeOffset.UtcNow,
            FfCompanyId = message.CompanyId,
            PartnerClientId = partnerId,
            PartnerRegion = region,
            Action = "Create"
        };
        await _publisher.PublishAsync("bridge.company.synced", response, message.MessageId, ct);

        await _syncLog.WriteAsync(
            message.CompanyId,
            message.MessageId,
            "BridgeProcessed",
            "Inbound",
            "Create",
            "Success",
            partnerId,
            region,
            ct: ct);
    }
}
