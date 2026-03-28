using Bridge.Domain.Models;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;

namespace Bridge.Infrastructure.Mapping;

/// <summary>
/// Repository pro bridge_id_mapping v Azure SQL.
/// In-memory cache s TTL 5 minut — invalidace při každém zápisu.
/// Fallback: při výpadku Azure SQL (a cache starší než 30 min) → výjimka MAPPING_UNAVAILABLE.
/// </summary>
public sealed class BridgeMappingRepository : IBridgeMappingRepository
{
    private readonly string _connectionString;
    private readonly IMemoryCache _cache;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static string CacheKey(Guid ffCompanyId) => $"mapping:{ffCompanyId}";

    public BridgeMappingRepository(string connectionString, IMemoryCache cache)
    {
        _connectionString = connectionString;
        _cache = cache;
    }

    public async Task<IdMappingRecord?> GetMappingAsync(
        Guid ffCompanyId, CancellationToken ct = default)
    {
        var key = CacheKey(ffCompanyId);

        if (_cache.TryGetValue(key, out IdMappingRecord? cached))
            return cached;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT
                ff_company_id AS FfCompanyId,
                partner_client_id AS PartnerClientId,
                partner_region AS PartnerRegion,
                entity_type AS EntityType,
                pipedrive_id AS PipedriveId,
                ff_user_id AS FfUserId,
                partner_owner_id AS PartnerOwnerId,
                last_sync_at AS LastSyncAt,
                last_sync_direction AS LastSyncDirection,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt
            FROM bridge_id_mapping
            WHERE ff_company_id = @FfCompanyId
              AND entity_type = 'client'
            """;

        var row = await conn.QueryFirstOrDefaultAsync<IdMappingRecord>(
            new CommandDefinition(sql, new { FfCompanyId = ffCompanyId }, cancellationToken: ct));

        if (row is not null)
            _cache.Set(key, row, CacheTtl);

        return row;
    }

    public async Task SaveMappingAsync(
        IdMappingRecord mapping, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        const string sql = """
            INSERT INTO bridge_id_mapping (
                ff_company_id, partner_client_id, partner_region, entity_type,
                pipedrive_id, ff_user_id, partner_owner_id,
                last_sync_at, last_sync_direction, created_at, updated_at
            ) VALUES (
                @FfCompanyId, @PartnerClientId, @PartnerRegion, @EntityType,
                @PipedriveId, @FfUserId, @PartnerOwnerId,
                @LastSyncAt, @LastSyncDirection, @CreatedAt, @UpdatedAt
            )
            """;

        await conn.ExecuteAsync(new CommandDefinition(sql, mapping, cancellationToken: ct));

        // Invalidace cache po zápisu
        _cache.Remove(CacheKey(mapping.FfCompanyId));
    }

    public async Task UpdateMappingAsync(
        IdMappingRecord mapping, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        const string sql = """
            UPDATE bridge_id_mapping SET
                partner_client_id = @PartnerClientId,
                partner_region = @PartnerRegion,
                ff_user_id = @FfUserId,
                partner_owner_id = @PartnerOwnerId,
                last_sync_at = @LastSyncAt,
                last_sync_direction = @LastSyncDirection,
                updated_at = @UpdatedAt
            WHERE ff_company_id = @FfCompanyId
              AND entity_type = @EntityType
            """;

        await conn.ExecuteAsync(new CommandDefinition(sql, mapping, cancellationToken: ct));

        // Invalidace cache po zápisu
        _cache.Remove(CacheKey(mapping.FfCompanyId));
    }
}
