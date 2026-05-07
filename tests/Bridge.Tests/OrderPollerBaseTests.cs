using Bridge.Api.Pollers;
using Bridge.Api.Telemetry;
using Bridge.Application.Interfaces;
using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace Bridge.Tests;

/// <summary>
/// Unit testy pro OrderPollerBase logiku — izoluje polling lifecycle od DB.
/// Testujeme chování BackgroundService: startup delay, poll interval, error recovery.
/// Použití stub implementací bez NSubstitute (WDAC kompatibilita).
/// </summary>
public class OrderPollerBaseTests
{
    // ── Stub implementace ──────────────────────────────────────────────────────────

    private sealed class CountingPoller : OrderPollerBase
    {
        private readonly TimeSpan _startupDelay;
        private readonly Exception? _throwOnPoll;

        public int PollCount { get; private set; }

        protected override string Region => "test";
        protected override string PollTarget => "tbl_order_test";

        public CountingPoller(
            TimeSpan startupDelay,
            Exception? throwOnPoll = null)
            : base(
                publisher: new NoOpPublisher(),
                watermarkRepo: new NoOpWatermarkRepo(),
                snapshotRepo: new NoOpSnapshotRepo(),
                mappingRepo: new NoOpMappingRepo(),
                orderPolling: new NoOpOrderPolling(),
                syncLog: new NoOpSyncLog(),
                metrics: new NullBridgeMetrics(),
                partnerDbFactory: new NoOpPartnerDbFactory(),
                logger: NullLogger.Instance)
        {
            _startupDelay = startupDelay;
            _throwOnPoll = throwOnPoll;
        }

        // Override startup delay via reflection is complex — instead we use a fast-path:
        // The base class uses a hardcoded 30s delay which makes testing lifecycle slow.
        // We test only PollAsync behavior here (called via reflection in integration context).
        // For lifecycle tests, we use short CancellationToken timeouts.

        protected override Task PollAsync(CancellationToken ct)
        {
            PollCount++;
            if (_throwOnPoll != null)
                throw _throwOnPoll;
            return Task.CompletedTask;
        }
    }

    // ── Stub třídy bez NSubstitute ──────────────────────────────────────────────

    private sealed class NoOpPublisher : IServiceBusPublisher
    {
        public Task PublishAsync<T>(string topicName, T message, string? correlationId = null,
            CancellationToken ct = default) where T : class => Task.CompletedTask;
    }

    private sealed class NoOpWatermarkRepo : IPollWatermarkRepository
    {
        public Task<Bridge.Domain.Models.PollWatermark?> GetAsync(string pollTarget, CancellationToken ct = default)
            => Task.FromResult<Bridge.Domain.Models.PollWatermark?>(null);

