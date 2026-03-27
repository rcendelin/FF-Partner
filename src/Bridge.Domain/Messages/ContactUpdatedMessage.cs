namespace Bridge.Domain.Messages;

/// <summary>
/// Zpráva o změně emailu/telefonu primárního kontaktu firmy.
/// Topic: ff.contact.updated
/// </summary>
public sealed class ContactUpdatedMessage
{
    public required string MessageId { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public required Guid FfCompanyId { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
}
