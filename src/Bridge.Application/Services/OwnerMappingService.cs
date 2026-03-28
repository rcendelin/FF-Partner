namespace Bridge.Application.Services;

/// <summary>
/// Konfigurace mappingu FieldForce User.Id → Partner3 id_owner.
/// Konfigurujte v appsettings.json pod klíčem "OwnerMapping".
/// </summary>
public sealed class OwnerMappingOptions
{
    public const string SectionName = "OwnerMapping";

    /// <summary>
    /// Klíč = FieldForce User.Id (Guid jako string), hodnota = Partner3 id_owner.
    /// </summary>
    public Dictionary<string, int> Mappings { get; init; } = new();

    /// <summary>
    /// Fallback owner ID pokud AssignedUserId není v mappingu nebo je null.
    /// Null = id_owner bude NULL v Partner3 DB.
    /// </summary>
    public int? DefaultOwnerId { get; init; }
}

public interface IOwnerMappingService
{
    /// <summary>
    /// Přeloží FieldForce User.Id na Partner3 id_owner.
    /// Pokud not found nebo null, vrátí DefaultOwnerId (může být null).
    /// </summary>
    int? ResolveOwnerId(Guid? ffUserId);
}

public sealed class OwnerMappingService : IOwnerMappingService
{
    private readonly IReadOnlyDictionary<Guid, int> _mappings;
    private readonly int? _defaultOwnerId;

    public OwnerMappingService(OwnerMappingOptions options)
    {
        _mappings = options.Mappings
            .Where(kv => Guid.TryParse(kv.Key, out _))
            .ToDictionary(
                kv => Guid.Parse(kv.Key),
                kv => kv.Value);
        _defaultOwnerId = options.DefaultOwnerId;
    }

    public int? ResolveOwnerId(Guid? ffUserId)
    {
        if (ffUserId is null)
            return _defaultOwnerId;

        return _mappings.TryGetValue(ffUserId.Value, out var ownerId)
            ? ownerId
            : _defaultOwnerId;
    }
}
