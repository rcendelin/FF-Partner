using MySqlConnector;

namespace Bridge.Infrastructure.Gaia;

public interface IGaiaDbConnectionFactory
{
    MySqlConnection CreateConnection();
}

public sealed class GaiaDbConnectionFactory : IGaiaDbConnectionFactory
{
    private readonly string _connectionString;

    public GaiaDbConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    public MySqlConnection CreateConnection() => new(_connectionString);
}
