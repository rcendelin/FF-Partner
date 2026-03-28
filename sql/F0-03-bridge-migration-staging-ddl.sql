-- ============================================================
-- F0-03 · Migrační staging tabulka: bridge_migration_staging
-- Databáze: Azure SQL (bridge DB)
-- Účel: Mezivrstva pro párování Pipedrive org ID ↔ tbl_client.idclient
--       Naplní se z pipe_organizations (GAIA MySQL) přes F0-03-populate-staging.py
--       Výsledek použije F0-04 pro INSERT do bridge_id_mapping
-- Prerekvizity: F0-01 (conn strings), F0-02 (bridge Azure SQL tabul. existují)
-- Idempotentní: IF NOT EXISTS guardy
-- ============================================================

-- Staging tabulka (dočasná, po dokončení F0-04 může být DROP-nuta)
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE name = 'bridge_migration_staging' AND schema_id = SCHEMA_ID('dbo')
)
BEGIN
    CREATE TABLE dbo.bridge_migration_staging (
        id                  INT IDENTITY(1,1) PRIMARY KEY,

        -- Pipedrive identifikátory
        pipe_id             BIGINT NOT NULL,            -- Pipedrive org ID (páruje s Company.PipedriveId)
        pipe_type           VARCHAR(5) NOT NULL,        -- CE / US / PL

        -- Partner3 identifikátory (z pole partner_id v pipe_organizations)
        partner_id          INT NULL,                   -- tbl_client.idclient (-1 → NULL = nepárováno)
        partner_region      VARCHAR(5) NULL,            -- cz / hu / pl / us

        -- Pipedrive metadata
        role_label          NVARCHAR(100) NULL,         -- Textový label role (z pipe_organizations.role)
        country_label       NVARCHAR(100) NULL,         -- Textový label země
        org_name            NVARCHAR(250) NULL,         -- Název organizace

        -- Odvozená data (vypočítaná při importu)
        client_right        TINYINT NULL,               -- 0=Customer, 1=PartnerHW, 2=Partner (z role_label)

        -- Stav párovacího procesu
        match_status        VARCHAR(30) NOT NULL
            CONSTRAINT DF_staging_status DEFAULT 'pending',
            -- pending     = čeká na spárování s FieldForce (F0-04)
            -- matched     = spárováno s FieldForce Company.PipedriveId → ready pro bridge_id_mapping
            -- no_partner_id = partner_id = -1, nelze auto-párovat (manuální práce)
            -- ff_matched  = FieldForce Company.PipedriveId nalezeno, vloženo do bridge_id_mapping
            -- ff_missing  = FieldForce Company s tímto PipedriveId neexistuje

        ff_company_id       UNIQUEIDENTIFIER NULL,      -- Vyplní F0-04 po spárování s FieldForce
        notes               NVARCHAR(1000) NULL,        -- Poznámky (např. důvod manuálního zpracování)
        created_at          DATETIME2(3) NOT NULL
            CONSTRAINT DF_staging_created DEFAULT SYSUTCDATETIME(),
        updated_at          DATETIME2(3) NULL
    );

    -- Indexy pro efektivní hledání při F0-04 párování
    CREATE INDEX IX_staging_pipe_id
        ON dbo.bridge_migration_staging (pipe_id, pipe_type);

    CREATE INDEX IX_staging_partner
        ON dbo.bridge_migration_staging (partner_id, partner_region)
        WHERE partner_id IS NOT NULL;

    CREATE INDEX IX_staging_status
        ON dbo.bridge_migration_staging (match_status);

    PRINT 'bridge_migration_staging: tabulka vytvořena.';
END
ELSE
BEGIN
    PRINT 'bridge_migration_staging: tabulka již existuje, přeskočeno.';
END
GO

-- ============================================================
-- Ověření po vytvoření
-- ============================================================
SELECT
    COLUMN_NAME,
    DATA_TYPE,
    CHARACTER_MAXIMUM_LENGTH,
    IS_NULLABLE,
    COLUMN_DEFAULT
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'bridge_migration_staging'
ORDER BY ORDINAL_POSITION;
GO
