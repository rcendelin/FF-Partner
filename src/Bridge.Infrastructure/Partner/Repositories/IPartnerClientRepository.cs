using Bridge.Domain.Models;

namespace Bridge.Infrastructure.Partner.Repositories;

public interface IPartnerClientRepository
{
    Task<PartnerClient?> GetByFfCompanyIdAsync(Guid ffCompanyId, string region, CancellationToken ct = default);
    Task<PartnerClient?> GetByPartnerIdAsync(int partnerId, string region, CancellationToken ct = default);
    Task<int> InsertAsync(PartnerClient client, string region, CancellationToken ct = default);
    Task UpdateAsync(PartnerClient client, string region, CancellationToken ct = default);
    Task DisableAsync(int partnerId, string region, CancellationToken ct = default);
}
