namespace Bridge.Api.Consumers;

/// <summary>
/// Jednoduchý in-process retry s exponential backoff pro přechodné chyby (výpadek DB, síť).
///
/// Použití: obalit konkrétní operace, u nichž je retry žádoucí (INSERT, UPDATE, DELETE).
/// NEPOUŽÍ­VAT pro terminální chyby (NO_MAPPING, UNSUPPORTED_REGION) — ty se musí řešit jinak.
///
/// Delays: 1s → 5s → 30s → výjimka (abandon zprávy na Service Bus → SB retry).
/// </summary>
internal static class TransientRetryPolicy
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
    ];

    /// <summary>
    /// Provede operaci s exponential backoff retry (max 3 pokusy).
    /// Po 3 neúspěšných pokusech vyhodí poslední výjimku.
    /// CancellationToken přeruší čekání mezi pokusy (např. při shutdown).
    /// </summary>
    public static async Task ExecuteAsync(Func<Task> operation, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await operation();
                return;
            }
            catch (Exception ex) when (attempt < Delays.Length
                                       && !ct.IsCancellationRequested
                                       && ex is not OperationCanceledException)
            {
                await Task.Delay(Delays[attempt], ct);
            }
        }
    }
}
