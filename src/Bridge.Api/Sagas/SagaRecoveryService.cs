using Bridge.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bridge.Api.Sagas;

/// <summary>
/// Spouští se jednou při startu Bridge. Detekuje nedokončené region-change ságy
/// z bridge_sync_log a pokusí se je doběhnout nebo zkompenzovat.
///
/// Chyba v recovery nesmí zastavit start Bridge — logujeme a pokračujeme.
/// </summary>
public sealed class SagaRecoveryService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SagaRecoveryService> _logger;

    public SagaRecoveryService(
        IServiceScopeFactory scopeFactory,
        ILogger<SagaRecoveryService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // Recovery timeout: max 60s při startu — zabraňuje blokaci StartuAsync v edge case (stovky ság)
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(60);

    public async Task StartAsync(CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(RecoveryTimeout);
        // linkedCt se použije pro operace s timeoutem; stoppingToken pro rozlišení timeout vs. shutdown
        var linkedCt = cts.Token;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var syncLog = scope.ServiceProvider.GetRequiredService<IPartnerSyncLog>();
            var saga = scope.ServiceProvider.GetRequiredService<MoveClientToRegionSaga>();

            var pendingSagas = await syncLog.GetPendingSagasAsync(linkedCt);

            if (pendingSagas.Count == 0)
            {
                _logger.LogInformation("SagaRecovery: žádné nedokončené ságy nalezeny.");
                return;
            }

            _logger.LogWarning(
                "SagaRecovery: nalezeno {Count} nedokončených region-change ság — zahajuji recovery",
                pendingSagas.Count);

            foreach (var pendingSaga in pendingSagas)
            {
                if (linkedCt.IsCancellationRequested)
                    break;

                try
                {
                    await saga.RecoverAsync(pendingSaga, linkedCt);
                }
                catch (Exception ex)
                {
                    // Chyba v recovery jedné ságy nesmí zastavit recovery ostatních
                    _logger.LogError(ex,
                        "SagaRecovery: recovery selhala pro CompanyId={CompanyId} (created_at={CreatedAt})",
                        pendingSaga.CompanyId, pendingSaga.CreatedAt);
                }
            }

            _logger.LogInformation(
                "SagaRecovery: dokončeno ({Count} ság zpracováno)",
                pendingSagas.Count);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Timeout recovery (60s překročeno) — rozlišujeme timeout od normálního shutdown
            _logger.LogWarning(
                "SagaRecovery: timeout ({Timeout}s) — recovery přerušena. " +
                "Zbývající ságy budou zpracovány při příštím restartu Bridge.",
                (int)RecoveryTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            // Chyba v celém recovery (např. Azure SQL nedostupné) nesmí zastavit Bridge
            _logger.LogError(ex,
                "SagaRecovery: recovery selhala (celková chyba) — Bridge pokračuje bez recovery. " +
                "Nedokončené ságy budou zpracovány při příštím restartu.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
