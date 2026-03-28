-- ============================================================
-- F0-02: DDL migrace — rozšíření tbl_client pro FF Bridge
-- Spustit na VŠECH 4 regionálních Partner3 MySQL databázích:
--   CZ: 172.24.0.12 / db_cz
--   PL: 172.24.0.12 / db_pl
--   HU: 172.24.0.12 / db_hu
--   US: 172.24.0.12 / db_us
--
-- Skript je IDEMPOTENTNÍ — bezpečné opakované spuštění.
-- Po každé DB ověřit: DESCRIBE tbl_client;
-- ============================================================

-- ──────────────────────────────────────────────────────────────
-- Krok 1: Přidat nové sloupce (pouze pokud neexistují)
-- Poznámka: @col_exists je vždy resetován na NULL před každým
-- dotazem, aby se zabránilo použití zastaralé session proměnné.
-- ──────────────────────────────────────────────────────────────

-- ff_company_id: FieldForce Company.Id (GUID jako VARCHAR(36))
-- DŮLEŽITÉ: Company.Id je GUID — ověřeno 2026-03-27.
SET @col_exists = NULL;
SET @col_exists = (
    SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'tbl_client'
      AND COLUMN_NAME = 'ff_company_id'
);
SET @sql = IF(@col_exists = 0,
    'ALTER TABLE tbl_client ADD COLUMN ff_company_id VARCHAR(36) NULL COMMENT ''FieldForce Company.Id (GUID)'' AFTER idclient',
    'SELECT ''ff_company_id already exists'' AS info'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- ff_sync_source: odkud přišel záznam ('FF' = Bridge, 'PIPE' = Pipedrive legacy)
SET @col_exists = NULL;
SET @col_exists = (
    SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'tbl_client'
      AND COLUMN_NAME = 'ff_sync_source'
);
SET @sql = IF(@col_exists = 0,
    'ALTER TABLE tbl_client ADD COLUMN ff_sync_source VARCHAR(10) NULL COMMENT ''FF = FieldForce (Bridge), PIPE = Pipedrive (legacy)'' AFTER ff_company_id',
    'SELECT ''ff_sync_source already exists'' AS info'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- data_owner: kdo vlastní záznam (PIPEDRIVE / FIELDFORCE / PARTNER)
SET @col_exists = NULL;
SET @col_exists = (
    SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'tbl_client'
      AND COLUMN_NAME = 'data_owner'
);
SET @sql = IF(@col_exists = 0,
    "ALTER TABLE tbl_client ADD COLUMN data_owner ENUM('PIPEDRIVE','FIELDFORCE','PARTNER') NOT NULL DEFAULT 'PIPEDRIVE' AFTER ff_sync_source",
    'SELECT ''data_owner already exists'' AS info'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- last_ff_sync_at: timestamp posledního zápisu z Bridge (conflict detection)
SET @col_exists = NULL;
SET @col_exists = (
    SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'tbl_client'
      AND COLUMN_NAME = 'last_ff_sync_at'
);
SET @sql = IF(@col_exists = 0,
    'ALTER TABLE tbl_client ADD COLUMN last_ff_sync_at DATETIME NULL COMMENT ''Čas posledního zápisu z Bridge'' AFTER data_owner',
    'SELECT ''last_ff_sync_at already exists'' AS info'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- ──────────────────────────────────────────────────────────────
-- Krok 2: Přidat index na ff_company_id (pouze pokud neexistuje)
-- ──────────────────────────────────────────────────────────────

SET @idx_exists = NULL;
SET @idx_exists = (
    SELECT COUNT(*) FROM information_schema.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'tbl_client'
      AND INDEX_NAME = 'idx_ff_company_id'
);
SET @sql = IF(@idx_exists = 0,
    'ALTER TABLE tbl_client ADD INDEX idx_ff_company_id (ff_company_id)',
    'SELECT ''idx_ff_company_id already exists'' AS info'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- ──────────────────────────────────────────────────────────────
-- Krok 3: Označit existující záznamy jako Pipedrive origin
-- Guard: kontrolujeme OBOU sloupců (ff_sync_source IS NULL AND data_owner = 'PIPEDRIVE')
-- Invariant: ff_sync_source a data_owner musí být vždy konzistentní páry.
-- NEMĚNIT záznamy, kde ff_sync_source je již nastaveno (Bridge nebo jiný zdroj).
-- Záznamy s data_owner != 'PIPEDRIVE' jsou Bridge-owned — NEPŘEPISOVAT.
-- ──────────────────────────────────────────────────────────────

UPDATE tbl_client
SET ff_sync_source = 'PIPE',
    data_owner = 'PIPEDRIVE'
WHERE ff_sync_source IS NULL
  AND data_owner = 'PIPEDRIVE';  -- double-guard: obě pole musí indikovat Pipedrive origin

-- ──────────────────────────────────────────────────────────────
-- Krok 4: Ověření — zkontrolovat výsledek migrace
-- ──────────────────────────────────────────────────────────────

-- Ověřit, že nové sloupce existují
SELECT
    COLUMN_NAME,
    COLUMN_TYPE,
    IS_NULLABLE,
    COLUMN_DEFAULT,
    COLUMN_COMMENT
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'tbl_client'
  AND COLUMN_NAME IN ('ff_company_id', 'ff_sync_source', 'data_owner', 'last_ff_sync_at')
ORDER BY ORDINAL_POSITION;

-- Ověřit počty záznamů per data_owner (před Fází 1 by mělo být vše 'PIPEDRIVE')
SELECT data_owner, ff_sync_source, COUNT(*) AS cnt
FROM tbl_client
GROUP BY data_owner, ff_sync_source
ORDER BY data_owner;

-- ──────────────────────────────────────────────────────────────
-- POZNÁMKY:
-- • pipe_id, pipeType, int_client — NIKDY neměnit (historické hodnoty)
-- • data_owner = 'FIELDFORCE' se nastaví Bridge při CREATE/UPDATE
-- • last_ff_sync_at se aktualizuje při každém zápisu z Bridge
-- • ff_company_id je jedinečný pro FieldForce firmy (nullable pro Pipedrive záznamy)
-- • ff_sync_source a data_owner jsou vždy konzistentní páry — neměnit odděleně
-- • Spustit po každé regionální DB, ne jen na jedné!
-- ============================================================
