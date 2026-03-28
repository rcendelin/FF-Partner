using Bridge.Application.Interfaces;
using Bridge.Domain.Models;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Bridge.Infrastructure.Polling;

/// <summary>
/// Ukládá a načítá watermark pro polling nových objednávek do bridge_poll_watermark (Azure SQL).
/// Tabulka se inicializuje s hodnotou 0 pro každý region — poprvé polluje od epoch.
/// </summary>
public sealed class PollWatermarkRepository : IPollWatermarkRepository
{
    private readonly string _connectionString;

    public PollWatermarkRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<PollWatermark?> GetAsync(string pollTarget, CancellationToken ct = default)
    {
        const string sql = """
            SELECT poll_target AS PollTarget,
                   last_processed_order_date AS LastProcessedOrderDate,
                   last_processed_id AS LastProcessedId,
                   updated_at AS UpdatedAt
            FROM bridge_poll_watermark
            WHERE poll_target = @PollTarget
            """;

        await using var conn = new SqlConnection(_connectionString);
        var result = await conn.QuerySingleOrDefaultAsync<PollWatermark>(
            new CommandDefinition(sql, new { PollTarget = pollTarget },
                cancellationToken: ct));
        return result;
    }

    public async Task UpsertAsync(PollWatermark watermark, CancellationToken ct = default)
    {
        const string sql = """
            MERGE bridge_poll_watermark AS target
            USING (SELECT @PollTarget AS poll_target) AS source
                ON target.poll_target = source.poll_target
            WHEN MATCHED THEN
                UPDATE SET
                    last_processed_order_date = @LastProcessedOrderDate,
                    last_processed_id = @LastProcessedId,
                    updated_at = @UpdatedAt
            WHEN NOT MATCHED THEN
                INSERT (poll_target, last_processed_order_date, last_processed_id, updated_at)
                VALUES (@PollTarget, @LastProcessedOrderDate, @LastProcessedId, @UpdatedAt);
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            new CommandDefinition(sql, new
            {
                watermark.PollTarget,
                watermark.LastProcessedOrderDate,
                watermark.LastProcessedId,
                watermark.UpdatedAt
            }, cancellationToken: ct));
    }
}
