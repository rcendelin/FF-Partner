using Bridge.Application.Interfaces;
using Bridge.Domain.Models;
using Dapper;

namespace Bridge.Infrastructure.Partner.Repositories;

/// <summary>
/// Čte objednávky z tbl_order v Partner3 MySQL DB.
///
/// Design poznámky:
/// - Pouze SELECT — Bridge NIKDY nepíše do tbl_order (CLAUDE.md sekce 17)
/// - order_date_modified NEEXISTUJE (ověřeno 2026-03-27) — nelze použít pro change detection
/// - GetNewOrdersAsync: watermark strategie přes order_date_start (unix timestamp)
/// - GetActiveOrderStatesAsync: plný scan aktivních objednávek pro MD5 snapshot comparison
/// - IN @ClientIds: Dapper automaticky rozbalí IReadOnlyList na SQL IN seznam
/// - CancellationToken propagován přes CommandDefinition pro podporu graceful shutdown
/// </summary>
public sealed class OrderPollingRepository : IOrderPollingRepository
{
    private readonly IPartnerDbConnectionFactory _factory;

    // Sdílený SELECT seznam sloupců pro obě metody — vynechány sloupce nepotřebné pro polling
    private const string OrderColumns = """
        idorder AS IdOrder,
        id_client AS IdClient,
        order_date_start AS OrderDateStart,
        order_state AS OrderState,
        order_close AS OrderClose,
        order_close_pay AS OrderClosePay,
        order_automat_close AS OrderAutomatClose,
        order_deactive AS OrderDeactive,
        order_price AS OrderPrice,
        order_car_vin AS OrderCarVin,
        order_car_mark AS OrderCarMark,
        order_car_model AS OrderCarModel,
        order_car_type AS OrderCarType,
        order_car_category AS OrderCarCategory,
        order_car_power_hp AS OrderCarPowerHp
        """;

    public OrderPollingRepository(IPartnerDbConnectionFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Vrátí objednávky s order_date_start > <paramref name="afterUnixTimestamp"/> pro dané klienty.
    /// Backfill volání: afterUnixTimestamp = nyní - 12 měsíců.
    /// Polling volání: afterUnixTimestamp = hodnota z bridge_poll_watermark.
    /// </summary>
    public async Task<IReadOnlyList<TblOrderRow>> GetNewOrdersAsync(
        string region,
        IReadOnlyList<int> clientIds,
        int afterUnixTimestamp,
        CancellationToken ct = default)
    {
        if (clientIds.Count == 0)
            return Array.Empty<TblOrderRow>();

        var sql = $"""
            SELECT {OrderColumns}
            FROM tbl_order
            WHERE order_date_start > @AfterTs
              AND order_deactive = 0
              AND order_delete = 0
              AND id_client IN @ClientIds
            ORDER BY order_date_start ASC, idorder ASC
            """;

        // await using zajistí asynchronní dispose MySqlConnection (vs. synchronní Dispose)
        await using var conn = _factory.CreateConnection(region);
        var result = await conn.QueryAsync<TblOrderRow>(
            new CommandDefinition(sql, new { AfterTs = afterUnixTimestamp, ClientIds = clientIds },
                cancellationToken: ct));
        return result.AsList();
    }

    /// <summary>
    /// Vrátí aktuální stavy všech aktivních objednávek pro dané klienty.
    /// Výsledek se porovná s bridge_order_snapshot (MD5 hash) pro change detection.
    /// </summary>
    public async Task<IReadOnlyList<TblOrderRow>> GetActiveOrderStatesAsync(
        string region,
        IReadOnlyList<int> clientIds,
        CancellationToken ct = default)
    {
        if (clientIds.Count == 0)
            return Array.Empty<TblOrderRow>();

        var sql = $"""
            SELECT {OrderColumns}
            FROM tbl_order
            WHERE order_deactive = 0
              AND order_delete = 0
              AND id_client IN @ClientIds
            """;

        await using var conn = _factory.CreateConnection(region);
        var result = await conn.QueryAsync<TblOrderRow>(
            new CommandDefinition(sql, new { ClientIds = clientIds }, cancellationToken: ct));
        return result.AsList();
    }
}
