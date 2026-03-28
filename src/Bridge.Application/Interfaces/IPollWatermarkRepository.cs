using Bridge.Domain.Models;

namespace Bridge.Application.Interfaces;

/// <summary>
/// Ukládá a načítá watermark pro polling nových objednávek (bridge_poll_watermark, Azure SQL).
/// </summary>
public interface IPollWatermarkRepository
{
    /// <summary>Vrátí aktuální watermark pro daný target, nebo null pokud záznam neexistuje.</summary>
    Task<PollWatermark?> GetAsync(string pollTarget, CancellationToken ct = default);

    /// <summary>INSERT OR UPDATE — pokud záznam pro daný target existuje, aktualizuje ho.</summary>
    Task UpsertAsync(PollWatermark watermark, CancellationToken ct = default);
}
