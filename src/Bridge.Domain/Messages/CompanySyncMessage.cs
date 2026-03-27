namespace Bridge.Domain.Messages;

/// <summary>
/// Zpráva z FieldForce pro synchronizaci firmy do Partner3.
/// Topic: ff.company.sync
/// </summary>
public sealed class CompanySyncMessage
{
    public required string MessageId { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public required string Action { get; init; } // "Create" nebo "Update"
    public required Guid CompanyId { get; init; }
    public required string CompanyName { get; init; }
    public string? Ico { get; init; }
    public string? Dic { get; init; }
    public required string CountryCode { get; init; }
    public string? Street { get; init; }
    public string? City { get; init; }
    public string? PostalCode { get; init; }
    public string? State { get; init; }
    public string? County { get; init; }
    public string? PrimaryContactEmail { get; init; }
    public string? PrimaryContactPhone { get; init; }
    public required string CompanyRole { get; init; }
    public Guid? AssignedUserId { get; init; }
    public long? PipedriveId { get; init; }
}
