using Bridge.Domain.Models;

namespace Bridge.Application.Interfaces;

/// <summary>
/// Read-only přístup k cfg_country v GAIA DB.
/// POUZE SELECT — nikdy INSERT, UPDATE, DELETE.
/// </summary>
public interface IGaiaCountryRepository
{
    Task<CfgCountry?> FindByIsoCodeAsync(string isoCode, CancellationToken ct = default);
}

/// <summary>
/// Read-only přístup k cfg_zip v GAIA DB.
/// POUZE SELECT — nikdy INSERT, UPDATE, DELETE.
/// Při nenalezení vrátit null — sync NEBLOKOVAT.
/// </summary>
public interface IGaiaZipRepository
{
    Task<CfgZip?> FindBestMatchAsync(string? postalCode, int countryId, CancellationToken ct = default);
}

/// <summary>
/// Read-only přístup k cfg_state v GAIA DB.
/// POUZE SELECT — nikdy INSERT, UPDATE, DELETE.
/// </summary>
public interface IGaiaStateRepository
{
    Task<CfgState?> FindBestMatchAsync(string? stateName, int countryId, CancellationToken ct = default);
}

/// <summary>
/// Read-only přístup k cfg_county v GAIA DB.
/// POUZE SELECT — nikdy INSERT, UPDATE, DELETE.
/// </summary>
public interface IGaiaCountyRepository
{
    Task<CfgCounty?> FindBestMatchAsync(string? countyName, int? parentCountyId, CancellationToken ct = default);
}
