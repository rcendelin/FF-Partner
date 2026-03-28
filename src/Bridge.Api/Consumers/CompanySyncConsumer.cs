using Azure.Messaging.ServiceBus;
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
    private readonly ISyncLogRepository _syncLog;
    private readonly IOwnerMappingService _ownerMapping;
    private readonly ILogger<CompanySyncConsumer> _logger;
    private readonly string _topicName;
    private readonly string _subscriptionName;

    public CompanySyncConsumer(
        ServiceBusClient serviceBusClient,
        IServiceScopeFactory scopeFactory,
        IServiceBusPublisher publisher,
        IBridgeMappingRepository mappingRepo,
        ISyncLogRepository syncLog,
        IOwnerMappingService ownerMapping,
        IConfiguration configuration,
        ILogger<CompanySyncConsumer> logger)
    {
        _serviceBusClient = serviceBusClient;
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _mappingRepo = mappingRepo;
        _syncLog = syncLog;
        _ownerMapping = ownerMapping;
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

            if (!string.Equals(message.Action, "Create", StringComparison.OrdinalIgnoreCase))
            {
                // Action=Update bude implementováno v F1-06; zprávu complete aby se nereplikovaly
                _logger.LogDebug(
                    "Přeskakuji Action={Action} pro CompanyId={CompanyId} (F1-06)",
                    message.Action, message.CompanyId);
                await args.CompleteMessageAsync(args.Message, CancellationToken.None);
                return;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            await ProcessCreateAsync(scope.ServiceProvider, message, args.Message.MessageId, ct);
            // CancellationToken.None — settlement musí proběhnout i při shutdown
            await args.CompleteMessageAsync(args.Message, CancellationToken.None);
        }
        catch (JsonException ex)
        {
            // Malformovaný JSON je trvalá chyba — retry nemá smysl, rovnou dead-letter
            _logger.LogError(ex,
                "Malformovaný JSON v CompanySyncMessage {MessageId} — dead-letter",
                args.Message.MessageId);
            await args.DeadLetterMessageAsync(args.Message,
                deadLetterReason: "MALFORMED_JSON",
                cancellationToken: CancellationToken.None);
        }
        catch (UnsupportedRegionException ex)
        {
            _logger.LogWarning(
                "Nepodporovaný region pro CompanyId={CompanyId}: {Msg}",
                message?.CompanyId, ex.Message);
            await PublishSyncFailedAsync(
                message, args.Message.MessageId, "UNSUPPORTED_REGION", ex.Message, ct);
            await args.CompleteMessageAsync(args.Message, CancellationToken.None);
        }
        catch (GeoValidationException ex)
        {
            _logger.LogWarning(
                "GeoValidation selhal pro CompanyId={CompanyId}: {Msg}",
                message?.CompanyId, ex.Message);
            await PublishSyncFailedAsync(
                message, args.Message.MessageId,
                ex.ErrorCode.ToString().ToUpperInvariant(), ex.Message, ct);
            await args.CompleteMessageAsync(args.Message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Transientní chyba při zpracování zprávy {MessageId} — abandon pro retry",
                args.Message.MessageId);
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

        // 7. Publikovat bridge.company.synced → FieldForce
        var response = new CompanySyncedResponse
        {
            MessageId = Guid.NewGuid().ToString(),
            SentAt = DateTimeOffset.UtcNow,
            FfCompanyId = message.CompanyId,
            PartnerClientId = partnerId,
            PartnerRegion = region,
            Action = "Create"
        };
        await _publisher.PublishAsync("bridge.company.synced", response, sbMessageId, ct);

        // 8. Log úspěchu
        await _syncLog.WriteAsync(new SyncLogEntry
        {
            FfCompanyId = message.CompanyId,
            PartnerClientId = partnerId,
            PartnerRegion = region,
            Operation = "create",
            ServiceBusMessageId = sbMessageId,
            Status = "success",
            Severity = "Info"
        }, ct);

        _logger.LogInformation(
            "CREATE: FF CompanyId={CompanyId} → Partner ClientId={PartnerId}, region={Region}",
            message.CompanyId, partnerId, region);
    }

    private async Task PublishSyncFailedAsync(
        CompanySyncMessage? message,
        string sbMessageId,
        string errorCode,
        string errorMsg,
        CancellationToken ct)
    {
        var failed = new CompanySyncFailedMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            SentAt = DateTimeOffset.UtcNow,
            FfCompanyId = message?.CompanyId ?? Guid.Empty,
            ErrorCode = errorCode,
            ErrorMessage = errorMsg,
            OriginalMessageId = sbMessageId
        };

        try
        {
            await _publisher.PublishAsync("bridge.company.sync-failed", failed, sbMessageId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Nepodařilo se publikovat sync-failed pro CompanyId={CompanyId}",
                message?.CompanyId);
        }

        // CancellationToken.None — sync log musí být zapsán i při shutdown (terminální operace)
        await _syncLog.WriteAsync(new SyncLogEntry
        {
            FfCompanyId = message?.CompanyId,
            Operation = "create",
            ServiceBusMessageId = sbMessageId,
            Status = "failed",
            ErrorMessage = $"{errorCode}: {errorMsg}",
            Severity = "Error"
        }, CancellationToken.None);
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
