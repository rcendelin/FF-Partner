using Bridge.Application.Interfaces;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Bridge.Infrastructure.Mapping;

public sealed class BridgeSyncLogRepository : ISyncLogRepository
{
    private readonly string _connectionString;

    public BridgeSyncLogRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task WriteAsync(SyncLogEntry entry, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        const string sql = """
            INSERT INTO bridge_sync_log (
                ff_company_id, partner_client_id, partner_region,
                operation, service_bus_message_id, status,
                error_message, payload_json, severity, created_at
            ) VALUES (
                @FfCompanyId, @PartnerClientId, @PartnerRegion,
                @Operation, @ServiceBusMessageId, @Status,
                @ErrorMessage, @PayloadJson, @Severity, @CreatedAt
            )
            """;

        await conn.ExecuteAsync(sql, entry);
    }

    public async Task<IReadOnlyList<SyncLogEntry>> GetLastAsync(
        int count, CancellationToken ct = default)
    {
        if (count is < 1 or > 500)
            count = 50;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT TOP (@Count)
                ff_company_id AS FfCompanyId,
                partner_client_id AS PartnerClientId,
                partner_region AS PartnerRegion,
                operation AS Operation,
                service_bus_message_id AS ServiceBusMessageId,
                status AS Status,
                error_message AS ErrorMessage,
                payload_json AS PayloadJson,
                severity AS Severity,
                created_at AS CreatedAt
            FROM bridge_sync_log
            ORDER BY created_at DESC
            """;

        var rows = await conn.QueryAsync<SyncLogEntry>(sql, new { Count = count });
        return rows.ToList();
    }
}
