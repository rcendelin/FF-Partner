namespace Bridge.IntegrationTests.Phase4;

/// <summary>
/// Integrační testy pro OrderPollingRepository — ověřují přímý přístup k Partner3 tbl_order.
///
/// Pokrývají scénáře F4-08 (Bridge strana):
///   Watermark strategie: GetNewOrdersAsync s budoucím timestampem → prázdný výsledek
///   State snapshot: GetActiveOrderStatesAsync s prázdným seznamem klientů → prázdný výsledek
///   Robustnost: dotazy nesmí selhat při validních ale nestandardních vstupech
///
/// BEZPEČNOST: všechny testy jsou READ-ONLY vůči Partner3 DB.
/// NIKDY nevolat INSERT/UPDATE/DELETE na tbl_order.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OrderPollerQueryIntegrationTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fix;

    // Fiktivní clientId, který s největší pravděpodobností neexistuje v tbl_client.
    // Partner3 používá AUTO_INCREMENT od 1 — hodnota blízko maxint je bezpečná pro read-only testy.
    private const int NonExistentClientId = 999_999_999;

    public OrderPollerQueryIntegrationTests(IntegrationTestFixture fix)
        => _fix = fix;

    // ── GetNewOrders: prázdný clientIds → prázdný výsledek ────────────────

    [FactIfPartnerDb]
    public async Task GetNewOrders_WithEmptyClientIds_ReturnsEmpty()
    {
        var repo = _fix.CreateOrderPollingRepository();

        var result = await repo.GetNewOrdersAsync(
            region: "cz",
            clientIds: [],      // Žádní klienti → žádné objednávky
            afterUnixTimestamp: 0);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    // ── GetNewOrders: budoucí timestamp → prázdný výsledek ────────────────

    [FactIfPartnerDb]
    public async Task GetNewOrders_WithFutureTimestamp_ReturnsEmpty()
    {
        var repo = _fix.CreateOrderPollingRepository();

        // Unix timestamp za 100 let — žádná objednávka v budoucnosti neexistuje
        var futureTimestamp = (int)DateTimeOffset.UtcNow.AddYears(100).ToUnixTimeSeconds();

        var result = await repo.GetNewOrdersAsync(
            region: "cz",
            clientIds: [NonExistentClientId],  // Fiktivní clientId — s největší pravděpodobností neexistuje
            afterUnixTimestamp: futureTimestamp);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    // ── GetNewOrders: velmi starý timestamp → dotaz proběhne bez výjimky ──

    [FactIfPartnerDb]
    public async Task GetNewOrders_WithAncientTimestamp_DoesNotThrow()
    {
        var repo = _fix.CreateOrderPollingRepository();

        // Timestamp 0 (1970-01-01) — Fiktivní clientId → vrátí prázdný seznam bez výjimky
        var ex = await Record.ExceptionAsync(() =>
            repo.GetNewOrdersAsync(
                region: "cz",
                clientIds: [NonExistentClientId],
                afterUnixTimestamp: 0));

        Assert.Null(ex);
    }

    // ── GetActiveOrderStates: prázdný clientIds → prázdný výsledek ────────

    [FactIfPartnerDb]
    public async Task GetActiveOrderStates_WithEmptyClientIds_ReturnsEmpty()
    {
        var repo = _fix.CreateOrderPollingRepository();

        var result = await repo.GetActiveOrderStatesAsync(
            region: "cz",
            clientIds: []);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    // ── GetActiveOrderStates: neexistující klient → prázdný výsledek ──────

    [FactIfPartnerDb]
    public async Task GetActiveOrderStates_WithNonExistentClientId_ReturnsEmpty()
    {
        var repo = _fix.CreateOrderPollingRepository();

        var result = await repo.GetActiveOrderStatesAsync(
            region: "cz",
            clientIds: [NonExistentClientId]);  // Fiktivní clientId

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    // ── GetNewOrders: PL region — dotaz funguje i pro PL ──────────────────

    [FactIfPartnerDb]
    public async Task GetNewOrders_PlRegion_DoesNotThrow()
    {
        var repo = _fix.CreateOrderPollingRepository();

        var ex = await Record.ExceptionAsync(() =>
            repo.GetNewOrdersAsync(
                region: "pl",
                clientIds: [NonExistentClientId],
                afterUnixTimestamp: 0));

        Assert.Null(ex);
    }

    // ── GetActiveOrderStates: PL region — dotaz funguje pro PL ───────────

    [FactIfPartnerDb]
    public async Task GetActiveOrderStates_PlRegion_DoesNotThrow()
    {
        var repo = _fix.CreateOrderPollingRepository();

        var ex = await Record.ExceptionAsync(() =>
            repo.GetActiveOrderStatesAsync(
                region: "pl",
                clientIds: [NonExistentClientId]));

        Assert.Null(ex);
    }
}
