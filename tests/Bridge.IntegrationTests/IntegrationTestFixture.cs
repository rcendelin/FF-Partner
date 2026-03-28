using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner;
using Bridge.Infrastructure.Partner.Repositories;
using Bridge.Infrastructure.Polling;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using MySqlConnector;
using System.Collections.Concurrent;

namespace Bridge.IntegrationTests;

/// <summary>
/// Sdílená fixture pro integrační testy — spravuje připojení k DB a cleanup.
///
/// Connection strings čte z env proměnných (NIKDY z konfiguračního souboru ani kódu):
///   BRIDGE_IT_PARTNER_CZ_CONN   — Partner3 CZ MySQL
///   BRIDGE_IT_PARTNER_PL_CONN   — Partner3 PL MySQL
///   BRIDGE_IT_PARTNER_HU_CONN   — Partner3 HU MySQL (volitelné)
///   BRIDGE_IT_PARTNER_US_CONN   — Partner3 US MySQL (volitelné)
///   BRIDGE_IT_AZURE_SQL_CONN    — Azure SQL (bridge_id_mapping, bridge_sync_log)
///   BRIDGE_IT_GAIA_CONN         — GAIA MySQL (číselníky, read-only)
///
/// BEZPEČNOSTNÍ UPOZORNĚNÍ:
///   Env proměnné BRIDGE_IT_* smí ukazovat POUZE na testovací / staging databázi.
///   NIKDY nepřiřazovat produkční connection stringy těmto proměnným.
///   GoNoGo testy (Category=GoNoGo) jsou výjimkou — ty se záměrně spouštějí
///   proti produkčnímu prostředí pro validaci migrace, ale provádí pouze SELECT.
///
/// Testy používají unikátní Guid jako ff_company_id pro izolaci testovacích dat.
/// DisposeAsync provede cleanup všech testovacích záznamů (DELETE WHERE ... IN ...).
/// </summary>
public sealed class IntegrationTestFixture : IAsyncLifetime
{
    // ConcurrentBag je thread-safe — chrání před race condition při paralelním běhu testů
    private readonly ConcurrentBag<(Guid FfCompanyId, string Region)> _insertedClients = [];
    private readonly ConcurrentBag<Guid> _insertedMappings = [];
    private readonly ConcurrentBag<string> _insertedWatermarks = [];
    private readonly ConcurrentBag<long> _insertedSnapshotOrderIds = [];
    private readonly ConcurrentBag<string> _insertedSyncLogOperations = [];

    // ── Connection strings (null = není k dispozici) ───────────────────────

    public string? PartnerCzConn { get; } =
        Environment.GetEnvironmentVariable("BRIDGE_IT_PARTNER_CZ_CONN");

    public string? PartnerPlConn { get; } =
        Environment.GetEnvironmentVariable("BRIDGE_IT_PARTNER_PL_CONN");

    public string? PartnerHuConn { get; } =
        Environment.GetEnvironmentVariable("BRIDGE_IT_PARTNER_HU_CONN");

    public string? PartnerUsConn { get; } =
        Environment.GetEnvironmentVariable("BRIDGE_IT_PARTNER_US_CONN");

    public string? AzureSqlConn { get; } =
        Environment.GetEnvironmentVariable("BRIDGE_IT_AZURE_SQL_CONN");

    public string? GaiaConn { get; } =
        Environment.GetEnvironmentVariable("BRIDGE_IT_GAIA_CONN");

    // ── Dostupnost infrastruktury ──────────────────────────────────────────

    public bool HasPartnerCzDb => PartnerCzConn is not null;
    public bool HasPartnerPlDb => PartnerPlConn is not null;
    public bool HasPartnerDb => HasPartnerCzDb && HasPartnerPlDb;
    public bool HasAzureSql => AzureSqlConn is not null;
    public bool HasGaia => GaiaConn is not null;
    public bool HasAllInfra => HasPartnerDb && HasAzureSql && HasGaia;

    // ── Factory metody — Fáze 1 ────────────────────────────────────────────

    public IPartnerDbConnectionFactory CreatePartnerFactory()
    {
        var connStrings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (PartnerCzConn is not null) connStrings["cz"] = PartnerCzConn;
        if (PartnerPlConn is not null) connStrings["pl"] = PartnerPlConn;
        if (PartnerHuConn is not null) connStrings["hu"] = PartnerHuConn;
        if (PartnerUsConn is not null) connStrings["us"] = PartnerUsConn;

        return new PartnerDbConnectionFactory(connStrings);
    }

