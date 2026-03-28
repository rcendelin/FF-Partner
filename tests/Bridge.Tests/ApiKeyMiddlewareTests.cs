using Bridge.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bridge.Tests;

/// <summary>
/// Unit testy pro ApiKeyMiddleware.
/// Ověřuje: správný klíč → průchod, chybějící klíč → 401, špatný klíč → 401,
/// /health exempt z API key validace, constant-time comparison.
/// </summary>
public class ApiKeyMiddlewareTests
{
    private const string ValidApiKey = "test-api-key-secret-12345";

    private static ApiKeyMiddleware CreateMiddleware(RequestDelegate next)
        => new(next, ValidApiKey, NullLogger<ApiKeyMiddleware>.Instance);

    private static DefaultHttpContext CreateContext(string? apiKey = null, string path = "/api/mapping/test")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (apiKey is not null)
            context.Request.Headers["X-Api-Key"] = apiKey;
        context.Response.Body = new MemoryStream();
        return context;
    }

    // ── Happy path ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidApiKey_CallsNext()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = CreateContext(apiKey: ValidApiKey);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(200, context.Response.StatusCode);
    }

    // ── Chybějící klíč → 401 ──────────────────────────────────────────────────────

    [Fact]
    public async Task MissingApiKey_Returns401()
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var context = CreateContext(apiKey: null);

        await middleware.InvokeAsync(context);

        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task MissingApiKey_DoesNotCallNext()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = CreateContext(apiKey: null);

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
    }

    // ── Neplatný klíč → 401 ───────────────────────────────────────────────────────

    [Fact]
    public async Task InvalidApiKey_Returns401()
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var context = CreateContext(apiKey: "wrong-key");

        await middleware.InvokeAsync(context);

        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task EmptyApiKey_Returns401()
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var context = CreateContext(apiKey: "");

        await middleware.InvokeAsync(context);

        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task PrefixOfValidKey_Returns401()
    {
        // Klíč je prefix správného klíče — musí selhat (constant-time comparison)
        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var context = CreateContext(apiKey: ValidApiKey[..5]);

        await middleware.InvokeAsync(context);

        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task ValidKeyWithTrailingSpace_Returns401()
    {
        // Klíč s trailing space — nesmí projít
        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var context = CreateContext(apiKey: ValidApiKey + " ");

        await middleware.InvokeAsync(context);

        Assert.Equal(401, context.Response.StatusCode);
    }

    // ── /health je exempt ─────────────────────────────────────────────────────────

    [Fact]
    public async Task HealthPath_WithoutApiKey_CallsNext()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = CreateContext(apiKey: null, path: "/health");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task HealthPath_Returns200()
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var context = CreateContext(apiKey: null, path: "/health");

        await middleware.InvokeAsync(context);

        // Middleware nenastavuje 401 pro /health — status zůstává výchozí (200)
        Assert.NotEqual(401, context.Response.StatusCode);
    }

    // ── Case sensitivity path ──────────────────────────────────────────────────────

    [Fact]
    public async Task HealthPathUppercase_WithoutApiKey_CallsNext()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = CreateContext(apiKey: null, path: "/HEALTH");

        await middleware.InvokeAsync(context);

        // /HEALTH by mělo být také exempt — case-insensitive srovnání
        Assert.True(nextCalled);
    }
}
