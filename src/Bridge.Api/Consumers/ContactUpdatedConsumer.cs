using Azure.Messaging.ServiceBus;
using Bridge.Api.Telemetry;
using Bridge.Application.Interfaces;
using Bridge.Domain.Messages;
using Bridge.Domain.Models;
using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace Bridge.Api.Consumers;

/// <summary>
/// Service Bus consumer pro topic ff.contact.updated.
/// Aktualizuje email a telefon primárního kontaktu v tbl_client.
///
/// Terminální chyby (bez retry):
/// - NO_MAPPING: firma není v bridge_id_mapping → sync-failed + complete
/// - CLIENT_NOT_FOUND: tbl_client záznam nenalezen → sync-failed + complete
/// Přechodné chyby → retry 1s/5s/30s → abandon → Service Bus retry (max 5 → DLQ).
/// </summary>
public sealed class ContactUpdatedConsumer : BackgroundService
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
    private readonly IBridgeMetrics _metrics;
    private readonly ILogger<ContactUpdatedConsumer> _logger;
    private readonly string _topicName;
    private readonly string _subscriptionName;

    public ContactUpdatedConsumer(
        ServiceBusClient serviceBusClient,
        IServiceScopeFactory scopeFactory,
        IServiceBusPublisher publisher,
        IBridgeMappingRepository mappingRepo,
        IPartnerSyncLog syncLog,
        IBridgeMetrics metrics,
        IConfiguration configuration,
        ILogger<ContactUpdatedConsumer> logger)
    {
        _serviceBusClient = serviceBusClient;
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _mappingRepo = mappingRepo;
        _syncLog = syncLog;
        _metrics = metrics;
        _topicName = configuration["ServiceBus:ContactUpdatedTopic"] ?? "ff.contact.updated";
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
        using var _ = CorrelationContext.Push(args.Message.MessageId);

        var sw = Stopwatch.StartNew();
        var ct = args.CancellationToken;
        ContactUpdatedMessage? message = null;

        try
        {
            message = JsonSerializer.Deserialize<ContactUpdatedMessage>(
                args.Message.Body.ToString(), JsonOptions);

            if (message is null)
            {
                _logger.LogError(
                    "Deserializace ContactUpdatedMessage selhala (SB MessageId: {MessageId})",
                    args.Message.MessageId);
                _metrics.TrackSyncError("contact_update", "unknown", "DESERIALIZATION_ERROR");
                await args.DeadLetterMessageAsync(args.Message,
                    deadLetterReason: "DESERIALIZATION_ERROR",
                    cancellationToken: CancellationToken.None);
                return;
            }

            if (message.FfCompanyId == Guid.Empty)
            {
                _logger.LogError(
                    "ContactUpdatedMessage obsahuje Guid.Empty jako FfCompanyId (SB MessageId: {MessageId}) — dead-letter",
                    args.Message.MessageId);
                _metrics.TrackSyncError("contact_update", "unknown", "INVALID_MESSAGE");
                await args.DeadLetterMessageAsync(args.Message,
                    deadLetterReason: "INVALID_MESSAGE",
                    cancellationToken: CancellationToken.None);
                return;
            }

            await ProcessContactUpdateAsync(message, args.Message.MessageId, ct);

            await args.CompleteMessageAsync(args.Message, CancellationToken.None);

            sw.Stop();
            _metrics.TrackSyncSuccess("contact_update", "unknown", sw.Elapsed);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "Malformovaný JSON v ContactUpdatedMessage {MessageId} — dead-letter",
                args.Message.MessageId);
            _metrics.TrackSyncError("contact_update", "unknown", "MALFORMED_JSON");
            await args.DeadLetterMessageAsync(args.Message,
                deadLetterReason: "MALFORMED_JSON",
                cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Přechodná chyba při zpracování ContactUpdatedMessage {MessageId} — abandon pro retry",
                args.Message.MessageId);
            _metrics.TrackSyncError("contact_update", "unknown", "TRANSIENT_ERROR");
            await args.AbandonMessageAsync(args.Message, cancellationToken: CancellationToken.None);
        }
    }

    private async Task ProcessContactUpdateAsync(
        ContactUpdatedMessage message,
        string sbMessageId,
        CancellationToken ct)
    {
        await _syncLog.WriteAsync(message.FfCompanyId, message.MessageId,
            "BridgeReceived", "Inbound", "ContactUpdate", "InProgress", ct: ct);

        // 1. Lookup mapping — firma musí být v bridge_id_mapping
        var mapping = await _mappingRepo.GetMappingAsync(message.FfCompanyId, ct);
        if (mapping is null)
        {
            _logger.LogWarning(
                "CONTACT_UPDATE ignorován — žádný mapping pro CompanyId={CompanyId}. Firma nebyla dříve synced.",
                message.FfCompanyId);
            _metrics.TrackSyncError("contact_update", "unknown", "NO_MAPPING");
            await PublishFailedAsync(message, sbMessageId, "NO_MAPPING",
                $"Mapping pro CompanyId={message.FfCompanyId} neexistuje.");
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var partnerRepo = scope.ServiceProvider.GetRequiredService<IPartnerClientRepository>();

        // 2. Cílený UPDATE email + telefon — retry pro přechodné chyby
        try
        {
            await TransientRetryPolicy.ExecuteAsync(
                () => partnerRepo.UpdateContactAsync(
                    mapping.PartnerClientId, mapping.PartnerRegion,
                    message.Email, message.Phone, ct),
                ct);
        }
        catch (InvalidOperationException)
        {
            // Klient nenalezen v Partner DB — terminální, bez retry
            _logger.LogWarning(
                "CONTACT_UPDATE: tbl_client id={PartnerId} v regionu {Region} nenalezen.",
                mapping.PartnerClientId, mapping.PartnerRegion);
            _metrics.TrackSyncError("contact_update", mapping.PartnerRegion, "CLIENT_NOT_FOUND");
            await PublishFailedAsync(message, sbMessageId, "CLIENT_NOT_FOUND",
                $"Klient id={mapping.PartnerClientId} nenalezen v regionu {mapping.PartnerRegion}.");
            return;
        }

        await _syncLog.WriteAsync(message.FfCompanyId, message.MessageId,
            "BridgeProcessed", "Inbound", "ContactUpdate", "Success",
            partnerClientId: mapping.PartnerClientId, partnerRegion: mapping.PartnerRegion, ct: CancellationToken.None);

        // 3. Publish bridge.company.synced → FieldForce
        try
        {
            await _publisher.PublishAsync("bridge.company.synced", new CompanySyncedResponse
            {
                MessageId = Guid.NewGuid().ToString(),
                SentAt = DateTimeOffset.UtcNow,
                FfCompanyId = message.FfCompanyId,
                PartnerClientId = mapping.PartnerClientId,
                PartnerRegion = mapping.PartnerRegion,
                Action = "ContactUpdate",
                OriginalMessageId = message.MessageId
            }, sbMessageId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Nepodařilo se publikovat bridge.company.synced (ContactUpdate) pro CompanyId={CompanyId}. " +
                "Kontakt v Partner3 aktualizován — data jsou konzistentní.",
                message.FfCompanyId);
        }

        // BridgeProcessed/Success výše (~řádek 207) už pokrývá audit — duplicate write smazán.

        _logger.LogInformation(
            "CONTACT_UPDATE: FF CompanyId={CompanyId} → Partner ClientId={PartnerId}, region={Region}",
            message.FfCompanyId, mapping.PartnerClientId, mapping.PartnerRegion);
    }

    private async Task PublishFailedAsync(
        ContactUpdatedMessage message,
        string sbMessageId,
        string errorCode,
        string errorMsg)
    {
        await _syncLog.WriteAsync(message.FfCompanyId, message.MessageId,
            "BridgeFailed", "Inbound", "ContactUpdate", "Failed",
            errorCode: errorCode, errorMessage: errorMsg);

        var failed = new CompanySyncFailedMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            SentAt = DateTimeOffset.UtcNow,
            FfCompanyId = message.FfCompanyId,
            ErrorCode = errorCode,
            ErrorMessage = errorMsg,
            OriginalMessageId = message.MessageId
        };

        try
        {
            await _publisher.PublishAsync(
                "bridge.company.sync-failed", failed, sbMessageId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Nepodařilo se publikovat sync-failed (contact_update) pro CompanyId={CompanyId}",
                message.FfCompanyId);
        }

        // BridgeFailed/Failed výše už pokrývá audit — duplicate write smazán.
    }

    private Task HandleErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception,
            "Service Bus procesor chyba — source: {ErrorSource}, entity: {EntityPath}",
            args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }
}
