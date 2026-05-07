using Bridge.Application.Interfaces;
using Bridge.Domain.Exceptions;
using Bridge.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Bridge.Application.Services;

/// <summary>
/// Validace adresy proti GAIA číselníkům.
/// Implementuje přesně pravidla z CLAUDE.md sekce 7.
///
/// Klíčová pravidla:
/// - Neznámá ZEMĚ → GeoValidationException(UnknownCountry) — tvrdá chyba, sync selže
/// - Neznámé PSČ → zip_id = null, logovat Warning — sync NEBLOKOVAT
/// - Neznámý kraj/okres → null — sync NEBLOKOVAT
/// - NIKDY INSERT do cfg_zip, cfg_county, cfg_state, cfg_country
/// </summary>
public interface IGeoValidationService
{
    Task<GeoValidationResult> ValidateAsync(AddressDto address, CancellationToken ct = default);
}

public sealed class GeoValidationService : IGeoValidationService
{
    private readonly IGaiaCountryRepository _countryRepo;
    private readonly IGaiaZipRepository _zipRepo;
    private readonly IGaiaStateRepository _stateRepo;
    private readonly IGaiaCountyRepository _countyRepo;
    private readonly IPartnerSyncLog _syncLog;
    private readonly ILogger<GeoValidationService> _logger;

    public GeoValidationService(
        IGaiaCountryRepository countryRepo,
        IGaiaZipRepository zipRepo,
        IGaiaStateRepository stateRepo,
        IGaiaCountyRepository countyRepo,
        IPartnerSyncLog syncLog,
        ILogger<GeoValidationService> logger)
    {
        _countryRepo = countryRepo;
        _zipRepo = zipRepo;
        _stateRepo = stateRepo;
        _countyRepo = countyRepo;
        _syncLog = syncLog;
        _logger = logger;
    }

    public async Task<GeoValidationResult> ValidateAsync(
        AddressDto address, CancellationToken ct = default)
    {
        // 1. Lookup country — POUZE SELECT, nikdy INSERT
        // Neznámá země = tvrdá chyba → sync selže, publikovat sync-failed
        var country = await _countryRepo.FindByIsoCodeAsync(address.CountryCode, ct)
            ?? throw new GeoValidationException(
                $"Neznámá země: {address.CountryCode}",
                GeoValidationErrorCode.UnknownCountry);

        // 2. Fuzzy lookup PSČ — při nenalezení VRÁTIT NULL, ne výjimku
        // ZIP NENÍ TVRDÁ CHYBA — sync pokračuje s zip_id = null
        var zip = await _zipRepo.FindBestMatchAsync(address.PostalCode, country.Id, ct);
        if (zip == null && !string.IsNullOrWhiteSpace(address.PostalCode))
        {
            _logger.LogWarning(
                "Neznámé PSČ {PostalCode} pro {Country} — zip_id bude NULL. Sync pokračuje.",
                address.PostalCode, address.CountryCode);

            await _syncLog.WriteAsync(
                companyId: Guid.Empty,
                correlationMessageId: $"geo-{address.CountryCode}-{address.PostalCode}",
                phase: "Validation",
                direction: "Internal",
                operation: "geo_validation",
                status: "Warning",
                payloadJson: System.Text.Json.JsonSerializer.Serialize(new
                {
                    postalCode = address.PostalCode,
                    countryCode = address.CountryCode,
                    city = address.City
                }),
                ct: ct);
        }

        // 3. Fuzzy lookup kraj — při nenalezení vrátit null (ne výjimka)
        var state = await _stateRepo.FindBestMatchAsync(address.State, country.Id, ct);

        // 4. Fuzzy lookup okres — pouze pokud máme zip (zip obsahuje county_id)
        var county = zip?.CountyId.HasValue == true
            ? await _countyRepo.FindBestMatchAsync(address.County, zip.CountyId, ct)
            : null;

        return new GeoValidationResult
        {
            CountryId = country.Id,
            CountryShort = country.Short,
            ZipId = zip?.Id,            // MŮŽE BÝT NULL
            City = zip?.City ?? address.City,
            StateId = state?.Id,        // MŮŽE BÝT NULL
            State = state?.Name ?? address.State,
            CountyId = county?.Id,      // MŮŽE BÝT NULL
            County = county?.Name ?? address.County
        };
    }
}
