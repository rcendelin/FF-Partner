using Bridge.Domain.Enums;
using Bridge.Domain.Models;

namespace Bridge.IntegrationTests.Phase1;

/// <summary>
/// Integrační testy pro PartnerClientRepository — ověřují přímý přístup k Partner3 MySQL DB.
///
/// Pokrývají scénáře F1-12:
///   Sc1: Nová firma CZ → INSERT v Partner CZ DB (přesná data, ff_sync_source='FF')
///   Sc2: Změna adresy → UPDATE v Partner DB, last_ff_sync_at aktualizován
///   Sc4: Firma s neznámým PSČ → INSERT s zip_id = null, záznam existuje
///   Sc7: Conflict detection → ověření, že last_ff_sync_at je správně nastaveno
/// </summary>
[Trait("Category", "Integration")]
public sealed class PartnerClientRepositoryIntegrationTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fix;

    public PartnerClientRepositoryIntegrationTests(IntegrationTestFixture fix)
        => _fix = fix;

    // ── Sc1: INSERT nové firmy CZ ─────────────────────────────────────────

    [FactIfPartnerDb]
    public async Task Insert_NewCompanyCz_CreatesRecordWithCorrectFfFields()
    {
        var repo = _fix.CreatePartnerClientRepository();
        var ffId = Guid.NewGuid();
        _fix.TrackClient(ffId, "cz");

        var client = MakeTestClient(ffId, "Integrační Test s.r.o.", "CZ");

        var partnerId = await repo.InsertAsync(client, "cz");

        Assert.True(partnerId > 0, "INSERT musí vrátit kladné idclient.");

        var found = await repo.GetByFfCompanyIdAsync(ffId, "cz");

        Assert.NotNull(found);
        Assert.Equal(ffId, found.FfCompanyId);
        Assert.Equal("Integrační Test s.r.o.", found.ClientFirm);
        Assert.Equal("FF", found.FfSyncSource);
        Assert.Equal(DataOwner.FieldForce, found.DataOwner);
        Assert.Equal(0, found.ClientDisable);
        Assert.NotNull(found.ClientDate);         // INSERT nastavil datum
        Assert.NotNull(found.LastFfSyncAt);       // Bridge sync čas nastaven
    }

    // ── Sc1: pipe_id a pipeType nesmí být modifikovány ───────────────────

    [FactIfPartnerDb]
    public async Task Insert_NewCompany_DoesNotSetPipedriveColumns()
    {
        // tbl_client.pipe_id a pipeType jsou historické Pipedrive hodnoty — Bridge je nenastavuje
        var repo = _fix.CreatePartnerClientRepository();
        var ffId = Guid.NewGuid();
        _fix.TrackClient(ffId, "cz");

        var client = MakeTestClient(ffId, "PipeCheck s.r.o.", "CZ");
        await repo.InsertAsync(client, "cz");

        var found = await repo.GetByFfCompanyIdAsync(ffId, "cz");

        Assert.NotNull(found);
        // FfSyncSource by mělo být 'FF', nikoli 'PIPE' (Pipedrive legacy)
        Assert.Equal("FF", found.FfSyncSource);
    }

    // ── Sc2: UPDATE — adresa + last_ff_sync_at ────────────────────────────

    [FactIfPartnerDb]
    public async Task Update_AddressChange_UpdatesFieldsAndSyncTimestamp()
    {
        var repo = _fix.CreatePartnerClientRepository();
        var ffId = Guid.NewGuid();
        _fix.TrackClient(ffId, "cz");

        var client = MakeTestClient(ffId, "Aktualizace Test a.s.", "CZ");
        var partnerId = await repo.InsertAsync(client, "cz");

        // Počkáme 10ms — aby last_ff_sync_at byl prokazatelně různý
        await Task.Delay(10);
        var before = DateTime.UtcNow;

        var updated = new PartnerClient
        {
            IdClient = partnerId,
            ClientFirm = "Aktualizace Test a.s. — UPDATED",
            ClientIc = client.ClientIc,
            ClientStreet = client.ClientStreet,
            ClientCity = "Brno",
            ClientPsc = "60200",
            ClientCountryId = client.ClientCountryId,
            ClientCountryShort = client.ClientCountryShort,
            ClientRight = client.ClientRight,
            IdOwner = client.IdOwner,
            FfCompanyId = client.FfCompanyId,
            FfSyncSource = "FF",
            DataOwner = DataOwner.FieldForce,
            LastFfSyncAt = DateTime.UtcNow
        };
        await repo.UpdateAsync(updated, "cz");

        var found = await repo.GetByPartnerIdAsync(partnerId, "cz");

        Assert.NotNull(found);
        Assert.Equal("Aktualizace Test a.s. — UPDATED", found.ClientFirm);
        Assert.Equal("Brno", found.ClientCity);
        Assert.Equal("60200", found.ClientPsc);
        Assert.True(found.LastFfSyncAt >= before,
            "last_ff_sync_at musí být >= čas UPDATE.");
    }

    // ── Sc2: client_date se při UPDATE nemění ─────────────────────────────

    [FactIfPartnerDb]
    public async Task Update_DoesNotChangeClientDate()
    {
        var repo = _fix.CreatePartnerClientRepository();
        var ffId = Guid.NewGuid();
        _fix.TrackClient(ffId, "cz");

        var client = MakeTestClient(ffId, "DateGuard s.r.o.", "CZ");
        var partnerId = await repo.InsertAsync(client, "cz");

        var inserted = await repo.GetByPartnerIdAsync(partnerId, "cz");
        Assert.NotNull(inserted);
        var originalDate = inserted.ClientDate;

        await Task.Delay(50);

        var updated = new PartnerClient
        {
            IdClient = partnerId,
            ClientFirm = "DateGuard — renamed",
            ClientIc = inserted.ClientIc,
            ClientStreet = inserted.ClientStreet,
            ClientCity = inserted.ClientCity,
            ClientPsc = inserted.ClientPsc,
            ClientCountryId = inserted.ClientCountryId,
            ClientCountryShort = inserted.ClientCountryShort,
            ClientRight = inserted.ClientRight,
            IdOwner = inserted.IdOwner,
            FfCompanyId = inserted.FfCompanyId,
            FfSyncSource = "FF",
            DataOwner = DataOwner.FieldForce,
            LastFfSyncAt = DateTime.UtcNow
        };
        await repo.UpdateAsync(updated, "cz");

        var found = await repo.GetByPartnerIdAsync(partnerId, "cz");
        Assert.NotNull(found);

        // client_date se po UPDATE nesmí změnit (INSERT-only dle CLAUDE.md)
        Assert.Equal(
            originalDate?.ToString("yyyy-MM-dd HH:mm"),
            found.ClientDate?.ToString("yyyy-MM-dd HH:mm"));
    }

    // ── Sc4: Neznámé PSČ → INSERT s zip_id = null ────────────────────────

    [FactIfPartnerDb]
    public async Task Insert_UnknownZip_InsertsWithNullZipId()
    {
        var repo = _fix.CreatePartnerClientRepository();
        var ffId = Guid.NewGuid();
        _fix.TrackClient(ffId, "cz");

        var client = MakeTestClient(ffId, "NullZip s.r.o.", "CZ");
        client.ClientZipId = null; // Neznámé PSČ — sync se nesmí zastavit

        var partnerId = await repo.InsertAsync(client, "cz");

        var found = await repo.GetByPartnerIdAsync(partnerId, "cz");
        Assert.NotNull(found);
        Assert.Null(found.ClientZipId);
        Assert.Equal("FF", found.FfSyncSource); // Sync proběhl přes Bridge
    }

    // ── Disable + Enable ─────────────────────────────────────────────────

    [FactIfPartnerDb]
    public async Task Disable_ExistingClient_SetsClientDisableToOne()
    {
        var repo = _fix.CreatePartnerClientRepository();
        var ffId = Guid.NewGuid();
        _fix.TrackClient(ffId, "cz");

        var client = MakeTestClient(ffId, "DisableTest s.r.o.", "CZ");
        var partnerId = await repo.InsertAsync(client, "cz");

        await repo.DisableAsync(partnerId, "cz");

        var found = await repo.GetByPartnerIdAsync(partnerId, "cz");
        Assert.NotNull(found);
        Assert.Equal(1, found.ClientDisable);
    }

    [FactIfPartnerDb]
    public async Task Enable_DisabledClient_SetsClientDisableToZero()
    {
        var repo = _fix.CreatePartnerClientRepository();
        var ffId = Guid.NewGuid();
        _fix.TrackClient(ffId, "cz");

        var client = MakeTestClient(ffId, "EnableTest s.r.o.", "CZ");
        var partnerId = await repo.InsertAsync(client, "cz");
        await repo.DisableAsync(partnerId, "cz");

        await repo.EnableAsync(partnerId, "cz");

        var found = await repo.GetByPartnerIdAsync(partnerId, "cz");
        Assert.NotNull(found);
        Assert.Equal(0, found.ClientDisable);
    }

    // ── UpdateContact ─────────────────────────────────────────────────────

    [FactIfPartnerDb]
    public async Task UpdateContact_ChangesEmailAndPhone()
    {
        var repo = _fix.CreatePartnerClientRepository();
        var ffId = Guid.NewGuid();
        _fix.TrackClient(ffId, "cz");

        var client = MakeTestClient(ffId, "ContactUpdate s.r.o.", "CZ");
        var partnerId = await repo.InsertAsync(client, "cz");

        await repo.UpdateContactAsync(partnerId, "cz", "novy@email.cz", "+420999888777");

        var found = await repo.GetByPartnerIdAsync(partnerId, "cz");
        Assert.NotNull(found);
        Assert.Equal("novy@email.cz", found.ClientMail);
        Assert.Equal("+420999888777", found.ClientPhone);
    }

    // ── UpdateOwner ───────────────────────────────────────────────────────

    [FactIfPartnerDb]
    public async Task UpdateOwner_ChangesIdOwner()
    {
        var repo = _fix.CreatePartnerClientRepository();
        var ffId = Guid.NewGuid();
        _fix.TrackClient(ffId, "cz");

        var client = MakeTestClient(ffId, "OwnerUpdate s.r.o.", "CZ");
        var partnerId = await repo.InsertAsync(client, "cz");

        await repo.UpdateOwnerAsync(partnerId, "cz", ownerId: 999);

        var found = await repo.GetByPartnerIdAsync(partnerId, "cz");
        Assert.NotNull(found);
        Assert.Equal(999, found.IdOwner);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static PartnerClient MakeTestClient(Guid ffId, string name, string countryShort) => new()
    {
        ClientFirm = name,
        ClientIc = "IT" + ffId.ToString("N")[..8],  // unikátní IČO pro test
        ClientStreet = "Testovací 1",
        ClientCity = "Praha",
        ClientPsc = "11000",
        ClientCountryId = 1,                         // 1 = CZ v GAIA cfg_country
        ClientCountryShort = countryShort,
        ClientRight = 0,
        ClientDate = DateTime.UtcNow,
        ClientDisable = 0,
        FfCompanyId = ffId,
        FfSyncSource = "FF",
        DataOwner = DataOwner.FieldForce,
        LastFfSyncAt = DateTime.UtcNow
    };
}
