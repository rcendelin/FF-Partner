using Bridge.Application.Interfaces;
using Bridge.Domain.Messages;
using Bridge.Domain.Models;
using Bridge.Infrastructure.Mapping;
using Bridge.Infrastructure.Partner.Repositories;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Bridge.Api.Sagas;

/// <summary>
/// Saga pro přesun firmy mezi regionálními Partner3 DB při změně CountryCode.
///
/// Kritická sekvence (dle CLAUDE.md sekce 9):
///   Krok 1: INSERT do cílové DB → chyba → STOP (HasNoSideEffects)
///   Krok 2: Log 'pending_region_change'
///   Krok 3: DISABLE v původní DB → chyba → DELETE z cílové → CompensatedAtStep3
///   Krok 4: UpdateMappingAsync → chyba → EnableAsync původní + DELETE z cílové → CompensatedAtStep4
///   Krok 5: Publish bridge.company.synced
///
/// Při startu Bridge: <see cref="SagaRecoveryService"/> detekuje nedokončené ságy a doběhne je.
/// </summary>
public sealed class MoveClientToRegionSaga
{
    private readonly IPartnerClientRepository _partnerRepo;
    private readonly IBridgeMappingRepository _mappingRepo;
    private readonly ISyncLogRepository _syncLog;
    private readonly IServiceBusPublisher _publisher;
    private readonly ILogger<MoveClientToRegionSaga> _logger;

    public MoveClientToRegionSaga(
        IPartnerClientRepository partnerRepo,
        IBridgeMappingRepository mappingRepo,
        ISyncLogRepository syncLog,
        IServiceBusPublisher publisher,
        ILogger<MoveClientToRegionSaga> logger)
    {
        _partnerRepo = partnerRepo;
        _mappingRepo = mappingRepo;
        _syncLog = syncLog;
        _publisher = publisher;
        _logger = logger;
    }

