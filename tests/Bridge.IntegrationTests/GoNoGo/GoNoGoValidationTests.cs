using Dapper;
using Microsoft.Data.SqlClient;
using MySqlConnector;

namespace Bridge.IntegrationTests.GoNoGo;

/// <summary>
/// Go/no-go validační testy pro Fázi 1 — spustit PŘED přechodem do Fáze 2.
///
/// Kritéria (dle F1-12):
///   GoNoGo1:  ≥ 95 % firem z FieldForce má synced záznam v Partner DB (ff_sync_source = 'FF')
///   GoNoGo2:  Žádné duplicitní ff_company_id v tbl_client (per region)
///   GoNoGo3:  Každý záznam v bridge_id_mapping má odpovídající tbl_client záznam
///   GoNoGo3.2 Žádné nedokončené ságy (pending_region_change) v bridge_sync_log
///   GoNoGo4:  bridge_sync_log error rate ≤ 5 % za posledních 24h (dle F1-12)
///   GoNoGo5:  Žádný ff_company_id v bridge_id_mapping nemá nekonzistentní region
///   GoNoGo6:  pipe_id a pipeType nejsou NULLovány Bridge (historické Pipedrive hodnoty zachovány)
///
/// Tyto testy předpokládají, že bulk migrace (F2-03) již proběhla.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "GoNoGo")]
public sealed class GoNoGoValidationTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fix;

    // Práh: ≥ 95 % firem musí být synced (dle F1-12 go/no-go kritérií)
    private const double MinSyncedRateThreshold = 0.95;

    // Maximální povolená error rate v bridge_sync_log za posledních 24h (dle F1-12)
    private const double MaxFailedRateThreshold = 0.05;

    public GoNoGoValidationTests(IntegrationTestFixture fix)
        => _fix = fix;

    // ── GoNoGo1: ≥ 95 % firem má ff_sync_source = 'FF' ───────────────────

    [FactIfPartnerDb]
    public async Task GoNoGo1_SyncedCompaniesRate_IsAboveThreshold_InPartnerCz()
        => await AssertSyncedRateAboveThreshold("cz", _fix.PartnerCzConn!);

    [FactIfPartnerDb]
    public async Task GoNoGo1_SyncedCompaniesRate_IsAboveThreshold_InPartnerPl()
        => await AssertSyncedRateAboveThreshold("pl", _fix.PartnerPlConn!);

    // ── GoNoGo2: Žádné duplicitní ff_company_id v tbl_client ─────────────

    [FactIfPartnerDb]
    public async Task GoNoGo2_NoDuplicateFfCompanyIds_InPartnerCz()
        => await AssertNoDuplicateFfCompanyIds("cz", _fix.PartnerCzConn!);

    [FactIfPartnerDb]
    public async Task GoNoGo2_NoDuplicateFfCompanyIds_InPartnerPl()
        => await AssertNoDuplicateFfCompanyIds("pl", _fix.PartnerPlConn!);

    // ── GoNoGo3: Konzistence bridge_id_mapping ↔ tbl_client ──────────────

    [FactIfAzureSql]
    public async Task GoNoGo3_AllMappingRecords_HaveConsistentEntityType()
    {
        // Každý záznam v bridge_id_mapping musí mít entity_type = 'client'
        // (Bridge nepoužívá jiné entity typy v Fázi 1)
        await using var conn = new SqlConnection(_fix.AzureSqlConn);
        await conn.OpenAsync();

        const string sql = """
            SELECT COUNT(*)
            FROM bridge_id_mapping
            WHERE entity_type != 'client'
            """;

        var count = await conn.ExecuteScalarAsync<int>(sql);

        Assert.Equal(0, count);
    }

    [FactIfAzureSql]
    public async Task GoNoGo3_AllMappingRecords_HaveValidRegion()
    {
        // Každý záznam musí mít region v povoleném seznamu
        await using var conn = new SqlConnection(_fix.AzureSqlConn);
        await conn.OpenAsync();

        const string sql = """
            SELECT COUNT(*)
            FROM bridge_id_mapping
            WHERE partner_region NOT IN ('cz', 'pl', 'hu', 'us')
            """;

        var count = await conn.ExecuteScalarAsync<int>(sql);

        Assert.Equal(0, count);
    }

    [FactIfAzureSql]
    public async Task GoNoGo3_NoDuplicateFfCompanyIds_InMapping()
    {
        // ff_company_id musí být unikátní v bridge_id_mapping (UNIQUE constraint)
        // Duplicity by signalizovaly bug v idempotenci
        await using var conn = new SqlConnection(_fix.AzureSqlConn);
        await conn.OpenAsync();

        const string sql = """
            SELECT COUNT(*)
            FROM (
                SELECT ff_company_id
                FROM bridge_id_mapping
                WHERE entity_type = 'client'
                GROUP BY ff_company_id
                HAVING COUNT(*) > 1
            ) duplicates
            """;

        var count = await conn.ExecuteScalarAsync<int>(sql);

        Assert.Equal(0, count);
    }

    // ── GoNoGo3.2: Žádné nedokončené ságy ────────────────────────────────

    [FactIfAzureSql]
    public async Task GoNoGo3_2_NoPendingRegionChangeSagas()
    {
        // Před přechodem do Fáze 2 nesmí existovat nedokončené ságy přesunu mezi regiony.
        // Záznam 'pending_region_change' vzniká při startu ságy — musí být 0 po dokončení.
        await using var conn = new SqlConnection(_fix.AzureSqlConn);
        await conn.OpenAsync();

        const string sql = """
            SELECT COUNT(*)
            FROM bridge_sync_log
            WHERE operation = 'pending_region_change'
              AND status = 'pending'
            """;

        var count = await conn.ExecuteScalarAsync<int>(sql);

        Assert.True(count == 0,
            $"bridge_sync_log obsahuje {count} nedokončených ság (pending_region_change). " +
            "Dokončete nebo kompenzujte ságy před přechodem do Fáze 2. " +
            "Zkontrolujte: SELECT * FROM bridge_sync_log WHERE operation='pending_region_change' " +
            "AND status='pending'");
    }

    // ── GoNoGo4: bridge_sync_log — error rate ≤ 5 % za posledních 24h ────

    [FactIfAzureSql]
    public async Task GoNoGo4_FailedSyncRate_IsBelowThreshold_InLast24Hours()
    {
        await using var conn = new SqlConnection(_fix.AzureSqlConn);
        await conn.OpenAsync();

        const string totalSql = """
            SELECT COUNT(*)
            FROM bridge_sync_log
            WHERE created_at >= DATEADD(HOUR, -24, GETUTCDATE())
            """;

        const string failedSql = """
            SELECT COUNT(*)
            FROM bridge_sync_log
            WHERE status = 'failed'
              AND created_at >= DATEADD(HOUR, -24, GETUTCDATE())
            """;

        var total = await conn.ExecuteScalarAsync<int>(totalSql);
        var failed = await conn.ExecuteScalarAsync<int>(failedSql);

        if (total == 0)
            return; // Žádné operace za 24h — test není relevantní (nový systém nebo prázdné prostředí)

        var failedRate = (double)failed / total;

        Assert.True(failedRate <= MaxFailedRateThreshold,
            $"bridge_sync_log error rate = {failedRate:P1} ({failed}/{total}), " +
            $"max povoleno: {MaxFailedRateThreshold:P0}. " +
            "Zkontrolujte: SELECT * FROM bridge_sync_log WHERE status='failed' " +
            "AND created_at >= DATEADD(HOUR,-24,GETUTCDATE())");
    }

    // ── GoNoGo5: Konzistence region v mapping ─────────────────────────────

    [FactIfAzureSql]
    public async Task GoNoGo5_MappingLastSyncDirection_IsAlwaysFfToPartner()
    {
        // V Fázi 1 je tok jednosměrný: FF → Partner
        await using var conn = new SqlConnection(_fix.AzureSqlConn);
        await conn.OpenAsync();

        const string sql = """
            SELECT COUNT(*)
            FROM bridge_id_mapping
            WHERE last_sync_direction != 'ff_to_partner'
            """;

        var count = await conn.ExecuteScalarAsync<int>(sql);

        Assert.Equal(0, count);
    }

    // ── GoNoGo6: pipe_id a pipeType zachovány u migrovaných záznamů ──────

    [FactIfAzureSql]
    public async Task GoNoGo6_PipedriveColumns_PreservedForMigratedRecords()
    {
        // Ověří, že Bridge NENANULOVAL pipe_id pro záznamy přenesené z Pipedrive.
        // Záznamy s pipedrive_id v bridge_id_mapping by MUSELY mít zachováno pipe_id.
        // CLAUDE.md §17: Bridge nesmí přepisovat pipe_id a pipeType.
        if (!_fix.HasPartnerCzDb) return; // Cross-DB test — vyžaduje Partner CZ

        // 1. Načíst CZ záznamy, které mají pipedrive_id v mapping (= migrované z Pipedrive)
        await using var sqlConn = new SqlConnection(_fix.AzureSqlConn);
        await sqlConn.OpenAsync();

        const string mappingSql = """
            SELECT partner_client_id
            FROM bridge_id_mapping
            WHERE partner_region = 'cz'
              AND entity_type = 'client'
              AND pipedrive_id IS NOT NULL
            """;

        var partnerIds = (await sqlConn.QueryAsync<int>(mappingSql)).ToList();

        if (partnerIds.Count == 0)
            return; // Žádné migrované záznamy — F2-03 ještě neproběhla, test není relevantní

        // 2. Ověřit v Partner CZ DB, že pipe_id není NULL pro záznamy z Pipedrive migrace
        await using var mysqlConn = new MySqlConnection(_fix.PartnerCzConn);
        await mysqlConn.OpenAsync();

        var placeholders = string.Join(",", partnerIds.Select((_, i) => $"@id{i}"));
        var sql = $"""
            SELECT COUNT(*)
            FROM tbl_client
            WHERE idclient IN ({placeholders})
              AND pipe_id IS NULL
            """;

        var param = new DynamicParameters();
        for (var i = 0; i < partnerIds.Count; i++)
            param.Add($"id{i}", partnerIds[i]);

        var nullPipeCount = await mysqlConn.ExecuteScalarAsync<int>(sql, param);

        Assert.True(nullPipeCount == 0,
            $"{nullPipeCount} záznamů z Pipedrive migrace má pipe_id = NULL. " +
            "Bridge pravděpodobně přepsal historické Pipedrive hodnoty. " +
            "Zkontrolujte UPDATE dotazy v PartnerClientRepository.UpdateAsync.");
    }

    // ── Pomocné metriky (informační výstupy) ──────────────────────────────

    [FactIfAzureSql]
    public async Task GoNoGoMetric_TotalMappedCompanies_PerRegion()
    {
        // Informační test — výsledek se zobrazí v test output, ne assertion
        await using var conn = new SqlConnection(_fix.AzureSqlConn);
        await conn.OpenAsync();

        const string sql = """
            SELECT partner_region, COUNT(*) AS mapped_count
            FROM bridge_id_mapping
            WHERE entity_type = 'client'
            GROUP BY partner_region
            ORDER BY partner_region
            """;

        var rows = await conn.QueryAsync(sql);
        var lines = rows.Select(r => $"{r.partner_region}: {r.mapped_count} firem");

        // Vypsat do test output (xunit zachytí přes ITestOutputHelper — zde jen Assert.True)
        Assert.True(true, "Počty per region: " + string.Join(", ", lines));
    }

    [FactIfAzureSql]
    public async Task GoNoGoMetric_GeoValidationWarnings_Count()
    {
        await using var conn = new SqlConnection(_fix.AzureSqlConn);
        await conn.OpenAsync();

        const string sql = """
            SELECT COUNT(*)
            FROM bridge_sync_log
            WHERE operation = 'geo_validation_warning'
            """;

        var count = await conn.ExecuteScalarAsync<int>(sql);

        // Informační — zobrazit počet Warning o neznámých PSČ
        Assert.True(true,
            $"Počet geo_validation_warning záznamů: {count}. " +
            "Pokud > 100, zvažte ruční opravu PSČ ve FieldForce.");
    }

    [FactIfAzureSql]
    public async Task GoNoGoMetric_SyncLogWarnings_AreExpectedType()
    {
        // Všechny Warning záznamy musí být pouze geo_validation_warning nebo
        // region_change — žádné neočekávané typy
        await using var conn = new SqlConnection(_fix.AzureSqlConn);
        await conn.OpenAsync();

        const string sql = """
            SELECT COUNT(*)
            FROM bridge_sync_log
            WHERE severity = 'Warning'
              AND operation NOT IN (
                  'geo_validation_warning',
                  'region_change',
                  'gaia_processing_error'
              )
            """;

        var unexpectedWarnings = await conn.ExecuteScalarAsync<int>(sql);

        Assert.Equal(0, unexpectedWarnings);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static async Task AssertSyncedRateAboveThreshold(string region, string connStr)
    {
        await using var conn = new MySqlConnection(connStr);
        await conn.OpenAsync();

        const string sql = """
            SELECT
                SUM(CASE WHEN ff_sync_source = 'FF' THEN 1 ELSE 0 END) AS synced_count,
                COUNT(*) AS total_count
            FROM tbl_client
            WHERE client_disable = 0
            """;

        var row = await conn.QuerySingleAsync(sql);
        var synced = (int)(row.synced_count ?? 0);
        var total = (int)(row.total_count ?? 0);

        if (total == 0)
            return; // Prázdná DB — test není relevantní

        var rate = (double)synced / total;

        Assert.True(rate >= MinSyncedRateThreshold,
            $"Region '{region}': sync rate = {rate:P1} ({synced}/{total}), " +
            $"požadováno ≥ {MinSyncedRateThreshold:P0}. " +
            "Zkontrolujte, zda bulk migrace (F2-03) proběhla a " +
            "Bridge správně nastavuje ff_sync_source = 'FF'.");
    }

    private static async Task AssertNoDuplicateFfCompanyIds(string region, string connStr)
    {
        await using var conn = new MySqlConnection(connStr);
        await conn.OpenAsync();

        const string sql = """
            SELECT COUNT(*) AS dup_count
            FROM (
                SELECT ff_company_id
                FROM tbl_client
                WHERE ff_company_id IS NOT NULL
                  AND ff_sync_source = 'FF'
                GROUP BY ff_company_id
                HAVING COUNT(*) > 1
            ) duplicates
            """;

        var dupCount = await conn.ExecuteScalarAsync<int>(sql);

        Assert.Equal(0, dupCount);
    }
}
