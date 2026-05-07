using Bridge.Application.Interfaces;
using Bridge.Domain.Messages;
using Bridge.Infrastructure.Mapping;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bridge.Api.Pollers;

/// <summary>
/// Jednorázový backfill objednávek z posledních 12 měsíců při inicializaci Fáze 4.
/// Spustí se jednou 60 sekund po startu Bridge. Idempotentní — přeskočí region,
/// pro který již existuje záznam 'order_backfill' se status='success' v bridge_sync_log.
///
/// Publikuje eventy v batchích po 100 s pauzou 1s mezi batchi aby nepřetížil Service Bus.
/// CLAUDE.md sekce 11: Backfill při inicializaci Fáze 4.
/// </summary>
public sealed class OrderBackfillService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);
    private static readonly string[] Regions = ["cz", "pl", "hu", "us"];
    private const int BatchSize = 100;
    private static readonly TimeSpan BatchPause = TimeSpan.FromSeconds(1);

    private readonly IOrderPollingRepository _orderPolling;
    private readonly IBridgeMappingRepository _mappingRepo;
    private readonly IPartnerSyncLog _syncLog;
    private readonly IServiceBusPublisher _publisher;
    private readonly ILogger<OrderBackfillService> _logger;

    public OrderBackfillService(
        IOrderPollingRepository orderPolling,
        IBridgeMappingRepository mappingRepo,
        IPartnerSyncLog syncLog,
        IServiceBusPublisher publisher,
        ILogger<OrderBackfillService> logger)
    {
        _orderPolling = orderPolling;
        _mappingRepo = mappingRepo;
        _syncLog = syncLog;
        _publisher = publisher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "OrderBackfillService: start, čekám {Delay}s před zahájením backfillu",
            StartupDelay.TotalSeconds);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger.LogInformation("OrderBackfillService: zahajuji backfill objednávek za posledních 12 měsíců");

        // Cutoff = nyní - 12 měsíců jako Unix timestamp
        var cutoffUnix = (int)DateTimeOffset.UtcNow.AddMonths(-12).ToUnixTimeSeconds();

        foreach (var region in Regions)
        {
            if (stoppingToken.IsCancellationRequested) break;

            await RunRegionBackfillAsync(region, cutoffUnix, stoppingToken);
        }

        _logger.LogInformation("OrderBackfillService: backfill dokončen pro všechny regiony");
    }

    private async Task RunRegionBackfillAsync(string region, int cutoffUnix, CancellationToken ct)
    {
        try
        {
            // Idempotence — přeskočit pokud backfill pro tento region již proběhl úspěšně
            var alreadyDone = await _syncLog.HasOperationSucceededAsync("order_backfill", region, ct);
            if (alreadyDone)
            {
                _logger.LogInformation(
                    "OrderBackfillService [{Region}]: backfill již proběhl (idempotence), přeskočeno", region);
                return;
            }

            var clientIds = await _mappingRepo.GetPartnerClientIdsForRegionAsync(region, ct);
            if (clientIds.Count == 0)
            {
                _logger.LogInformation(
                    "OrderBackfillService [{Region}]: žádní mapovaní klienti — přeskočeno", region);
                return;
            }

            _logger.LogInformation(
                "OrderBackfillService [{Region}]: načítám objednávky od timestamp={Cutoff}, klientů={Count}",
                region, cutoffUnix, clientIds.Count);

            var orders = await _orderPolling.GetNewOrdersAsync(region, clientIds, cutoffUnix, ct);

            _logger.LogInformation(
                "OrderBackfillService [{Region}]: nalezeno {Total} objednávek, publikuji v batchích po {BatchSize}",
                region, orders.Count, BatchSize);

            var published = 0;
            var sentAt = DateTimeOffset.UtcNow;

            for (var i = 0; i < orders.Count; i += BatchSize)
            {
                if (ct.IsCancellationRequested) return;

                var batch = orders.Skip(i).Take(BatchSize).ToList();

                foreach (var order in batch)
                {
                    if (ct.IsCancellationRequested) return;

                    var mapping = await _mappingRepo.GetMappingByPartnerClientAsync(
                        order.IdClient, region, ct);
                    if (mapping is null) continue;

                    var messageId = Guid.NewGuid().ToString();
                    try
                    {
                        await _publisher.PublishAsync("bridge.order.created", new OrderCreatedMessage
                        {
                            MessageId = messageId,
                            SentAt = sentAt,
                            PartnerRegion = region,
                            PartnerOrderId = order.IdOrder,
                            FfCompanyId = mapping.FfCompanyId,
                            PartnerClientId = order.IdClient,
                            OrderDateStart = order.OrderDateStart,
                            OrderState = order.OrderState,
                            OrderPrice = order.OrderPrice,
                            VehicleVin = order.OrderCarVin,
                            VehicleMark = order.OrderCarMark,
                            VehicleModel = order.OrderCarModel,
                            VehicleType = order.OrderCarType,
                            VehicleCategory = order.OrderCarCategory,
                            VehiclePowerHp = order.OrderCarPowerHp
                        }, messageId, CancellationToken.None);

                        published++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "OrderBackfillService [{Region}]: chyba při publikování idorder={OrderId}",
                            region, order.IdOrder);
                    }
                }

                // Pauza mezi batchi
                if (i + BatchSize < orders.Count)
                {
                    try { await Task.Delay(BatchPause, ct); }
                    catch (OperationCanceledException) { return; }
                }
            }

            // Zapsat úspěšné dokončení backfillu pro region (idempotence key)
            await _syncLog.WriteAsync(
                companyId: Guid.Empty,
                correlationMessageId: $"backfill-{region}-{cutoffUnix}",
                phase: "BackfillCompleted",
                direction: "Internal",
                operation: "order_backfill",
                status: "Success",
                partnerRegion: region,
                payloadJson: $"{{\"total\":{orders.Count},\"published\":{published},\"cutoffUnix\":{cutoffUnix}}}",
                ct: CancellationToken.None);

            _logger.LogInformation(
                "OrderBackfillService [{Region}]: dokončeno, publikováno={Published}/{Total}",
                region, published, orders.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — tichý exit, BackgroundService framework zpracuje korektně
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "OrderBackfillService [{Region}]: neočekávaná chyba při backfillu (region přeskočen)",
                region);
        }
    }
}
