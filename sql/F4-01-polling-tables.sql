-- ============================================================
-- F4-01: Polling infrastruktura — tabulky v Azure SQL (bridge DB)
-- Spustit jednou před aktivací Fáze 4.
-- ============================================================

-- Watermark pro polling nových objednávek (per region)
-- poll_target hodnoty: 'tbl_order_cz', 'tbl_order_pl', 'tbl_order_hu', 'tbl_order_us'
CREATE TABLE bridge_poll_watermark (
    poll_target                 VARCHAR(50)     NOT NULL,
    last_processed_order_date   INT             NOT NULL DEFAULT 0,  -- unix timestamp z order_date_start
    last_processed_id           BIGINT          NOT NULL DEFAULT 0,
    updated_at                  DATETIME        NOT NULL,
    CONSTRAINT PK_bridge_poll_watermark PRIMARY KEY (poll_target)
);

-- Inicializovat 4 záznamy (watermark = 0 → při prvním pollování načte vše od epoch)
-- Pro backfill 12 měsíců nastavit na unix timestamp před 12 měsíci (F4-03).
INSERT INTO bridge_poll_watermark (poll_target, last_processed_order_date, last_processed_id, updated_at)
VALUES
    ('tbl_order_cz', 0, 0, GETUTCDATE()),
    ('tbl_order_pl', 0, 0, GETUTCDATE()),
    ('tbl_order_hu', 0, 0, GETUTCDATE()),
    ('tbl_order_us', 0, 0, GETUTCDATE());

-- ============================================================

-- Snapshot stavů objednávek pro detekci změn (change detection)
-- Hash = MD5(order_state || '|' || order_close || '|' || order_close_pay
--            || '|' || order_automat_close || '|' || order_deactive)
CREATE TABLE bridge_order_snapshot (
    partner_region  VARCHAR(5)      NOT NULL,
    order_id        BIGINT          NOT NULL,
    state_hash      VARCHAR(32)     NOT NULL,  -- MD5 hex string, 32 znaků
    last_checked    DATETIME        NOT NULL,
    CONSTRAINT PK_bridge_order_snapshot PRIMARY KEY (partner_region, order_id)
);

-- ============================================================
-- Poznámky k provozu:
-- • bridge_poll_watermark: PollWatermarkRepository.UpsertAsync po každém úspěšném poll cyklu
-- • bridge_order_snapshot: OrderSnapshotRepository.BulkUpsertAsync po každém poll cyklu
-- • Při inicializaci Fáze 4: nastavit last_processed_order_date na UNIX_TIMESTAMP(NOW() - INTERVAL 12 MONTH)
--   pro backfill historických dat (F4-03).
-- ============================================================
