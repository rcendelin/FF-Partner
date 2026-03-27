namespace Bridge.Domain.Models;

public sealed class IdMappingRecord
{
    public required Guid FfCompanyId { get; init; }
    public required int PartnerClientId { get; init; }
    public required string PartnerRegion { get; init; }
    public required string EntityType { get; init; }
    public long? PipedriveId { get; init; }
    public Guid? FfUserId { get; init; }
    public int? PartnerOwnerId { get; init; }
    public required DateTime LastSyncAt { get; init; }
    public required string LastSyncDirection { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
}
