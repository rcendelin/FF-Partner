using Bridge.Application.Interfaces;
using Bridge.Domain.Models;
using Dapper;

namespace Bridge.Infrastructure.Gaia.Repositories;

public sealed class GaiaCountryRepository : IGaiaCountryRepository
{
    private readonly IGaiaDbConnectionFactory _connectionFactory;

    public GaiaCountryRepository(IGaiaDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<CfgCountry?> FindByIsoCodeAsync(
        string isoCode, CancellationToken ct = default)
    {
        await using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync(ct);

        // POUZE SELECT — nikdy INSERT
        const string sql = """
            SELECT id AS Id, short AS Short, name AS Name
            FROM cfg_country
            WHERE short = @IsoCode
            LIMIT 1
            """;

        return await conn.QueryFirstOrDefaultAsync<CfgCountry>(
            sql, new { IsoCode = isoCode.ToUpperInvariant() });
    }
}
