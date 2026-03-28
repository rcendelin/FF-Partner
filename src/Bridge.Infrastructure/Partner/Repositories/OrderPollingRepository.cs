using Bridge.Application.Interfaces;
using Bridge.Domain.Models;
using Dapper;

namespace Bridge.Infrastructure.Partner.Repositories;

/// <summary>
/// Čte objednávky z tbl_order v Partner3 MySQL DB.
/// Vyžaduje Dapper multi-param support pro IN clause.
/// Pouze SELECT — Bridge nikdy nepíše do tbl_order.
/// </summary>
public sealed class OrderPollingRepository : IOrderPollingRepository
{
    private readonly IPartnerDbConnectionFactory _factory;

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

        using var conn = _factory.CreateConnection(region);
        var result = await conn.QueryAsync<TblOrderRow>(sql, new
        {
            AfterTs = afterUnixTimestamp,
            ClientIds = clientIds
        });
        return result.AsList();
    }

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

        using var conn = _factory.CreateConnection(region);
        var result = await conn.QueryAsync<TblOrderRow>(sql, new { ClientIds = clientIds });
        return result.AsList();
    }
}
