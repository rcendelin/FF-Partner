-- =============================================================================
-- Migrace: 2026-05-07
-- Cíl:     Přenést kritické řádky z bridge_sync_log do PartnerSyncLog před cut-over.
-- Kontext: PR "Replace ISyncLogRepository with IPartnerSyncLog" — eliminuje duální
--          zápis do bridge_sync_log a PartnerSyncLog. Po deployi nové verze Bridge
--          bude PartnerSyncLog jediným zdrojem pravdy.
--
-- Tento skript NENÍ idempotentní pro celkový stav PartnerSyncLog — INSERTuje řádky,
-- jejichž duplikáty by se v dotazech projevily. Spustit POUZE JEDNOU, před deployem.
-- =============================================================================
--
-- POŘADÍ SPUŠTĚNÍ (operations):
--   1. Pause inbound queues (ff.company.sync, ff.contact.updated,
--      ff.company.owner-changed, ff.company.disabled).
--   2. Počkat, než doběhne aktuálně zpracovávaná zpráva (≤ 5 min).
--   3. Spustit tento skript proti FieldForce Azure SQL.
--   4. Deploy nové verze Bridge.
--   5. Resume inbound queues.
--
-- =============================================================================

SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- -----------------------------------------------------------------------------
-- 1) Migrace nedokončených region-change ság (pending_region_change → SagaPending).
--    Bridge SagaRecoveryService po startu hledá v PartnerSyncLog řádky
--    Operation='region_change' AND Status='InProgress' bez pozdějšího terminálního
--    statusu. Bez této migrace by ságy nedoběhly po prvním restartu po deployi.
--
--    Filtr: 7 dní zpět, pouze 'in_progress' bez následného 'region_change'
--    se status IN ('success','compensated','compensation_failed').
-- -----------------------------------------------------------------------------
INSERT INTO PartnerSyncLog
    (CompanyId, CorrelationMessageId, Phase, Direction, Operation, Status,
     PartnerClientId, PartnerRegion, ErrorCode, ErrorMessage, PayloadJson,
     Source, CreatedAt)
SELECT
    l.ff_company_id            AS CompanyId,
    ISNULL(l.service_bus_message_id, CONVERT(NVARCHAR(36), NEWID())) AS CorrelationMessageId,
    'SagaPending'              AS Phase,
    'Internal'                 AS Direction,
    'region_change'            AS Operation,
    'InProgress'               AS Status,
    l.partner_client_id        AS PartnerClientId,
    l.partner_region           AS PartnerRegion,
    NULL                       AS ErrorCode,
    l.error_message            AS ErrorMessage,
    l.payload_json             AS PayloadJson,
    'Bridge'                   AS Source,
    CAST(l.created_at AS DATETIMEOFFSET) AS CreatedAt
FROM bridge_sync_log l
WHERE l.operation = 'pending_region_change'
  AND l.status = 'in_progress'
  AND l.ff_company_id IS NOT NULL
  AND l.created_at > DATEADD(day, -7, GETUTCDATE())
  AND NOT EXISTS (
      SELECT 1 FROM bridge_sync_log l2
      WHERE l2.ff_company_id = l.ff_company_id
        AND l2.operation = 'region_change'
        AND l2.created_at > l.created_at
  );

PRINT N'Migrated pending sagas: ' + CAST(@@ROWCOUNT AS NVARCHAR(10));

-- -----------------------------------------------------------------------------
-- 2) Migrace order_backfill úspěchů (idempotency key pro OrderBackfillService).
--    Bez této migrace by se backfill po deployi pokusil znovu pro každý region.
--    Vybíráme pouze NEJNOVĚJŠÍ úspěšný řádek per (operation, partner_region) —
--    duplicitní vstupy v bridge_sync_log nemají vliv na logiku idempotence
--    (dotaz používá EXISTS, ne COUNT).
-- -----------------------------------------------------------------------------
WITH LatestBackfill AS (
    SELECT
        ff_company_id, partner_client_id, partner_region,
        operation, service_bus_message_id, error_message, payload_json,
        created_at,
        ROW_NUMBER() OVER (
            PARTITION BY operation, partner_region
            ORDER BY created_at DESC
        ) AS rn
    FROM bridge_sync_log
    WHERE operation = 'order_backfill'
      AND status = 'success'
)
INSERT INTO PartnerSyncLog
    (CompanyId, CorrelationMessageId, Phase, Direction, Operation, Status,
     PartnerClientId, PartnerRegion, ErrorCode, ErrorMessage, PayloadJson,
     Source, CreatedAt)
SELECT
    ISNULL(l.ff_company_id, '00000000-0000-0000-0000-000000000000') AS CompanyId,
    ISNULL(l.service_bus_message_id,
           CONCAT('backfill-migrated-', l.partner_region, '-',
                  CONVERT(NVARCHAR(20), l.created_at, 121))) AS CorrelationMessageId,
    'BackfillCompleted'        AS Phase,
    'Internal'                 AS Direction,
    l.operation                AS Operation,
    'Success'                  AS Status,
    l.partner_client_id        AS PartnerClientId,
    l.partner_region           AS PartnerRegion,
    NULL                       AS ErrorCode,
    l.error_message            AS ErrorMessage,
    l.payload_json             AS PayloadJson,
    'Bridge'                   AS Source,
    CAST(l.created_at AS DATETIMEOFFSET) AS CreatedAt
FROM LatestBackfill l
WHERE l.rn = 1;

PRINT N'Migrated backfill rows: ' + CAST(@@ROWCOUNT AS NVARCHAR(10));

-- -----------------------------------------------------------------------------
-- Verifikace (read-only, ponechat zakomentováno v produkci):
-- -----------------------------------------------------------------------------
-- SELECT TOP 50 *
-- FROM PartnerSyncLog
-- WHERE Source = 'Bridge'
--   AND (Phase IN ('SagaPending', 'BackfillCompleted'))
-- ORDER BY CreatedAt DESC;

COMMIT TRANSACTION;
