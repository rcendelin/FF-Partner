namespace Bridge.Domain.Messages;

/// <summary>
/// Zpráva o deaktivaci firmy.
/// Topic: ff.company.disabled
/// </summary>
public sealed class CompanyDisabledMessage
{
    public required string MessageId { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public required Guid FfCompanyId { get; init; }
}
