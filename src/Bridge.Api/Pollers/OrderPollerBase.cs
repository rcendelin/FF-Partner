using Bridge.Api.Telemetry;
using Bridge.Application.Interfaces;
using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Bridge.Api.Pollers;

/// <summary>
/// Abstraktní základ pro 4 regionální Order pollery (CZ, PL, HU, US).
/// Každý poller běží nezávisle jako BackgroundService a spouští PollAsync každých 5 minut.
/// Selhání jednoho polleru neovlivní ostatní regiony.
///
/// Implementační poznámka: PollAsync je prázdný stub — konkrétní SQL logika přichází v F4-02/F4-03.
/// </summary>
public abstract class OrderPollerBase : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    protected readonly IServiceBusPublisher Publisher;
    protected readonly IPollWatermarkRepository WatermarkRepo;
    protected readonly IOrderSnapshotRepository SnapshotRepo;
    protected readonly IBridgeMappingRepository MappingRepo;
    protected readonly ISyncLogRepository SyncLog;
    protected readonly IBridgeMetrics Metrics;
    protected readonly ILogger Logger;

    /// <summary>Region kód: 'cz', 'pl', 'hu', 'us'</summary>
    protected abstract string Region { get; }

    /// <summary>Klíč do bridge_poll_watermark: 'tbl_order_cz' atd.</summary>
    protected abstract string PollTarget { get; }

    /// <summary>Connection factory pro čtení z tbl_order v příslušné regionální Partner DB.</summary>
    protected readonly IPartnerDbConnectionFactory PartnerDbFactory;

    protected OrderPollerBase(
        IServiceBusPublisher publisher,
        IPollWatermarkRepository watermarkRepo,
        IOrderSnapshotRepository snapshotRepo,
        IBridgeMappingRepository mappingRepo,
        ISyncLogRepository syncLog,
        IBridgeMetrics metrics,
        IPartnerDbConnectionFactory partnerDbFactory,
        ILogger logger)
    {
        Publisher = publisher;
        WatermarkRepo = watermarkRepo;
        SnapshotRepo = snapshotRepo;
        MappingRepo = mappingRepo;
        SyncLog = syncLog;
        Metrics = metrics;
        PartnerDbFactory = partnerDbFactory;
        Logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("OrderPoller {Region}: start, čekám {Delay}s před prvním pollem",
            Region, StartupDelay.TotalSeconds);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await PollAsync(stoppingToken);
                sw.Stop();
                Logger.LogDebug("OrderPoller {Region}: poll dokončen za {ElapsedMs}ms", Region, sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Logger.LogError(ex, "OrderPoller {Region}: neočekávaná chyba při pollování (bude opakováno za {IntervalMin} minut)",
                    Region, PollInterval.TotalMinutes);
                Metrics.TrackSyncError("order_poll", Region, "POLL_ERROR");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Logger.LogInformation("OrderPoller {Region}: zastaven", Region);
    }

    /// <summary>
    /// Implementace konkrétního poll cyklu — přepsána v F4-02/F4-03.
    /// Odpovědnost:
    ///   1. Načíst watermark z WatermarkRepo
    ///   2. SELECT nových objednávek z tbl_order (order_date_start > watermark) z PartnerDbFactory
    ///   3. Načíst snapshoty z SnapshotRepo, porovnat MD5 hashe
    ///   4. Publikovat události přes Publisher (bridge.order.created/state-changed/completed/cancelled)
    ///   5. Aktualizovat watermark a snapshoty
    ///   6. Zapsat do SyncLog
    /// </summary>
    protected abstract Task PollAsync(CancellationToken ct);
}
