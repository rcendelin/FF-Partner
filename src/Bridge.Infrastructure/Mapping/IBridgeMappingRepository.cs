using Bridge.Domain.Models;

namespace Bridge.Infrastructure.Mapping;

public interface IBridgeMappingRepository
{
    Task<IdMappingRecord?> GetMappingAsync(Guid ffCompanyId, CancellationToken ct = default);
    Task SaveMappingAsync(IdMappingRecord mapping, CancellationToken ct = default);
    Task UpdateMappingAsync(IdMappingRecord mapping, CancellationToken ct = default);
}
