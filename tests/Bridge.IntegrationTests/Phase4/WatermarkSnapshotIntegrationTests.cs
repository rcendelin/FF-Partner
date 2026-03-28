using Bridge.Application.Interfaces;
using Bridge.Domain.Models;

namespace Bridge.IntegrationTests.Phase4;

/// <summary>
/// Integrační testy pro PollWatermarkRepository, OrderSnapshotRepository a BridgeSyncLogRepository.
///
/// Pokrývají scénáře F4-08 (Bridge strana):
///   Watermark: Upsert + GetAsync roundtrip, idempotence
///   Snapshot:  GetHashAsync + UpsertAsync roundtrip, BulkUpsert, GetRegionSnapshots
///   SyncLog:   HasOperationSucceededAsync (backfill idempotence)
///
/// Testovací izolace:
///   Watermarks: unikátní poll_target s prefixem "it_wm_" + Guid[..12]
///   Snapshots:  záporná order_id — nikdy nekolizují s reálnými tbl_order záznamy (BIGINT UNSIGNED)
///   SyncLog:    operace s prefixem "it_backfill_" + Guid[..8]
/// </summary>
[Trait("Category", "Integration")]
public sealed class WatermarkSnapshotIntegrationTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fix;

    public WatermarkSnapshotIntegrationTests(IntegrationTestFixture fix)
        => _fix = fix;

    // ── PollWatermark: Upsert + Get roundtrip ────────────────────────────

    [FactIfAzureSql]
    public async Task Watermark_Upsert_CanBeRetrievedByPollTarget()
    {
        var repo = _fix.CreateWatermarkRepository();
        var pollTarget = "it_wm_" + Guid.NewGuid().ToString("N")[..12];
        _fix.TrackWatermark(pollTarget);

        var now = DateTime.UtcNow;
        var watermark = new PollWatermark
        {
            PollTarget = pollTarget,
            LastProcessedOrderDate = 1_700_000_000,   // unix timestamp — testovací hodnota
            LastProcessedId = 42L,
            UpdatedAt = now
        };
        await repo.UpsertAsync(watermark);

        var found = await repo.GetAsync(pollTarget);

        Assert.NotNull(found);
        Assert.Equal(pollTarget, found.PollTarget);
        Assert.Equal(1_700_000_000, found.LastProcessedOrderDate);
        Assert.Equal(42L, found.LastProcessedId);
    }

    // ── PollWatermark: druhý Upsert přepíše hodnotu ────────────────────────

    [FactIfAzureSql]
    public async Task Watermark_Upsert_IsIdempotent_SecondCallUpdates()
    {
        var repo = _fix.CreateWatermarkRepository();
        var pollTarget = "it_wm_" + Guid.NewGuid().ToString("N")[..12];
        _fix.TrackWatermark(pollTarget);

        var first = new PollWatermark
        {
            PollTarget = pollTarget,
            LastProcessedOrderDate = 1_000_000,
            LastProcessedId = 1L,
            UpdatedAt = DateTime.UtcNow
        };
        await repo.UpsertAsync(first);

        // Druhý upsert — vyšší hodnoty
        var second = new PollWatermark
        {
            PollTarget = pollTarget,
            LastProcessedOrderDate = 2_000_000,
            LastProcessedId = 99L,
            UpdatedAt = DateTime.UtcNow
        };
        await repo.UpsertAsync(second);

        var found = await repo.GetAsync(pollTarget);

        Assert.NotNull(found);
        Assert.Equal(2_000_000, found.LastProcessedOrderDate);
        Assert.Equal(99L, found.LastProcessedId);
    }

    // ── PollWatermark: neexistující poll_target → null ────────────────────

    [FactIfAzureSql]
    public async Task Watermark_Get_ForUnknownTarget_ReturnsNull()
    {
        var repo = _fix.CreateWatermarkRepository();
        var nonExistent = "it_wm_" + Guid.NewGuid().ToString("N")[..12];
        // Není registrován pro cleanup — záznam vůbec nevzniká

        var found = await repo.GetAsync(nonExistent);

        Assert.Null(found);
    }

    // ── OrderSnapshot: Upsert + GetHash roundtrip ─────────────────────────

    [FactIfAzureSql]
    public async Task Snapshot_UpsertAndGetHash_ReturnsCorrectHash()
    {
        var repo = _fix.CreateSnapshotRepository();
        var orderId = TestOrderId(Guid.NewGuid());
        _fix.TrackSnapshot(orderId);

        const string region = "cz";
        const string hash = "abc123def456";

        await repo.UpsertAsync(region, orderId, hash);

        var found = await repo.GetHashAsync(region, orderId);

        Assert.NotNull(found);
        Assert.Equal(hash, found);
    }

    // ── OrderSnapshot: druhý Upsert přepíše hash ──────────────────────────

    [FactIfAzureSql]
    public async Task Snapshot_Upsert_UpdatesExistingHash()
    {
        var repo = _fix.CreateSnapshotRepository();
        var orderId = TestOrderId(Guid.NewGuid());
        _fix.TrackSnapshot(orderId);

        await repo.UpsertAsync("pl", orderId, "hash_v1");
        await repo.UpsertAsync("pl", orderId, "hash_v2");

        var found = await repo.GetHashAsync("pl", orderId);

        Assert.NotNull(found);
        Assert.Equal("hash_v2", found);
    }

    // ── OrderSnapshot: neexistující order → null ──────────────────────────

    [FactIfAzureSql]
    public async Task Snapshot_GetHash_ForUnknownOrder_ReturnsNull()
    {
        var repo = _fix.CreateSnapshotRepository();
        var nonExistentOrderId = TestOrderId(Guid.NewGuid());
        // Není registrován pro cleanup — záznam vůbec nevzniká

        var found = await repo.GetHashAsync("cz", nonExistentOrderId);

        Assert.Null(found);
    }

    // ── OrderSnapshot: BulkUpsert + GetRegionSnapshots ────────────────────

    [FactIfAzureSql]
    public async Task Snapshot_BulkUpsert_CanBeRetrievedByRegion()
    {
        var repo = _fix.CreateSnapshotRepository();
        var seed = Guid.NewGuid();
        var orderId1 = TestOrderId(seed, 0);
        var orderId2 = TestOrderId(seed, 1);
        var orderId3 = TestOrderId(seed, 2);

        _fix.TrackSnapshot(orderId1);
        _fix.TrackSnapshot(orderId2);
        _fix.TrackSnapshot(orderId3);

        const string region = "hu";

        var snapshots = new[]
        {
            new OrderSnapshot { PartnerRegion = region, OrderId = orderId1, StateHash = "h1", LastChecked = DateTime.UtcNow },
            new OrderSnapshot { PartnerRegion = region, OrderId = orderId2, StateHash = "h2", LastChecked = DateTime.UtcNow },
            new OrderSnapshot { PartnerRegion = region, OrderId = orderId3, StateHash = "h3", LastChecked = DateTime.UtcNow }
        };
        await repo.BulkUpsertAsync(snapshots);

        var allStored = await repo.GetRegionSnapshotsAsync(region);

        // Filtrovat pouze testovací záznamy — produkční data v regionu ignorovat
        var testOrderIds = new HashSet<long> { orderId1, orderId2, orderId3 };
        var stored = allStored.Where(s => testOrderIds.Contains(s.OrderId)).ToList();

        Assert.Equal(3, stored.Count);

        var snapshot1 = stored.First(s => s.OrderId == orderId1);
        Assert.Equal("h1", snapshot1.StateHash);

        var snapshot2 = stored.First(s => s.OrderId == orderId2);
        Assert.Equal("h2", snapshot2.StateHash);

        var snapshot3 = stored.First(s => s.OrderId == orderId3);
        Assert.Equal("h3", snapshot3.StateHash);
    }

    // ── SyncLog: HasOperationSucceeded — nová operace → false ─────────────

    [FactIfAzureSql]
    public async Task SyncLog_HasOperationSucceeded_ReturnsFalse_ForNewOperation()
    {
        var repo = _fix.CreateSyncLogRepository();
        // Unikátní testovací operace — v DB neexistuje
        var operation = "it_backfill_" + Guid.NewGuid().ToString("N")[..8];

        var result = await repo.HasOperationSucceededAsync(operation, "cz");

        Assert.False(result);
    }

    // ── SyncLog: HasOperationSucceeded — po zápisu success → true ─────────

    [FactIfAzureSql]
    public async Task SyncLog_HasOperationSucceeded_ReturnsTrue_AfterWriteSuccess()
    {
        var repo = _fix.CreateSyncLogRepository();
        var operation = "it_backfill_" + Guid.NewGuid().ToString("N")[..8];
        _fix.TrackSyncLogOperation(operation);

        // Zápis úspěšného záznamu (simulace dokončeného backfillu)
        await repo.WriteAsync(new SyncLogEntry
        {
            Operation = operation,
            Status = "success",
            PartnerRegion = "cz",
            Severity = "Info",
            CreatedAt = DateTime.UtcNow
        });

        var result = await repo.HasOperationSucceededAsync(operation, "cz");

        Assert.True(result);
    }

    // ── SyncLog: HasOperationSucceeded — failed záznam → false ───────────

    [FactIfAzureSql]
    public async Task SyncLog_HasOperationSucceeded_ReturnsFalse_WhenOnlyFailedEntryExists()
    {
        var repo = _fix.CreateSyncLogRepository();
        var operation = "it_backfill_" + Guid.NewGuid().ToString("N")[..8];
        _fix.TrackSyncLogOperation(operation);

        // Zápis neúspěšného záznamu
        await repo.WriteAsync(new SyncLogEntry
        {
            Operation = operation,
            Status = "failed",
            PartnerRegion = "cz",
            Severity = "Error",
            CreatedAt = DateTime.UtcNow
        });

        var result = await repo.HasOperationSucceededAsync(operation, "cz");

        Assert.False(result, "Pouhý failed záznam nesmí být vyhodnocen jako succeeded.");
    }

    // ── SyncLog: GetLastAsync — vrací záznamy v správném pořadí ───────────

    [FactIfAzureSql]
    public async Task SyncLog_GetLast_ReturnsRecentEntriesInDescOrder()
    {
        var repo = _fix.CreateSyncLogRepository();
        var opOlder = "it_backfill_" + Guid.NewGuid().ToString("N")[..8];
        var opNewer = "it_backfill_" + Guid.NewGuid().ToString("N")[..8];
        _fix.TrackSyncLogOperation(opOlder);
        _fix.TrackSyncLogOperation(opNewer);

        // Zápis dvou záznamů s rozdílným časem — ověříme DESC pořadí
        await repo.WriteAsync(new SyncLogEntry
        {
            Operation = opOlder,
            Status = "success",
            PartnerRegion = "pl",
            Severity = "Info",
            CreatedAt = DateTime.UtcNow.AddSeconds(-5)
        });

        await repo.WriteAsync(new SyncLogEntry
        {
            Operation = opNewer,
            Status = "success",
            PartnerRegion = "pl",
            Severity = "Info",
            CreatedAt = DateTime.UtcNow
        });

        var last = await repo.GetLastAsync(50);

        Assert.NotEmpty(last);
        var list = last.ToList();
        var idxOlder = list.FindIndex(e => e.Operation == opOlder);
        var idxNewer = list.FindIndex(e => e.Operation == opNewer);

        Assert.True(idxOlder >= 0, $"Starší záznam '{opOlder}' nenalezen ve výsledku GetLastAsync.");
        Assert.True(idxNewer >= 0, $"Novější záznam '{opNewer}' nenalezen ve výsledku GetLastAsync.");
        Assert.True(idxNewer < idxOlder,
            "Novější záznam musí být před starším (DESC pořadí dle created_at).");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Generuje záporné order_id pro testovací izolaci.
    /// Záporná čísla nikdy nekolizují s reálnými tbl_order.idorder (BIGINT UNSIGNED).
    /// Používá 63 bitů z GUID (ne jen 32-bitový GetHashCode) pro minimalizaci kolizí.
    /// </summary>
    private static long TestOrderId(Guid seed, int index = 0)
    {
        var bytes = seed.ToByteArray();
        var magnitude = (long)(BitConverter.ToUInt64(bytes, 0) & 0x7FFF_FFFF_FFFF_FFFFUL);
        return -(magnitude + 1L + index);
    }
}
