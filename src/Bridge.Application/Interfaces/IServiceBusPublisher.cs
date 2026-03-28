namespace Bridge.Application.Interfaces;

/// <summary>
/// Abstrakce pro publikování zpráv do Azure Service Bus topics.
/// Implementace v Bridge.Infrastructure.ServiceBus.ServiceBusPublisher.
/// </summary>
public interface IServiceBusPublisher
{
    Task PublishAsync<T>(
        string topicName,
        T message,
        string? correlationId = null,
        CancellationToken ct = default) where T : class;
}
