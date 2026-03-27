namespace Bridge.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Bez autentizace — Docker healthcheck
        app.MapGet("/health", () => Results.Ok(new
        {
            status = "healthy",
            service = "FF-Partner Bridge",
            timestamp = DateTimeOffset.UtcNow
        }))
        .WithName("Health")
        .WithTags("Health")
        .AllowAnonymous();

        return app;
    }
}
