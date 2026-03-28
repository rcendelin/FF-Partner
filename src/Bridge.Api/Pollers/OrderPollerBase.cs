using Bridge.Api.Telemetry;
using Bridge.Application.Interfaces;
using Bridge.Domain.Messages;
using Bridge.Domain.Models;
using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Bridge.Api.Pollers;

/// <summary>
/// Abstraktní základ pro 4 regionální Order pollery (CZ, PL, HU, US).
/// Každý poller běží nezávisle jako BackgroundService a spouští PollAsync každých 5 minut.
/// Selhání jednoho polleru neovlivní ostatní regiony.
///
/// Polling strategie (CLAUDE.md sekce 11):
///   Nové objednávky: watermark na order_date_start (unix timestamp)
///   Změny stavů: MD5 snapshot — hash z order_state|order_close|order_close_pay|order_automat_close|order_deactive
/// </summary>
public abstract class OrderPollerBase : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private const short OrderStateCancelled = 30;

    /// <summary>order_automat_close = -1 → GAIA processing error. Pouze logovat, nenotifikovat obchodníka (CLAUDE.md sekce 16).</summary>
    private const sbyte GaiaAutomatCloseError = -1;

    protected readonly IServiceBusPublisher Publisher;
    protected readonly IPollWatermarkRepository WatermarkRepo;
    protected readonly IOrderSnapshotRepository SnapshotRepo;
    protected readonly IBridgeMappingRepository MappingRepo;
    protected readonly IOrderPollingRepository OrderPolling;
    protected readonly ISyncLogRepository SyncLog;
    protected readonly IBridgeMetrics Metrics;
    protected readonly ILogger Logger;
    protected readonly IPartnerDbConnectionFactory PartnerDbFactory;

    /// <summary>Region kód: 'cz', 'pl', 'hu', 'us'</summary>
    protected abstract string Region { get; }

    /// <summary>Klíč do bridge_poll_watermark: 'tbl_order_cz' atd.</summary>
    protected abstract string PollTarget { get; }

    protected OrderPollerBase(
        IServiceBusPublisher publisher,
        IPollWatermarkRepository watermarkRepo,
        IOrderSnapshotRepository snapshotRepo,
        IBridgeMappingRepository mappingRepo,
        IOrderPollingRepository orderPolling,
        ISyncLogRepository syncLog,
        IBridgeMetrics metrics,
        IPartnerDbConnectionFactory partnerDbFactory,
        ILogger logger)
    {
        Publisher = publisher;
        WatermarkRepo = watermarkRepo;
        SnapshotRepo = snapshotRepo;
        MappingRepo = mappingRepo;
        OrderPolling = orderPolling;
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
    /// Jeden poll cyklus:
    ///   1. Načíst watermark a mapované klienty
    ///   2. Nové objednávky → publish OrderCreated/Completed/Cancelled
    ///   3. Změny stavů (MD5 snapshot) → publish OrderStateChanged/Completed/Cancelled
    ///   4. Aktualizovat watermark a snapshoty
    /// </summary>
    protected virtual async Task PollAsync(CancellationToken ct)
    {
        // 1. Watermark — inicializovat na 0 pokud záznam neexistuje
        var watermark = await WatermarkRepo.GetAsync(PollTarget, ct)
            ?? new PollWatermark
            {
                PollTarget = PollTarget,
                LastProcessedOrderDate = 0,
                LastProcessedId = 0,
                UpdatedAt = DateTime.UtcNow
            };

        // 2. Mapovaní klienti pro tento region (z Azure SQL)
        var clientIds = await MappingRepo.GetPartnerClientIdsForRegionAsync(Region, ct);
        if (clientIds.Count == 0)
        {
            Logger.LogDebug("OrderPoller {Region}: žádní mapovaní klienti, poll přeskočen", Region);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var newOrderCount = 0;
        var stateChangeCount = 0;
        var maxDateStart = watermark.LastProcessedOrderDate;

        // 3. Nové objednávky (watermark based)
        var newOrders = await OrderPolling.GetNewOrdersAsync(
            Region, clientIds, watermark.LastProcessedOrderDate, ct);

        foreach (var order in newOrders)
        {
            if (ct.IsCancellationRequested) break;

            var mapping = await MappingRepo.GetMappingByPartnerClientAsync(order.IdClient, Region, ct);
            if (mapping is null)
            {
                Logger.LogWarning("OrderPoller {Region}: id_client={ClientId} není v bridge_id_mapping (přeskočeno)", Region, order.IdClient);
                continue;
            }

            await PublishNewOrderEventsAsync(order, mapping, now, ct);
            newOrderCount++;

            if (order.OrderDateStart > maxDateStart)
                maxDateStart = order.OrderDateStart;
        }

        // 4. Change detection — MD5 snapshot pro všechny aktivní objednávky
        var activeOrders = await OrderPolling.GetActiveOrderStatesAsync(Region, clientIds, ct);
        var existingSnapshots = await SnapshotRepo.GetRegionSnapshotsAsync(Region, ct);
        var snapshotLookup = existingSnapshots.ToDictionary(s => s.OrderId, s => s.StateHash);

        var updatedSnapshots = new List<OrderSnapshot>(activeOrders.Count);

        foreach (var order in activeOrders)
        {
            if (ct.IsCancellationRequested) break;

            var hash = ComputeStateHash(order);
            var isNew = !snapshotLookup.TryGetValue(order.IdOrder, out var existingHash);

            // Stav se změnil — publikovat event (nové objednávky jsou zpracovány výše, snapshot-only check)
            if (!isNew && existingHash != hash)
            {
                var mapping = await MappingRepo.GetMappingByPartnerClientAsync(order.IdClient, Region, ct);
                if (mapping is not null)
                {
                    await PublishStateChangeEventsAsync(order, mapping, now, existingHash!, ct);
                    stateChangeCount++;
                }
            }

            updatedSnapshots.Add(new OrderSnapshot
            {
                PartnerRegion = Region,
                OrderId = order.IdOrder,
                StateHash = hash,
                LastChecked = DateTime.UtcNow
            });
        }

        // 5. Aktualizovat watermark (pouze pokud byly nové objednávky)
        if (maxDateStart > watermark.LastProcessedOrderDate)
        {
            await WatermarkRepo.UpsertAsync(new PollWatermark
            {
                PollTarget = PollTarget,
                LastProcessedOrderDate = maxDateStart,
                LastProcessedId = 0,
                UpdatedAt = DateTime.UtcNow
            }, ct);
        }

        // 6. Aktualizovat snapshoty
        if (updatedSnapshots.Count > 0)
        {
            await SnapshotRepo.BulkUpsertAsync(updatedSnapshots, ct);
        }

        // 7. Log úspěchu pokud bylo co zpracovat
        if (newOrderCount > 0 || stateChangeCount > 0)
        {
            Logger.LogInformation(
                "OrderPoller {Region}: nové={New}, změny stavů={Changes}, klientů={Clients}",
                Region, newOrderCount, stateChangeCount, clientIds.Count);

            await SyncLog.WriteAsync(new Application.Interfaces.SyncLogEntry
            {
                Operation = "order_poll",
                Status = "success",
                PartnerRegion = Region,
                Severity = "Info",
                PayloadJson = $"{{\"new\":{newOrderCount},\"stateChanges\":{stateChangeCount}}}"
            }, CancellationToken.None);
        }
    }

    // ── Event publishing ────────────────────────────────────────────────────────

    private async Task PublishNewOrderEventsAsync(
        TblOrderRow order, IdMappingRecord mapping, DateTimeOffset sentAt, CancellationToken ct)
    {
        var messageId = Guid.NewGuid().ToString();

        // Vždy publish OrderCreated pro nové objednávky
        try
        {
            await Publisher.PublishAsync("bridge.order.created", new OrderCreatedMessage
            {
                MessageId = messageId,
                SentAt = sentAt,
                PartnerRegion = Region,
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
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "OrderPoller {Region}: nepodařilo se publikovat OrderCreated pro idorder={OrderId}", Region, order.IdOrder);
        }

        // Pokud je zároveň zaplaceno — publish Completed
        if (order.OrderClosePay == 1)
        {
            await PublishOrderCompletedAsync(order, mapping, sentAt);
        }

        // Pokud je zároveň zrušeno — publish Cancelled
        if (order.OrderState == OrderStateCancelled)
        {
            await PublishOrderCancelledAsync(order, mapping, sentAt);
        }

        // F4-07: nová objednávka v GAIA error stavu — logovat Warning bez podmínky PreviousHash
        if (order.OrderAutomatClose == GaiaAutomatCloseError)
        {
            Logger.LogWarning(
                "OrderPoller {Region}: GAIA processing error u nové objednávky idorder={OrderId} (order_automat_close=-1)",
                Region, order.IdOrder);

            try
            {
                await SyncLog.WriteAsync(new Application.Interfaces.SyncLogEntry
                {
                    Operation = "gaia_processing_error",
                    Status = "warning",
                    PartnerRegion = Region,
                    PartnerClientId = order.IdClient,
                    Severity = "Warning",
                    PayloadJson = $"{{\"orderId\":{order.IdOrder},\"automatClose\":{order.OrderAutomatClose}}}"
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex,
                    "OrderPoller {Region}: nepodařilo se zapsat GAIA warning log pro novou objednávku idorder={OrderId}",
                    Region, order.IdOrder);
            }
        }
    }

    private async Task PublishStateChangeEventsAsync(
        TblOrderRow order, IdMappingRecord mapping, DateTimeOffset sentAt,
        string previousHash, CancellationToken ct)
    {
        try
        {
            // Prioritní events: Completed > Cancelled > StateChanged
            if (order.OrderClosePay == 1)
            {
                await PublishOrderCompletedAsync(order, mapping, sentAt);
            }
            else if (order.OrderState == OrderStateCancelled)
            {
                await PublishOrderCancelledAsync(order, mapping, sentAt);
            }
            else
            {
                var messageId = Guid.NewGuid().ToString();
                await Publisher.PublishAsync("bridge.order.state-changed", new OrderStateChangedMessage
                {
                    MessageId = messageId,
                    SentAt = sentAt,
                    PartnerRegion = Region,
                    PartnerOrderId = order.IdOrder,
                    FfCompanyId = mapping.FfCompanyId,
                    PartnerClientId = order.IdClient,
                    OrderState = order.OrderState,
                    OrderClose = order.OrderClose,
                    OrderClosePay = order.OrderClosePay,
                    OrderAutomatClose = order.OrderAutomatClose,
                    OrderDeactive = order.OrderDeactive
                }, messageId, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "OrderPoller {Region}: nepodařilo se publikovat state-change event pro idorder={OrderId}", Region, order.IdOrder);
        }

        // F4-07: GAIA error detection — logovat Warning při PŘECHODU na order_automat_close = -1.
        // Podmínka: nový stav má automat_close=-1 A předchozí hash nezakódoval automat_close=-1.
        // Prevence duplicitního logu pokud jiné pole změní hash zatímco GAIA zůstává v chybě.
        // Nenotifikovat obchodníka (CLAUDE.md sekce 16). Bez výjimky při selhání logu.
        if (order.OrderAutomatClose == GaiaAutomatCloseError
            && !PreviousHashHadGaiaError(previousHash, order))
        {
            // Logujeme pouze orderId — idClient je uložen v SyncLogEntry, ne v App Insights logu
            Logger.LogWarning(
                "OrderPoller {Region}: GAIA processing error pro idorder={OrderId} (order_automat_close=-1)",
                Region, order.IdOrder);

            try
            {
                await SyncLog.WriteAsync(new Application.Interfaces.SyncLogEntry
                {
                    Operation = "gaia_processing_error",
                    Status = "warning",
                    PartnerRegion = Region,
                    PartnerClientId = order.IdClient,
                    Severity = "Warning",
                    PayloadJson = $"{{\"orderId\":{order.IdOrder},\"automatClose\":{order.OrderAutomatClose}}}"
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex,
                    "OrderPoller {Region}: nepodařilo se zapsat GAIA warning log pro idorder={OrderId}",
                    Region, order.IdOrder);
            }
        }
    }

    private async Task PublishOrderCompletedAsync(TblOrderRow order, IdMappingRecord mapping, DateTimeOffset sentAt)
    {
        var messageId = Guid.NewGuid().ToString();
        try
        {
            await Publisher.PublishAsync("bridge.order.completed", new OrderCompletedMessage
            {
                MessageId = messageId,
                SentAt = sentAt,
                PartnerRegion = Region,
                PartnerOrderId = order.IdOrder,
                FfCompanyId = mapping.FfCompanyId,
                PartnerClientId = order.IdClient,
                VehicleVin = order.OrderCarVin,
                VehicleMark = order.OrderCarMark,
                VehicleModel = order.OrderCarModel,
                VehicleCategory = order.OrderCarCategory,
                VehiclePowerHp = order.OrderCarPowerHp
            }, messageId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "OrderPoller {Region}: nepodařilo se publikovat OrderCompleted pro idorder={OrderId}", Region, order.IdOrder);
        }
    }

    private async Task PublishOrderCancelledAsync(TblOrderRow order, IdMappingRecord mapping, DateTimeOffset sentAt)
    {
        var messageId = Guid.NewGuid().ToString();
        try
        {
            await Publisher.PublishAsync("bridge.order.cancelled", new OrderCancelledMessage
            {
                MessageId = messageId,
                SentAt = sentAt,
                PartnerRegion = Region,
                PartnerOrderId = order.IdOrder,
                FfCompanyId = mapping.FfCompanyId,
                PartnerClientId = order.IdClient
            }, messageId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "OrderPoller {Region}: nepodařilo se publikovat OrderCancelled pro idorder={OrderId}", Region, order.IdOrder);
        }
    }

    // ── MD5 state hash ─────────────────────────────────────────────────────────

    /// <summary>
    /// Vypočítá MD5 hash stavu objednávky pro change detection.
    /// Hash = MD5(order_state|order_close|order_close_pay|order_automat_close|order_deactive)
    /// Vrátí lowercase hex string, 32 znaků.
    /// </summary>
    public static string ComputeStateHash(TblOrderRow order)
    {
        var input = $"{order.OrderState}|{order.OrderClose}|{order.OrderClosePay}|{order.OrderAutomatClose}|{order.OrderDeactive}";
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Zjistí, zda předchozí snapshot hash zakódoval GAIA error (automat_close = -1).
    /// Porovnává předchozí hash s hashem aktuálního řádku ale se substituovanou hodnotou automat_close = -1.
    /// Pokud se shodují, předchozí stav TAKÉ měl automat_close = -1 → nepublikovat duplicitní Warning.
    /// </summary>
    public static bool PreviousHashHadGaiaError(string previousHash, TblOrderRow currentOrder)
    {
        // Vytvoříme "testovací" řádek shodný s aktuálním, ale s automat_close = -1
        // Pokud výsledný hash == previousHash, pak předchozí stav měl stejné pole = -1
        var testRow = new TblOrderRow
        {
            IdOrder = currentOrder.IdOrder,
            IdClient = currentOrder.IdClient,
            OrderDateStart = currentOrder.OrderDateStart,
            OrderState = currentOrder.OrderState,
            OrderClose = currentOrder.OrderClose,
            OrderClosePay = currentOrder.OrderClosePay,
            OrderAutomatClose = GaiaAutomatCloseError,  // substituujeme -1
            OrderDeactive = currentOrder.OrderDeactive
        };
        return ComputeStateHash(testRow) == previousHash;
    }
}
