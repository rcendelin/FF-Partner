using Bridge.Domain.Models;

namespace Bridge.Infrastructure.Mapping;

public interface IBridgeMappingRepository
{
    Task<IdMappingRecord?> GetMappingAsync(Guid ffCompanyId, CancellationToken ct = default);
    Task SaveMappingAsync(IdMappingRecord mapping, CancellationToken ct = default);
    Task UpdateMappingAsync(IdMappingRecord mapping, CancellationToken ct = default);

    /// <summary>
    /// Vrátí všechna partner_client_id pro daný region.
    /// Používá Order poller pro filtraci tbl_order dotazů.
    /// Výsledek není cachován — voláme jednou per poll cyklus.
    /// </summary>
    Task<IReadOnlyList<int>> GetPartnerClientIdsForRegionAsync(string region, CancellationToken ct = default);

    /// <summary>
    /// Vyhledá mapping dle partner_client_id a regionu.
    /// Používá Order poller pro překlad id_client → FfCompanyId při publikování eventů.
    /// </summary>
    Task<IdMappingRecord?> GetMappingByPartnerClientAsync(int partnerClientId, string region, CancellationToken ct = default);
}
