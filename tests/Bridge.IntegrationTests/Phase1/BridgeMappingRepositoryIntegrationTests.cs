using Bridge.Domain.Models;

namespace Bridge.IntegrationTests.Phase1;

/// <summary>
/// Integrační testy pro BridgeMappingRepository — ověřují přímý přístup k Azure SQL bridge_id_mapping.
///
/// Pokrývají scénáře F1-12:
///   Sc1: Mapping uložen po CREATE
///   Sc5: Mapping aktualizován při přesunu do jiného regionu (saga)
///   Sc7: Idempotence — GetMappingAsync vrací správný záznam
/// </summary>
[Trait("Category", "Integration")]
public sealed class BridgeMappingRepositoryIntegrationTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fix;

    public BridgeMappingRepositoryIntegrationTests(IntegrationTestFixture fix)
        => _fix = fix;

    // ── Sc1: SaveMapping + GetMapping ─────────────────────────────────────

    [FactIfAzureSql]
    public async Task SaveMapping_NewRecord_CanBeRetrievedByFfCompanyId()
    {
        var repo = _fix.CreateMappingRepository();
        var ffId = Guid.NewGuid();
        _fix.TrackMapping(ffId);

        var now = DateTime.UtcNow;
        var mapping = new IdMappingRecord
        {
            FfCompanyId = ffId,
            PartnerClientId = 1001,
            PartnerRegion = "cz",
            EntityType = "client",
            LastSyncAt = now,
            LastSyncDirection = "ff_to_partner",
            CreatedAt = now,
            UpdatedAt = now
        };
        await repo.SaveMappingAsync(mapping);

        var found = await repo.GetMappingAsync(ffId);

        Assert.NotNull(found);
        Assert.Equal(ffId, found.FfCompanyId);
        Assert.Equal(1001, found.PartnerClientId);
        Assert.Equal("cz", found.PartnerRegion);
        Assert.Equal("client", found.EntityType);
        Assert.Equal("ff_to_partner", found.LastSyncDirection);
    }

    // ── Sc1: Cache invalidace po SaveMapping ──────────────────────────────

    [FactIfAzureSql]
    public async Task SaveMapping_AfterGet_CacheIsInvalidated()
    {
        var repo = _fix.CreateMappingRepository();
        var ffId = Guid.NewGuid();
        _fix.TrackMapping(ffId);

        // První GET → null (cache miss, DB miss)
        var notFound = await repo.GetMappingAsync(ffId);
        Assert.Null(notFound);

        // SaveMapping (cache invalidace)
        var now = DateTime.UtcNow;
        var mapping = new IdMappingRecord
        {
            FfCompanyId = ffId,
            PartnerClientId = 2002,
            PartnerRegion = "pl",
            EntityType = "client",
            LastSyncAt = now,
            LastSyncDirection = "ff_to_partner",
            CreatedAt = now,
            UpdatedAt = now
        };
        await repo.SaveMappingAsync(mapping);

        // Druhý GET musí vrátit nový záznam (ne cached null)
        var found = await repo.GetMappingAsync(ffId);
        Assert.NotNull(found);
        Assert.Equal(2002, found.PartnerClientId);
    }

    // ── Sc5: UpdateMapping při přesunu mezi regiony ───────────────────────

    [FactIfAzureSql]
    public async Task UpdateMapping_RegionChange_UpdatesRegionAndClientId()
    {
        var repo = _fix.CreateMappingRepository();
        var ffId = Guid.NewGuid();
        _fix.TrackMapping(ffId);

        // Původní záznam: CZ, partnerId=3001
        var now = DateTime.UtcNow;
        var original = new IdMappingRecord
        {
            FfCompanyId = ffId,
            PartnerClientId = 3001,
            PartnerRegion = "cz",
            EntityType = "client",
            LastSyncAt = now,
            LastSyncDirection = "ff_to_partner",
            CreatedAt = now,
            UpdatedAt = now
        };
        await repo.SaveMappingAsync(original);

        // Přesun do PL — UpdateMapping s novými hodnotami
        var updatedNow = DateTime.UtcNow;
        var updated = new IdMappingRecord
        {
            FfCompanyId = ffId,
            PartnerClientId = 3002,
            PartnerRegion = "pl",
            EntityType = "client",
            LastSyncAt = updatedNow,
            LastSyncDirection = "ff_to_partner",
            CreatedAt = now,
            UpdatedAt = updatedNow
        };
        await repo.UpdateMappingAsync(updated);

        var found = await repo.GetMappingAsync(ffId);
        Assert.NotNull(found);
        Assert.Equal(3002, found.PartnerClientId);
        Assert.Equal("pl", found.PartnerRegion);
    }

    // ── GetPartnerClientIds pro polling ───────────────────────────────────

    [FactIfAzureSql]
    public async Task GetPartnerClientIdsForRegion_ReturnsInsertedId()
    {
        var repo = _fix.CreateMappingRepository();
        var ffId = Guid.NewGuid();
        _fix.TrackMapping(ffId);

        var partnerId = 9901;
        var now = DateTime.UtcNow;
        var mapping = new IdMappingRecord
        {
            FfCompanyId = ffId,
            PartnerClientId = partnerId,
            PartnerRegion = "cz",
            EntityType = "client",
            LastSyncAt = now,
            LastSyncDirection = "ff_to_partner",
            CreatedAt = now,
            UpdatedAt = now
        };
        await repo.SaveMappingAsync(mapping);

        var ids = await repo.GetPartnerClientIdsForRegionAsync("cz");
        Assert.Contains(partnerId, ids);
    }

    // ── GetMappingByPartnerClient ─────────────────────────────────────────

    [FactIfAzureSql]
    public async Task GetMappingByPartnerClient_ReturnsCorrectFfCompanyId()
    {
        var repo = _fix.CreateMappingRepository();
        var ffId = Guid.NewGuid();
        _fix.TrackMapping(ffId);

        var partnerId = 9902;
        var now = DateTime.UtcNow;
        var mapping = new IdMappingRecord
        {
            FfCompanyId = ffId,
            PartnerClientId = partnerId,
            PartnerRegion = "pl",
            EntityType = "client",
            LastSyncAt = now,
            LastSyncDirection = "ff_to_partner",
            CreatedAt = now,
            UpdatedAt = now
        };
        await repo.SaveMappingAsync(mapping);

        var found = await repo.GetMappingByPartnerClientAsync(partnerId, "pl");
        Assert.NotNull(found);
        Assert.Equal(ffId, found.FfCompanyId);
    }
}
