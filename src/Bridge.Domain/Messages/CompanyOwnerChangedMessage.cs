namespace Bridge.Domain.Messages;

/// <summary>
/// Zpráva o přeřazení obchodníka k firmě.
/// Topic: ff.company.owner-changed
/// </summary>
public sealed class CompanyOwnerChangedMessage
{
    public required string MessageId { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public required Guid FfCompanyId { get; init; }
    public required Guid NewOwnerUserId { get; init; }
    public Guid? PreviousOwnerUserId { get; init; }
}