    /// <summary>
    /// Spustí přesun klienta z <paramref name="sourceRegion"/> do <paramref name="targetRegion"/>.
    /// Vstupní <paramref name="updatedClient"/> musí mít všechna pole nastavena na nové hodnoty
    /// (adresa, kontakt, geo FK) — volaný je zodpovědný za přípravu stavu.
    /// </summary>
    public async Task<SagaResult> ExecuteAsync(
        PartnerClient updatedClient,
        string sourceRegion,
        string targetRegion,
        IdMappingRecord existingMapping,
        string sbMessageId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        _logger.LogInformation(
            "Saga START: CompanyId={CompanyId} přesun {Source}→{Target} (sourcePartnerId={SourceId})",
            updatedClient.FfCompanyId, sourceRegion, targetRegion, existingMapping.PartnerClientId);

        // ── Krok 1: INSERT do cílové DB ──────────────────────────────────────────────
        // Idempotence guard: zkontrolovat zda záznam již existuje v cílové DB (partial failure / restart).
        // Při chybě: STOP — klient zůstane aktivní v původním regionu, žádné trvalé změny.
        int targetPartnerId;
        try
        {
            // Kontrola: pokud záznam s ff_company_id již v cílové DB existuje, použít ho (idempotence).
            // Zabraňuje double-INSERT při race condition (SagaRecovery + Consumer zpracovávají zároveň).
            var existingInTarget = await _partnerRepo.GetByFfCompanyIdAsync(
                updatedClient.FfCompanyId!.Value, targetRegion, ct);

            if (existingInTarget is not null)
            {
                _logger.LogWarning(
                    "Saga Krok 1: záznam ff_company_id={CompanyId} již existuje v cílové DB {Target} (id={Id}) — " +
                    "přeskakuji INSERT (idempotence / partial failure recovery).",
                    updatedClient.FfCompanyId, targetRegion, existingInTarget.IdClient);
                targetPartnerId = existingInTarget.IdClient;
            }
            else
            {
                // Klonovat klienta pro cílový region — resetovat IdClient (nový INSERT)
                var targetClient = BuildTargetClient(updatedClient, now);
                targetPartnerId = await _partnerRepo.InsertAsync(targetClient, targetRegion, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Saga Krok 1 SELHAL (INSERT do {Target}) — CompanyId={CompanyId}, žádné změny",
                targetRegion, updatedClient.FfCompanyId);

            await WriteSyncLogAsync(updatedClient.FfCompanyId, existingMapping.PartnerClientId,
                sourceRegion, "region_change_failed", sbMessageId, "failed",
                $"Krok 1 (INSERT do {targetRegion}) selhal: {ex.Message}");

            return new SagaResult
            {
                Outcome = SagaOutcome.FailedAtStep1_NoChanges,
                ErrorMessage = ex.Message
            };
        }

        _logger.LogInformation(
            "Saga Krok 1 OK: targetPartnerId={TargetId} v regionu {Target}",
            targetPartnerId, targetRegion);

        // ── Krok 2: Zapsat pending_region_change ──────────────────────────────────────
        var payload = JsonSerializer.Serialize(new
        {
            sourceRegion,
            targetRegion,
            sourcePartnerId = existingMapping.PartnerClientId,
            targetPartnerId
            // sbMessageId není potřeba — service_bus_message_id DB sloupec ho uchovává
        });

        // CancellationToken.None — tento log MUSÍ být zapsán jako transakční marker
        await WriteSyncLogAsync(updatedClient.FfCompanyId, existingMapping.PartnerClientId,
            sourceRegion, "pending_region_change", sbMessageId, "in_progress",
            $"INSERT do {targetRegion} hotov (targetId={targetPartnerId}), čeká na DISABLE v {sourceRegion}.",
            payload, CancellationToken.None);

        // ── Krok 3: DISABLE v původní DB ─────────────────────────────────────────────
        // Při chybě: DELETE z cílové DB (kompenzace)
        try
        {
            await _partnerRepo.DisableAsync(existingMapping.PartnerClientId, sourceRegion, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Saga Krok 3 SELHAL (DISABLE v {Source}) — kompenzuji DELETE z {Target} (id={TargetId})",
                sourceRegion, targetRegion, targetPartnerId);

            await CompensateDeleteFromTargetAsync(
                targetPartnerId, targetRegion,
                updatedClient.FfCompanyId, existingMapping.PartnerClientId, sourceRegion, sbMessageId,
                $"Krok 3 selhal: {ex.Message}");

            return new SagaResult
            {
                Outcome = SagaOutcome.CompensatedAtStep3,
                ErrorMessage = ex.Message
            };
        }

        _logger.LogInformation(
            "Saga Krok 3 OK: DISABLE sourcePartnerId={SourceId} v {Source}",
            existingMapping.PartnerClientId, sourceRegion);

        // ── Krok 4: UPDATE bridge_id_mapping ─────────────────────────────────────────
        // Při chybě: EnableAsync původní + DELETE z cílové (kompenzace)
        try
        {
            var updatedMapping = new IdMappingRecord
            {
                FfCompanyId = existingMapping.FfCompanyId,
                PartnerClientId = targetPartnerId,
                PartnerRegion = targetRegion,
                EntityType = existingMapping.EntityType,
                PipedriveId = existingMapping.PipedriveId,
                FfUserId = existingMapping.FfUserId,
                PartnerOwnerId = existingMapping.PartnerOwnerId,
                LastSyncAt = now,
                LastSyncDirection = "ff_to_partner",
                CreatedAt = existingMapping.CreatedAt,
                UpdatedAt = now
            };
            await _mappingRepo.UpdateMappingAsync(updatedMapping, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Saga Krok 4 SELHAL (UpdateMapping) — kompenzuji: Enable {Source}/{SourceId}, Delete {Target}/{TargetId}",
                sourceRegion, existingMapping.PartnerClientId, targetRegion, targetPartnerId);

            await CompensateEnableSourceDeleteTargetAsync(
                existingMapping.PartnerClientId, sourceRegion,
                targetPartnerId, targetRegion,
                updatedClient.FfCompanyId, existingMapping.PartnerClientId, sbMessageId,
                $"Krok 4 selhal: {ex.Message}");

            return new SagaResult
            {
                Outcome = SagaOutcome.CompensatedAtStep4,
                ErrorMessage = ex.Message
            };
        }

        _logger.LogInformation(
            "Saga Krok 4 OK: mapping přepsán na {Target}/{TargetId}",
            targetRegion, targetPartnerId);

        // ── Krok 5: Publish bridge.company.synced ────────────────────────────────────
        try
        {
            await _publisher.PublishAsync("bridge.company.synced", new CompanySyncedResponse
            {
                MessageId = Guid.NewGuid().ToString(),
                SentAt = DateTimeOffset.UtcNow,
                FfCompanyId = existingMapping.FfCompanyId,
                PartnerClientId = targetPartnerId,
                PartnerRegion = targetRegion,
                Action = "RegionChange"
            }, sbMessageId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Publish selhal — data jsou konzistentní (mapping + DB jsou správné), jen FieldForce nedostal potvrzení.
            // Logujeme Warning (ne Error) — příští sync opraví stav.
            _logger.LogWarning(ex,
                "Saga Krok 5: publish bridge.company.synced selhal pro CompanyId={CompanyId}. Data jsou konzistentní.",
                updatedClient.FfCompanyId);
        }

        // ── Úspěch ───────────────────────────────────────────────────────────────────
        await WriteSyncLogAsync(updatedClient.FfCompanyId, targetPartnerId,
            targetRegion, "region_change", sbMessageId, "success",
            $"Přesun z {sourceRegion}/{existingMapping.PartnerClientId} do {targetRegion}/{targetPartnerId} dokončen.",
            cancellationToken: CancellationToken.None);

        _logger.LogInformation(
            "Saga ÚSPĚCH: CompanyId={CompanyId} přesunuto {Source}→{Target} (sourceId={SourceId}, targetId={TargetId})",
            updatedClient.FfCompanyId, sourceRegion, targetRegion,
            existingMapping.PartnerClientId, targetPartnerId);

        return new SagaResult { Outcome = SagaOutcome.Success };
    }

    // ── Recovery metoda — volána z SagaRecoveryService při startu ────────────────────

    /// <summary>
    /// Pokusí se doběhnout nebo zkompenzovat nedokončenou ságu po restartu Bridge.
    /// Detekuje aktuální stav z DB a mappingu a rozhodne o dalším postupu.
    /// </summary>
    public async Task RecoverAsync(
        SyncLogEntry pendingSaga,
        CancellationToken ct)
    {
        if (pendingSaga.FfCompanyId is null || string.IsNullOrEmpty(pendingSaga.PayloadJson))
        {
            _logger.LogWarning(
                "Recovery: prázdný payload v pending_region_change záznamu (id created_at={CreatedAt}) — přeskakuji",
                pendingSaga.CreatedAt);
            return;
        }

        SagaPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<SagaPayload>(pendingSaga.PayloadJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Deserializace payload vrátila null");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Recovery: nepodařilo se deserializovat payload pro CompanyId={CompanyId}",
                pendingSaga.FfCompanyId);
            return;
        }

        _logger.LogInformation(
            "Recovery: zpracovávám nedokončenou ságu CompanyId={CompanyId} {Source}→{Target} " +
            "(sourceId={SourceId}, targetId={TargetId})",
            pendingSaga.FfCompanyId, payload.SourceRegion, payload.TargetRegion,
            payload.SourcePartnerId, payload.TargetPartnerId);

        // Zjistit aktuální stav
        var mapping = await _mappingRepo.GetMappingAsync(pendingSaga.FfCompanyId.Value, ct);
        var targetExists = await _partnerRepo.GetByPartnerIdAsync(payload.TargetPartnerId, payload.TargetRegion, ct);
        var sourceClient = await _partnerRepo.GetByPartnerIdAsync(payload.SourcePartnerId, payload.SourceRegion, ct);

        // Případ A: Mapping již ukazuje na target region → saga se dokončila (nebo ji opravil jiný restart)
        if (mapping?.PartnerRegion == payload.TargetRegion && mapping?.PartnerClientId == payload.TargetPartnerId)
        {
            _logger.LogInformation(
                "Recovery A: mapping již ukazuje na {Target}/{TargetId} — saga dokončena, zapisuji region_change/success",
                payload.TargetRegion, payload.TargetPartnerId);

            await WriteSyncLogAsync(pendingSaga.FfCompanyId, payload.TargetPartnerId,
                payload.TargetRegion, "region_change", pendingSaga.ServiceBusMessageId, "success",
                "Recovery: detekováno jako již dokončeno.", cancellationToken: CancellationToken.None);
            return;
        }

        // Případ B: Target existuje, source je disabled, mapping ještě nebyl aktualizován → doběhnout od Kroku 4
        if (targetExists is not null && sourceClient?.ClientDisable == 1)
        {
            _logger.LogInformation(
                "Recovery B: target={TargetId} v {Target} existuje, source={SourceId} je DISABLED → pokračuji od Kroku 4",
                payload.TargetPartnerId, payload.TargetRegion, payload.SourcePartnerId);

            try
            {
                // Zachovat PipedriveId, FfUserId, PartnerOwnerId z existujícího mappingu (dle CLAUDE.md — nemodifikovat historické hodnoty)
                var updatedMapping = new IdMappingRecord
                {
                    FfCompanyId = pendingSaga.FfCompanyId.Value,
                    PartnerClientId = payload.TargetPartnerId,
                    PartnerRegion = payload.TargetRegion,
                    EntityType = mapping?.EntityType ?? "client",
                    PipedriveId = mapping?.PipedriveId,          // zachovat historické Pipedrive ID
                    FfUserId = mapping?.FfUserId,                // zachovat FF user
                    PartnerOwnerId = mapping?.PartnerOwnerId,    // zachovat owner
                    LastSyncAt = DateTime.UtcNow,
                    LastSyncDirection = "ff_to_partner",
                    CreatedAt = mapping?.CreatedAt ?? DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                await _mappingRepo.UpdateMappingAsync(updatedMapping, ct);

                await WriteSyncLogAsync(pendingSaga.FfCompanyId, payload.TargetPartnerId,
                    payload.TargetRegion, "region_change", pendingSaga.ServiceBusMessageId, "success",
                    "Recovery B: mapping opraven.", cancellationToken: CancellationToken.None);

                _logger.LogInformation("Recovery B ÚSPĚCH: mapping opraven pro CompanyId={CompanyId}", pendingSaga.FfCompanyId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Recovery B SELHAL: UpdateMapping pro CompanyId={CompanyId}",
                    pendingSaga.FfCompanyId);
            }
            return;
        }

        // Případ C: Target existuje, source je stále aktivní → Krok 3 selhal, kompenzovat
        if (targetExists is not null && (sourceClient is null || sourceClient.ClientDisable == 0))
        {
            _logger.LogWarning(
                "Recovery C: target={TargetId} existuje, source={SourceId} je stále AKTIVNÍ → kompenzuji DELETE z {Target}",
                payload.TargetPartnerId, payload.SourcePartnerId, payload.TargetRegion);

            await CompensateDeleteFromTargetAsync(
                payload.TargetPartnerId, payload.TargetRegion,
                pendingSaga.FfCompanyId, payload.SourcePartnerId, payload.SourceRegion,
                pendingSaga.ServiceBusMessageId, "Recovery C: source stále aktivní, DELETE target.");
            return;
        }

        // Případ D: Target neexistuje → INSERT selhal nebo byl zkompenzován → zapisuji jako vyřešeno
        _logger.LogInformation(
            "Recovery D: target={TargetId} v {Target} neexistuje → INSERT selhal nebo zkompenzován. Zapisuji region_change/compensated",
            payload.TargetPartnerId, payload.TargetRegion);

        await WriteSyncLogAsync(pendingSaga.FfCompanyId, payload.SourcePartnerId,
            payload.SourceRegion, "region_change", pendingSaga.ServiceBusMessageId, "compensated",
            "Recovery D: target neexistuje — saga považována za zkompenzovanou.", cancellationToken: CancellationToken.None);
    }

    // ── Privátní helpers ─────────────────────────────────────────────────────────────

    private static PartnerClient BuildTargetClient(PartnerClient source, DateTime now) => new()
    {
        // IdClient = 0 — nový INSERT, DB přidělí nové ID
        ClientFirm = source.ClientFirm,
        ClientIc = source.ClientIc,
        ClientDic = source.ClientDic,
        ClientStreet = source.ClientStreet,
        ClientCity = source.ClientCity,
        ClientPsc = source.ClientPsc,
        ClientCountryId = source.ClientCountryId,
        ClientCountryShort = source.ClientCountryShort,
        ClientState = source.ClientState,
        ClientStateId = source.ClientStateId,
        ClientCounty = source.ClientCounty,
        ClientCountyId = source.ClientCountyId,
        ClientZipId = source.ClientZipId,
        ClientPhone = source.ClientPhone,
        ClientMail = source.ClientMail,
        ClientRight = source.ClientRight,
        ClientDate = now,   // client_date: nastavit pouze při INSERT
        ClientDisable = 0,
        IdOwner = source.IdOwner,
        FfCompanyId = source.FfCompanyId,
        FfSyncSource = "FF",
        DataOwner = Domain.Enums.DataOwner.FieldForce,
        LastFfSyncAt = now
    };

    private async Task CompensateDeleteFromTargetAsync(
        int targetPartnerId, string targetRegion,
        Guid? ffCompanyId, int sourcePartnerId, string sourceRegion,
        string? sbMessageId, string reason)
    {
        try
        {
            await _partnerRepo.DeleteAsync(targetPartnerId, targetRegion, CancellationToken.None);

            _logger.LogInformation(
                "Kompenzace OK: DELETE targetPartnerId={TargetId} z {Target}. Klient {SourceId} v {Source} zůstal aktivní.",
                targetPartnerId, targetRegion, sourcePartnerId, sourceRegion);

            await WriteSyncLogAsync(ffCompanyId, sourcePartnerId, sourceRegion,
                "region_change", sbMessageId, "compensated",
                $"CompensatedAtStep3: DELETE z {targetRegion}/{targetPartnerId}. Důvod: {reason}",
                cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "KOMPENZACE SELHALA: nepodařilo se DELETE targetPartnerId={TargetId} z {Target}. " +
                "Klient existuje v OBOU regionech! Nutný manuální zásah.",
                targetPartnerId, targetRegion);

            await WriteSyncLogAsync(ffCompanyId, targetPartnerId, targetRegion,
                "region_change", sbMessageId, "compensation_failed",
                $"CRITICAL: DELETE z {targetRegion}/{targetPartnerId} selhal: {ex.Message}. Duplikát v DB!",
                cancellationToken: CancellationToken.None);
        }
    }

    private async Task CompensateEnableSourceDeleteTargetAsync(
        int sourcePartnerId, string sourceRegion,
        int targetPartnerId, string targetRegion,
        Guid? ffCompanyId, int logPartnerId, string? sbMessageId,
        string reason)
    {
        var enableOk = false;
        var deleteOk = false;

        try
        {
            await _partnerRepo.EnableAsync(sourcePartnerId, sourceRegion, CancellationToken.None);
            enableOk = true;
            _logger.LogInformation(
                "Kompenzace: EnableAsync {Source}/{SourceId} OK",
                sourceRegion, sourcePartnerId);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "KOMPENZACE SELHALA: EnableAsync {Source}/{SourceId}. Klient je DISABLED ale mapping je starý! " +
                "Delete z cílové DB NEBUDE proveden (bezpečnější: klient aktivní v cílové, než nikde).",
                sourceRegion, sourcePartnerId);
            // CRITICAL: Delete NESMÍ proběhnout pokud Enable selhal.
            // Raději nechat klienta aktivního v OBOU regionech (detekovatelný stav) než nikde (nedetekovatelný stav).
        }

        if (enableOk)
        {
            // Delete z cílové DB POUZE pokud Enable v původní uspěl — jinak by firma zmizela ze všech regionů.
            try
            {
                await _partnerRepo.DeleteAsync(targetPartnerId, targetRegion, CancellationToken.None);
                deleteOk = true;
                _logger.LogInformation(
                    "Kompenzace: DeleteAsync {Target}/{TargetId} OK",
                    targetRegion, targetPartnerId);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex,
                    "KOMPENZACE SELHALA: DeleteAsync {Target}/{TargetId}. Klient existuje v OBOU regionech!",
                    targetRegion, targetPartnerId);
            }
        }

        var status = (enableOk && deleteOk) ? "compensated" : "compensation_failed";
        var msg = enableOk && deleteOk
            ? $"CompensatedAtStep4: Enable {sourceRegion}/{sourcePartnerId}, Delete {targetRegion}/{targetPartnerId}. Důvod: {reason}"
            : $"CRITICAL: Kompenzace Kroku 4 selhala (enable={enableOk}, delete={deleteOk}). Nutný manuální zásah! Důvod: {reason}";

        await WriteSyncLogAsync(ffCompanyId, logPartnerId, sourceRegion,
            "region_change", sbMessageId, status, msg, cancellationToken: CancellationToken.None);
    }

    private Task WriteSyncLogAsync(
        Guid? ffCompanyId, int? partnerId, string? region,
        string operation, string? sbMessageId, string status, string? errorMessage,
        string? payloadJson = null, CancellationToken cancellationToken = default)
        => _syncLog.WriteAsync(new SyncLogEntry
        {
            FfCompanyId = ffCompanyId,
            PartnerClientId = partnerId,
            PartnerRegion = region,
            Operation = operation,
            ServiceBusMessageId = sbMessageId,
            Status = status,
            ErrorMessage = errorMessage,
            PayloadJson = payloadJson,
            Severity = status is "failed" or "compensation_failed" ? "Error"
                     : status == "warning" ? "Warning"
                     : "Info"
        }, cancellationToken);

    // DTO pro deserializaci payload_json v recovery
    private sealed class SagaPayload
    {
        public string SourceRegion { get; set; } = string.Empty;
        public string TargetRegion { get; set; } = string.Empty;
        public int SourcePartnerId { get; set; }
        public int TargetPartnerId { get; set; }
    }
}
