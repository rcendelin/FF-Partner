namespace Bridge.Api.Telemetry;

/// <summary>
/// Rozhraní pro sledování provozních metrik Bridge.
/// Implementace: BridgeMetrics (Application Insights), NullBridgeMetrics (DEV bez App Insights).
///
/// Custom metriky dle CLAUDE.md sekce 14:
/// - bridge.sync.duration  — latence zpracování zprávy (ms)
/// - bridge.sync.errors    — počet sync chyb
/// (bridge.dlq.depth je sledováno separátně v DlqMonitorService)
///
/// Alerting v Application Insights:
/// - sync error rate &gt; 5 % za 15 min → alert
/// - P95 bridge.sync.duration &gt; SLA threshold → alert
/// </summary>
public interface IBridgeMetrics
{
    /// <summary>
    /// Zaznamenává úspěšné zpracování zprávy s dobou trvání.
    /// </summary>
    void TrackSyncSuccess(string operation, string region, TimeSpan duration);

    /// <summary>
    /// Zaznamenává chybu při zpracování zprávy.
    /// </summary>
    void TrackSyncError(string operation, string region, string errorCode);
}

/// <summary>
/// No-op implementace pro DEV prostředí bez Application Insights.
/// </summary>
public sealed class NullBridgeMetrics : IBridgeMetrics
{
    public void TrackSyncSuccess(string operation, string region, TimeSpan duration) { }
    public void TrackSyncError(string operation, string region, string errorCode) { }
}
