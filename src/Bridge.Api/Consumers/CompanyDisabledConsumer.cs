using Azure.Messaging.ServiceBus;
using Bridge.Api.Telemetry;
using Bridge.Application.Interfaces;
using Bridge.Domain.Messages;
using Bridge.Domain.Models;
using Bridge.Infrastructure.FieldForce;
using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace Bridge.Api.Consumers;

/// <summary>
/// Service Bus consumer pro topic ff.company.disabled.
/// Deaktivuje firmu v příslušné regionální Partner3 DB a pubslishes bridge.company.synced.
///
/// Idempotentní: opakované zpracování DisableAsync nemá vedlejší efekty
/// (client_disable = 1 nastaveno opakovaně → výsledek stejný, ale throw pokud klient nenalezen).
/// NO_MAPPING → publish sync-failed + complete (terminální chyba, bez retry).
/// CLIENT_NOT_FOUND → publish sync-failed + complete (terminální chyba, bez retry).
/// Přechodné DB chyby → retry 1s/5s/30s → abandon → Service Bus retry (max delivery count = 5 → DLQ).
/// </summary>
public sealed class CompanyDisabledConsumer : BackgroundService
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
    private readonly SyncLogWriter _syncLogWriter;
    private readonly IBridgeMetrics _metrics;
    private readonly ILogger<CompanyDisabledConsumer> _logger;
    private readonly string _topicName;
    private readonly string _subscriptionName;

    public CompanyDisabledConsumer(
        ServiceBusClient serviceBusClient,
        IServiceScopeFactory scopeFactory,
        IServiceBusPublisher publisher,
        IBridgeMappingRepository mappingRepo,
        ISyncLogRepository syncLog,
        SyncLogWriter syncLogWriter,
        IBridgeMetrics metrics,
        IConfiguration configuration,
        ILogger<CompanyDisabledConsumer> logger)
    {
        _serviceBusClient = serviceBusClient;
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _mappingRepo = mappingRepo;
        _syncLog = syncLog;
        _syncLogWriter = syncLogWriter;
        _metrics = metrics;
        _topicName = configuration["ServiceBus:CompanyDisabledTopic"] ?? "ff.company.disabled";
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
        CompanyDisabledMessage? message = null;

        try
        {
            message = JsonSerializer.Deserialize<CompanyDisabledMessage>(
                args.Message.Body.ToString(), JsonOptions);

            if (message is null)
            {
                _logger.LogError(
                    "Deserializace CompanyDisabledMessage selhala (SB MessageId: {MessageId})",
                    args.Message.MessageId);
                _metrics.TrackSyncError("disable", "unknown", "DESERIALIZATION_ERROR");
                await args.DeadLetterMessageAsync(args.Message,
                    deadLetterReason: "DESERIALIZATION_ERROR",
                    cancellationToken: CancellationToken.None);
                return;
            }

            if (message.FfCompanyId == Guid.Empty)
            {
                _logger.LogError(
                    "CompanyDisabledMessage obsahuje Guid.Empty jako FfCompanyId (SB MessageId: {MessageId}) — dead-letter",
                    args.Message.MessageId);
                _metrics.TrackSyncError("disable", "unknown", "INVALID_MESSAGE");
                await args.DeadLetterMessageAsync(args.Message,
                    deadLetterReason: "INVALID_MESSAGE",
                    cancellationToken: CancellationToken.None);
                return;
            }

            await ProcessDisableAsync(message, args.Message.MessageId, ct);

            // CancellationToken.None — settlement musí proběhnout i při shutdown
            await args.CompleteMessageAsync(args.Message, CancellationToken.None);

            sw.Stop();
            _metrics.TrackSyncSuccess("disable", "unknown", sw.Elapsed);
        }
        catch (JsonException ex)
        {
            // Malformovaný JSON je trvalá chyba — retry nemá smysl
            _logger.LogError(ex,
                "Malformovaný JSON v CompanyDisabledMessage {MessageId} — dead-letter",
                args.Message.MessageId);
            _metrics.TrackSyncError("disable", "unknown", "MALFORMED_JSON");
            await args.DeadLetterMessageAsync(args.Message,
                deadLetterReason: "MALFORMED_JSON",
                cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Přechodná chyba (DB nedostupná, síť) — abandon pro SB retry
            _logger.LogError(ex,
                "Přechodná chyba při zpracování CompanyDisabledMessage {MessageId} — abandon pro retry",
                args.Message.MessageId);
            _metrics.TrackSyncError("disable", "unknown", "TRANSIENT_ERROR");
            await args.AbandonMessageAsync(args.Message, cancellationToken: CancellationToken.None);
        }
    }

    private async Task ProcessDisableAsync(
        CompanyDisabledMessage message,
        string sbMessageId,
        CancellationToken ct)
    {
        await _syncLogWriter.WriteAsync(message.FfCompanyId, message.MessageId,
            "BridgeReceived", "Inbound", "Disable", "InProgress", ct: ct);

        // 1. Lookup mapping — firma musí být v bridge_id_mapping
        var mapping = await _mappingRepo.GetMappingAsync(message.FfCompanyId, ct);
        if (mapping is null)
        {
            _logger.LogWarning(
                "DISABLE ignorován — žádný mapping pro CompanyId={CompanyId}. Firma nebyla dříve synced.",
                message.FfCompanyId);
            _metrics.TrackSyncError("disable", "unknown", "NO_MAPPING");
            await PublishDisableFailedAsync(message, sbMessageId, "NO_MAPPING",
                $"Mapping pro CompanyId={message.FfCompanyId} neexistuje.");
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var partnerRepo = scope.ServiceProvider.GetRequiredService<IPartnerClientRepository>();

        // 2. Deaktivace v Partner3 DB — retry pro přechodné chyby
        // InvalidOperationException (klient nenalezen) se nezachycuje zde — propaguje se výše
        // jako přechodná chyba. Ale protože je to terminální stav, zpracujeme ji zvlášť.
        try
        {
            await TransientRetryPolicy.ExecuteAsync(
                () => partnerRepo.DisableAsync(mapping.PartnerClientId, mapping.PartnerRegion, ct),
                ct);
        }
        catch (InvalidOperationException)
        {
            // DisableAsync: klient nenalezen v Partner DB — terminální, bez retry
            // ex.Message záměrně NENÍ předáno na Service Bus ani do sync-failed zprávy —
            // mohlo by obsahovat interní schéma DB (tabulka, sloupec). Logujeme strukturovaně.
            _logger.LogWarning(
                "DISABLE: tbl_client id={PartnerId} v regionu {Region} nenalezen — " +
                "záznam mohl být odstraněn manuálně.",
                mapping.PartnerClientId, mapping.PartnerRegion);
            _metrics.TrackSyncError("disable", mapping.PartnerRegion, "CLIENT_NOT_FOUND");
            await PublishDisableFailedAsync(message, sbMessageId, "CLIENT_NOT_FOUND",
                $"Klient id={mapping.PartnerClientId} nenalezen v regionu {mapping.PartnerRegion}.");
            return;
        }

        await _syncLogWriter.WriteAsync(message.FfCompanyId, message.MessageId,
            "BridgeProcessed", "Inbound", "Disable", "Success",
            partnerClientId: mapping.PartnerClientId, partnerRegion: mapping.PartnerRegion, ct: CancellationToken.None);

        // 3. Publish bridge.company.synced → FieldForce
        // CancellationToken.None — terminální operace musí proběhnout i při shutdown
        try
        {
            await _publisher.PublishAsync("bridge.company.synced", new CompanySyncedResponse
            {
                MessageId = Guid.NewGuid().ToString(),
                SentAt = DateTimeOffset.UtcNow,
                FfCompanyId = message.FfCompanyId,
                PartnerClientId = mapping.PartnerClientId,
                PartnerRegion = mapping.PartnerRegion,
                Action = "Disable",
                OriginalMessageId = message.MessageId
            }, sbMessageId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Nepodařilo se publikovat bridge.company.synced (Disable) pro CompanyId={CompanyId}. " +
                "Deaktivace v Partner3 proběhla — data jsou konzistentní.",
                message.FfCompanyId);
        }

        // 4. Log úspěchu
        await _syncLog.WriteAsync(new SyncLogEntry
        {
            FfCompanyId = message.FfCompanyId,
            PartnerClientId = mapping.PartnerClientId,
            PartnerRegion = mapping.PartnerRegion,
            Operation = "disable",
            ServiceBusMessageId = sbMessageId,
            Status = "success",
            Severity = "Info"
        }, CancellationToken.None);

        _logger.LogInformation(
            "DISABLE: FF CompanyId={CompanyId} → Partner ClientId={PartnerId}, region={Region}",
            message.FfCompanyId, mapping.PartnerClientId, mapping.PartnerRegion);
    }

    private async Task PublishDisableFailedAsync(
        CompanyDisabledMessage message,
        string sbMessageId,
        string errorCode,
        string errorMsg)
    {
        await _syncLogWriter.WriteAsync(message.FfCompanyId, message.MessageId,
            "BridgeFailed", "Inbound", "Disable", "Failed",
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
            // CancellationToken.None — terminální publish musí proběhnout i při shutdown
            await _publisher.PublishAsync("bridge.company.sync-failed", failed, sbMessageId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Nepodařilo se publikovat sync-failed (disable) pro CompanyId={CompanyId}",
                message.FfCompanyId);
        }

        await _syncLog.WriteAsync(new SyncLogEntry
        {
            FfCompanyId = message.FfCompanyId,
            Operation = "disable",
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
}
