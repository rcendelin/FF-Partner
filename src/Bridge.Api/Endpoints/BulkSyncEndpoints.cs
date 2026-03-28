using Bridge.Application.Interfaces;
using Bridge.Domain.Messages;

namespace Bridge.Api.Endpoints;

/// <summary>
/// Admin endpoint pro jednorázový bulk sync — odesílá dávku CompanySyncMessage do Service Bus.
/// Vyžaduje API key (ApiKeyMiddleware).
///
/// Použití: jednorázová migrace existujících firem (FieldForce → Partner3).
/// Throttling: pauza 100 ms každých 10 zpráv = max ~100 zpráv/sec (ochrana SB).
/// Idempotence: CompanySyncConsumer ignoruje duplicitní CREATE (mapping check).
///
/// Acceptance kritérium dle CLAUDE.md: ≥ 95 % firem bez chyby.
/// </summary>
public static class BulkSyncEndpoints
{
    private const int MaxBatchSize = 500;
    private const int ThrottleBatchSize = 10;
    private static readonly TimeSpan ThrottleDelay = TimeSpan.FromMilliseconds(100);

    public static IEndpointRouteBuilder MapBulkSyncEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/bulk-sync", async (
            BulkSyncRequest request,
            IServiceBusPublisher publisher,
            ILogger<BulkSyncRequest> logger,
            CancellationToken ct) =>
        {
            // Validace action
            if (!string.Equals(request.Action, "Create", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(request.Action, "Update", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new
                {
                    error = "Neplatná action. Povolené hodnoty: 'Create', 'Update'.",
                    provided = request.Action
                });
            }

            // Validace velikosti dávky
            if (request.Companies is null || request.Companies.Count == 0)
                return Results.BadRequest(new { error = "Companies nesmí být prázdný seznam." });

            if (request.Companies.Count > MaxBatchSize)
            {
                return Results.BadRequest(new
                {
                    error = $"Příliš mnoho firem v jedné dávce. Maximum: {MaxBatchSize}.",
                    provided = request.Companies.Count
                });
            }

            var sent = 0;
            var failed = 0;
            var errors = new List<object>();
            var sentAt = DateTimeOffset.UtcNow;

            for (var i = 0; i < request.Companies.Count; i++)
            {
                if (ct.IsCancellationRequested)
                    break;

                var company = request.Companies[i];

                if (company.CompanyId == Guid.Empty)
                {
                    failed++;
                    errors.Add(new { companyId = company.CompanyId, error = "CompanyId nesmí být Guid.Empty." });
                    continue;
                }

                var message = new CompanySyncMessage
                {
                    MessageId = Guid.NewGuid().ToString(),
                    SentAt = sentAt,
                    CompanyId = company.CompanyId,
                    Action = request.Action,
                    // required string fields: fallback na empty string —
                    // consumer vyhodí terminální chybu (GeoValidation/sync-failed) pokud je prázdné
                    CompanyName = company.CompanyName ?? string.Empty,
                    CountryCode = company.CountryCode ?? string.Empty,
                    CompanyRole = company.CompanyRole ?? string.Empty,
                    Ico = company.Ico,
                    Dic = company.Dic,
                    Street = company.Street,
                    City = company.City,
                    PostalCode = company.PostalCode,
                    State = company.State,
                    County = company.County,
                    PrimaryContactEmail = company.PrimaryContactEmail,
                    PrimaryContactPhone = company.PrimaryContactPhone,
                    AssignedUserId = company.AssignedUserId,
                    PipedriveId = company.PipedriveId
                };

                try
                {
                    await publisher.PublishAsync("ff.company.sync", message, message.MessageId, ct);
                    sent++;
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add(new { companyId = company.CompanyId, error = ex.Message });
                    logger.LogError(ex,
                        "BulkSync: Nepodařilo se publikovat zprávu pro CompanyId={CompanyId}",
                        company.CompanyId);
                }

                // Throttling: pauza každých ThrottleBatchSize zpráv
                if ((i + 1) % ThrottleBatchSize == 0 && i < request.Companies.Count - 1)
                {
                    await Task.Delay(ThrottleDelay, ct);
                }
            }

            logger.LogInformation(
                "BulkSync dokončen: celkem={Total}, odesláno={Sent}, selhalo={Failed}, action={Action}",
                request.Companies.Count, sent, failed, request.Action);

            var statusCode = failed == 0 ? StatusCodes.Status200OK : StatusCodes.Status207MultiStatus;
            return Results.Json(new
            {
                total = request.Companies.Count,
                sent,
                failed,
                action = request.Action,
                errors = errors.Count > 0 ? errors : null
            }, statusCode: statusCode);
        })
        .WithName("BulkSync")
        .WithTags("Admin")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status207MultiStatus)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }
}

/// <summary>
/// Požadavek pro bulk sync — dávka CompanySyncMessage určená k odeslání do Service Bus.
/// </summary>
public sealed class BulkSyncRequest
{
    /// <summary>Action pro všechny zprávy v dávce: "Create" nebo "Update".</summary>
    public required string Action { get; init; }

    /// <summary>Seznam firem k synchronizaci. Maximum: 500 per request.</summary>
    public required List<BulkSyncItem> Companies { get; init; }
}

/// <summary>
/// Data jedné firmy pro bulk sync — mapuje na CompanySyncMessage.
/// </summary>
public sealed class BulkSyncItem
{
    public required Guid CompanyId { get; init; }
    public string? CompanyName { get; init; }
    public string? Ico { get; init; }
    public string? Dic { get; init; }
    public string? Street { get; init; }
    public string? City { get; init; }
    public string? PostalCode { get; init; }
    public string? CountryCode { get; init; }
    public string? State { get; init; }
    public string? County { get; init; }
    public string? PrimaryContactEmail { get; init; }
    public string? PrimaryContactPhone { get; init; }
    public string? CompanyRole { get; init; }
    public Guid? AssignedUserId { get; init; }
    public long? PipedriveId { get; init; }
}
