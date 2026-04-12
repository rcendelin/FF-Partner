using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Bridge.Infrastructure.FieldForce;

/// <summary>
/// Writes sync phases to the shared PartnerSyncLog table in FieldForce's Azure SQL.
/// Fire-and-forget safe — failures are logged but never block Partner3 sync.
/// </summary>
public class SyncLogWriter
{
    private readonly string? _connectionString;
    private readonly ILogger<SyncLogWriter> _logger;

    public SyncLogWriter(IConfiguration configuration, ILogger<SyncLogWriter> logger)
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
                    ErrorMessage = Truncate(errorMessage, 2000),
                    PayloadJson = Truncate(payloadJson, 65536),
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write PartnerSyncLog for Company {CompanyId} Phase {Phase}", companyId, phase);
        }
    }

    private static string? Truncate(string? value, int maxLength)
        => value is { Length: > 0 } && value.Length > maxLength ? value[..maxLength] : value;
}
