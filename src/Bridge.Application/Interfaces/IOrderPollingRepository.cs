using Bridge.Domain.Models;

namespace Bridge.Application.Interfaces;

/// <summary>
/// Čte data z tbl_order v Partner3 MySQL DB pro Order polling (Fáze 4).
/// Pouze SELECT — Bridge nikdy nepíše do tbl_order.
/// </summary>
public interface IOrderPollingRepository
{
    /// <summary>
    /// Vrátí nové objednávky vytvořené po daném unix timestamp.
    /// Filtrace dle id_client ze seznamu mapovaných klientů pro daný region.
    /// Řazeno dle order_date_start ASC (pro správnou aktualizaci watermarku).
    /// </summary>
    Task<IReadOnlyList<TblOrderRow>> GetNewOrdersAsync(
        string region,
        IReadOnlyList<int> clientIds,
        int afterUnixTimestamp,
        CancellationToken ct = default);

    /// <summary>
    /// Vrátí aktuální stavy všech aktivních objednávek pro dané klienty.
    /// Používá se pro MD5 hash change detection.
    /// Filtrace: order_deactive = 0 AND order_delete = 0.
    /// </summary>
    Task<IReadOnlyList<TblOrderRow>> GetActiveOrderStatesAsync(
        string region,
        IReadOnlyList<int> clientIds,
        CancellationToken ct = default);
}
