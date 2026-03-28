using Bridge.Infrastructure.Mapping;

namespace Bridge.Api.Endpoints;

/// <summary>
/// REST endpoint pro diagnostiku ID mappingu.
/// Vyžaduje API key (ApiKeyMiddleware).
/// </summary>
public static class MappingEndpoints
{
    public static IEndpointRouteBuilder MapMappingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/mapping/{ffCompanyId:guid}", async (
            Guid ffCompanyId,
            IBridgeMappingRepository mappingRepo,
            CancellationToken ct) =>
        {
            var mapping = await mappingRepo.GetMappingAsync(ffCompanyId, ct);

            if (mapping is null)
                return Results.NotFound(new { error = "Mapping nenalezen.", ffCompanyId });

            return Results.Ok(new
            {
                ffCompanyId = mapping.FfCompanyId,
                partnerClientId = mapping.PartnerClientId,
                partnerRegion = mapping.PartnerRegion,
                entityType = mapping.EntityType,
                lastSyncAt = mapping.LastSyncAt,
                lastSyncDirection = mapping.LastSyncDirection,
                createdAt = mapping.CreatedAt
            });
        })
        .WithName("GetMapping")
        .WithTags("Diagnostics")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
