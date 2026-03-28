using Bridge.Api.Telemetry;
using Bridge.Application.Interfaces;
using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner;
using Microsoft.Extensions.Logging;

namespace Bridge.Api.Pollers;

/// <summary>
/// Poller pro PL region — čte tbl_order z Partner3 PL DB.
/// Interval: 5 minut. Startup delay: 30 sekund.
/// SQL logika: F4-02.
/// </summary>
public sealed class OrderPollerPl : OrderPollerBase
{
    protected override string Region => "pl";
    protected override string PollTarget => "tbl_order_pl";

    public OrderPollerPl(
        IServiceBusPublisher publisher,
        IPollWatermarkRepository watermarkRepo,
        IOrderSnapshotRepository snapshotRepo,
        IBridgeMappingRepository mappingRepo,
        ISyncLogRepository syncLog,
        IBridgeMetrics metrics,
        IPartnerDbConnectionFactory partnerDbFactory,
        ILogger<OrderPollerPl> logger)
        : base(publisher, watermarkRepo, snapshotRepo, mappingRepo, syncLog, metrics, partnerDbFactory, logger)
    { }

    protected override Task PollAsync(CancellationToken ct)
    {
        // SQL logika implementována v F4-02.
        return Task.CompletedTask;
    }
}
