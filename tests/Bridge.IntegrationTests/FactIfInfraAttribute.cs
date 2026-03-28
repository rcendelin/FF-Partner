namespace Bridge.IntegrationTests;

/// <summary>
/// Spustí test pouze pokud jsou k dispozici Partner3 MySQL connection strings (CZ + PL).
/// Pokud env proměnné chybí, test se přeskočí s informativní zprávou.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FactIfPartnerDbAttribute : FactAttribute
{
    public FactIfPartnerDbAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BRIDGE_IT_PARTNER_CZ_CONN")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BRIDGE_IT_PARTNER_PL_CONN")))
        {
            Skip = "Přeskočeno: BRIDGE_IT_PARTNER_CZ_CONN nebo BRIDGE_IT_PARTNER_PL_CONN není nastavena. " +
                   "Integrační testy vyžadují živou Partner3 MySQL infrastrukturu.";
        }
    }
}

/// <summary>
/// Spustí test pouze pokud jsou k dispozici Azure SQL connection strings.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FactIfAzureSqlAttribute : FactAttribute
{
    public FactIfAzureSqlAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BRIDGE_IT_AZURE_SQL_CONN")))
        {
            Skip = "Přeskočeno: BRIDGE_IT_AZURE_SQL_CONN není nastavena. " +
                   "Integrační testy vyžadují živou Azure SQL infrastrukturu.";
        }
    }
}

/// <summary>
/// Spustí test pouze pokud jsou k dispozici VŠECHNY connection strings (Partner CZ+PL, Azure SQL, GAIA).
/// Používá se pro end-to-end testy zahrnující geo-validaci.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FactIfAllInfraAttribute : FactAttribute
{
    public FactIfAllInfraAttribute()
    {
        var missing = new List<string>();

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BRIDGE_IT_PARTNER_CZ_CONN")))
            missing.Add("BRIDGE_IT_PARTNER_CZ_CONN");
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BRIDGE_IT_PARTNER_PL_CONN")))
            missing.Add("BRIDGE_IT_PARTNER_PL_CONN");
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BRIDGE_IT_AZURE_SQL_CONN")))
            missing.Add("BRIDGE_IT_AZURE_SQL_CONN");
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BRIDGE_IT_GAIA_CONN")))
            missing.Add("BRIDGE_IT_GAIA_CONN");

        if (missing.Count > 0)
        {
            Skip = $"Přeskočeno: chybí env proměnné: {string.Join(", ", missing)}. " +
                   "End-to-end testy vyžadují plnou infrastrukturu.";
        }
    }
}
