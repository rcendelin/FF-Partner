namespace Bridge.Domain.Models;

/// <summary>
/// GAIA číselník zemí — cfg_country.
/// Bridge pouze čte, nikdy nepíše.
/// </summary>
public sealed class CfgCountry
{
    public int Id { get; init; }
    public required string Short { get; init; }  // ISO kód (CZ, PL, ...)
    public string? Name { get; init; }
}

/// <summary>
/// GAIA číselník PSČ — cfg_zip.
/// Bridge pouze čte, nikdy nepíše.
/// </summary>
public sealed class CfgZip
{
    public int Id { get; init; }
    public required string ZipCode { get; init; }
    public string? City { get; init; }
    public int CountryId { get; init; }
    public int? CountyId { get; init; }
    public int? StateId { get; init; }
}

/// <summary>
/// GAIA číselník krajů — cfg_state.
/// Bridge pouze čte, nikdy nepíše.
/// </summary>
public sealed class CfgState
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public int CountryId { get; init; }
}

/// <summary>
/// GAIA číselník okresů — cfg_county.
/// Bridge pouze čte, nikdy nepíše.
/// </summary>
public sealed class CfgCounty
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public int? StateId { get; init; }
}
