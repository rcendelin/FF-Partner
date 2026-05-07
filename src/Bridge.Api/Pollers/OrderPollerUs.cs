using Bridge.Api.Telemetry;
using Bridge.Application.Interfaces;
using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner;
using Microsoft.Extensions.Logging;

namespace Bridge.Api.Pollers;

/// <summary>
/// Poller pro US region — čte tbl_order z Partner3 US DB každých 5 minut.
/// Logika PollAsync je sdílena v OrderPollerBase (implementována v F4-02).
/// US region aktivní — backfill viz F4-03.
/// </summary>
public sealed class OrderPollerUs : OrderPollerBase
{
    protected override string Region => "us";
    protected override string PollTarget => "tbl_order_us";

    public OrderPollerUs(
        IServiceBusPublisher publisher,
        IPollWatermarkRepository watermarkRepo,
        IOrderSnapshotRepository snapshotRepo,
        IBridgeMappingRepository mappingRepo,
        IOrderPollingRepository orderPolling,
        IPartnerSyncLog syncLog,
        IBridgeMetrics metrics,
        IPartnerDbConnectionFactory partnerDbFactory,
        ILogger<OrderPollerUs> logger)
        : base(publisher, watermarkRepo, snapshotRepo, mappingRepo, orderPolling, syncLog, metrics, partnerDbFactory, logger)
    { }
}
