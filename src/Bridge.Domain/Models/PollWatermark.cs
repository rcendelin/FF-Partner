namespace Bridge.Domain.Models;

/// <summary>
/// Watermark pro polling nových objednávek z tbl_order per region.
/// Ukládá se do bridge_poll_watermark (Azure SQL).
/// </summary>
public sealed class PollWatermark
{
    /// <summary>Klíč: 'tbl_order_cz', 'tbl_order_pl', 'tbl_order_hu', 'tbl_order_us'</summary>
    public required string PollTarget { get; init; }
    /// <summary>Unix timestamp z order_date_start — zpracovány všechny záznamy s order_date_start > tato hodnota</summary>
    public required int LastProcessedOrderDate { get; init; }
    public required long LastProcessedId { get; init; }
    public required DateTime UpdatedAt { get; init; }
}
