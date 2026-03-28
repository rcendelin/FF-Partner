-- ============================================================
-- F0: DDL pro Bridge Azure SQL tabulky
-- Spustit JEDNOU na Azure SQL databázi Bridge (sdílená s FieldForce namespace).
-- Skript je IDEMPOTENTNÍ — tabulky i indexy mají individuální IF NOT EXISTS guardy.
--
-- Tabulky:
--   bridge_id_mapping   — mapování FieldForce GUID ↔ Partner3 idclient
--   bridge_sync_log     — audit log všech sync operací
--
-- POZOR: bridge_poll_watermark a bridge_order_snapshot jsou v sql/F4-01-polling-tables.sql
-- ============================================================

-- ──────────────────────────────────────────────────────────────
-- bridge_id_mapping
-- Klíčová tabulka: FF Company.Id (GUID) ↔ Partner3 idclient ↔ region
-- ──────────────────────────────────────────────────────────────

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'bridge_id_mapping')
BEGIN
    CREATE TABLE bridge_id_mapping (
        id                  INT             IDENTITY(1,1)   NOT NULL,
        ff_company_id       UNIQUEIDENTIFIER                NOT NULL,   -- FieldForce Company.Id (GUID)
        partner_client_id   INT                             NOT NULL,   -- tbl_client.idclient
        partner_region      VARCHAR(5)                      NOT NULL,   -- 'cz', 'pl', 'hu', 'us'
        entity_type         VARCHAR(20)                     NOT NULL    CONSTRAINT DF_bridge_id_mapping_entity_type DEFAULT 'client',
        pipedrive_id        BIGINT                          NULL,       -- Pro migraci: Pipedrive Org ID
        ff_user_id          UNIQUEIDENTIFIER                NULL,       -- FieldForce User.Id (owner)
        partner_owner_id    INT                             NULL,       -- id_owner v Partner DB
        last_sync_at        DATETIME2(3)                    NOT NULL,   -- DATETIME2(3) = ms přesnost
        last_sync_direction VARCHAR(10)                     NOT NULL,   -- 'ff_to_partner'
        created_at          DATETIME2(3)                    NOT NULL,
        updated_at          DATETIME2(3)                    NOT NULL,

        CONSTRAINT PK_bridge_id_mapping PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_bridge_id_mapping_company UNIQUE (ff_company_id, entity_type)
    );

    PRINT 'bridge_id_mapping: tabulka vytvořena.';
END
ELSE
BEGIN
    PRINT 'bridge_id_mapping: tabulka již existuje, přeskočeno.';
END
GO

-- Indexy bridge_id_mapping — každý má vlastní guard
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_bridge_id_mapping_partner' AND object_id = OBJECT_ID('bridge_id_mapping'))
BEGIN
    -- Lookup podle Partner klienta + region (order polling: GetMappingByPartnerClientAsync)
    CREATE INDEX IX_bridge_id_mapping_partner
        ON bridge_id_mapping (partner_client_id, partner_region);
    PRINT 'IX_bridge_id_mapping_partner: vytvořen.';
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_bridge_id_mapping_pipedrive' AND object_id = OBJECT_ID('bridge_id_mapping'))
BEGIN
    -- Lookup pro migraci: match FieldForce Company.PipedriveId → bridge_id_mapping
    CREATE INDEX IX_bridge_id_mapping_pipedrive
        ON bridge_id_mapping (pipedrive_id)
        WHERE pipedrive_id IS NOT NULL;
    PRINT 'IX_bridge_id_mapping_pipedrive: vytvořen.';
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_bridge_id_mapping_region_entity' AND object_id = OBJECT_ID('bridge_id_mapping'))
BEGIN
    -- GetPartnerClientIdsForRegionAsync (order polling — pre-query před MySQL)
    CREATE INDEX IX_bridge_id_mapping_region_entity
        ON bridge_id_mapping (partner_region, entity_type)
        INCLUDE (partner_client_id);
    PRINT 'IX_bridge_id_mapping_region_entity: vytvořen.';
END
GO

