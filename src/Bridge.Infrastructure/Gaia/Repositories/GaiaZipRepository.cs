using Bridge.Application.Interfaces;
using Bridge.Domain.Models;
using Dapper;

namespace Bridge.Infrastructure.Gaia.Repositories;

public sealed class GaiaZipRepository : IGaiaZipRepository
{
    private readonly IGaiaDbConnectionFactory _connectionFactory;

    public GaiaZipRepository(IGaiaDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Fuzzy lookup PSČ: nejprve přesná shoda, pak shoda bez mezer, pak Levenshtein ≤ 2.
    /// Při nenalezení vrátí null — sync se NEBLOKUJE.
    /// POUZE SELECT — nikdy INSERT.
    /// </summary>
    public async Task<CfgZip?> FindBestMatchAsync(
        string? postalCode, int countryId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(postalCode))
            return null;

        await using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync(ct);

        // 1. Přesná shoda
        const string exactSql = """
            SELECT id AS Id, zip AS ZipCode, city AS City,
                   country_id AS CountryId, county_id AS CountyId, state_id AS StateId
            FROM cfg_zip
            WHERE zip = @PostalCode AND country_id = @CountryId
            LIMIT 1
            """;

        var exact = await conn.QueryFirstOrDefaultAsync<CfgZip>(
            exactSql, new { PostalCode = postalCode, CountryId = countryId });

        if (exact is not null)
            return exact;

        // 2. Shoda bez mezer (CZ PSČ: "110 00" → "11000")
        var normalized = postalCode.Replace(" ", "").Replace("-", "");
        if (normalized != postalCode)
        {
            var normalizedMatch = await conn.QueryFirstOrDefaultAsync<CfgZip>(
                exactSql, new { PostalCode = normalized, CountryId = countryId });

            if (normalizedMatch is not null)
                return normalizedMatch;
        }

        // 3. Fuzzy — kandidáti dle prefix (prvních 3 znaků), pak Levenshtein ≤ 2 v C#
        if (normalized.Length < 3)
            return null;

        var prefix = normalized[..3];
        const string fuzzySql = """
            SELECT id AS Id, zip AS ZipCode, city AS City,
                   country_id AS CountryId, county_id AS CountyId, state_id AS StateId
            FROM cfg_zip
            WHERE country_id = @CountryId
              AND zip LIKE @Prefix
            LIMIT 50
            """;

        var candidates = (await conn.QueryAsync<CfgZip>(
            fuzzySql, new { CountryId = countryId, Prefix = prefix + "%" })).ToList();

        return candidates
            .Select(c => (Record: c, Distance: LevenshteinDistance(
                normalized, c.ZipCode.Replace(" ", "").Replace("-", ""))))
            .Where(x => x.Distance <= 2)
            .OrderBy(x => x.Distance)
            .Select(x => x.Record)
            .FirstOrDefault();
    }

    private static int LevenshteinDistance(string s, string t)
    {
        if (s.Length == 0) return t.Length;
        if (t.Length == 0) return s.Length;

        var d = new int[s.Length + 1, t.Length + 1];

        for (var i = 0; i <= s.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= t.Length; j++) d[0, j] = j;

        for (var i = 1; i <= s.Length; i++)
        for (var j = 1; j <= t.Length; j++)
        {
            var cost = s[i - 1] == t[j - 1] ? 0 : 1;
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                d[i - 1, j - 1] + cost);
        }

        return d[s.Length, t.Length];
    }
}
