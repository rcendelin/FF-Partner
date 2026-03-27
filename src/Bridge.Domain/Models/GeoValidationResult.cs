namespace Bridge.Domain.Models;

public sealed class GeoValidationResult
{
    public required int CountryId { get; init; }
    public required string CountryShort { get; init; }
    public int? ZipId { get; init; }
    public string? City { get; init; }
    public int? StateId { get; init; }
    public string? State { get; init; }
    public int? CountyId { get; init; }
    public string? County { get; init; }
}
