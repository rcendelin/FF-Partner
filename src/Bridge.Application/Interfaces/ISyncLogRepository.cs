namespace Bridge.Application.Interfaces;

public sealed class SyncLogEntry
{
    public Guid? FfCompanyId { get; init; }
    public int? PartnerClientId { get; init; }
    public string? PartnerRegion { get; init; }
    public required string Operation { get; init; }
    public string? ServiceBusMessageId { get; init; }
    public required string Status { get; init; }
    public string? ErrorMessage { get; init; }
    public string? PayloadJson { get; init; }
    public string Severity { get; init; } = "Info";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public interface ISyncLogRepository
{
    Task WriteAsync(SyncLogEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<SyncLogEntry>> GetLastAsync(int count, CancellationToken ct = default);
}