    public IPartnerClientRepository CreatePartnerClientRepository()
        => new PartnerClientRepository(CreatePartnerFactory());

    public BridgeMappingRepository CreateMappingRepository()
    {
        if (AzureSqlConn is null)
            throw new InvalidOperationException("BRIDGE_IT_AZURE_SQL_CONN není nastavena.");

        return new BridgeMappingRepository(AzureSqlConn, new MemoryCache(new MemoryCacheOptions()));
    }

    // ── Factory metody — Fáze 4 ────────────────────────────────────────────

    public PollWatermarkRepository CreateWatermarkRepository()
    {
        if (AzureSqlConn is null)
            throw new InvalidOperationException("BRIDGE_IT_AZURE_SQL_CONN není nastavena.");

        return new PollWatermarkRepository(AzureSqlConn);
    }

    public OrderSnapshotRepository CreateSnapshotRepository()
    {
        if (AzureSqlConn is null)
            throw new InvalidOperationException("BRIDGE_IT_AZURE_SQL_CONN není nastavena.");

        return new OrderSnapshotRepository(AzureSqlConn);
    }

    public BridgeSyncLogRepository CreateSyncLogRepository()
    {
        if (AzureSqlConn is null)
            throw new InvalidOperationException("BRIDGE_IT_AZURE_SQL_CONN není nastavena.");

        return new BridgeSyncLogRepository(AzureSqlConn);
    }

    public OrderPollingRepository CreateOrderPollingRepository()
        => new OrderPollingRepository(CreatePartnerFactory());

    // ── Testovací data — registrace pro cleanup ────────────────────────────

    /// <summary>
    /// Zaregistruje ff_company_id pro cleanup po testu.
    /// Volat vždy PŘED každým INSERT do tbl_client — aby cleanup proběhl i při selhání testu.
    /// </summary>
    public void TrackClient(Guid ffCompanyId, string region)
        => _insertedClients.Add((ffCompanyId, region));

    /// <summary>
    /// Zaregistruje ff_company_id pro cleanup v bridge_id_mapping.
    /// </summary>
    public void TrackMapping(Guid ffCompanyId)
        => _insertedMappings.Add(ffCompanyId);

    /// <summary>
    /// Zaregistruje poll_target pro cleanup v bridge_poll_watermark.
    /// Volat PŘED každým UpsertAsync — aby cleanup proběhl i při selhání testu.
    /// </summary>
    public void TrackWatermark(string pollTarget)
        => _insertedWatermarks.Add(pollTarget);

    /// <summary>
    /// Zaregistruje order_id pro cleanup v bridge_order_snapshot.
    /// Testovací order IDs musí být záporná čísla (nezkolizují s reálnými tbl_order záznamy).
    /// </summary>
    public void TrackSnapshot(long orderId)
        => _insertedSnapshotOrderIds.Add(orderId);

    /// <summary>
    /// Zaregistruje operation name pro cleanup v bridge_sync_log.
    /// Používat jen pro testovací operace s unikátním prefixem (např. "it_backfill_xxxx").
    /// </summary>
    public void TrackSyncLogOperation(string operation)
        => _insertedSyncLogOperations.Add(operation);

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Cleanup tbl_client — DELETE testovacích záznamů per region
        // Každý cleanup blok je zabalený v try/catch — selhání jednoho bloku nezastaví ostatní
        if (HasPartnerCzDb || HasPartnerPlDb)
        {
            var byRegion = _insertedClients
                .GroupBy(x => x.Region, StringComparer.OrdinalIgnoreCase);

            foreach (var group in byRegion)
            {
                var region = group.Key;
                var conn = region switch
                {
                    "cz" => PartnerCzConn,
                    "pl" => PartnerPlConn,
                    "hu" => PartnerHuConn,
                    "us" => PartnerUsConn,
                    _ => null
                };

                if (conn is null) continue;

                var ids = group.Select(x => x.FfCompanyId.ToString()).Distinct().ToArray();
                if (ids.Length == 0) continue;

                try
                {
                    await using var mysqlConn = new MySqlConnection(conn);
                    await mysqlConn.OpenAsync();

                    // Parametrizovaný DELETE — placeholdery z indexů (ne z uživatelských dat)
                    var placeholders = string.Join(",", ids.Select((_, i) => $"@id{i}"));
                    var sql = $"DELETE FROM tbl_client WHERE ff_company_id IN ({placeholders})";
                    var param = new DynamicParameters();
                    for (var i = 0; i < ids.Length; i++)
                        param.Add($"id{i}", ids[i]);

                    await mysqlConn.ExecuteAsync(sql, param);
                }
                catch (Exception ex)
                {
                    // Cleanup selhání neblokuje ostatní regiony — logovat na stderr
                    await Console.Error.WriteLineAsync(
                        $"[IntegrationTestFixture] Cleanup selhal pro region '{region}': {ex.Message}. " +
                        $"Testovací data mohou zůstat v DB pro ff_company_id: {string.Join(", ", ids)}");
                }
            }
        }

