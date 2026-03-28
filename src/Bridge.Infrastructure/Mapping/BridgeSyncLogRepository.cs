using Bridge.Application.Interfaces;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Bridge.Infrastructure.Mapping;

/// <summary>
/// Repository pro audit log Bridge operací (bridge_sync_log v Azure SQL).
///
/// Tabulka slouží třem účelům:
/// 1. Diagnostika: GetLastAsync pro /api/sync-log endpoint (provozní monitoring)
/// 2. Saga recovery: GetPendingSagasAsync — detekuje nedokončené region přesuny po restartu
/// 3. Idempotence: HasOperationSucceededAsync — jednorázové operace (order_backfill)
///
/// Poznámka k INSERT: SyncLogEntry.CreatedAt má default = DateTime.UtcNow,
/// takže se při každém new SyncLogEntry { ... } automaticky nastaví na aktuální čas.
/// </summary>
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

        await conn.ExecuteAsync(new CommandDefinition(sql, entry, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<SyncLogEntry>> GetLastAsync(
        int count, CancellationToken ct = default)
    {
        // Clamp count: zamezit neúmyslně velkým dotazům z /api/sync-log?last=999999
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

        var rows = await conn.QueryAsync<SyncLogEntry>(
            new CommandDefinition(sql, new { Count = count }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<SyncLogEntry>> GetPendingSagasAsync(
        CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Logika: najít pending_region_change záznamy (operation='pending_region_change', status='in_progress')
        // které NEMAJÍ navazující region_change záznam pro stejné ff_company_id.
        // Okno 7 dní: zabraňuje prohledávání neomezeně historických dat při každém startu.
        // Výsledek se předá SagaRecoveryService která rozhodne, zda ságu doběhnout nebo zkompenzovat.
        const string sql = """
            SELECT
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
            FROM bridge_sync_log l
            WHERE l.operation = 'pending_region_change'
              AND l.status = 'in_progress'
              AND l.ff_company_id IS NOT NULL
              AND l.created_at > DATEADD(day, -7, GETUTCDATE())
              AND NOT EXISTS (
                  SELECT 1 FROM bridge_sync_log l2
                  WHERE l2.ff_company_id = l.ff_company_id
                    AND l2.operation = 'region_change'
                    AND l2.created_at > l.created_at
              )
            ORDER BY l.created_at ASC
            """;

        var rows = await conn.QueryAsync<SyncLogEntry>(
            new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<bool> HasOperationSucceededAsync(
        string operation, string region, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // SELECT EXISTS pattern: efektivnější než COUNT(*) — zastaví se u prvního nalezeného řádku.
        // Použití: OrderBackfillService kontroluje před každým regionem zda backfill již proběhl.
        const string sql = """
            SELECT CAST(
                CASE WHEN EXISTS (
                    SELECT 1 FROM bridge_sync_log
                    WHERE operation = @Operation
                      AND partner_region = @Region
                      AND status = 'success'
                ) THEN 1 ELSE 0 END
            AS BIT)
            """;

        return await conn.ExecuteScalarAsync<bool>(
            new CommandDefinition(sql, new { Operation = operation, Region = region },
                cancellationToken: ct));
    }
}
