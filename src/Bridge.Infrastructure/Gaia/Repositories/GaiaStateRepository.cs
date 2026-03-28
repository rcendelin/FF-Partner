using Bridge.Application.Interfaces;
using Bridge.Domain.Models;
using Dapper;

namespace Bridge.Infrastructure.Gaia.Repositories;

public sealed class GaiaStateRepository : IGaiaStateRepository
{
    private readonly IGaiaDbConnectionFactory _connectionFactory;

    public GaiaStateRepository(IGaiaDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// LIKE match pro kraj. Při nenalezení vrátí null (sync se neblokuje).
    /// POUZE SELECT — nikdy INSERT.
    /// </summary>
    public async Task<CfgState?> FindBestMatchAsync(
        string? stateName, int countryId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(stateName))
            return null;

        await using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT id AS Id, name AS Name, country_id AS CountryId
            FROM cfg_state
            WHERE country_id = @CountryId
              AND name LIKE @Pattern
            LIMIT 1
            """;

        return await conn.QueryFirstOrDefaultAsync<CfgState>(
            sql, new { CountryId = countryId, Pattern = $"%{stateName}%" });
    }
}
