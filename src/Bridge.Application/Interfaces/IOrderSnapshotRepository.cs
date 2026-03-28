using Bridge.Domain.Models;

namespace Bridge.Application.Interfaces;

/// <summary>
/// Spravuje MD5 state snapshoty objednávek pro detekci změn stavů (bridge_order_snapshot, Azure SQL).
/// </summary>
public interface IOrderSnapshotRepository
{
    /// <summary>Vrátí aktuálně uložený hash pro danou objednávku, nebo null.</summary>
    Task<string?> GetHashAsync(string region, long orderId, CancellationToken ct = default);

    /// <summary>INSERT OR UPDATE — uloží nebo aktualizuje hash pro danou objednávku.</summary>
    Task UpsertAsync(string region, long orderId, string stateHash, CancellationToken ct = default);

    /// <summary>
    /// Vrátí všechny snapshoty pro daný region.
    /// Používá se při každém poll cyklu pro bulk porovnání hashů.
    /// </summary>
    Task<IReadOnlyList<OrderSnapshot>> GetRegionSnapshotsAsync(string region, CancellationToken ct = default);

    /// <summary>
    /// Batch upsert — aktualizuje více snapshotů najednou pro efektivitu.
    /// Volá se po každém poll cyklu s aktuálními stavy.
    /// </summary>
    Task BulkUpsertAsync(IEnumerable<OrderSnapshot> snapshots, CancellationToken ct = default);
}
