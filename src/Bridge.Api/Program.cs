using Azure.Messaging.ServiceBus.Administration;
using Bridge.Api.Consumers;
using Bridge.Api.Endpoints;
using Bridge.Api.Sagas;
using Bridge.Api.SecretReaders;
using Bridge.Application.Services;
using Bridge.Infrastructure;
using Serilog;
using Serilog.Events;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    Log.Information("Spouštím FF-Partner Bridge");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"));

    // Application Insights — pouze pokud je connection string nakonfigurován
    var appInsightsConnection = builder.Configuration["ApplicationInsights:ConnectionString"];
    if (!string.IsNullOrWhiteSpace(appInsightsConnection))
    {
        builder.Services.AddApplicationInsightsTelemetry();
    }

    // Swagger pouze v DEV prostředí
    if (builder.Environment.IsDevelopment())
    {
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
    }

    // Infrastructure + Service Bus — pouze pokud jsou dostupné connection strings
    // (v testech nejsou → služby se nezaregistrují, /health endpoint funguje i bez nich)
    var azureSqlConn = DockerSecretsReader.TryRead(
        "azure_sql_conn", builder.Configuration, "Bridge:AzureSql");
    var gaiaConn = DockerSecretsReader.TryRead(
        "gaia_conn", builder.Configuration, "Bridge:Gaia");
    var serviceBusConn = DockerSecretsReader.TryRead(
        "servicebus_conn", builder.Configuration, "Bridge:ServiceBus:ConnectionString");

    if (!string.IsNullOrEmpty(azureSqlConn)
        && !string.IsNullOrEmpty(gaiaConn)
        && !string.IsNullOrEmpty(serviceBusConn))
    {
        var partnerConnStrings = DockerSecretsReader.ReadPartnerConnectionStrings(builder.Configuration);
        var ownerMappingOptions = builder.Configuration
            .GetSection(OwnerMappingOptions.SectionName)
            .Get<OwnerMappingOptions>() ?? new OwnerMappingOptions();

        builder.Services.AddInfrastructure(
            azureSqlConnectionString: azureSqlConn,
            gaiaConnectionString: gaiaConn,
            partnerConnectionStrings: partnerConnStrings,
            serviceBusConnectionString: serviceBusConn,
            ownerMappingOptions: ownerMappingOptions);

        builder.Services.AddHostedService<CompanySyncConsumer>();
        builder.Services.AddHostedService<CompanyDisabledConsumer>();

        // DLQ monitor — kontroluje dead-letter queue každých 5 minut, loguje Warning při depth > 0
        builder.Services.AddSingleton(new ServiceBusAdministrationClient(serviceBusConn));
        builder.Services.AddHostedService<DlqMonitorService>();

        // Saga — transient (nové instance per použití)
        builder.Services.AddTransient<MoveClientToRegionSaga>();

        // SagaRecoveryService — spustí se jednou při startu, detekuje nedokončené ságy
        builder.Services.AddHostedService<SagaRecoveryService>();

        Log.Information("Infrastructure, Service Bus konzumenti a Saga zaregistrovány");
    }
    else
    {
        Log.Warning(
            "Chybí connection strings — Infrastructure a Service Bus konzumenti NEBUDOU zaregistrováni. " +
            "Pouze /health endpoint je dostupný.");
    }

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseSerilogRequestLogging();

    app.MapHealthEndpoints();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException && ex.GetType().Name != "StopTheHostException")
{
    Log.Fatal(ex, "Bridge se ukončil s neočekávanou chybou");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

// Nutné pro WebApplicationFactory v integračních testech
public partial class Program { }
