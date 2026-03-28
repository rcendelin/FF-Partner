using Bridge.Application.Interfaces;
using Bridge.Application.Services;
using Bridge.Domain.Exceptions;
using Bridge.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Bridge.Tests;

public class GeoValidationServiceTests
{
    private readonly IGaiaCountryRepository _countryRepo = Substitute.For<IGaiaCountryRepository>();
    private readonly IGaiaZipRepository _zipRepo = Substitute.For<IGaiaZipRepository>();
    private readonly IGaiaStateRepository _stateRepo = Substitute.For<IGaiaStateRepository>();
    private readonly IGaiaCountyRepository _countyRepo = Substitute.For<IGaiaCountyRepository>();
    private readonly ISyncLogRepository _syncLog = Substitute.For<ISyncLogRepository>();

    private GeoValidationService CreateService() => new(
        _countryRepo, _zipRepo, _stateRepo, _countyRepo, _syncLog,
        NullLogger<GeoValidationService>.Instance);

    // ---- Přesná adresa CZ ----

    [Fact]
    public async Task ValidateAsync_KnownCzAddress_ReturnsFullResult()
    {
        _countryRepo.FindByIsoCodeAsync("CZ")
            .Returns(new CfgCountry { Id = 1, Short = "CZ", Name = "Česká republika" });
        _zipRepo.FindBestMatchAsync("11000", 1)
            .Returns(new CfgZip { Id = 10, ZipCode = "11000", City = "Praha", CountryId = 1, CountyId = 5 });
        _stateRepo.FindBestMatchAsync("Praha", 1)
            .Returns(new CfgState { Id = 3, Name = "Praha", CountryId = 1 });
        _countyRepo.FindBestMatchAsync("Praha 1", 5)
            .Returns(new CfgCounty { Id = 50, Name = "Praha 1", StateId = 3 });

        var address = new AddressDto { CountryCode = "CZ", PostalCode = "11000", City = "Praha", State = "Praha", County = "Praha 1" };
        var result = await CreateService().ValidateAsync(address);

        Assert.Equal(1, result.CountryId);
        Assert.Equal("CZ", result.CountryShort);
        Assert.Equal(10, result.ZipId);
        Assert.Equal("Praha", result.City);
        Assert.Equal(3, result.StateId);
        Assert.Equal(50, result.CountyId);
    }

    // ---- Neznámé PSČ — sync nesmí selhat ----

    [Fact]
    public async Task ValidateAsync_UnknownZip_ReturnsNullZipId_AndContinues()
    {
        _countryRepo.FindByIsoCodeAsync("PL")
            .Returns(new CfgCountry { Id = 2, Short = "PL" });
        _zipRepo.FindBestMatchAsync("99-999", 2)
            .Returns((CfgZip?)null);
        _stateRepo.FindBestMatchAsync(null, 2)
            .Returns((CfgState?)null);

        var address = new AddressDto { CountryCode = "PL", PostalCode = "99-999", City = "Nieznane" };
        var result = await CreateService().ValidateAsync(address);

        Assert.Null(result.ZipId);     // zip_id = null je OK
        Assert.Equal(2, result.CountryId);
        // Sync log musí obsahovat geo_validation_warning
        await _syncLog.Received(1).WriteAsync(
            Arg.Is<SyncLogEntry>(e => e.Operation == "geo_validation_warning" && e.Severity == "Warning"));
    }

    // ---- Neznámá ZEMĚ — tvrdá chyba ----

    [Fact]
    public async Task ValidateAsync_UnknownCountry_ThrowsGeoValidationException()
    {
        _countryRepo.FindByIsoCodeAsync("XX")
            .Returns((CfgCountry?)null);

        var address = new AddressDto { CountryCode = "XX" };
        var ex = await Assert.ThrowsAsync<GeoValidationException>(
            () => CreateService().ValidateAsync(address));

        Assert.Equal(GeoValidationErrorCode.UnknownCountry, ex.ErrorCode);
    }

    // ---- Prázdné PSČ — nelogovat warning ----

    [Fact]
    public async Task ValidateAsync_EmptyZip_NoWarningLogged()
    {
        _countryRepo.FindByIsoCodeAsync("HU")
            .Returns(new CfgCountry { Id = 3, Short = "HU" });
        _zipRepo.FindBestMatchAsync(null, 3)
            .Returns((CfgZip?)null);
        _stateRepo.FindBestMatchAsync(null, 3)
            .Returns((CfgState?)null);

        var address = new AddressDto { CountryCode = "HU", PostalCode = null };
        await CreateService().ValidateAsync(address);

        // Prázdné PSČ — žádný warning
        await _syncLog.DidNotReceive().WriteAsync(Arg.Any<SyncLogEntry>());
    }

    // ---- Kraj/okres null — sync pokračuje ----

    [Fact]
    public async Task ValidateAsync_UnknownStateAndCounty_ReturnsNulls()
    {
        _countryRepo.FindByIsoCodeAsync("US")
            .Returns(new CfgCountry { Id = 4, Short = "US" });
        _zipRepo.FindBestMatchAsync("10001", 4)
            .Returns(new CfgZip { Id = 100, ZipCode = "10001", City = "New York", CountryId = 4, CountyId = null });
        _stateRepo.FindBestMatchAsync("New York", 4)
            .Returns((CfgState?)null);

        var address = new AddressDto { CountryCode = "US", PostalCode = "10001", State = "New York" };
        var result = await CreateService().ValidateAsync(address);

        Assert.Null(result.StateId);
        Assert.Null(result.CountyId);
        Assert.Equal(100, result.ZipId);
    }

    // ---- Fallback City na address.City pokud zip.City = null ----

    [Fact]
    public async Task ValidateAsync_ZipFoundButNoCity_UsesFallbackCity()
    {
        _countryRepo.FindByIsoCodeAsync("RO")
            .Returns(new CfgCountry { Id = 5, Short = "RO" });
        _zipRepo.FindBestMatchAsync("010001", 5)
            .Returns(new CfgZip { Id = 200, ZipCode = "010001", City = null, CountryId = 5 });
        _stateRepo.FindBestMatchAsync(null, 5)
            .Returns((CfgState?)null);

        var address = new AddressDto { CountryCode = "RO", PostalCode = "010001", City = "București" };
        var result = await CreateService().ValidateAsync(address);

        Assert.Equal("București", result.City); // fallback na address.City
    }
}
