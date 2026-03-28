using Bridge.Domain.Exceptions;

namespace Bridge.Infrastructure.Partner;

/// <summary>
/// Routuje firmy do správné regionální Partner3 DB dle ISO kódu země.
/// Dle CLAUDE.md sekce 6 — nesmí být změněno bez explicitního souhlasu.
/// </summary>
public static class RegionRouter
{
    public static string ResolveRegion(string countryCode) => countryCode switch
    {
        "CZ" or "SK" or "UA" or "AT" or "FR" => "cz",
        "PL" or "LT" or "LV" or "EE"         => "pl",
        "HU" or "RO"                           => "hu",
        "US" or "CA" or "AU" or "BR"          => "us",
        "DE" => throw new InvalidOperationException(
            "DE nemá automatický region — nutná konfigurace v owner mappingu."),
        _ => throw new UnsupportedRegionException(countryCode)
    };
}
