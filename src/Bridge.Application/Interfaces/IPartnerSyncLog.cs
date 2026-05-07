namespace Bridge.Application.Interfaces;

/// <summary>
/// Záznam z PartnerSyncLog (FieldForce Azure SQL).
/// Sloupce odpovídají schématu PartnerSyncLog vlastněnému FF týmem.
/// </summary>
public sealed class PartnerSyncLogEntry
{
    public Guid CompanyId { get; init; }
    public required string CorrelationMessageId { get; init; }
    public required string Phase { get; init; }
    public required string Direction { get; init; }
    public required string Operation { get; init; }
    public required string Status { get; init; }
    public int? PartnerClientId { get; init; }
    public string? PartnerRegion { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? PayloadJson { get; init; }
    public string Source { get; init; } = "Bridge";
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Sjednocený zápis i čtení do PartnerSyncLog v FieldForce Azure SQL.
/// Implementace: <c>Bridge.Infrastructure.FieldForce.PartnerSyncLogRepository</c>.
///
/// Žije v Application vrstvě (ne v Infrastructure.FieldForce), aby ho mohly používat
/// Application služby (např. GeoValidationService) bez porušení vrstvení.
///
/// Fire-and-forget filozofie: implementace nesmí propustit výjimku z WriteAsync —
/// sync log nikdy neblokuje hlavní sync operaci.
/// </summary>
public interface IPartnerSyncLog
{
    /// <summary>
    /// Zapíše jeden řádek do PartnerSyncLog. Selhání je logováno, ale neproparuje výjimku.
    /// </summary>
    Task WriteAsync(
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
        CancellationToken ct = default);

    /// <summary>
    /// Vrátí posledních <paramref name="count"/> řádků (DESC by CreatedAt) — pro /api/sync-log diagnostiku.
    /// </summary>
    Task<IReadOnlyList<PartnerSyncLogEntry>> GetLastAsync(int count, CancellationToken ct = default);

    /// <summary>
    /// Vrátí nedokončené region-change ságy: Operation='region_change' a Status='InProgress',
    /// které nemají pozdější Status IN ('Success','Compensated','CompensationFailed').
    /// Použito v <c>SagaRecoveryService</c> při startu Bridge.
    /// Okno 7 dní — zabraňuje neomezenému prohledávání historie.
    /// </summary>
    Task<IReadOnlyList<PartnerSyncLogEntry>> GetPendingSagasAsync(CancellationToken ct = default);

    /// <summary>
    /// Idempotency check — vrací true pokud existuje řádek
    /// Operation=<paramref name="operation"/> AND PartnerRegion=<paramref name="region"/>
    /// AND Status='Success' AND Source='Bridge'.
    /// Použito v <c>OrderBackfillService</c>.
    /// </summary>
    Task<bool> HasOperationSucceededAsync(string operation, string region, CancellationToken ct = default);
}
