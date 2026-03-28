using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bridge.Api.Consumers;

/// <summary>
/// Periodicky kontroluje hloubku dead-letter queue pro všechny Bridge subscriptions.
/// Loguje Warning při DLQ depth > 0 — Application Insights alerting zachytí tento log.
///
/// Alert konfigurace (Azure Monitor / Application Insights):
///   - Query: traces | where severityLevel == 2 (Warning) and message contains "DLQ alert"
///   - Threshold: 1 výskyt za 15 minut → upozornění
///   - Action: email / Teams notifikace
///
/// CLAUDE.md: "dead-letter queue depth > 0 → alert"
/// </summary>
public sealed class DlqMonitorService : BackgroundService
{
    // Kontrola každých 5 minut — dostatečně rychlé pro alert, ne příliš agresivní
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    // Sledované subscriptions — rozšíř při přidání nových consumerů
    private static readonly (string Topic, string Subscription)[] MonitoredTopics =
    [
        ("ff.company.sync",          "bridge-main"),
        ("ff.company.disabled",      "bridge-main"),
        ("ff.contact.updated",       "bridge-main"),
        ("ff.company.owner-changed", "bridge-main"),
    ];

    private readonly ServiceBusAdministrationClient _adminClient;
    private readonly ILogger<DlqMonitorService> _logger;

    public DlqMonitorService(
        ServiceBusAdministrationClient adminClient,
        ILogger<DlqMonitorService> logger)
    {
        _adminClient = adminClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Počáteční zpoždění — nechte ostatní HostedService nastartovat
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAllDlqsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nekritická chyba — monitorovací smyčka pokračuje
                _logger.LogError(ex, "Kritická chyba v DlqMonitorService.CheckAllDlqsAsync — smyčka pokračuje");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CheckAllDlqsAsync(CancellationToken ct)
    {
        foreach (var (topic, subscription) in MonitoredTopics)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                var props = await _adminClient.GetSubscriptionRuntimePropertiesAsync(
                    topic, subscription, ct);

                var dlqCount = props.Value.DeadLetterMessageCount;
                if (dlqCount > 0)
                {
                    // Structured log → Application Insights alert query:
                    // traces | where message contains "DLQ alert"
                    _logger.LogWarning(
                        "DLQ alert: {Topic}/{Subscription} má {DlqCount} zpráv v dead-letter queue. " +
                        "Zkontrolujte příčinu selhání a zpracujte nebo znovu odešlete zprávy.",
                        topic, subscription, dlqCount);
                }
                else
                {
                    _logger.LogDebug(
                        "DLQ check OK: {Topic}/{Subscription} — žádné zprávy v DLQ",
                        topic, subscription);
                }
            }
            catch (Exception ex)
            {
                // Chyba při kontrole DLQ nesmí zastavit monitoring ostatních topics
                _logger.LogError(ex,
                    "Chyba při kontrole DLQ pro {Topic}/{Subscription}",
                    topic, subscription);
            }
        }
    }
}
