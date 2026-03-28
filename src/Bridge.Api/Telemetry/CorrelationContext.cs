using Serilog.Context;

namespace Bridge.Api.Telemetry;

/// <summary>
/// Přidá Service Bus MessageId do Serilog LogContext pro všechny logy v rámci zpracování zprávy.
/// Použití: using var _ = CorrelationContext.Push(args.Message.MessageId);
///
/// Correlation ID je dostupné ve všech structured log zápisech (Console + Application Insights)
/// jako property "CorrelationId". Umožňuje filtrovat logy per zpráva v Application Insights.
/// </summary>
public static class CorrelationContext
{
    /// <summary>
    /// Přidá MessageId jako "CorrelationId" do LogContext.
    /// Vrácená hodnota IDisposable musí být uvolněna (using) — odstraní property ze scope.
    /// </summary>
    public static IDisposable Push(string messageId)
        => LogContext.PushProperty("CorrelationId", messageId);
}
