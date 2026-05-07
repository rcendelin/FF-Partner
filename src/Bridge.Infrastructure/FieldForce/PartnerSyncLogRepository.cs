using Bridge.Application.Interfaces;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Bridge.Infrastructure.FieldForce;

/// <summary>
/// Sjednocený přístup k PartnerSyncLog v FieldForce Azure SQL.
/// Nahrazuje původní <c>SyncLogWriter</c> + <c>BridgeSyncLogRepository</c> — viz F-PartnerSyncLog migrace.
///
/// Fire-and-forget zápis: WriteAsync nikdy neproparuje výjimku — sync log nesmí blokovat sync.
/// Read metody (GetLastAsync, GetPendingSagasAsync, HasOperationSucceededAsync) výjimky propouštějí —
/// volající (SagaRecoveryService, OrderBackfillService, /api/sync-log) řeší sami.
/// </summary>
public sealed class PartnerSyncLogRepository : IPartnerSyncLog
{
    private const int MaxErrorMessageLength = 2000;
    private const int MaxPayloadLength = 65536;

    private readonly string? _connectionString;
    private readonly ILogger<PartnerSyncLogRepository> _logger;

    public PartnerSyncLogRepository(IConfiguration configuration, ILogger<PartnerSyncLogRepository> logger)
    {
        _connectionString = configuration["FieldForceDb"];
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_connectionString))
            _logger.LogWarning("FieldForceDb connection string not configured — sync log writes will be skipped");
    }

    public async Task WriteAsync(
        Guid companyId,
        string correlationMessageId,
        string phase,
        string direction,
        string operation,
        string status,
        int? partnerClientId = null,
        string? partnerRegion = null,
        string? errorCode = null,
        string? errorMessage = null,
        string? payloadJson = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return;

        try
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync(
                @"INSERT INTO PartnerSyncLog
                    (CompanyId, CorrelationMessageId, Phase, Direction, Operation, Status,
                     PartnerClientId, PartnerRegion, ErrorCode, ErrorMessage, PayloadJson,
                     Source, CreatedAt)
                  VALUES
                    (@CompanyId, @CorrelationMessageId, @Phase, @Direction, @Operation, @Status,
                     @PartnerClientId, @PartnerRegion, @ErrorCode, @ErrorMessage, @PayloadJson,
                     'Bridge', SYSDATETIMEOFFSET())",
                new
                {
                    CompanyId = companyId,
                    CorrelationMessageId = correlationMessageId,
                    Phase = phase,
                    Direction = direction,
                    Operation = operation,
                    Status = status,
                    PartnerClientId = partnerClientId,
                    PartnerRegion = partnerRegion,
                    ErrorCode = errorCode,
                    ErrorMessage = Truncate(errorMessage, MaxErrorMessageLength),
                    PayloadJson = Truncate(payloadJson, MaxPayloadLength),
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write PartnerSyncLog for Company {CompanyId} Phase {Phase}", companyId, phase);
        }
    }

    public async Task<IReadOnlyList<PartnerSyncLogEntry>> GetLastAsync(int count, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return Array.Empty<PartnerSyncLogEntry>();

        // Clamp — chrání před DoS přes /api/sync-log?last=999999
        if (count is < 1 or > 500) count = 50;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        const string sql = @"
            SELECT TOP (@Count)
                CompanyId, CorrelationMessageId, Phase, Direction, Operation, Status,
                PartnerClientId, PartnerRegion, ErrorCode, ErrorMessage, PayloadJson,
                Source, CreatedAt
            FROM PartnerSyncLog
            WHERE Source = 'Bridge'
            ORDER BY CreatedAt DESC";

        var rows = await conn.QueryAsync<PartnerSyncLogEntry>(
            new CommandDefinition(sql, new { Count = count }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<PartnerSyncLogEntry>> GetPendingSagasAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return Array.Empty<PartnerSyncLogEntry>();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Logika: najít region_change záznamy se Status='InProgress' (Phase='SagaPending'),
        // které nemají pozdější terminální status (Success, Compensated, CompensationFailed).
        // Okno 7 dní: omezuje historický scope při každém startu Bridge.
        const string sql = @"
            SELECT
                CompanyId, CorrelationMessageId, Phase, Direction, Operation, Status,
                PartnerClientId, PartnerRegion, ErrorCode, ErrorMessage, PayloadJson,
                Source, CreatedAt
            FROM PartnerSyncLog l
            WHERE l.Source = 'Bridge'
              AND l.Operation = 'region_change'
              AND l.Status = 'InProgress'
              AND l.CreatedAt > DATEADD(day, -7, SYSUTCDATETIME())
              AND NOT EXISTS (
                  SELECT 1 FROM PartnerSyncLog l2
                  WHERE l2.Source = 'Bridge'
                    AND l2.CompanyId = l.CompanyId
                    AND l2.Operation = 'region_change'
                    AND l2.Status IN ('Success', 'Compensated', 'CompensationFailed')
                    AND l2.CreatedAt > l.CreatedAt
              )
            ORDER BY l.CreatedAt ASC";

        var rows = await conn.QueryAsync<PartnerSyncLogEntry>(
            new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<bool> HasOperationSucceededAsync(string operation, string region, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return false;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // SELECT EXISTS — efektivnější než COUNT(*), zastaví se u prvního nalezeného řádku.
        const string sql = @"
            SELECT CAST(
                CASE WHEN EXISTS (
                    SELECT 1 FROM PartnerSyncLog
                    WHERE Source = 'Bridge'
                      AND Operation = @Operation
                      AND PartnerRegion = @Region
                      AND Status = 'Success'
                ) THEN 1 ELSE 0 END
            AS BIT)";

        return await conn.ExecuteScalarAsync<bool>(
            new CommandDefinition(sql, new { Operation = operation, Region = region }, cancellationToken: ct));
    }

    private static string? Truncate(string? value, int maxLength)
        => value is { Length: > 0 } && value.Length > maxLength ? value[..maxLength] : value;
}
