using Bridge.Application.Interfaces;

namespace Bridge.Api.Endpoints;

/// <summary>
/// REST endpoint pro diagnostiku sync logu.
/// Vyžaduje API key (ApiKeyMiddleware).
/// </summary>
public static class SyncLogEndpoints
{
    private const int DefaultCount = 50;
    private const int MaxCount = 100; // Omezení DoS při velkém error_message/payload_json

    public static IEndpointRouteBuilder MapSyncLogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/sync-log", async (
            ISyncLogRepository syncLog,
            int? last,
            CancellationToken ct) =>
        {
            var count = last is null or <= 0 or > MaxCount ? DefaultCount : last.Value;

            var entries = await syncLog.GetLastAsync(count, ct);

            return Results.Ok(new
            {
                count = entries.Count,
                requested = count,
                entries = entries.Select(e => new
                {
                    e.FfCompanyId,
                    e.PartnerClientId,
                    e.PartnerRegion,
                    e.Operation,
                    e.ServiceBusMessageId,
                    e.Status,
                    e.Severity,
                    // ErrorMessage truncováno — může obsahovat interní DB detaily (tabulky, SQL)
                    ErrorMessage = e.ErrorMessage is null ? null
                        : e.ErrorMessage.Length <= 200 ? e.ErrorMessage
                        : e.ErrorMessage[..200] + "...[truncated]",
                    e.CreatedAt
                })
            });
        })
        .WithName("GetSyncLog")
        .WithTags("Diagnostics")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }
}
