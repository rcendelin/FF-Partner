using MySqlConnector;

namespace Bridge.Infrastructure.Partner;

/// <summary>
/// Factory pro vytváření MySQL připojení k 4 regionálním Partner3 DB.
/// Connection strings se načítají z Docker Secrets nebo konfigurace.
/// </summary>
public interface IPartnerDbConnectionFactory
{
    MySqlConnection CreateConnection(string region);
}

public sealed class PartnerDbConnectionFactory : IPartnerDbConnectionFactory
{
    private readonly IReadOnlyDictionary<string, string> _connectionStrings;

    public PartnerDbConnectionFactory(IReadOnlyDictionary<string, string> connectionStrings)
    {
        _connectionStrings = connectionStrings;
    }

    public MySqlConnection CreateConnection(string region)
    {
        var normalizedRegion = region.ToLowerInvariant();

        if (!_connectionStrings.TryGetValue(normalizedRegion, out var connectionString))
            throw new InvalidOperationException(
                $"Connection string pro region '{normalizedRegion}' není nakonfigurován.");

        // MySqlConnection nikdy neloguje connection string
        return new MySqlConnection(connectionString);
    }
}
