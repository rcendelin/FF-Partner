using Azure.Messaging.ServiceBus;
using Bridge.Application.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Bridge.Infrastructure.ServiceBus;

/// <summary>
/// Publikuje zprávy do Azure Service Bus topics.
/// ServiceBusClient je sdílený singleton — tento publisher ho nevlastní (nevolá Dispose).
/// </summary>
public sealed class ServiceBusPublisher : IServiceBusPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ServiceBusClient _client;
    private readonly ILogger<ServiceBusPublisher> _logger;

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

        await using var sender = _client.CreateSender(topicName);
        await sender.SendMessageAsync(sbMessage, ct);

        _logger.LogDebug(
            "Publikována zpráva {MessageType} do topic {Topic}", typeof(T).Name, topicName);
    }
}
