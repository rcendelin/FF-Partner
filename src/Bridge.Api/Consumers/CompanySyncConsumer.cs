using Azure.Messaging.ServiceBus;
using Bridge.Api.Sagas;
using Bridge.Api.Telemetry;
using Bridge.Application.Interfaces;
using Bridge.Application.Services;
using Bridge.Domain.Enums;
using Bridge.Domain.Exceptions;
using Bridge.Domain.Messages;
using Bridge.Domain.Models;
using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner;
using Bridge.Infrastructure.Partner.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace Bridge.Api.Consumers;

/// <summary>
/// Service Bus consumer pro topic ff.company.sync.
/// Zpracovává Action = "Create" — vloží firmu do správné regionální Partner3 DB.
/// Action = "Update" bude implementován v F1-06.
///
/// Idempotentní: duplicitní CREATE (existující mapping) se přeskočí bez chyby.
/// Terminální chyby (neznámá země, nepodporovaný region) → publish sync-failed + complete.
/// Transientní chyby (DB nedostupná) → abandon → Service Bus retry.
/// </summary>
public sealed class CompanySyncConsumer : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ServiceBusClient _serviceBusClient;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceBusPublisher _publisher;
    private readonly IBridgeMappingRepository _mappingRepo;
    private readonly IPartnerSyncLog _syncLog;
    private readonly IOwnerMappingService _ownerMapping;
    private readonly IBridgeMetrics _metrics;
    private readonly ILogger<CompanySyncConsumer> _logger;
    private readonly string _topicName;
    private readonly string _subscriptionName;

    public CompanySyncConsumer(
        ServiceBusClient serviceBusClient,
        IServiceScopeFactory scopeFactory,
        IServiceBusPublisher publisher,
        IBridgeMappingRepository mappingRepo,
        IPartnerSyncLog syncLog,
        IOwnerMappingService ownerMapping,
        IBridgeMetrics metrics,
        IConfiguration configuration,
        ILogger<CompanySyncConsumer> logger)
    {
        _serviceBusClient = serviceBusClient;
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _mappingRepo = mappingRepo;
        _syncLog = syncLog;
        _ownerMapping = ownerMapping;
        _metrics = metrics;
        _topicName = configuration["ServiceBus:CompanySyncTopic"] ?? "ff.company.sync";
        _subscriptionName = configuration["ServiceBus:SubscriptionName"] ?? "bridge-main";
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processorOptions = new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = 1,
            AutoCompleteMessages = false,
            ReceiveMode = ServiceBusReceiveMode.PeekLock
        };

        await using var processor = _serviceBusClient.CreateProcessor(
            _topicName, _subscriptionName, processorOptions);

        processor.ProcessMessageAsync += HandleMessageAsync;
        processor.ProcessErrorAsync += HandleErrorAsync;

        await processor.StartProcessingAsync(stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normální ukončení při shutdown
        }
        finally
        {
            await processor.StopProcessingAsync();
        }
    }

    private async Task HandleMessageAsync(ProcessMessageEventArgs args)
    {
        // CorrelationId propaguje MessageId do všech structured log zápisů v rámci zpracování
        using var _ = CorrelationContext.Push(args.Message.MessageId);

        var sw = Stopwatch.StartNew();
        var ct = args.CancellationToken;
        CompanySyncMessage? message = null;

        try
        {
            message = JsonSerializer.Deserialize<CompanySyncMessage>(
                args.Message.Body.ToString(), JsonOptions);

            if (message is null)
            {
                _logger.LogError(
                    "Deserializace CompanySyncMessage selhala (SB MessageId: {MessageId})",
                    args.Message.MessageId);
                // Terminální chyba — CancellationToken.None zajistí provedení i při shutdown
                await args.DeadLetterMessageAsync(args.Message,
                    deadLetterReason: "DESERIALIZATION_ERROR",
                    cancellationToken: CancellationToken.None);
                return;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();

            if (string.Equals(message.Action, "Create", StringComparison.OrdinalIgnoreCase))
            {
                await ProcessCreateAsync(scope.ServiceProvider, message, args.Message.MessageId, ct);
            }
            else if (string.Equals(message.Action, "Update", StringComparison.OrdinalIgnoreCase))
            {
                await ProcessUpdateAsync(scope.ServiceProvider, message, args.Message.MessageId, ct);
            }
            else
            {
                _logger.LogWarning(
                    "Neznámá action '{Action}' pro CompanyId={CompanyId} — zpráva se přeskočí",
                    message.Action, message.CompanyId);
            }

            // CancellationToken.None — settlement musí proběhnout i při shutdown
            await args.CompleteMessageAsync(args.Message, CancellationToken.None);

            sw.Stop();
            _metrics.TrackSyncSuccess(
                message.Action.ToLowerInvariant(),
                message.CountryCode ?? "unknown",
                sw.Elapsed);
        }
        catch (JsonException ex)
        {
            // Malformovaný JSON je trvalá chyba — retry nemá smysl, rovnou dead-letter
            _logger.LogError(ex,
                "Malformovaný JSON v CompanySyncMessage {MessageId} — dead-letter",
                args.Message.MessageId);
            _metrics.TrackSyncError("deserialize", "unknown", "MALFORMED_JSON");
            await args.DeadLetterMessageAsync(args.Message,
                deadLetterReason: "MALFORMED_JSON",
                cancellationToken: CancellationToken.None);
        }
        catch (UnsupportedRegionException ex)
        {
            _logger.LogWarning(
                "Nepodporovaný region pro CompanyId={CompanyId}: {Msg}",
                message?.CompanyId, ex.Message);
            var action = message?.Action?.ToLowerInvariant() ?? "unknown";
            _metrics.TrackSyncError(action, message?.CountryCode ?? "unknown", "UNSUPPORTED_REGION");
            await PublishSyncFailedAsync(
                message, args.Message.MessageId, "UNSUPPORTED_REGION", ex.Message, action, ct);
            await args.CompleteMessageAsync(args.Message, CancellationToken.None);
        }
        catch (GeoValidationException ex)
        {
            _logger.LogWarning(
                "GeoValidation selhal pro CompanyId={CompanyId}: {Msg}",
                message?.CompanyId, ex.Message);
            var action = message?.Action?.ToLowerInvariant() ?? "unknown";
            var errCode = ex.ErrorCode.ToString().ToUpperInvariant();
            _metrics.TrackSyncError(action, message?.CountryCode ?? "unknown", errCode);
            await PublishSyncFailedAsync(
                message, args.Message.MessageId, errCode, ex.Message, action, ct);
            await args.CompleteMessageAsync(args.Message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Transientní chyba při zpracování zprávy {MessageId} — abandon pro retry",
                args.Message.MessageId);
            _metrics.TrackSyncError(
                message?.Action?.ToLowerInvariant() ?? "unknown",
                message?.CountryCode ?? "unknown",
                "TRANSIENT_ERROR");
            // CancellationToken.None — abandon musí proběhnout i při shutdown
            await args.AbandonMessageAsync(args.Message, cancellationToken: CancellationToken.None);
        }
    }

    private async Task ProcessCreateAsync(
        IServiceProvider sp,
        CompanySyncMessage message,
        string sbMessageId,
        CancellationToken ct)
    {
        await _syncLog.WriteAsync(message.CompanyId, message.MessageId,
            "BridgeReceived", "Inbound", "Create", "InProgress", ct: ct);

        // Idempotence: duplicitní CREATE přeskočit (mapping již existuje)
        var existingMapping = await _mappingRepo.GetMappingAsync(message.CompanyId, ct);
        if (existingMapping is not null)
        {
            _logger.LogWarning(
                "Duplicitní CREATE ignorován — CompanyId={CompanyId} již v regionu {Region}",
                message.CompanyId, existingMapping.PartnerRegion);
            return;
        }

        var geoService = sp.GetRequiredService<IGeoValidationService>();
        var partnerRepo = sp.GetRequiredService<IPartnerClientRepository>();

        // 1. Routing do správného regionu dle CountryCode
        var region = RegionRouter.ResolveRegion(message.CountryCode);

        // 2. Geo validace — neznámá země → výjimka (terminální); neznámé PSČ → null, sync pokračuje
        var address = new AddressDto
        {
            CountryCode = message.CountryCode,
            PostalCode = message.PostalCode,
            City = message.City,
            State = message.State,
            County = message.County
        };
        var geo = await geoService.ValidateAsync(address, ct);

        // 3. Mapování CompanyRole na client_right (0=Customer, 1=OEM, 2=Dealer)
        var clientRight = Enum.TryParse<CompanyRole>(message.CompanyRole, ignoreCase: true, out var role)
            ? MapCompanyRole(role)
            : 0;  // fallback = Customer

        // 4. Mapování FF User.Id na Partner3 id_owner
        var ownerId = _ownerMapping.ResolveOwnerId(message.AssignedUserId);

        // 5. INSERT do regionální Partner3 DB
        // Ochrana proti partial failure: pokud tbl_client záznam existuje (předchozí pokus selhal
        // po INSERT ale před SaveMappingAsync), použít existující idclient — neprovádět duplicitní INSERT.
        var orphaned = await partnerRepo.GetByFfCompanyIdAsync(message.CompanyId, region, ct);

        var now = DateTime.UtcNow;
        var partnerClient = new PartnerClient
        {
            ClientFirm = message.CompanyName,
            ClientIc = message.Ico,
            ClientDic = message.Dic,
            ClientStreet = message.Street,
            ClientCity = geo.City,
            ClientPsc = message.PostalCode,
            ClientCountryId = geo.CountryId,
            ClientCountryShort = geo.CountryShort,
            ClientState = geo.State,
            ClientStateId = geo.StateId,
            ClientCounty = geo.County,
            ClientCountyId = geo.CountyId,
            ClientZipId = geo.ZipId,           // může být null — neblokuje sync
            ClientPhone = message.PrimaryContactPhone,
            ClientMail = message.PrimaryContactEmail,
            ClientRight = clientRight,
            ClientDate = now,
            ClientDisable = 0,
            IdOwner = ownerId,
            FfCompanyId = message.CompanyId,
            FfSyncSource = "FF",
            DataOwner = DataOwner.FieldForce,
            LastFfSyncAt = now
        };

        int partnerId;
        if (orphaned is not null)
        {
            _logger.LogWarning(
                "Obnova po partial failure: tbl_client s ff_company_id={CompanyId} nalezen v regionu {Region} " +
                "bez mappingu (PartnerId={PartnerId}). Přeskakuji INSERT.",
                message.CompanyId, region, orphaned.IdClient);
            partnerId = orphaned.IdClient;
        }
        else
        {
            partnerId = await partnerRepo.InsertAsync(partnerClient, region, ct);
        }

        // 6. Uložit ID mapping do Azure SQL
        var mapping = new IdMappingRecord
        {
            FfCompanyId = message.CompanyId,
            PartnerClientId = partnerId,
            PartnerRegion = region,
            EntityType = "client",
            PipedriveId = message.PipedriveId,
            FfUserId = message.AssignedUserId,
            PartnerOwnerId = ownerId,
            LastSyncAt = now,
            LastSyncDirection = "ff_to_partner",
            CreatedAt = now,
            UpdatedAt = now
        };
        await _mappingRepo.SaveMappingAsync(mapping, ct);

        await _syncLog.WriteAsync(message.CompanyId, message.MessageId,
            "BridgeProcessed", "Inbound", "Create", "Success",
            partnerClientId: partnerId, partnerRegion: region, ct: ct);

        // 7. Publikovat bridge.company.synced → FieldForce
        var response = new CompanySyncedResponse
        {
            MessageId = Guid.NewGuid().ToString(),
            SentAt = DateTimeOffset.UtcNow,
            FfCompanyId = message.CompanyId,
            PartnerClientId = partnerId,
            PartnerRegion = region,
            Action = "Create",
            OriginalMessageId = message.MessageId
        };
        await _publisher.PublishAsync("bridge.company.synced", response, sbMessageId, ct);

        // BridgeProcessed/Success na řádku ~314 už pokrývá audit — duplicate write smazán.

        _logger.LogInformation(
            "CREATE: FF CompanyId={CompanyId} → Partner ClientId={PartnerId}, region={Region}",
            message.CompanyId, partnerId, region);
    }

    private async Task ProcessUpdateAsync(
        IServiceProvider sp,
        CompanySyncMessage message,
        string sbMessageId,
        CancellationToken ct)
    {
        await _syncLog.WriteAsync(message.CompanyId, message.MessageId,
            "BridgeReceived", "Inbound", "Update", "InProgress", ct: ct);

        // 1. Lookup mapping — firma musí být v bridge_id_mapping
        var mapping = await _mappingRepo.GetMappingAsync(message.CompanyId, ct);
        if (mapping is null)
        {
            _logger.LogWarning(
                "UPDATE ignorován — žádný mapping pro CompanyId={CompanyId}. Firma nebyla dříve synced.",
                message.CompanyId);
            await PublishSyncFailedAsync(message, sbMessageId, "NO_MAPPING",
                $"Mapping pro CompanyId={message.CompanyId} neexistuje.", "update", ct);
            return;
        }

        var region = mapping.PartnerRegion;
        var partnerRepo = sp.GetRequiredService<IPartnerClientRepository>();
        var geoService = sp.GetRequiredService<IGeoValidationService>();

        // 2. Načíst existující záznam z Partner DB
        var existingClient = await partnerRepo.GetByPartnerIdAsync(mapping.PartnerClientId, region, ct);
        if (existingClient is null)
        {
            _logger.LogWarning(
                "UPDATE ignorován — tbl_client id={PartnerId} v regionu {Region} neexistuje (stale mapping).",
                mapping.PartnerClientId, region);
            await PublishSyncFailedAsync(message, sbMessageId, "ORPHANED_MAPPING",
                $"tbl_client id={mapping.PartnerClientId} v regionu {region} nenalezen.", "update", ct);
            return;
        }

        // 3. Conflict detection dle CLAUDE.md sekce 10
        // Stale message guard: pokud Bridge zapsal DO Partner DB VÍCE NEŽ 5 MINUT po odeslání zprávy,
        // zpráva dorazila out-of-order (novější sync ji předběhl) → přeskočit.
        // Tolerance 5 min kryje clock skew. Podmínka: lastFfSyncAt > sentAt + 5min
        if (existingClient.LastFfSyncAt.HasValue &&
            existingClient.LastFfSyncAt.Value > message.SentAt.UtcDateTime.AddMinutes(5))
        {
            _logger.LogWarning(
                "Conflict pro CompanyId={CompanyId} (PartnerId={PartnerId}) — záznam v Partner DB " +
                "je novější než zpráva + tolerance 5 min (last_ff_sync_at={Existing}, SentAt={SentAt}). " +
                "Zpráva je stale — zápis přeskočen.",
                message.CompanyId, mapping.PartnerClientId,
                existingClient.LastFfSyncAt, message.SentAt);

            try
            {
                await _publisher.PublishAsync("bridge.company.conflict", new CompanyConflictMessage
                {
                    MessageId = Guid.NewGuid().ToString(),
                    SentAt = DateTimeOffset.UtcNow,
                    FfCompanyId = message.CompanyId,
                    PartnerClientId = mapping.PartnerClientId,
                    PartnerRegion = region,
                    ExistingLastSyncAt = new DateTimeOffset(existingClient.LastFfSyncAt.Value, TimeSpan.Zero),
                    IncomingMessageSentAt = message.SentAt,
                    OriginalMessageId = message.MessageId
                }, sbMessageId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Nepodařilo se publikovat bridge.company.conflict pro CompanyId={CompanyId}",
                    message.CompanyId);
            }

            // CancellationToken.None — log musí být zapsán i při shutdown
            await _syncLog.WriteAsync(
                companyId: message.CompanyId,
                correlationMessageId: message.MessageId,
                phase: "BridgeProcessed",
                direction: "Inbound",
                operation: "Update",
                status: "Conflict",
                partnerClientId: mapping.PartnerClientId,
                partnerRegion: region,
                errorCode: "STALE_MESSAGE",
                errorMessage: "Stale message — novější sync detekován, zápis přeskočen.",
                ct: CancellationToken.None);

            return;
        }

        // 4. Geo validace — neznámá země → výjimka; neznámé PSČ → null, sync pokračuje
        var address = new AddressDto
        {
            CountryCode = message.CountryCode,
            PostalCode = message.PostalCode,
            City = message.City,
            State = message.State,
            County = message.County
        };
        var geo = await geoService.ValidateAsync(address, ct);

        // 5. Mapování role + owner (před region check — saga potřebuje aktuální hodnoty)
        var clientRight = Enum.TryParse<CompanyRole>(message.CompanyRole, ignoreCase: true, out var role)
            ? MapCompanyRole(role)
            : existingClient.ClientRight;

        var ownerId = _ownerMapping.ResolveOwnerId(message.AssignedUserId)
            ?? existingClient.IdOwner;

        // 6. Připrav nový stav klienta v paměti (platí pro UPDATE i pro Saga INSERT do cílového regionu)
        var now = DateTime.UtcNow;

        // Uložit původní geo hodnoty PŘED mutací — použijí se při fallback UPDATE (viz CRITICAL-1):
        // geo FK hodnoty (CountryId, StateId, ZipId) jsou specifické pro region GAIA DB;
        // při fallback UPDATE v původním regionu nesmíme zapsat cizí FK z jiného regionu.
        var originalGeo = (
            City: existingClient.ClientCity,
            CountryId: existingClient.ClientCountryId,
            CountryShort: existingClient.ClientCountryShort,
            State: existingClient.ClientState,
            StateId: existingClient.ClientStateId,
            County: existingClient.ClientCounty,
            CountyId: existingClient.ClientCountyId,
            ZipId: existingClient.ClientZipId,
            Psc: existingClient.ClientPsc
        );

        existingClient.ClientFirm = message.CompanyName;
        existingClient.ClientIc = message.Ico;
        existingClient.ClientDic = message.Dic;
        existingClient.ClientStreet = message.Street;
        existingClient.ClientCity = geo.City;
        existingClient.ClientPsc = message.PostalCode;
        existingClient.ClientCountryId = geo.CountryId;
        existingClient.ClientCountryShort = geo.CountryShort;
        existingClient.ClientState = geo.State;
        existingClient.ClientStateId = geo.StateId;
        existingClient.ClientCounty = geo.County;
        existingClient.ClientCountyId = geo.CountyId;
        existingClient.ClientZipId = geo.ZipId;
        existingClient.ClientPhone = message.PrimaryContactPhone;
        existingClient.ClientMail = message.PrimaryContactEmail;
        existingClient.ClientRight = clientRight;
        existingClient.IdOwner = ownerId;
        existingClient.FfSyncSource = "FF";
        existingClient.DataOwner = DataOwner.FieldForce;
        existingClient.LastFfSyncAt = now;

        // 7. Detekce změny regionu — spustit ságu místo prostého UPDATE
        var newRegion = RegionRouter.ResolveRegion(message.CountryCode);
        if (newRegion != region)
        {
            _logger.LogWarning(
                "Firma CompanyId={CompanyId} změnila zemi na {CountryCode} (nový region: {NewRegion}, " +
                "aktuální: {CurrentRegion}). Spouštím MoveClientToRegionSaga.",
                message.CompanyId, message.CountryCode, newRegion, region);

            var saga = sp.GetRequiredService<MoveClientToRegionSaga>();
            var sagaResult = await saga.ExecuteAsync(
                existingClient, region, newRegion, mapping, sbMessageId, ct);

            if (sagaResult.IsSuccess)
            {
                // Saga úspěšně přesunula klienta — konec zpracování
                return;
            }

            if (sagaResult.HasNoSideEffects)
            {
                // INSERT do cílového regionu selhal — klient zůstal beze změn.
                // Fallback UPDATE v původním regionu: MUSÍME obnovit původní geo FK hodnoty.
                // Geo validace proběhla pro novou zemi (cílový region) — ty FK hodnoty NESMÍME
                // zapsat do původního regionu (data corruption: PL country_id v CZ DB).
                existingClient.ClientCity = originalGeo.City;
                existingClient.ClientPsc = originalGeo.Psc;
                existingClient.ClientCountryId = originalGeo.CountryId;
                existingClient.ClientCountryShort = originalGeo.CountryShort;
                existingClient.ClientState = originalGeo.State;
                existingClient.ClientStateId = originalGeo.StateId;
                existingClient.ClientCounty = originalGeo.County;
                existingClient.ClientCountyId = originalGeo.CountyId;
                existingClient.ClientZipId = originalGeo.ZipId;

                _logger.LogWarning(
                    "Saga step 1 selhal pro CompanyId={CompanyId} — fallback UPDATE v {Region} " +
                    "s původními geo hodnotami (adresa nebyla aktualizována).",
                    message.CompanyId, region);
                // Fall-through na normální UPDATE níže
            }
            else
            {
                // Saga kompenzovala — žádné trvalé změny, publish sync-failed
                await PublishSyncFailedAsync(message, sbMessageId, "REGION_CHANGE_FAILED",
                    $"Saga kompenzována ({sagaResult.Outcome}): {sagaResult.ErrorMessage}", "update", ct);
                return;
            }
        }

        // 8. UPDATE v Partner DB (normální cesta nebo fallback po Saga Krok 1 selhání)
        await partnerRepo.UpdateAsync(existingClient, region, ct);

        // 9. Aktualizovat mapping (last_sync_at, owner)
        // CRITICAL: UpdateMappingAsync selhání nesmí způsobit abandon (UpdateAsync již proběhl).
        // Partner DB je zdrojem pravdy — mapping je pouze pomocný index. Logujeme warning, sync pokračuje.
        var updatedMapping = new IdMappingRecord
        {
            FfCompanyId = mapping.FfCompanyId,
            PartnerClientId = mapping.PartnerClientId,
            PartnerRegion = region,
            EntityType = mapping.EntityType,
            PipedriveId = mapping.PipedriveId,
            FfUserId = message.AssignedUserId,
            PartnerOwnerId = ownerId,
            LastSyncAt = now,
            LastSyncDirection = "ff_to_partner",
            CreatedAt = mapping.CreatedAt,
            UpdatedAt = now
        };
        try
        {
            await _mappingRepo.UpdateMappingAsync(updatedMapping, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Nepodařilo se aktualizovat bridge_id_mapping pro CompanyId={CompanyId} " +
                "(PartnerId={PartnerId}). tbl_client byl aktualizován — mapping je stale. " +
                "Bude opraven při příštím sync.",
                message.CompanyId, mapping.PartnerClientId);

            await _syncLog.WriteAsync(
                companyId: message.CompanyId,
                correlationMessageId: message.MessageId,
                phase: "BridgeProcessed",
                direction: "Inbound",
                operation: "Update",
                status: "Warning",
                partnerClientId: mapping.PartnerClientId,
                partnerRegion: region,
                errorCode: "MAPPING_STALE",
                errorMessage: $"UpdateMappingAsync selhal: {ex.Message}. tbl_client aktualizován, mapping stale.",
                ct: CancellationToken.None);
            // Pokračovat — zpráva bude completed, ne abandoned
        }

        await _syncLog.WriteAsync(message.CompanyId, message.MessageId,
            "BridgeProcessed", "Inbound", "Update", "Success",
            partnerClientId: mapping.PartnerClientId, partnerRegion: region, ct: ct);

        // 10. Publish bridge.company.synced
        await _publisher.PublishAsync("bridge.company.synced", new CompanySyncedResponse
        {
            MessageId = Guid.NewGuid().ToString(),
            SentAt = DateTimeOffset.UtcNow,
            FfCompanyId = message.CompanyId,
            PartnerClientId = mapping.PartnerClientId,
            PartnerRegion = region,
            Action = "Update",
            OriginalMessageId = message.MessageId
        }, sbMessageId, ct);

        // BridgeProcessed/Success na řádku ~590 už pokrývá audit — duplicate write smazán.

        _logger.LogInformation(
            "UPDATE: FF CompanyId={CompanyId} → Partner ClientId={PartnerId}, region={Region}",
            message.CompanyId, mapping.PartnerClientId, region);
    }

    private async Task PublishSyncFailedAsync(
        CompanySyncMessage? message,
        string sbMessageId,
        string errorCode,
        string errorMsg,
        string operation,
        CancellationToken ct)
    {
        if (message is not null)
        {
            // CancellationToken.None — terminální audit musí proběhnout i při shutdown
            // (konzistentní s publish na řádku ~635)
            await _syncLog.WriteAsync(message.CompanyId, message.MessageId,
                "BridgeFailed", "Inbound", operation, "Failed",
                errorCode: errorCode, errorMessage: errorMsg,
                ct: CancellationToken.None);
        }

        var failed = new CompanySyncFailedMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            SentAt = DateTimeOffset.UtcNow,
            FfCompanyId = message?.CompanyId ?? Guid.Empty,
            ErrorCode = errorCode,
            ErrorMessage = errorMsg,
            OriginalMessageId = message?.MessageId
        };

        try
        {
            // CancellationToken.None — terminální publish musí proběhnout i při shutdown
            await _publisher.PublishAsync("bridge.company.sync-failed", failed, sbMessageId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Nepodařilo se publikovat sync-failed pro CompanyId={CompanyId}",
                message?.CompanyId);
        }

        // BridgeFailed/Failed záznam výše pokrývá audit — duplicate write smazán.
    }

    private Task HandleErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception,
            "Service Bus procesor chyba — source: {ErrorSource}, entity: {EntityPath}",
            args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }

    // Dle CLAUDE.md sekce 8
    private static int MapCompanyRole(CompanyRole role) => role switch
    {
        CompanyRole.Customer => 0,
        CompanyRole.Dealer   => 2,
        CompanyRole.Oem      => 1,
        _                    => 0
    };
}
