using Bridge.Domain.Models;

namespace Bridge.Infrastructure.Partner.Repositories;

public interface IPartnerClientRepository
{
    Task<PartnerClient?> GetByFfCompanyIdAsync(Guid ffCompanyId, string region, CancellationToken ct = default);
    Task<PartnerClient?> GetByPartnerIdAsync(int partnerId, string region, CancellationToken ct = default);
    Task<int> InsertAsync(PartnerClient client, string region, CancellationToken ct = default);
    Task UpdateAsync(PartnerClient client, string region, CancellationToken ct = default);
    Task DisableAsync(int partnerId, string region, CancellationToken ct = default);

    /// <summary>Re-aktivuje zákazníka — používá se jako kompenzační akce v MoveClientToRegionSaga.</summary>
    Task EnableAsync(int partnerId, string region, CancellationToken ct = default);

    /// <summary>Tvrdě smaže záznam — POUZE jako kompenzace v MoveClientToRegionSaga (čerstvě vložený záznam bez objednávek).</summary>
    Task DeleteAsync(int partnerId, string region, CancellationToken ct = default);
}
