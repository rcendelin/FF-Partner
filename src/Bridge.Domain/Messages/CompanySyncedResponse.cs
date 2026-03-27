namespace Bridge.Domain.Messages;

/// <summary>
/// Odpověď Bridge po úspěšné synchronizaci firmy.
/// Topic: bridge.company.synced
/// </summary>
public sealed class CompanySyncedResponse
{
    public required string MessageId { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public required Guid FfCompanyId { get; init; }
    public required int PartnerClientId { get; init; }
    public required string PartnerRegion { get; init; }
    public required string Action { get; init; }
}
