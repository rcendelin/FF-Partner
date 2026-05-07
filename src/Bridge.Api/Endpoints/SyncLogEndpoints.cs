using Bridge.Application.Interfaces;

namespace Bridge.Api.Endpoints;

/// <summary>
/// REST endpoint pro diagnostiku PartnerSyncLog.
/// Vyžaduje API key (ApiKeyMiddleware).
/// </summary>
public static class SyncLogEndpoints
{
    private const int DefaultCount = 50;
    private const int MaxCount = 100; // Omezení DoS při velkém ErrorMessage/PayloadJson

    public static IEndpointRouteBuilder MapSyncLogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/sync-log", async (
            IPartnerSyncLog syncLog,
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
                    e.CompanyId,
                    e.CorrelationMessageId,
                    e.Phase,
                    e.Direction,
                    e.Operation,
                    e.Status,
                    e.PartnerClientId,
                    e.PartnerRegion,
                    e.ErrorCode,
                    // ErrorMessage truncováno — může obsahovat interní DB detaily (tabulky, SQL)
                    ErrorMessage = e.ErrorMessage is null ? null
                        : e.ErrorMessage.Length <= 200 ? e.ErrorMessage
                        : e.ErrorMessage[..200] + "...[truncated]",
                    e.Source,
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