        // Azure SQL cleanup — každý blok má vlastní connection (M3: prevence kaskádového selhání)
        if (HasAzureSql)
        {
            // Cleanup bridge_id_mapping
            if (!_insertedMappings.IsEmpty)
            {
                var mappingIds = _insertedMappings.Distinct().ToArray();
                try
                {
                    await using var conn = new SqlConnection(AzureSqlConn);
                    await conn.OpenAsync();
                    var placeholders = string.Join(",", mappingIds.Select((_, i) => $"@id{i}"));
                    var sql = $"DELETE FROM bridge_id_mapping WHERE ff_company_id IN ({placeholders})";
                    var param = new DynamicParameters();
                    for (var i = 0; i < mappingIds.Length; i++)
                        param.Add($"id{i}", mappingIds[i]);
                    await conn.ExecuteAsync(sql, param);
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync(
                        $"[IntegrationTestFixture] Cleanup bridge_id_mapping selhal: {ex.Message}.");
                }
            }

            // Cleanup bridge_poll_watermark
            if (!_insertedWatermarks.IsEmpty)
            {
                var targets = _insertedWatermarks.Distinct().ToArray();
                try
                {
                    await using var conn = new SqlConnection(AzureSqlConn);
                    await conn.OpenAsync();
                    var placeholders = string.Join(",", targets.Select((_, i) => $"@t{i}"));
                    var sql = $"DELETE FROM bridge_poll_watermark WHERE poll_target IN ({placeholders})";
                    var param = new DynamicParameters();
                    for (var i = 0; i < targets.Length; i++)
                        param.Add($"t{i}", targets[i]);
                    await conn.ExecuteAsync(sql, param);
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync(
                        $"[IntegrationTestFixture] Cleanup bridge_poll_watermark selhal: {ex.Message}.");
                }
            }

            // Cleanup bridge_order_snapshot — záporná order_id jsou vždy testovací
            if (!_insertedSnapshotOrderIds.IsEmpty)
            {
                var orderIds = _insertedSnapshotOrderIds.Distinct().ToArray();
                try
                {
                    await using var conn = new SqlConnection(AzureSqlConn);
                    await conn.OpenAsync();
                    var placeholders = string.Join(",", orderIds.Select((_, i) => $"@o{i}"));
                    var sql = $"DELETE FROM bridge_order_snapshot WHERE order_id IN ({placeholders})";
                    var param = new DynamicParameters();
                    for (var i = 0; i < orderIds.Length; i++)
                        param.Add($"o{i}", orderIds[i]);
                    await conn.ExecuteAsync(sql, param);
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync(
                        $"[IntegrationTestFixture] Cleanup bridge_order_snapshot selhal: {ex.Message}.");
                }
            }

            // Cleanup bridge_sync_log — pouze pro testovací operace (unikátní prefixované názvy)
            if (!_insertedSyncLogOperations.IsEmpty)
            {
                var operations = _insertedSyncLogOperations.Distinct().ToArray();
                try
                {
                    await using var conn = new SqlConnection(AzureSqlConn);
                    await conn.OpenAsync();
                    var placeholders = string.Join(",", operations.Select((_, i) => $"@op{i}"));
                    var sql = $"DELETE FROM bridge_sync_log WHERE operation IN ({placeholders})";
                    var param = new DynamicParameters();
                    for (var i = 0; i < operations.Length; i++)
                        param.Add($"op{i}", operations[i]);
                    await conn.ExecuteAsync(sql, param);
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync(
                        $"[IntegrationTestFixture] Cleanup bridge_sync_log selhal: {ex.Message}.");
                }
            }
        }
    }
}
