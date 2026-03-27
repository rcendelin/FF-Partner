namespace Bridge.Domain.Messages;

/// <summary>
/// Zpráva o selhání synchronizace firmy.
/// Topic: bridge.company.sync-failed
/// </summary>
public sealed class CompanySyncFailedMessage
{
    public required string MessageId { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public required Guid FfCompanyId { get; init; }
    public required string ErrorCode { get; init; }
    public required string ErrorMessage { get; init; }
    public string? OriginalMessageId { get; init; }
}
