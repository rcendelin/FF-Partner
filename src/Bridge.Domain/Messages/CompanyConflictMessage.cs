namespace Bridge.Domain.Messages;

/// <summary>
/// Zpráva o detekovaném konfliktu — zápis byl přeskočen.
/// Topic: bridge.company.conflict
/// </summary>
public sealed class CompanyConflictMessage
{
    public required string MessageId { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public required Guid FfCompanyId { get; init; }
    public required int PartnerClientId { get; init; }
    public required string PartnerRegion { get; init; }
    public required DateTimeOffset ExistingLastSyncAt { get; init; }
    public required DateTimeOffset IncomingMessageSentAt { get; init; }
    public string? OriginalMessageId { get; init; }
}
