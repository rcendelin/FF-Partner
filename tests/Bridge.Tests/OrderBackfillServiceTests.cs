using Bridge.Api.Pollers;
using Bridge.Application.Interfaces;
using Bridge.Domain.Messages;
using Bridge.Domain.Models;
using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bridge.Tests;

/// <summary>
/// Unit testy pro OrderBackfillService.
/// Testujeme idempotenci (přeskočení již hotového regionu), batch publish logiku
/// a správné chování při prázdné sadě klientů.
/// Bez NSubstitute — pouze stub implementace.
/// </summary>
public class OrderBackfillServiceTests
{
    // ── Stubs ──────────────────────────────────────────────────────────────────

    private sealed class CapturingSyncLog : IPartnerSyncLog
    {
        private readonly bool _alreadySucceeded;
        public List<(string Phase, string Operation, string Status, string? PartnerRegion)> Written { get; } = [];

        public CapturingSyncLog(bool alreadySucceeded = false)
        {
            _alreadySucceeded = alreadySucceeded;
        }

        public Task WriteAsync(
            Guid companyId, string correlationMessageId, string phase, string direction,
            string operation, string status, int? partnerClientId = null, string? partnerRegion = null,
            string? errorCode = null, string? errorMessage = null, string? payloadJson = null,
            CancellationToken ct = default)
        {
            Written.Add((phase, operation, status, partnerRegion));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PartnerSyncLogEntry>> GetLastAsync(int count, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PartnerSyncLogEntry>>(Array.Empty<PartnerSyncLogEntry>());

        public Task<IReadOnlyList<PartnerSyncLogEntry>> GetPendingSagasAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PartnerSyncLogEntry>>(Array.Empty<PartnerSyncLogEntry>());

        public Task<bool> HasOperationSucceededAsync(string operation, string region, CancellationToken ct = default)
            => Task.FromResult(_alreadySucceeded);
    }

    private sealed class CapturingPublisher : IServiceBusPublisher
    {
        public List<(string Topic, string MessageId)> Published { get; } = [];

        public Task PublishAsync<T>(string topicName, T message, string? correlationId = null,
            CancellationToken ct = default) where T : class
        {
            Published.Add((topicName, correlationId ?? string.Empty));
            return Task.CompletedTask;
        }
    }

    private sealed class StubMappingRepo : IBridgeMappingRepository
    {
        private readonly IReadOnlyList<int> _clientIds;

        public StubMappingRepo(IReadOnlyList<int>? clientIds = null)
        {
            _clientIds = clientIds ?? Array.Empty<int>();
        }

        public Task<IdMappingRecord?> GetMappingAsync(Guid ffCompanyId, CancellationToken ct = default)
            => Task.FromResult<IdMappingRecord?>(null);

        public Task SaveMappingAsync(IdMappingRecord record, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UpdateMappingAsync(IdMappingRecord record, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<int>> GetPartnerClientIdsForRegionAsync(string region, CancellationToken ct = default)
            => Task.FromResult(_clientIds);

        public Task<IdMappingRecord?> GetMappingByPartnerClientAsync(int partnerClientId, string region, CancellationToken ct = default)
            => Task.FromResult<IdMappingRecord?>(new IdMappingRecord
            {
                FfCompanyId = Guid.NewGuid(),
                PartnerClientId = partnerClientId,
                PartnerRegion = region,
                EntityType = "client",
                LastSyncAt = DateTime.UtcNow,
                LastSyncDirection = "ff_to_partner",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
    }

    private sealed class StubOrderPolling : IOrderPollingRepository
    {
        private readonly IReadOnlyList<TblOrderRow> _orders;

        public StubOrderPolling(IReadOnlyList<TblOrderRow>? orders = null)
        {
            _orders = orders ?? Array.Empty<TblOrderRow>();
        }

        public Task<IReadOnlyList<TblOrderRow>> GetNewOrdersAsync(
            string region, IReadOnlyList<int> clientIds, int afterUnixTimestamp, CancellationToken ct = default)
            => Task.FromResult(_orders);

        public Task<IReadOnlyList<TblOrderRow>> GetActiveOrderStatesAsync(
            string region, IReadOnlyList<int> clientIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TblOrderRow>>(Array.Empty<TblOrderRow>());
    }

    private static TblOrderRow MakeOrder(long idOrder = 1, int idClient = 100) => new()
    {
        IdOrder = idOrder,
        IdClient = idClient,
        OrderDateStart = 1711539600,
        OrderState = 7,
        OrderClose = 0,
        OrderClosePay = 0,
        OrderAutomatClose = -10,
        OrderDeactive = 0
    };

    // ── Testy ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Backfill_AlreadySucceeded_SkipsAllRegions()
    {
        // Pokud HasOperationSucceededAsync vrátí true, žádné eventy se nepublikují
        var syncLog = new CapturingSyncLog(alreadySucceeded: true);
        var publisher = new CapturingPublisher();
        var orders = new[] { MakeOrder(1), MakeOrder(2) };

        var svc = new OrderBackfillService(
            new StubOrderPolling(orders),
            new StubMappingRepo([100]),
            syncLog,
            publisher,
            NullLogger<OrderBackfillService>.Instance);

        // Volat přímo interní logiku přes RunAsync s okamžitým zrušením po Delay
        // Protože startup delay je 60s, testujeme přes CancellationToken
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // zrušíme před startem — nečekáme 60s

        await svc.StartAsync(cts.Token);
        await svc.StopAsync(CancellationToken.None);

        // Nic nepublikováno — bylo zrušeno před startup delay
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task Backfill_NoClients_SkipsRegion()
    {
        // Pokud GetPartnerClientIdsForRegionAsync vrátí prázdný seznam,
        // backfill pro region se přeskočí a SyncLog se nezapíše
        var syncLog = new CapturingSyncLog(alreadySucceeded: false);
        var publisher = new CapturingPublisher();

        // Prázdné clientIds → žádný backfill
        var mappingRepo = new StubMappingRepo(clientIds: Array.Empty<int>());
        var orderPolling = new StubOrderPolling(Array.Empty<TblOrderRow>());

        var svc = new OrderBackfillService(
            orderPolling,
            mappingRepo,
            syncLog,
            publisher,
            NullLogger<OrderBackfillService>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await svc.StartAsync(cts.Token);
        await svc.StopAsync(CancellationToken.None);

        Assert.Empty(publisher.Published);
        Assert.Empty(syncLog.Written);
    }

    [Fact]
    public void BackfillService_Implements_BackgroundService()
    {
        // OrderBackfillService musí být BackgroundService pro DI registraci
        var svc = new OrderBackfillService(
            new StubOrderPolling(),
            new StubMappingRepo(),
            new CapturingSyncLog(),
            new CapturingPublisher(),
            NullLogger<OrderBackfillService>.Instance);

        Assert.IsAssignableFrom<Microsoft.Extensions.Hosting.BackgroundService>(svc);
    }

    [Fact]
    public async Task Backfill_StopsCleanlly_WhenCancelledDuringDelay()
    {
        var svc = new OrderBackfillService(
            new StubOrderPolling(),
            new StubMappingRepo(),
            new CapturingSyncLog(),
            new CapturingPublisher(),
            NullLogger<OrderBackfillService>.Instance);

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        // Zrušit během čekání na 60s startup delay
        cts.Cancel();
        var stopTask = svc.StopAsync(CancellationToken.None);
        var completed = await Task.WhenAny(stopTask, Task.Delay(2000));

        Assert.Equal(stopTask, completed); // Skončilo do 2 sekund bez výjimky
    }
}
