namespace Bridge.Domain.Models;

/// <summary>
/// Snapshot stavu objednávky pro detekci změn stavů.
/// Ukládá se do bridge_order_snapshot (Azure SQL).
/// Hash = MD5(order_state|order_close|order_close_pay|order_automat_close|order_deactive)
/// </summary>
public sealed class OrderSnapshot
{
    public required string PartnerRegion { get; init; }
    public required long OrderId { get; init; }
    /// <summary>MD5 hex string, 32 znaků.</summary>
    public required string StateHash { get; init; }
    public required DateTime LastChecked { get; init; }
}
