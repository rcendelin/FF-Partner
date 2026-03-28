using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner;
using Bridge.Infrastructure.Partner.Repositories;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Bridge.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registruje všechny Infrastructure služby.
    /// Connection strings se předávají z Docker Secrets (čteny mimo DI).
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string azureSqlConnectionString,
        IReadOnlyDictionary<string, string> partnerConnectionStrings)
    {
        // In-memory cache pro mapping lookup
        services.AddMemoryCache();

        // Azure SQL — mapping + sync log
        services.AddSingleton<IBridgeMappingRepository>(sp =>
        {
            var cache = sp.GetRequiredService<IMemoryCache>();
            return new BridgeMappingRepository(azureSqlConnectionString, cache);
        });

        services.AddSingleton<IBridgeSyncLogRepository>(
            _ => new BridgeSyncLogRepository(azureSqlConnectionString));

        // MySQL — Partner3 regionální DB
        services.AddSingleton<IPartnerDbConnectionFactory>(
            _ => new PartnerDbConnectionFactory(partnerConnectionStrings));

        services.AddScoped<IPartnerClientRepository, PartnerClientRepository>();

        return services;
    }
}
