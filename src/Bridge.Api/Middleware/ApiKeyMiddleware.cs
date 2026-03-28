using System.Security.Cryptography;
using System.Text;

namespace Bridge.Api.Middleware;

/// <summary>
/// Middleware pro ověření API klíče na všech /api/* endpointech.
/// Klíč je předáván v HTTP hlavičce X-Api-Key.
///
/// /health endpoint je exempt — nevyžaduje autentizaci (Docker healthcheck).
///
/// Bezpečnostní vlastnosti:
/// - Constant-time porovnání klíče via CryptographicOperations.FixedTimeEquals — odolné vůči timing attacks.
///   Framework-provided, FIPS-auditovaná implementace (preferovaná nad vlastním XOR kódem).
/// - API klíč se nikdy neloguje ani neinspektuje nad rámec porovnání
/// - 401 bez WWW-Authenticate hlavičky (neodhaluje auth mechanismus třetím stranám)
/// - IP adresa se loguje pro detekci brute-force pokusů
///
/// Zdroj klíče: Docker Secret 'bridge_admin_api_key' (načten při startu v Program.cs).
/// Prázdný klíč v produkci → Bridge odmítne start (viz Program.cs).
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
        // Pre-encode očekávaný klíč jednou při startu — vyhýbáme se alokaci per request
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

        // CryptographicOperations.FixedTimeEquals: frameworková constant-time implementace.
        // Délky musí být shodné — různá délka okamžitě vrátí false, ale stále v constant time.
        // Výhoda oproti vlastní XOR implementaci: garantovaná constant-time i po JIT optimalizacích.
        var isValid = providedKeyBytes.Length == _expectedKeyBytes.Length
            && CryptographicOperations.FixedTimeEquals(providedKeyBytes, _expectedKeyBytes);

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
}
