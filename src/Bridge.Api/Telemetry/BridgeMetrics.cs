using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;

namespace Bridge.Api.Telemetry;

/// <summary>
/// Application Insights implementace IBridgeMetrics.
/// Zaznamenává custom metriky přes TelemetryClient.
///
/// Metriky jsou dostupné v Application Insights → Metrics Explorer:
/// - bridge.sync.duration [ms]: latence zpracování zprávy (dimensions: operation, region, status)
/// - bridge.sync.errors [count]: počet chyb (dimensions: operation, region, errorCode)
///
/// Alert konfigurace (Azure Monitor / Application Insights):
/// Sync error rate > 5% za 15 minut:
///   customMetrics | where name == "bridge.sync.errors"
///   | summarize errors = sum(value), total = sum(value) + prev_success_count by bin(timestamp, 15m)
///   | where (errors / (errors + total)) > 0.05
/// P95 latence > SLA threshold:
///   customMetrics | where name == "bridge.sync.duration"
///   | summarize percentile(value, 95) by bin(timestamp, 5m)
/// </summary>
public sealed class BridgeMetrics : IBridgeMetrics
{
    private readonly TelemetryClient _telemetry;

    public BridgeMetrics(TelemetryClient telemetryClient)
    {
        _telemetry = telemetryClient;
    }

    public void TrackSyncSuccess(string operation, string region, TimeSpan duration)
    {
        var metric = new MetricTelemetry("bridge.sync.duration", duration.TotalMilliseconds);
        metric.Properties["operation"] = operation;
        metric.Properties["region"] = region;
        metric.Properties["status"] = "success";
        _telemetry.TrackMetric(metric);
    }

    public void TrackSyncError(string operation, string region, string errorCode)
    {
        var metric = new MetricTelemetry("bridge.sync.errors", 1);
        metric.Properties["operation"] = operation;
        metric.Properties["region"] = region;
        metric.Properties["errorCode"] = errorCode;
        _telemetry.TrackMetric(metric);
    }
}