-- ──────────────────────────────────────────────────────────────
-- bridge_sync_log
-- Audit log všech sync operací Bridge.
-- Operace: create, update, disable, contact_update, owner_change,
--          region_change, pending_region_change, order_poll,
--          order_backfill, gaia_processing_error, geo_validation_warning
-- Status: success, failed, retry, warning, in_progress
-- ──────────────────────────────────────────────────────────────

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'bridge_sync_log')
BEGIN
    CREATE TABLE bridge_sync_log (
        id                      BIGINT          IDENTITY(1,1)   NOT NULL,
        ff_company_id           UNIQUEIDENTIFIER                NULL,
        partner_client_id       INT                             NULL,
        partner_region          VARCHAR(5)                      NULL,   -- 'cz', 'pl', 'hu', 'us'
        operation               VARCHAR(30)                     NOT NULL,
        service_bus_message_id  VARCHAR(100)                    NULL,
        status                  VARCHAR(20)                     NOT NULL,
        error_message           NVARCHAR(MAX)                   NULL,
        payload_json            NVARCHAR(MAX)                   NULL,
        severity                VARCHAR(10)                     NOT NULL    CONSTRAINT DF_bridge_sync_log_severity DEFAULT 'Info',
        created_at              DATETIME2(3)                    NOT NULL,   -- DATETIME2(3) = ms přesnost

        CONSTRAINT PK_bridge_sync_log PRIMARY KEY CLUSTERED (id)
    );

    PRINT 'bridge_sync_log: tabulka vytvořena.';
END
ELSE
BEGIN
    PRINT 'bridge_sync_log: tabulka již existuje, přeskočeno.';
END
GO

-- Indexy bridge_sync_log — každý má vlastní guard

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_bridge_sync_log_created' AND object_id = OBJECT_ID('bridge_sync_log'))
BEGIN
    -- GetLastAsync (nejpoužívanější query — DESC sort, TOP @Count)
    CREATE INDEX IX_bridge_sync_log_created
        ON bridge_sync_log (created_at DESC);
    PRINT 'IX_bridge_sync_log_created: vytvořen.';
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_bridge_sync_log_ff_company' AND object_id = OBJECT_ID('bridge_sync_log'))
BEGIN
    -- Lookup podle FieldForce Company GUID (history view per firma)
    CREATE INDEX IX_bridge_sync_log_ff_company
        ON bridge_sync_log (ff_company_id)
        WHERE ff_company_id IS NOT NULL;
    PRINT 'IX_bridge_sync_log_ff_company: vytvořen.';
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_bridge_sync_log_operation_region_status' AND object_id = OBJECT_ID('bridge_sync_log'))
BEGIN
    -- HasOperationSucceededAsync — idempotence check pro order_backfill per region
    -- (operation, partner_region, status) = přesná shoda pro EXISTS query
    CREATE INDEX IX_bridge_sync_log_operation_region_status
        ON bridge_sync_log (operation, partner_region, status);
    PRINT 'IX_bridge_sync_log_operation_region_status: vytvořen.';
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_bridge_sync_log_status_pending' AND object_id = OBJECT_ID('bridge_sync_log'))
BEGIN
    -- GetPendingSagasAsync — recovery při startu: hledá 'pending_region_change' + 'in_progress'
    -- z posledních 7 dní (DATEADD filter — index pokrývá sloupce pro smysl dotazu)
    CREATE INDEX IX_bridge_sync_log_status_pending
        ON bridge_sync_log (status, created_at)
        INCLUDE (ff_company_id, partner_client_id, partner_region, operation);
    PRINT 'IX_bridge_sync_log_status_pending: vytvořen.';
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_bridge_sync_log_message_id' AND object_id = OBJECT_ID('bridge_sync_log'))
BEGIN
    -- Idempotence per Service Bus message — dedup při redelivery (max 5 pokusů)
    -- Umožňuje rychlý lookup: "byl tento konkrétní messageId již zpracován?"
    CREATE INDEX IX_bridge_sync_log_message_id
        ON bridge_sync_log (service_bus_message_id)
        WHERE service_bus_message_id IS NOT NULL;
    PRINT 'IX_bridge_sync_log_message_id: vytvořen.';
END
GO

-- ──────────────────────────────────────────────────────────────
-- Ověření — seznam vytvořených tabulek a indexů
-- ──────────────────────────────────────────────────────────────

SELECT
    t.name AS table_name,
    i.name AS index_name,
    i.type_desc
FROM sys.tables t
JOIN sys.indexes i ON t.object_id = i.object_id
WHERE t.name IN ('bridge_id_mapping', 'bridge_sync_log')
  AND i.type > 0  -- excludovat heap
ORDER BY t.name, i.name;
GO

-- ──────────────────────────────────────────────────────────────
-- POZNÁMKY k provozu:
-- • DATETIME2(3) = milisekundová přesnost (doporučení MS pro nové schémata)
-- • bridge_id_mapping: cachována v Bridge (IMemoryCache, TTL 5 min)
-- • bridge_sync_log: bez TTL — zvážit archivaci starých záznamů po 90 dnech
-- • IX_bridge_sync_log_message_id: umožňuje dedup při retry (max 5 Service Bus pokusů)
-- • IX_bridge_sync_log_operation_region_status: pro HasOperationSucceededAsync
-- • IX_bridge_sync_log_status_pending: pro GetPendingSagasAsync (startup recovery)
-- ============================================================
