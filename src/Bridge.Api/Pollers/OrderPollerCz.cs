using Bridge.Api.Telemetry;
using Bridge.Application.Interfaces;
using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner;
using Microsoft.Extensions.Logging;

namespace Bridge.Api.Pollers;

/// <summary>
/// Poller pro CZ region — čte tbl_order z Partner3 CZ DB každých 5 minut.
/// Logika PollAsync je sdílena v OrderPollerBase (implementována v F4-02).
/// </summary>
public sealed class OrderPollerCz : OrderPollerBase
{
    protected override string Region => "cz";
    protected override string PollTarget => "tbl_order_cz";

    public OrderPollerCz(
        IServiceBusPublisher publisher,
        IPollWatermarkRepository watermarkRepo,
        IOrderSnapshotRepository snapshotRepo,
        IBridgeMappingRepository mappingRepo,
        IOrderPollingRepository orderPolling,
        ISyncLogRepository syncLog,
        IBridgeMetrics metrics,
        IPartnerDbConnectionFactory partnerDbFactory,
        ILogger<OrderPollerCz> logger)
        : base(publisher, watermarkRepo, snapshotRepo, mappingRepo, orderPolling, syncLog, metrics, partnerDbFactory, logger)
    { }
}
