using Azure.Messaging.ServiceBus;
using Bridge.Application.Interfaces;
using Bridge.Application.Services;
using Bridge.Infrastructure.Gaia;
using Bridge.Infrastructure.Gaia.Repositories;
using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner;
using Bridge.Infrastructure.Partner.Repositories;
using Bridge.Infrastructure.Polling;
using Bridge.Infrastructure.ServiceBus;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Bridge.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registruje všechny Infrastructure a Application služby.
    /// Connection strings se předávají z Docker Secrets (čteny mimo DI).
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string azureSqlConnectionString,
        string gaiaConnectionString,
        IReadOnlyDictionary<string, string> partnerConnectionStrings,
        string serviceBusConnectionString,
        OwnerMappingOptions ownerMappingOptions)
    {
        // In-memory cache pro mapping lookup
        services.AddMemoryCache();

        // Azure SQL — mapping + sync log
        services.AddSingleton<IBridgeMappingRepository>(sp =>
        {
            var cache = sp.GetRequiredService<IMemoryCache>();
            return new BridgeMappingRepository(azureSqlConnectionString, cache);
        });

        services.AddSingleton<ISyncLogRepository>(
            _ => new BridgeSyncLogRepository(azureSqlConnectionString));

        // GAIA číselníky (read-only)
        services.AddSingleton<IGaiaDbConnectionFactory>(
            _ => new GaiaDbConnectionFactory(gaiaConnectionString));

        services.AddSingleton<IGaiaCountryRepository, GaiaCountryRepository>();
        services.AddSingleton<IGaiaZipRepository, GaiaZipRepository>();
        services.AddSingleton<IGaiaStateRepository, GaiaStateRepository>();
        services.AddSingleton<IGaiaCountyRepository, GaiaCountyRepository>();

        // MySQL — Partner3 regionální DB
        services.AddSingleton<IPartnerDbConnectionFactory>(
            _ => new PartnerDbConnectionFactory(partnerConnectionStrings));

        services.AddScoped<IPartnerClientRepository, PartnerClientRepository>();

        // Application services
        services.AddScoped<IGeoValidationService, GeoValidationService>();

        // Azure Service Bus
        services.AddSingleton(new ServiceBusClient(serviceBusConnectionString));
        services.AddSingleton<IServiceBusPublisher, ServiceBusPublisher>();

        // Owner mapping (FF User.Id → Partner3 id_owner)
        services.AddSingleton<IOwnerMappingService>(new OwnerMappingService(ownerMappingOptions));

        // Polling infrastruktura — watermark + snapshot (Azure SQL)
        services.AddSingleton<IPollWatermarkRepository>(
            _ => new PollWatermarkRepository(azureSqlConnectionString));
        services.AddSingleton<IOrderSnapshotRepository>(
            _ => new OrderSnapshotRepository(azureSqlConnectionString));

        return services;
    }
}