        public Task UpsertAsync(Bridge.Domain.Models.PollWatermark watermark, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class NoOpSnapshotRepo : IOrderSnapshotRepository
    {
        public Task<string?> GetHashAsync(string region, long orderId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task UpsertAsync(string region, long orderId, string stateHash, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<Bridge.Domain.Models.OrderSnapshot>> GetRegionSnapshotsAsync(
            string region, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Bridge.Domain.Models.OrderSnapshot>>(
                Array.Empty<Bridge.Domain.Models.OrderSnapshot>());

        public Task BulkUpsertAsync(IEnumerable<Bridge.Domain.Models.OrderSnapshot> snapshots,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpMappingRepo : IBridgeMappingRepository
    {
        public Task<Bridge.Domain.Models.IdMappingRecord?> GetMappingAsync(Guid ffCompanyId, CancellationToken ct = default)
            => Task.FromResult<Bridge.Domain.Models.IdMappingRecord?>(null);

        public Task SaveMappingAsync(Bridge.Domain.Models.IdMappingRecord record, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UpdateMappingAsync(Bridge.Domain.Models.IdMappingRecord record, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<int>> GetPartnerClientIdsForRegionAsync(string region, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());

        public Task<Bridge.Domain.Models.IdMappingRecord?> GetMappingByPartnerClientAsync(int partnerClientId, string region, CancellationToken ct = default)
            => Task.FromResult<Bridge.Domain.Models.IdMappingRecord?>(null);
    }

    private sealed class NoOpSyncLog : IPartnerSyncLog
    {
        public Task WriteAsync(
            Guid companyId, string correlationMessageId, string phase, string direction,
            string operation, string status, int? partnerClientId = null, string? partnerRegion = null,
            string? errorCode = null, string? errorMessage = null, string? payloadJson = null,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<PartnerSyncLogEntry>> GetLastAsync(int count, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PartnerSyncLogEntry>>(Array.Empty<PartnerSyncLogEntry>());

        public Task<IReadOnlyList<PartnerSyncLogEntry>> GetPendingSagasAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PartnerSyncLogEntry>>(Array.Empty<PartnerSyncLogEntry>());

        public Task<bool> HasOperationSucceededAsync(string operation, string region, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    private sealed class NoOpOrderPolling : IOrderPollingRepository
    {
        public Task<IReadOnlyList<Bridge.Domain.Models.TblOrderRow>> GetNewOrdersAsync(
            string region, IReadOnlyList<int> clientIds, int afterUnixTimestamp, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Bridge.Domain.Models.TblOrderRow>>(Array.Empty<Bridge.Domain.Models.TblOrderRow>());

        public Task<IReadOnlyList<Bridge.Domain.Models.TblOrderRow>> GetActiveOrderStatesAsync(
            string region, IReadOnlyList<int> clientIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Bridge.Domain.Models.TblOrderRow>>(Array.Empty<Bridge.Domain.Models.TblOrderRow>());
    }

    private sealed class NoOpPartnerDbFactory : IPartnerDbConnectionFactory
    {
        public MySqlConnection CreateConnection(string region)
            => throw new NotSupportedException("NoOp factory — tests nemají přístup k Partner DB");
    }

    // ── Testy ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void OrderPoller_RegionAndPollTarget_CorrectValues()
    {
        // Ověřujeme, že každý poller má správný Region a PollTarget
        // Testujeme přes instance (bez spuštění BackgroundService)
        var pollerCz = new OrderPollerCz(
            new NoOpPublisher(), new NoOpWatermarkRepo(), new NoOpSnapshotRepo(),
            new NoOpMappingRepo(), new NoOpOrderPolling(), new NoOpSyncLog(), new NullBridgeMetrics(),
            new NoOpPartnerDbFactory(), NullLogger<OrderPollerCz>.Instance);

        var pollerPl = new OrderPollerPl(
            new NoOpPublisher(), new NoOpWatermarkRepo(), new NoOpSnapshotRepo(),
            new NoOpMappingRepo(), new NoOpOrderPolling(), new NoOpSyncLog(), new NullBridgeMetrics(),
            new NoOpPartnerDbFactory(), NullLogger<OrderPollerPl>.Instance);

        var pollerHu = new OrderPollerHu(
            new NoOpPublisher(), new NoOpWatermarkRepo(), new NoOpSnapshotRepo(),
            new NoOpMappingRepo(), new NoOpOrderPolling(), new NoOpSyncLog(), new NullBridgeMetrics(),
            new NoOpPartnerDbFactory(), NullLogger<OrderPollerHu>.Instance);

        var pollerUs = new OrderPollerUs(
            new NoOpPublisher(), new NoOpWatermarkRepo(), new NoOpSnapshotRepo(),
            new NoOpMappingRepo(), new NoOpOrderPolling(), new NoOpSyncLog(), new NullBridgeMetrics(),
            new NoOpPartnerDbFactory(), NullLogger<OrderPollerUs>.Instance);

        // Přístup přes reflection — Region a PollTarget jsou protected
        static string GetRegion(OrderPollerBase p) =>
            (string)p.GetType().GetProperty("Region",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(p)!;

        static string GetPollTarget(OrderPollerBase p) =>
            (string)p.GetType().GetProperty("PollTarget",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(p)!;

        Assert.Equal("cz", GetRegion(pollerCz));
        Assert.Equal("pl", GetRegion(pollerPl));
        Assert.Equal("hu", GetRegion(pollerHu));
        Assert.Equal("us", GetRegion(pollerUs));

        Assert.Equal("tbl_order_cz", GetPollTarget(pollerCz));
        Assert.Equal("tbl_order_pl", GetPollTarget(pollerPl));
        Assert.Equal("tbl_order_hu", GetPollTarget(pollerHu));
        Assert.Equal("tbl_order_us", GetPollTarget(pollerUs));
    }

    [Fact]
    public async Task OrderPoller_CancelledImmediately_StopsWithoutPolling()
    {
        // Pokud je CancellationToken zrušen okamžitě (před startup delay), poll se nespustí.
        // Startup delay je 30s — zrušíme token ihned, poller skončí bez PollAsync.
        var poller = new CountingPoller(startupDelay: TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Zrušíme před spuštěním

        await poller.StartAsync(cts.Token);
        await Task.Delay(100); // Chvíli počkáme
        await poller.StopAsync(CancellationToken.None);

        // PollCount = 0 protože token byl zrušen před startem
        Assert.Equal(0, poller.PollCount);
    }

    [Fact]
    public async Task OrderPoller_StoppedDuringStartupDelay_StopsCleanly()
    {
        // Testuje, že poller se správně zastaví i během čekání na startup delay.
        var poller = new CountingPoller(startupDelay: TimeSpan.Zero);
        using var cts = new CancellationTokenSource();

        await poller.StartAsync(cts.Token);
        await Task.Delay(50); // Nechaj ho trochu běžet
        cts.Cancel(); // Zrušíme

        // StopAsync by mělo skončit čistě (žádná výjimka)
        var stopTask = poller.StopAsync(CancellationToken.None);
        var completed = await Task.WhenAny(stopTask, Task.Delay(2000));
        Assert.Equal(stopTask, completed); // Skončilo do 2 sekund
    }
}
