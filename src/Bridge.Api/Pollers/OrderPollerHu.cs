using Bridge.Api.Telemetry;
using Bridge.Application.Interfaces;
using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner;
using Microsoft.Extensions.Logging;

namespace Bridge.Api.Pollers;

/// <summary>
/// Poller pro HU region — čte tbl_order z Partner3 HU DB.
/// Interval: 5 minut. Startup delay: 30 sekund.
/// SQL logika: F4-03.
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
        ISyncLogRepository syncLog,
        IBridgeMetrics metrics,
        IPartnerDbConnectionFactory partnerDbFactory,
        ILogger<OrderPollerHu> logger)
        : base(publisher, watermarkRepo, snapshotRepo, mappingRepo, syncLog, metrics, partnerDbFactory, logger)
    { }

    protected override Task PollAsync(CancellationToken ct)
    {
        // SQL logika implementována v F4-03.
        return Task.CompletedTask;
    }
}
