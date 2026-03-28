using Bridge.Application.Interfaces;
using Bridge.Domain.Models;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Bridge.Infrastructure.Polling;

/// <summary>
/// Spravuje MD5 state snapshoty objednávek pro detekci změn stavů (bridge_order_snapshot, Azure SQL).
/// </summary>
public sealed class OrderSnapshotRepository : IOrderSnapshotRepository
{
    private readonly string _connectionString;

    public OrderSnapshotRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<string?> GetHashAsync(string region, long orderId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT state_hash
            FROM bridge_order_snapshot
            WHERE partner_region = @Region AND order_id = @OrderId
            """;

        await using var conn = new SqlConnection(_connectionString);
        return await conn.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(sql, new { Region = region, OrderId = orderId },
                cancellationToken: ct));
    }

    public async Task UpsertAsync(string region, long orderId, string stateHash, CancellationToken ct = default)
    {
        const string sql = """
            MERGE bridge_order_snapshot AS target
            USING (SELECT @Region AS partner_region, @OrderId AS order_id) AS source
                ON target.partner_region = source.partner_region
                AND target.order_id = source.order_id
            WHEN MATCHED THEN
                UPDATE SET state_hash = @StateHash, last_checked = @LastChecked
            WHEN NOT MATCHED THEN
                INSERT (partner_region, order_id, state_hash, last_checked)
                VALUES (@Region, @OrderId, @StateHash, @LastChecked);
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            new CommandDefinition(sql, new
            {
                Region = region,
                OrderId = orderId,
                StateHash = stateHash,
                LastChecked = DateTime.UtcNow
            }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<OrderSnapshot>> GetRegionSnapshotsAsync(
        string region, CancellationToken ct = default)
    {
        const string sql = """
            SELECT partner_region AS PartnerRegion,
                   order_id AS OrderId,
                   state_hash AS StateHash,
                   last_checked AS LastChecked
            FROM bridge_order_snapshot
            WHERE partner_region = @Region
            """;

        await using var conn = new SqlConnection(_connectionString);
        var result = await conn.QueryAsync<OrderSnapshot>(
            new CommandDefinition(sql, new { Region = region }, cancellationToken: ct));
        return result.AsList();
    }

    public async Task BulkUpsertAsync(IEnumerable<OrderSnapshot> snapshots, CancellationToken ct = default)
    {
        const string sql = """
            MERGE bridge_order_snapshot AS target
            USING (SELECT @PartnerRegion AS partner_region, @OrderId AS order_id) AS source
                ON target.partner_region = source.partner_region
                AND target.order_id = source.order_id
            WHEN MATCHED THEN
                UPDATE SET state_hash = @StateHash, last_checked = @LastChecked
            WHEN NOT MATCHED THEN
                INSERT (partner_region, order_id, state_hash, last_checked)
                VALUES (@PartnerRegion, @OrderId, @StateHash, @LastChecked);
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        foreach (var snap in snapshots)
        {
            if (ct.IsCancellationRequested) break;
            await conn.ExecuteAsync(
                new CommandDefinition(sql, new
                {
                    snap.PartnerRegion,
                    snap.OrderId,
                    snap.StateHash,
                    snap.LastChecked
                }, cancellationToken: ct));
        }
    }
}
