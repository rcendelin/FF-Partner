using System.Text;

namespace Bridge.Api.Middleware;

/// <summary>
/// Middleware pro ověření API klíče na všech /api/* endpointech.
/// Klíč je předáván v HTTP hlavičce X-Api-Key.
///
/// /health endpoint je exempt — nevyžaduje autentizaci (Docker healthcheck).
///
/// Bezpečnostní vlastnosti:
/// - Constant-time porovnání klíče (CryptographicOperations.FixedTimeEquals) — odolné vůči timing attacks
/// - API klíč se nikdy neloguje
/// - 401 bez WWW-Authenticate hlavičky (neodhaluje auth mechanismus třetím stranám)
///
/// Zdroj klíče: Docker Secret 'bridge_admin_api_key' (načten při startu v Program.cs).
/// </summary>
public sealed class ApiKeyMiddleware
{
    private const string ApiKeyHeaderName = "X-Api-Key";

    private readonly RequestDelegate _next;
    private readonly byte[] _expectedKeyBytes;
    private readonly ILogger<ApiKeyMiddleware> _logger;

    public ApiKeyMiddleware(RequestDelegate next, string expectedApiKey, ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        // Uložit jako bytes jednou — konstantní porovnání nevyžaduje re-encoding per request
        _expectedKeyBytes = Encoding.UTF8.GetBytes(expectedApiKey);
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // /health je exempt — Docker healthcheck neodesílá API key
        if (context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var providedKeyValues)
            || string.IsNullOrEmpty(providedKeyValues))
        {
            _logger.LogWarning(
                "Neautorizovaný přístup — chybí {Header} (path: {Path}, IP: {Ip})",
                ApiKeyHeaderName,
                context.Request.Path,
                context.Connection.RemoteIpAddress);

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        var providedKeyBytes = Encoding.UTF8.GetBytes(providedKeyValues.ToString());

        // Constant-time porovnání bez délkového oracle:
        // XOR-redukce přes celou délku obou bufferů (max délka) — útočník nemůže rozlišit
        // "špatná délka" od "špatný obsah" na základě doby odpovědi.
        var isValid = ConstantTimeEquals(providedKeyBytes, _expectedKeyBytes);

        if (!isValid)
        {
            _logger.LogWarning(
                "Neplatný API klíč (path: {Path}, IP: {Ip})",
                context.Request.Path,
                context.Connection.RemoteIpAddress);

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// Constant-time porovnání dvou byte polí.
    /// Nevyzrazuje délku ani obsah přes časový kanál (timing side-channel).
    /// XOR akumuluje rozdíly přes max délku obou polí — výsledek 0 = rovnost.
    /// </summary>
    private static bool ConstantTimeEquals(byte[] a, byte[] b)
    {
        var maxLen = Math.Max(a.Length, b.Length);
        var diff = a.Length ^ b.Length; // nenulové pokud délky se liší

        for (var i = 0; i < maxLen; i++)
        {
            var byteA = i < a.Length ? a[i] : (byte)0;
            var byteB = i < b.Length ? b[i] : (byte)0;
            diff |= byteA ^ byteB;
        }

        return diff == 0;
    }
}
