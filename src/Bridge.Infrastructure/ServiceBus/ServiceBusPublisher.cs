using Azure.Messaging.ServiceBus;
using Bridge.Application.Interfaces;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Bridge.Infrastructure.ServiceBus;

/// <summary>
/// Publikuje zprávy do Azure Service Bus topics.
///
/// Design poznámky:
/// - ServiceBusClient je sdílený singleton — publisher ho nevlastní (nevolá Dispose)
/// - ServiceBusSender objekty jsou thread-safe a určené k opakovanému použití
///   → cachujeme je per-topic v ConcurrentDictionary (lazy init, GetOrAdd)
/// - Přidání nového Bridge topicu (bridge.*) nevyžaduje změnu tohoto kódu
/// - CorrelationId = SB MessageId původní zprávy → trasovatelnost odpovědí
/// </summary>
public sealed class ServiceBusPublisher : IServiceBusPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ServiceBusClient _client;
    private readonly ILogger<ServiceBusPublisher> _logger;

    // Cache senderů per topic — ServiceBusSender je thread-safe a navržen pro reuse.
    // Vytvoření senderu per volání způsobovalo zbytečné TCP handshaky pod zátěží.
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new(StringComparer.Ordinal);

    public ServiceBusPublisher(ServiceBusClient client, ILogger<ServiceBusPublisher> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task PublishAsync<T>(
        string topicName,
        T message,
        string? correlationId = null,
        CancellationToken ct = default) where T : class
    {
        var json = JsonSerializer.Serialize(message, JsonOptions);
        var sbMessage = new ServiceBusMessage(json)
        {
            ContentType = "application/json",
            MessageId = Guid.NewGuid().ToString()
        };

        if (correlationId is not null)
            sbMessage.CorrelationId = correlationId;

        // GetOrAdd je atomické — při souběžném prvním přístupu na stejný topic
        // může dojít k vytvoření více senderů, ale jen jeden bude uložen (ostatní zahozeny).
        // ServiceBusSender nemá Dispose nároky při zahazování nepoužitých instancí.
        var sender = _senders.GetOrAdd(topicName, _client.CreateSender);

        await sender.SendMessageAsync(sbMessage, ct);

        _logger.LogDebug(
            "Publikována zpráva {MessageType} do topic {Topic}", typeof(T).Name, topicName);
    }
}
