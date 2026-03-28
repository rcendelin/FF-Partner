using Bridge.Domain.Models;
using Dapper;

namespace Bridge.Infrastructure.Partner.Repositories;

/// <summary>
/// Dapper-based repository pro tbl_client v Partner3 MySQL DB.
/// POZOR: Nikdy nemodifikovat pipe_id, pipeType, int_client.
/// POZOR: client_date se zapisuje pouze při INSERT, nikdy při UPDATE.
/// </summary>
public sealed class PartnerClientRepository : IPartnerClientRepository
{
    private readonly IPartnerDbConnectionFactory _connectionFactory;

    public PartnerClientRepository(IPartnerDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<PartnerClient?> GetByFfCompanyIdAsync(
        Guid ffCompanyId, string region, CancellationToken ct = default)
    {
        await using var conn = _connectionFactory.CreateConnection(region);
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT
                idclient, client_firm, client_ic, client_dic,
                client_street, client_city, client_psc,
                client_country_id, client_country_short,
                client_state, client_state_id, client_county, client_county_id,
                client_zip_id, client_phone, client_mail,
                client_right, client_date, client_disable,
                id_owner, ff_company_id, ff_sync_source, data_owner, last_ff_sync_at
            FROM tbl_client
            WHERE ff_company_id = @FfCompanyId
            LIMIT 1
            """;

        var row = await conn.QueryFirstOrDefaultAsync<PartnerClientRow>(
            sql, new { FfCompanyId = ffCompanyId.ToString() });

        return row is null ? null : MapToDomain(row);
    }

    public async Task<PartnerClient?> GetByPartnerIdAsync(
        int partnerId, string region, CancellationToken ct = default)
    {
        await using var conn = _connectionFactory.CreateConnection(region);
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT
                idclient, client_firm, client_ic, client_dic,
                client_street, client_city, client_psc,
                client_country_id, client_country_short,
                client_state, client_state_id, client_county, client_county_id,
                client_zip_id, client_phone, client_mail,
                client_right, client_date, client_disable,
                id_owner, ff_company_id, ff_sync_source, data_owner, last_ff_sync_at
            FROM tbl_client
            WHERE idclient = @PartnerId
            LIMIT 1
            """;

        var row = await conn.QueryFirstOrDefaultAsync<PartnerClientRow>(
            sql, new { PartnerId = partnerId });

        return row is null ? null : MapToDomain(row);
    }

    public async Task<int> InsertAsync(
        PartnerClient client, string region, CancellationToken ct = default)
    {
        await using var conn = _connectionFactory.CreateConnection(region);
        await conn.OpenAsync(ct);

        // client_date: nastavit pouze při INSERT, nikdy při UPDATE
        // pipe_id, pipeType, int_client: NEMODIFIKOVAT — záměrně vynechány
        const string sql = """
            INSERT INTO tbl_client (
                client_firm, client_ic, client_dic,
                client_street, client_city, client_psc,
                client_country_id, client_country_short,
                client_state, client_state_id, client_county, client_county_id,
                client_zip_id, client_phone, client_mail,
                client_right, client_date, client_disable,
                id_owner, ff_company_id, ff_sync_source, data_owner, last_ff_sync_at
            ) VALUES (
                @ClientFirm, @ClientIc, @ClientDic,
                @ClientStreet, @ClientCity, @ClientPsc,
                @ClientCountryId, @ClientCountryShort,
                @ClientState, @ClientStateId, @ClientCounty, @ClientCountyId,
                @ClientZipId, @ClientPhone, @ClientMail,
                @ClientRight, @ClientDate, @ClientDisable,
                @IdOwner, @FfCompanyId, @FfSyncSource, @DataOwner, @LastFfSyncAt
            );
            SELECT LAST_INSERT_ID();
            """;

        var insertedId = await conn.ExecuteScalarAsync<int>(sql, new
        {
            client.ClientFirm,
            client.ClientIc,
            client.ClientDic,
            client.ClientStreet,
            client.ClientCity,
            client.ClientPsc,
            client.ClientCountryId,
            client.ClientCountryShort,
            client.ClientState,
            client.ClientStateId,
            client.ClientCounty,
            client.ClientCountyId,
            client.ClientZipId,
            client.ClientPhone,
            client.ClientMail,
            client.ClientRight,
            ClientDate = client.ClientDate ?? DateTime.UtcNow,
            client.ClientDisable,
            client.IdOwner,
            FfCompanyId = client.FfCompanyId?.ToString(),
            client.FfSyncSource,
            DataOwner = client.DataOwner.ToString().ToUpperInvariant(),
            client.LastFfSyncAt
        });

        return insertedId;
    }

    public async Task UpdateAsync(
        PartnerClient client, string region, CancellationToken ct = default)
    {
        await using var conn = _connectionFactory.CreateConnection(region);
        await conn.OpenAsync(ct);

        // client_date: NIKDY při UPDATE
        // pipe_id, pipeType, int_client: NIKDY modifikovat
        const string sql = """
            UPDATE tbl_client SET
                client_firm = @ClientFirm,
                client_ic = @ClientIc,
                client_dic = @ClientDic,
                client_street = @ClientStreet,
                client_city = @ClientCity,
                client_psc = @ClientPsc,
                client_country_id = @ClientCountryId,
                client_country_short = @ClientCountryShort,
                client_state = @ClientState,
                client_state_id = @ClientStateId,
                client_county = @ClientCounty,
                client_county_id = @ClientCountyId,
                client_zip_id = @ClientZipId,
                client_phone = @ClientPhone,
                client_mail = @ClientMail,
                client_right = @ClientRight,
                id_owner = @IdOwner,
                ff_sync_source = @FfSyncSource,
                data_owner = @DataOwner,
                last_ff_sync_at = @LastFfSyncAt
            WHERE idclient = @IdClient
            """;

        await conn.ExecuteAsync(sql, new
        {
            client.ClientFirm,
            client.ClientIc,
            client.ClientDic,
            client.ClientStreet,
            client.ClientCity,
            client.ClientPsc,
            client.ClientCountryId,
            client.ClientCountryShort,
            client.ClientState,
            client.ClientStateId,
            client.ClientCounty,
            client.ClientCountyId,
            client.ClientZipId,
            client.ClientPhone,
            client.ClientMail,
            client.ClientRight,
            client.IdOwner,
            client.FfSyncSource,
            DataOwner = client.DataOwner.ToString().ToUpperInvariant(),
            client.LastFfSyncAt,
            client.IdClient
        });
    }

    public async Task DisableAsync(
        int partnerId, string region, CancellationToken ct = default)
    {
        await using var conn = _connectionFactory.CreateConnection(region);
        await conn.OpenAsync(ct);

        const string sql = """
            UPDATE tbl_client SET
                client_disable = 1,
                last_ff_sync_at = @Now
            WHERE idclient = @PartnerId
            """;

        var affected = await conn.ExecuteAsync(sql, new { PartnerId = partnerId, Now = DateTime.UtcNow });
        if (affected == 0)
            throw new InvalidOperationException(
                $"DisableAsync: idclient={partnerId} nebyl nalezen v regionu {region}.");
    }

    public async Task EnableAsync(
        int partnerId, string region, CancellationToken ct = default)
    {
        await using var conn = _connectionFactory.CreateConnection(region);
        await conn.OpenAsync(ct);

        const string sql = """
            UPDATE tbl_client SET
                client_disable = 0,
                last_ff_sync_at = @Now
            WHERE idclient = @PartnerId
            """;

        var affected = await conn.ExecuteAsync(sql, new { PartnerId = partnerId, Now = DateTime.UtcNow });
        if (affected == 0)
            throw new InvalidOperationException(
                $"EnableAsync: idclient={partnerId} nebyl nalezen v regionu {region}.");
    }

    public async Task DeleteAsync(
        int partnerId, string region, CancellationToken ct = default)
    {
        await using var conn = _connectionFactory.CreateConnection(region);
        await conn.OpenAsync(ct);

        // POZOR: Pouze pro kompenzaci v MoveClientToRegionSaga — čerstvě vložený záznam bez objednávek.
        // Guard: DELETE pouze pokud neexistují aktivní objednávky (ochrana před data corruption).
        // Pokud FK constraints chybí (legacy MyISAM), tato podmínka zabraňuje orphaned tbl_order záznamům.
        const string sql = """
            DELETE FROM tbl_client
            WHERE idclient = @PartnerId
              AND NOT EXISTS (
                  SELECT 1 FROM tbl_order
                  WHERE id_client = @PartnerId
                    AND order_deactive = 0
              )
            """;

        var affected = await conn.ExecuteAsync(sql, new { PartnerId = partnerId });
        if (affected == 0)
            throw new InvalidOperationException(
                $"DeleteAsync: idclient={partnerId} nelze smazat — nenalezen nebo má aktivní objednávky. " +
                $"Nutný manuální zásah.");
    }

    private static PartnerClient MapToDomain(PartnerClientRow row) => new()
    {
        IdClient = row.idclient,
        ClientFirm = row.client_firm ?? string.Empty,
        ClientIc = row.client_ic,
        ClientDic = row.client_dic,
        ClientStreet = row.client_street,
        ClientCity = row.client_city,
        ClientPsc = row.client_psc,
        ClientCountryId = row.client_country_id,
        ClientCountryShort = row.client_country_short,
        ClientState = row.client_state,
        ClientStateId = row.client_state_id,
        ClientCounty = row.client_county,
        ClientCountyId = row.client_county_id,
        ClientZipId = row.client_zip_id,
        ClientPhone = row.client_phone,
        ClientMail = row.client_mail,
        ClientRight = row.client_right,
        ClientDate = row.client_date,
        ClientDisable = row.client_disable,
        IdOwner = row.id_owner,
        FfCompanyId = Guid.TryParse(row.ff_company_id, out var g) ? g : null,
        FfSyncSource = row.ff_sync_source,
        DataOwner = Enum.TryParse<Domain.Enums.DataOwner>(row.data_owner, ignoreCase: true, out var owner)
            ? owner
            : Domain.Enums.DataOwner.Pipedrive,
        // SpecifyKind: MySQL DATETIME nemá timezone info; Bridge vždy zapisuje UTC → interpretovat jako UTC
        LastFfSyncAt = row.last_ff_sync_at.HasValue
            ? DateTime.SpecifyKind(row.last_ff_sync_at.Value, DateTimeKind.Utc)
            : null
    };

    // Pomocný record pro Dapper mapping — odpovídá sloupcům tbl_client
    private sealed record PartnerClientRow(
        int idclient,
        string? client_firm,
        string? client_ic,
        string? client_dic,
        string? client_street,
        string? client_city,
        string? client_psc,
        int client_country_id,
        string? client_country_short,
        string? client_state,
        int? client_state_id,
        string? client_county,
        int? client_county_id,
        int? client_zip_id,
        string? client_phone,
        string? client_mail,
        int client_right,
        DateTime? client_date,
        byte client_disable,
        int? id_owner,
        string? ff_company_id,
        string? ff_sync_source,
        string? data_owner,
        DateTime? last_ff_sync_at
    );
}
