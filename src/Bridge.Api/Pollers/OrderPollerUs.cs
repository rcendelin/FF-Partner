using Bridge.Api.Telemetry;
using Bridge.Application.Interfaces;
using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner;
using Microsoft.Extensions.Logging;

namespace Bridge.Api.Pollers;

/// <summary>
/// Poller pro US region — čte tbl_order z Partner3 US DB.
/// Interval: 5 minut. Startup delay: 30 sekund.
/// SQL logika: F4-03.
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
        ISyncLogRepository syncLog,
        IBridgeMetrics metrics,
        IPartnerDbConnectionFactory partnerDbFactory,
        ILogger<OrderPollerUs> logger)
        : base(publisher, watermarkRepo, snapshotRepo, mappingRepo, syncLog, metrics, partnerDbFactory, logger)
    { }

    protected override Task PollAsync(CancellationToken ct)
    {
        // SQL logika implementována v F4-03.
        return Task.CompletedTask;
    }
}
