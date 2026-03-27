using Bridge.Api.Endpoints;
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
