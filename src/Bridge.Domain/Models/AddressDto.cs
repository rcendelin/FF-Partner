namespace Bridge.Domain.Models;

public sealed class AddressDto
{
    public required string CountryCode { get; init; }
    public string? PostalCode { get; init; }
    public string? City { get; init; }
    public string? Street { get; init; }
    public string? State { get; init; }
    public string? County { get; init; }
}
