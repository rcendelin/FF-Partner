namespace Bridge.Domain.Messages;

/// <summary>
/// Zpráva publikovaná při detekci nové objednávky v Partner3 tbl_order.
/// Topic: bridge.order.created
/// </summary>
public sealed class OrderCreatedMessage
{
    public required string MessageId { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public required string PartnerRegion { get; init; }
    public required long PartnerOrderId { get; init; }
    public required Guid FfCompanyId { get; init; }
    public required int PartnerClientId { get; init; }
    /// <summary>Unix timestamp z tbl_order.order_date_start</summary>
    public required int OrderDateStart { get; init; }
    public required short OrderState { get; init; }
    public required int? OrderPrice { get; init; }
    public string? VehicleVin { get; init; }
    public string? VehicleMark { get; init; }
    public string? VehicleModel { get; init; }
    public string? VehicleType { get; init; }
    public int? VehicleCategory { get; init; }
    public int? VehiclePowerHp { get; init; }
}

/// <summary>
/// Zpráva publikovaná při změně stavu objednávky v Partner3.
/// Topic: bridge.order.state-changed
/// Detekováno MD5 hash snapshoting: order_state|order_close|order_close_pay|order_automat_close|order_deactive
/// </summary>
public sealed class OrderStateChangedMessage
{
    public required string MessageId { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public required string PartnerRegion { get; init; }
    public required long PartnerOrderId { get; init; }
    public required Guid FfCompanyId { get; init; }
    public required int PartnerClientId { get; init; }
    public required short OrderState { get; init; }
    public required short OrderClose { get; init; }
    public required short OrderClosePay { get; init; }
    /// <summary>-10=čeká, -1=chyba GAIA, 0=hotovo</summary>
    public required sbyte OrderAutomatClose { get; init; }
    public required sbyte OrderDeactive { get; init; }
}

/// <summary>
/// Zpráva publikovaná při zaplacení objednávky (order_close_pay = 1).
/// Topic: bridge.order.completed
/// Triggr pro Machine enrichment: lookup VIN → aktualizace Machine.ChippedPowerKw.
/// </summary>
public sealed class OrderCompletedMessage
{
    public required string MessageId { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public required string PartnerRegion { get; init; }
    public required long PartnerOrderId { get; init; }
    public required Guid FfCompanyId { get; init; }
    public required int PartnerClientId { get; init; }
    public string? VehicleVin { get; init; }
    public string? VehicleMark { get; init; }
    public string? VehicleModel { get; init; }
    public int? VehicleCategory { get; init; }
    public int? VehiclePowerHp { get; init; }
}

/// <summary>
/// Zpráva publikovaná při zrušení objednávky (order_state = 30).
/// Topic: bridge.order.cancelled
/// FieldForce reaguje aktualizací Company.Stage → Lost (pokud nejsou jiné aktivní zakázky).
/// </summary>
public sealed class OrderCancelledMessage
{
    public required string MessageId { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public required string PartnerRegion { get; init; }
    public required long PartnerOrderId { get; init; }
    public required Guid FfCompanyId { get; init; }
    public required int PartnerClientId { get; init; }
}
