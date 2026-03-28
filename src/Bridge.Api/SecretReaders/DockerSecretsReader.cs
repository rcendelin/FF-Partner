namespace Bridge.Api.SecretReaders;

/// <summary>
/// Čte Docker Secrets z /run/secrets/ (Linux) nebo lokální konfigurace (DEV).
/// NIKDY neloguje hodnotu secretu — pouze klíč.
/// </summary>
public static class DockerSecretsReader
{
    private const string DockerSecretsPath = "/run/secrets";

    public static string Read(string secretName, IConfiguration configuration, string? configKey = null)
    {
        var value = TryRead(secretName, configuration, configKey);
        if (value is not null)
            return value;

        var key = configKey ?? secretName;
        throw new InvalidOperationException(
            $"Secret '{secretName}' nebyl nalezen v Docker Secrets ani v konfiguraci (klíč: '{key}').");
    }

    /// <summary>
    /// Jako Read, ale vrátí null místo výjimky pokud secret není nalezen.
    /// Vhodné pro volitelné secret (např. connection strings pro DEV/test prostředí).
    /// </summary>
    public static string? TryRead(string secretName, IConfiguration configuration, string? configKey = null)
    {
        // 1. Docker Secret (produkce)
        var secretFile = Path.Combine(DockerSecretsPath, secretName);
        if (File.Exists(secretFile))
            return File.ReadAllText(secretFile).Trim();

        // 2. Fallback na konfiguraci (DEV/testování)
        var key = configKey ?? secretName;
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static IReadOnlyDictionary<string, string> ReadPartnerConnectionStrings(
        IConfiguration configuration)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cz"] = Read("partner_cz_conn", configuration, "Bridge:Partner:Cz"),
            ["pl"] = Read("partner_pl_conn", configuration, "Bridge:Partner:Pl"),
            ["hu"] = Read("partner_hu_conn", configuration, "Bridge:Partner:Hu"),
            ["us"] = Read("partner_us_conn", configuration, "Bridge:Partner:Us"),
        };
    }
}
