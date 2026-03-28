namespace Bridge.Domain.Models;

/// <summary>
/// Řádek z tbl_order (Partner3 MySQL) — pouze sloupce relevantní pro Order polling.
/// Viz CLAUDE.md sekce 5 — tbl_order klíčové sloupce.
/// </summary>
public sealed class TblOrderRow
{
    public required long IdOrder { get; init; }
    public required int IdClient { get; init; }
    /// <summary>Unix timestamp — watermark pro nové objednávky.</summary>
    public required int OrderDateStart { get; init; }
    public required short OrderState { get; init; }
    public required short OrderClose { get; init; }
    public required short OrderClosePay { get; init; }
    /// <summary>-10=čeká, -1=chyba GAIA, 0=hotovo</summary>
    public required sbyte OrderAutomatClose { get; init; }
    public required sbyte OrderDeactive { get; init; }
    public int? OrderPrice { get; init; }
    public string? OrderCarVin { get; init; }
    public string? OrderCarMark { get; init; }
    public string? OrderCarModel { get; init; }
    public string? OrderCarType { get; init; }
    public int? OrderCarCategory { get; init; }
    public int? OrderCarPowerHp { get; init; }
}
