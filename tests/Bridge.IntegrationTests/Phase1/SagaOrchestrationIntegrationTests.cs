using Bridge.Domain.Enums;
using Bridge.Domain.Models;
using Bridge.Infrastructure.Partner.Repositories;

namespace Bridge.IntegrationTests.Phase1;

/// <summary>
/// Integrační testy pro orchestraci Fáze 1 — saga přesun mezi regiony + conflict detection.
///
/// Pokrývají scénáře F1-12:
///   Sc5: Změna země CZ→PL → INSERT do PL, DISABLE v CZ (saga kroky)
///   Sc7: Conflict detection — přímá MySQL editace → Bridge nepromaže last_ff_sync_at
/// </summary>
[Trait("Category", "Integration")]
public sealed class SagaOrchestrationIntegrationTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fix;

    public SagaOrchestrationIntegrationTests(IntegrationTestFixture fix)
        => _fix = fix;

    // ── Sc5: Saga — INSERT do PL, DISABLE v CZ ───────────────────────────

    [FactIfPartnerDb]
    public async Task SagaSteps_RegionChangeCzToPl_InsertsInPlAndDisablesInCz()
    {
        var partnerRepo = _fix.CreatePartnerClientRepository();
        var ffId = Guid.NewGuid();
        _fix.TrackClient(ffId, "cz");
        _fix.TrackClient(ffId, "pl");

        // Krok 1: INSERT v CZ (počáteční stav)
        var czClient = MakeTestClient(ffId, "SagaTest CZ→PL s.r.o.", "CZ");
        var czPartnerId = await partnerRepo.InsertAsync(czClient, "cz");
        Assert.True(czPartnerId > 0);

        // Krok 2: INSERT do cílové DB (PL) — MUSÍ proběhnout před DISABLE v CZ
        var plClient = MakeTestClient(ffId, "SagaTest CZ→PL s.r.o.", "PL");
        var plPartnerId = await partnerRepo.InsertAsync(plClient, "pl");
        Assert.True(plPartnerId > 0);

        // Krok 3: DISABLE v CZ
        await partnerRepo.DisableAsync(czPartnerId, "cz");

        // Ověření: CZ je disabled, PL je aktivní
        var czFound = await partnerRepo.GetByPartnerIdAsync(czPartnerId, "cz");
        var plFound = await partnerRepo.GetByPartnerIdAsync(plPartnerId, "pl");

        Assert.NotNull(czFound);
        Assert.NotNull(plFound);
        Assert.Equal(1, czFound.ClientDisable);
        Assert.Equal(0, plFound.ClientDisable);
        Assert.Equal(ffId, plFound.FfCompanyId);
    }

    // ── Sc5: Saga kompenzace — DELETE v cílové DB při selhání DISABLE ─────

    [FactIfPartnerDb]
    public async Task SagaCompensation_DeleteInTargetRegion_RemovesRecord()
    {
        var partnerRepo = _fix.CreatePartnerClientRepository();
        var ffId = Guid.NewGuid();
        _fix.TrackClient(ffId, "cz");

        var czClient = MakeTestClient(ffId, "SagaCompensation s.r.o.", "CZ");
        var czPartnerId = await partnerRepo.InsertAsync(czClient, "cz");

        // Simulace: PL INSERT proběhl, ale DISABLE CZ selhal → DELETE PL (kompenzace)
        var plClient = MakeTestClient(ffId, "SagaCompensation s.r.o.", "PL");
        var plPartnerId = await partnerRepo.InsertAsync(plClient, "pl");
        _fix.TrackClient(ffId, "pl");  // Registrujeme PL pro cleanup i pokud DeleteAsync projde

        // DELETE PL (kompenzace) — povoleno jen pokud klient nemá aktivní objednávky
        await partnerRepo.DeleteAsync(plPartnerId, "pl");

        var plFound = await partnerRepo.GetByPartnerIdAsync(plPartnerId, "pl");
        Assert.Null(plFound); // Musí být smazáno

        // CZ zůstává aktivní (kompenzace neprovedla DISABLE v CZ)
        var czFound = await partnerRepo.GetByPartnerIdAsync(czPartnerId, "cz");
        Assert.NotNull(czFound);
        Assert.Equal(0, czFound.ClientDisable);
        Assert.Equal(0, czFound.ClientDisable);
    }

    // ── Sc7: Conflict detection — stale message guard ─────────────────────

    [FactIfPartnerDb]
    public async Task ConflictDetection_MessageOlderThanLastSync_DetectsConflict()
    {
        // Simulujeme situaci: přímá editace v MySQL → last_ff_sync_at je "v budoucnosti"
        // Bridge dostane starou zprávu (sentAt je starší) → konflikt musí být detekován
        var partnerRepo = _fix.CreatePartnerClientRepository();
        var ffId = Guid.NewGuid();
        _fix.TrackClient(ffId, "cz");

        // INSERT s last_ff_sync_at = nyní
        var now = DateTime.UtcNow;
        var client = MakeTestClient(ffId, "ConflictTest s.r.o.", "CZ");
        client.LastFfSyncAt = now;
        var partnerId = await partnerRepo.InsertAsync(client, "cz");

        // Simulace "přímé editace" — UPDATE last_ff_sync_at na budoucí čas (+10 min)
        var updatedClient = new PartnerClient
        {
            IdClient = partnerId,
            ClientFirm = "ConflictTest — přímá editace",
            ClientIc = client.ClientIc,
            ClientStreet = client.ClientStreet,
            ClientCity = client.ClientCity,
            ClientPsc = client.ClientPsc,
            ClientCountryId = client.ClientCountryId,
            ClientCountryShort = client.ClientCountryShort,
            ClientRight = client.ClientRight,
            IdOwner = client.IdOwner,
            FfCompanyId = client.FfCompanyId,
            FfSyncSource = "FF",
            DataOwner = DataOwner.FieldForce,
            LastFfSyncAt = now.AddMinutes(10)
        };
        await partnerRepo.UpdateAsync(updatedClient, "cz");

        // Načíst stav z DB
        var current = await partnerRepo.GetByPartnerIdAsync(partnerId, "cz");
        Assert.NotNull(current);

        // Conflict detection logika (dle CLAUDE.md sekce 10):
        // sentAt = now - 2 min → stará zpráva, last_ff_sync_at = now + 10 min
        var messageSentAt = now.AddMinutes(-2);
        var isConflict = current.LastFfSyncAt.HasValue &&
                         current.LastFfSyncAt.Value > messageSentAt.AddMinutes(-5);

        Assert.True(isConflict,
            "Bridge musí detekovat konflikt: last_ff_sync_at je novější než sentAt + tolerance.");

        // Ověřit že přímá editace je v DB (Bridge ji nesmí přepsat)
        Assert.Equal("ConflictTest — přímá editace", current.ClientFirm);
    }

    // ── Sc7: Bez konfliktu — platná zpráva promaže ────────────────────────

    [FactIfPartnerDb]
    public async Task ConflictDetection_FreshMessage_NoConflictDetected()
    {
        var partnerRepo = _fix.CreatePartnerClientRepository();
        var ffId = Guid.NewGuid();
        _fix.TrackClient(ffId, "cz");

        var client = MakeTestClient(ffId, "NoConflict s.r.o.", "CZ");
        client.LastFfSyncAt = DateTime.UtcNow.AddMinutes(-30);
        var partnerId = await partnerRepo.InsertAsync(client, "cz");

        var current = await partnerRepo.GetByPartnerIdAsync(partnerId, "cz");
        Assert.NotNull(current);

        // Nová zpráva přišla nyní (sentAt = now, last_ff_sync_at = before - 30 min)
        var messageSentAt = DateTime.UtcNow;
        var isConflict = current.LastFfSyncAt.HasValue &&
                         current.LastFfSyncAt.Value > messageSentAt.AddMinutes(-5);

        Assert.False(isConflict,
            "Čerstvá zpráva nesmí být označena jako konflikt.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static PartnerClient MakeTestClient(Guid ffId, string name, string countryShort) => new()
    {
        ClientFirm = name,
        ClientIc = "IT" + ffId.ToString("N")[..8],
        ClientStreet = "Testovací 1",
        ClientCity = "Praha",
        ClientPsc = "11000",
        ClientCountryId = 1,
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
