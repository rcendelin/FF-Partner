using Bridge.Application.Interfaces;
using Bridge.Domain.Models;
using Dapper;

namespace Bridge.Infrastructure.Gaia.Repositories;

public sealed class GaiaCountyRepository : IGaiaCountyRepository
{
    private readonly IGaiaDbConnectionFactory _connectionFactory;

    public GaiaCountyRepository(IGaiaDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// LIKE match pro okres. Při nenalezení vrátí null (sync se neblokuje).
    /// POUZE SELECT — nikdy INSERT.
    /// </summary>
    public async Task<CfgCounty?> FindBestMatchAsync(
        string? countyName, int? parentCountyId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(countyName))
            return null;

        await using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync(ct);

        if (parentCountyId.HasValue)
        {
            const string sqlWithParent = """
                SELECT id AS Id, name AS Name, state_id AS StateId
                FROM cfg_county
                WHERE state_id = @StateId
                  AND name LIKE @Pattern
                LIMIT 1
                """;

            var result = await conn.QueryFirstOrDefaultAsync<CfgCounty>(
                sqlWithParent, new { StateId = parentCountyId.Value, Pattern = $"%{countyName}%" });

            if (result is not null)
                return result;
        }

        // Fallback bez parent filtru
        const string sqlFallback = """
            SELECT id AS Id, name AS Name, state_id AS StateId
            FROM cfg_county
            WHERE name LIKE @Pattern
            LIMIT 1
            """;

        return await conn.QueryFirstOrDefaultAsync<CfgCounty>(
            sqlFallback, new { Pattern = $"%{countyName}%" });
    }
}
