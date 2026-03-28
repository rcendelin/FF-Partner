using Bridge.Api.Telemetry;
using Bridge.Application.Interfaces;
using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner;
using Microsoft.Extensions.Logging;

namespace Bridge.Api.Pollers;

/// <summary>
/// Poller pro HU region — čte tbl_order z Partner3 HU DB každých 5 minut.
/// Logika PollAsync je sdílena v OrderPollerBase (implementována v F4-02).
/// HU region aktivní — backfill viz F4-03.
/// </summary>
public sealed class OrderPollerHu : OrderPollerBase
{
    protected override string Region => "hu";
    protected override string PollTarget => "tbl_order_hu";

    public OrderPollerHu(
        IServiceBusPublisher publisher,
        IPollWatermarkRepository watermarkRepo,
        IOrderSnapshotRepository snapshotRepo,
        IBridgeMappingRepository mappingRepo,
        IOrderPollingRepository orderPolling,
        ISyncLogRepository syncLog,
        IBridgeMetrics metrics,
        IPartnerDbConnectionFactory partnerDbFactory,
        ILogger<OrderPollerHu> logger)
        : base(publisher, watermarkRepo, snapshotRepo, mappingRepo, orderPolling, syncLog, metrics, partnerDbFactory, logger)
    { }
}
