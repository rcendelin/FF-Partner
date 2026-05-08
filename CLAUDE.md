# CLAUDE.md — FF-Partner Bridge

> Tento soubor je primárním průvodcem pro Claude Code při vývoji FF-Partner Bridge.
> Obsahuje veškerý architekturní kontext, rozhodnutí, schémata a implementační pravidla.
> **Vždy si přečti celý tento soubor před zahájením jakékoli implementace.**

---

## 1. Co je FF-Partner Bridge

FF-Partner Bridge je synchronizační komponenta (.NET 8, Docker) běžící **on-premise v síti XTuning**. Nahrazuje Pipedrive jako zdroj klientských dat pro legacy systém Partner3.

### Směry synchronizace

```
Fáze 1–3 (jednosměrně):   FieldForce (Azure) → Service Bus → Bridge → Partner3 MySQL
Fáze 4 (zpětný tok):      Partner3 MySQL ← polling ← Bridge → Service Bus → FieldForce
```

### Co Bridge dělá

- Přijímá zprávy ze Azure Service Bus a zapisuje data firem do 4 regionálních Partner3 MySQL databází
- Validuje adresy (země, PSČ, kraj, okres) proti GAIA číselníkům
- Routuje záznamy do správné regionální DB podle země firmy
- Udržuje ID mapping: FieldForce Company.Id (GUID) ↔ Partner3 idclient ↔ region
- Polluje `tbl_order` a posílá obchodní eventy zpět do FieldForce (Fáze 4)

### Co Bridge nedělá

- Nesynchronizuje uživatele (IT spravuje manuálně) — `UserSyncConsumer.cs` NEIMPLEMENTOVAT
- Nesynchronizuje pobočky (`tbl_client_branch`) v Fázi 1 — Varianta B, budoucí fáze
- Neprovádí auto-INSERT do GAIA číselníků (cfg_zip, cfg_country, cfg_state, cfg_county)
- Nekomunikuje s GAIA API (pouze přímý MySQL přístup)
- Nenahrazuje Partner3 portál — partneři ho dál používají beze změny

---

## 2. Systémový kontext

### FieldForce (.NET 8, Azure)
- CRM platforma pro obchodníky
- Clean Architecture, CQRS (MediatR), EF Core 8, React 19
- Azure SQL, App Service, Service Bus (sdílený namespace s Bridge)
- `Company.Id` je **Guid** — klíčový fakt pro DDL migrace
- Má `Company.PipedriveId (long?)` — použito při migraci dat

### Partner3 (Legacy PHP)
- B2B objednávkový portál, Nette Framework, Dibi ORM
- MySQL na `172.24.0.12`, user `gaia_user`, **79 tabulek**
- 4 regionální databáze: CZ, PL, HU, US
- Klíčová tabulka: `tbl_client` (27+ sloupců)
- Nesmí se dotknout: `tbl_order_file`, GAIA processing pipeline

### GAIA3 (Legacy Python)
- Python/Flask, ECU file processing pipeline
- Číselníky: `cfg_country`, `cfg_state`, `cfg_county`, `cfg_zip`
- Bridge čte tyto číselníky pro validaci — **pouze SELECT, nikdy INSERT**
- Tabulky `pipe_deal`, `pipe_organizations` — použity při migraci dat

---

## 3. Architektura projektu

### Solution struktura

```
FF-Partner-Bridge/
├── src/
│   ├── Bridge.Api/
│   │   ├── Program.cs                     # Host builder, DI, middleware
│   │   ├── Consumers/
│   │   │   ├── CompanySyncConsumer.cs      # ff.company.sync
│   │   │   ├── ContactUpdatedConsumer.cs   # ff.contact.updated
│   │   │   ├── OwnerChangedConsumer.cs     # ff.company.owner-changed
│   │   │   └── CompanyDisabledConsumer.cs  # ff.company.disabled
│   │   └── Endpoints/
│   │       ├── HealthEndpoints.cs          # GET /health (bez autentizace)
│   │       ├── MappingEndpoints.cs         # GET /api/mapping/{ffCompanyId}
│   │       └── SyncLogEndpoints.cs         # GET /api/sync-log?last=50
│   ├── Bridge.Application/
│   │   ├── Commands/                       # Zápis do Partner DB
│   │   ├── Queries/                        # Čtení z Partner DB
│   │   ├── Services/
│   │   │   └── GeoValidationService.cs     # Strict lookup (nikdy INSERT)
│   │   ├── Sagas/
│   │   │   └── MoveClientToRegionSaga.cs   # Transakce při změně regionu
│   │   └── Mappers/                        # Mapperly compile-time mapping
│   ├── Bridge.Domain/
│   │   ├── Messages/                       # Service Bus kontrakty
│   │   ├── Models/                         # DTO modely
│   │   └── Enums/                          # RegionEnum, RoleMapping atd.
│   └── Bridge.Infrastructure/
│       ├── Partner/
│       │   ├── PartnerDbContext.cs          # Factory pattern pro 4 DB
│       │   └── Repositories/
│       ├── Gaia/
│       │   ├── GaiaDbContext.cs
│       │   └── Repositories/               # Číselníky (read-only)
│       ├── Mapping/
│       │   └── BridgeMappingRepository.cs  # bridge_id_mapping v Azure SQL
│       └── ServiceBus/
│           └── ServiceBusClientWrapper.cs
├── tests/
├── Dockerfile
└── docker-compose.yml
```

**POZOR:** `UserSyncConsumer.cs` a `MachineSyncConsumer.cs` ze starší verze návrhu **NEIMPLEMENTOVAT**.

---

## 4. Azure Service Bus — topics a zprávy

### Namespace
**Sdílený s FieldForce** — nezakládat nový namespace, přidat topics do existujícího FieldForce namespace.

### Outbound (FieldForce → Bridge)

| Topic | Message typ | Popis |
|---|---|---|
| `ff.company.sync` | `CompanySyncMessage` | CREATE nebo UPDATE firmy |
| `ff.contact.updated` | `ContactUpdatedMessage` | Změna emailu/telefonu primárního kontaktu |
| `ff.company.owner-changed` | `CompanyOwnerChangedMessage` | Přeřazení obchodníka |
| `ff.company.disabled` | `CompanyDisabledMessage` | Deaktivace firmy |

### Inbound (Bridge → FieldForce)

| Topic | Message typ | Popis |
|---|---|---|
| `bridge.company.synced` | `CompanySyncedResponse` | Úspěch + vrácení Partner ID |
| `bridge.company.sync-failed` | `CompanySyncFailedMessage` | Chyba s kódem |
| `bridge.company.conflict` | `CompanyConflictMessage` | Detekován konflikt, zápis přeskočen |

### Fáze 4 — zpětný tok (Bridge → FieldForce)

| Topic | Popis |
|---|---|
| `bridge.order.created` | Nová objednávka v Partner3 |
| `bridge.order.state-changed` | Změna stavu objednávky |
| `bridge.order.completed` | Zakázka zaplacena (`order_close_pay = 1`) |
| `bridge.order.cancelled` | Zakázka zrušena (`order_state = 30`) |

### Konfigurace každého topicu
- Dead-letter queue: zapnuta
- Max delivery count: 5
- Lock duration: 5 minut
- Retention: 7 dní

### Retry policy pro consumery
```csharp
// Exponential backoff: 1s, 5s, 30s — pak dead-letter queue
```

---

## 5. Databázové schéma — klíčové tabulky

### tbl_client (Partner DB) — cílová tabulka Bridge

Nové sloupce přidané DDL migrací (F0-02):

```sql
ALTER TABLE tbl_client
    ADD COLUMN ff_company_id VARCHAR(36) NULL         -- FieldForce Company.Id (GUID)
        COMMENT 'FieldForce Company.Id (GUID)',
    ADD COLUMN ff_sync_source VARCHAR(10) NULL         -- 'FF' nebo 'PIPE'
        COMMENT 'FF = FieldForce, PIPE = Pipedrive (legacy)',
    ADD COLUMN data_owner ENUM('PIPEDRIVE','FIELDFORCE','PARTNER')
        NOT NULL DEFAULT 'PIPEDRIVE',
    ADD COLUMN last_ff_sync_at DATETIME NULL
        COMMENT 'Čas posledního zápisu z Bridge',
    ADD INDEX idx_ff_company_id (ff_company_id);

-- Existující záznamy označit jako Pipedrive origin
UPDATE tbl_client SET ff_sync_source = 'PIPE' WHERE ff_sync_source IS NULL;
```

Spustit na **všech 4 regionálních DB** (CZ, PL, HU, US).

### Klíčové existující sloupce tbl_client

| Sloupec | Typ | Bridge operace | Poznámka |
|---|---|---|---|
| `idclient` | int PRI AI | READ po INSERT | PK |
| `client_firm` | varchar(250) | WRITE | Název firmy |
| `client_ic` | varchar(250) | WRITE | IČO |
| `client_dic` | varchar(250) | WRITE | DIČ |
| `client_street` | varchar(250) | WRITE | Ulice |
| `client_city` | varchar(250) | WRITE | Město |
| `client_psc` | varchar(250) | WRITE | PSČ |
| `client_country_id` | int | WRITE | FK → cfg_country |
| `client_country_short` | varchar(5) | WRITE | ISO kód (CZ, PL...) |
| `client_state` / `_state_id` | varchar / int | WRITE | Kraj + FK |
| `client_county` / `_county_id` | varchar / int | WRITE | Okres + FK |
| `client_zip_id` | int | WRITE | FK → cfg_zip (může být NULL) |
| `client_phone` | varchar(250) | WRITE | Z primárního kontaktu |
| `client_mail` | varchar(250) | WRITE | Z primárního kontaktu |
| `client_right` | int | WRITE | Role (0/1/2 pro firmy) |
| `client_date` | datetime | WRITE (INSERT only) | Datum založení |
| `client_disable` | tinyint | WRITE | 0=aktivní, 1=disabled |
| `pipe_id` | varchar/int | NEMĚNIT | Historické Pipedrive ID |
| `pipeType` | varchar(10) | NEMĚNIT | Historická hodnota |
| `id_owner` | int | WRITE | Vlastník (mapped z FF User) |
| `int_client` | smallint | NEMĚNIT | Tenant ID (multi-tenant) |

### bridge_id_mapping (Azure SQL)

```sql
CREATE TABLE bridge_id_mapping (
    id          INT IDENTITY(1,1) PRIMARY KEY,
    ff_company_id       UNIQUEIDENTIFIER NOT NULL,   -- FieldForce Company.Id (GUID)
    partner_client_id   INT NOT NULL,                -- tbl_client.idclient
    partner_region      VARCHAR(5) NOT NULL,         -- 'cz', 'pl', 'hu', 'us'
    entity_type         VARCHAR(20) NOT NULL DEFAULT 'client',
    pipedrive_id        BIGINT NULL,                 -- Pro migraci
    ff_user_id          UNIQUEIDENTIFIER NULL,       -- FieldForce User.Id (owner)
    partner_owner_id    INT NULL,                    -- id_owner v Partner DB
    last_sync_at        DATETIME NOT NULL,
    last_sync_direction VARCHAR(10) NOT NULL,        -- 'ff_to_partner'
    created_at          DATETIME NOT NULL,
    updated_at          DATETIME NOT NULL,
    CONSTRAINT uq_ff_company UNIQUE (ff_company_id, entity_type),
    INDEX idx_partner (partner_client_id, partner_region),
    INDEX idx_pipedrive (pipedrive_id)
);
```

### bridge_sync_log (Azure SQL)

```sql
CREATE TABLE bridge_sync_log (
    id                      BIGINT IDENTITY(1,1) PRIMARY KEY,
    ff_company_id           UNIQUEIDENTIFIER NULL,
    partner_client_id       INT NULL,
    partner_region          VARCHAR(5) NULL,
    operation               VARCHAR(30) NOT NULL,
    -- hodnoty: 'create', 'update', 'disable', 'contact_update',
    --          'owner_change', 'region_change', 'machine_enrichment',
    --          'order_poll', 'geo_validation_warning'
    service_bus_message_id  VARCHAR(100) NULL,
    status                  VARCHAR(20) NOT NULL,    -- 'success','failed','retry','warning'
    error_message           NVARCHAR(MAX) NULL,
    payload_json            NVARCHAR(MAX) NULL,
    severity                VARCHAR(10) NOT NULL DEFAULT 'Info',
    created_at              DATETIME NOT NULL,
    INDEX idx_created (created_at),
    INDEX idx_ff_company (ff_company_id),
    INDEX idx_status (status)
);
```

### bridge_order_snapshot (Azure SQL) — Fáze 4

```sql
CREATE TABLE bridge_order_snapshot (
    partner_region  VARCHAR(5) NOT NULL,
    order_id        BIGINT UNSIGNED NOT NULL,
    state_hash      VARCHAR(32) NOT NULL,
    -- MD5(CONCAT(order_state, '|', order_close, '|', order_close_pay,
    --            '|', order_automat_close, '|', order_deactive))
    last_checked    DATETIME NOT NULL,
    PRIMARY KEY (partner_region, order_id)
);
```

### bridge_poll_watermark (Azure SQL) — Fáze 4

```sql
CREATE TABLE bridge_poll_watermark (
    poll_target             VARCHAR(50) PRIMARY KEY,
    -- hodnoty: 'tbl_order_cz', 'tbl_order_pl', 'tbl_order_hu', 'tbl_order_us'
    last_processed_order_date   INT NOT NULL DEFAULT 0,  -- unix timestamp
    last_processed_id           BIGINT NOT NULL DEFAULT 0,
    updated_at              DATETIME NOT NULL
);
```

### tbl_order — klíčové sloupce pro Fázi 4

**POZOR: `order_date_modified` NEEXISTUJE.** Poller používá kombinovanou strategii.

| Sloupec | Typ | Použití |
|---|---|---|
| `idorder` | bigint PRI AI | ID objednávky |
| `id_client` | int FK | Vazba na tbl_client |
| `order_date_start` | int(11) MUL | Unix timestamp vzniku — watermark pro nové |
| `order_state` | smallint | Stav: 7=nová, 20=realizace, 30=zrušena |
| `order_close` | smallint | Uzavřeno (0/1) |
| `order_close_pay` | smallint | Zaplaceno (0/1) |
| `order_automat_close` | tinyint | GAIA: -10=čeká, -1=chyba, 0=hotovo |
| `order_deactive` | tinyint MUL | Soft delete |
| `order_delete` | smallint | Smazáno |
| `order_price` | int | Cena |
| `order_car_vin` | varchar(250) MUL | VIN — Machine lookup |
| `order_car_power_hp` | int | Výkon HP — Machine enrichment |
| `order_car_mark` | varchar(250) | Značka |
| `order_car_model` | varchar(250) | Model |
| `order_car_type` | varchar(250) | Typ |
| `order_car_category` | int | FK na kategorie — MachineType enum |

---

## 6. Routování firem do regionálních DB

```csharp
public static string ResolveRegion(string countryCode) => countryCode switch
{
    "CZ" or "SK" or "UA" or "AT" or "FR" => "cz",
    "PL" or "LT" or "LV" or "EE"         => "pl",
    "HU" or "RO"                           => "hu",
    "US" or "CA" or "AU" or "BR"          => "us",
    "DE" => throw new ConfigurationException(
        "DE nemá automatický region — nutná konfigurace"),
    _ => throw new UnsupportedRegionException(countryCode)
        // → publish bridge.company.sync-failed s kódem UNSUPPORTED_REGION
        // Neblokovat sync, jen upozornit
};
```

**Země mimo whitelist** (IT, GB, ES, BE, NL, CH, atd.): publikovat `sync-failed` s kódem `UNSUPPORTED_REGION`, žádný zápis do Partner DB, logovat.

---

## 7. GeoValidationService — pravidla

```csharp
// NIKDY nevyhazovat výjimku pro neznámé PSČ — zapsat null
// NIKDY INSERT do cfg_zip, cfg_county, cfg_state, cfg_country

public async Task<GeoValidationResult> ValidateAsync(AddressDto address)
{
    // 1. Lookup country — POUZE SELECT, nikdy INSERT
    var country = await _cfgCountryRepo.FindByIsoCodeAsync(address.CountryCode)
        ?? throw new GeoValidationException(
            $"Neznámá země: {address.CountryCode}",
            GeoValidationErrorCode.UnknownCountry);
    // → UnknownCountry → sync-failed (tvrdá chyba)

    // 2. Fuzzy lookup PSČ — při nenalezení VRÁTIT NULL, ne výjimku
    var zip = await _cfgZipRepo.FindBestMatchAsync(address.PostalCode, country.Id);
    if (zip == null)
    {
        // ZIP NENÍ TVRDÁ CHYBA — zapsat null, logovat Warning
        _logger.Warning("Neznámé PSČ {PostalCode} pro {Country} — zip_id bude NULL",
            address.PostalCode, address.CountryCode);
        await _syncLog.WriteAsync(operation: "geo_validation_warning",
            severity: "Warning", payload: address);
        // Pokračovat dál s zip_id = null
    }

    // 3. Fuzzy lookup kraj/okres — při nenalezení vrátit null (ne výjimka)
    var state = await _cfgStateRepo.FindBestMatchAsync(address.State, country.Id);
    var county = zip != null
        ? await _cfgCountyRepo.FindBestMatchAsync(address.County, zip.CountyId)
        : null;

    return new GeoValidationResult
    {
        CountryId = country.Id,
        CountryShort = country.Short,
        ZipId = zip?.Id,       // MŮŽE BÝT NULL
        City = zip?.City ?? address.City,
        StateId = state?.Id,   // MŮŽE BÝT NULL
        State = state?.Name ?? address.State,
        CountyId = county?.Id, // MŮŽE BÝT NULL
        County = county?.Name ?? address.County
    };
}
```

---

## 8. Role mapping — pouze firemní role

Bridge synchronizuje **pouze firmy (Company)**, ne uživatele.

```csharp
public static int MapCompanyRoleToClientRight(CompanyRole role) => role switch
{
    CompanyRole.Customer => 0,
    CompanyRole.Dealer   => 2,
    CompanyRole.OEM      => 1,
    _ => 0  // fallback = zákazník
};
```

Uživatelské role (4=HW Technik, 5=Obchodník, 7=Manager, 10=SW Technik, 100=Admin) jsou **mimo scope Bridge** — IT spravuje manuálně.

---

## 9. Saga — přesun firmy mezi regiony

**Kritické pravidlo:** Vždy nejdříve INSERT do cílové DB, teprve potom DISABLE v původní.

```csharp
public class MoveClientToRegionSaga
{
    // Pořadí NESMÍ být změněno:
    // Krok 1: INSERT do cílové DB
    //   → při chybě: STOP (klient zůstane aktivní v původním regionu)
    // Krok 2: Zapsat 'pending_region_change' do bridge_sync_log
    // Krok 3: DISABLE v původní DB
    //   → při chybě: DELETE z cílové + revert mapping
    // Krok 4: UPDATE bridge_id_mapping (nový region)
    //   → při chybě: enableClient() v původní + DELETE z cílové
    // Krok 5: Publish bridge.company.synced

    // Při startu Bridge: detekovat záznamy se stavem 'pending_region_change'
    // → doběhnout nebo kompenzovat
}
```

---

## 10. Conflict detection

```csharp
// Při UPDATE: zkontrolovat timestamp před zápisem
if (existingClient.LastFfSyncAt.HasValue &&
    existingClient.LastFfSyncAt > message.SentAt.AddMinutes(-5))
{
    _logger.Warning("Conflict pro client {PartnerId} — přeskočeno", partnerId);
    await _serviceBus.PublishAsync(new CompanyConflictMessage { ... });
    return; // NEPŘEPISOVAT
}
// Jinak: UPDATE + aktualizovat last_ff_sync_at = now()
```

---

## 11. Fáze 4 — Polling strategie

### Proč kombinovaná strategie

`order_date_modified` v `tbl_order` **NEEXISTUJE** (ověřeno 2026-03-27, 126 sloupců).

### Nové objednávky — watermark na order_date_start

```sql
SELECT idorder, id_client, order_date_start, order_state, order_close,
       order_close_pay, order_automat_close, order_deactive, order_price,
       order_car_vin, order_car_mark, order_car_model, order_car_type,
       order_car_category, order_car_power_hp
FROM tbl_order
WHERE order_date_start > @lastRunUnixTimestamp
  AND order_deactive = 0
  AND id_client IN (
      SELECT partner_client_id FROM bridge_id_mapping
      WHERE partner_region = @region
  )
```

### Změny stavů — MD5 snapshot

Hash = `MD5(order_state || '|' || order_close || '|' || order_close_pay
           || '|' || order_automat_close || '|' || order_deactive)`

Porovnat s `bridge_order_snapshot` → rozdíl = event.

### 4 samostatné BackgroundService (per region)

Každý region má vlastní `BackgroundService` běžící každých 5 minut. Selhání jednoho regionu neovlivní ostatní.

### Machine enrichment z dokončených zakázek

```csharp
// Po přijetí bridge.order.completed:
// 1. Lookup přes order_car_vin (MUL index) — přesná shoda
// 2. Fallback: order_car_mark + order_car_model + CompanyId
// Pokud nalezena → UPDATE Machine.ChippedPowerKw z order_car_power_hp
//                  UPDATE Machine.MachineType z order_car_category → enum
// Pokud nenalezena → logovat s payloadem, NEVYTVÁŘET nový Machine
```

### Backfill při inicializaci Fáze 4

- SELECT všech `tbl_order` za posledních 12 měsíců (per region)
- Batch publish: 100 zpráv, pauza 1s mezi batchi
- Idempotence: přeskočit záznamy s existujícím záznamem v bridge_sync_log
- Spustit POUZE JEDNOU při inicializaci

---

## 12. REST API

| Endpoint | Auth | Popis |
|---|---|---|
| `GET /health` | Žádná | Docker healthcheck, neobsahuje citlivá data |
| `GET /api/mapping/{ffCompanyId}` | API Key | Partner ID + region + last_sync_at |
| `GET /api/sync-log?last=50` | API Key | Posledních N sync operací |

**API Key middleware:** číst `X-Api-Key` header, hodnota z Docker Secret.

**Network binding:** Bridge naslouchá pouze na interní síti XTuning — ne na `0.0.0.0`.

**Swagger:** POUZE v DEV prostředí.

---

## 13. Secrets management

Aktuální deployment model je **čistě Docker Secrets + env vars**. Azure Key Vault
v kódu **není konzumován** — bicep šablona a setup skript jsou ponechány jako
deprecated, kdyby bylo v budoucnu důvod KV reaktivovat (např. migrace Bridge do
Azure Container Apps, kde by system-assigned MI fungovala bez bootstrap secretu).

```yaml
# docker-compose.yml
services:
  ff-partner-bridge:
    image: ${ACR_NAME}.azurecr.io/ff-partner-bridge:${IMAGE_TAG:-latest}
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://+:8080
      # App Insights conn — write-only telemetrický endpoint, low-sensitivity.
      # Hodnotu načítá z ./.env (mode 600).
      - ApplicationInsights__ConnectionString=${ApplicationInsights__ConnectionString:-}
    secrets:
      - azure_sql_conn       # Azure SQL connection string (Bridge metadata DB)
      - gaia_conn            # GAIA MySQL connection string (číselníky, read-only)
      - partner_cz_conn      # MySQL connection string Partner CZ
      - partner_pl_conn      # MySQL connection string Partner PL
      - partner_hu_conn      # MySQL connection string Partner HU
      - partner_us_conn      # MySQL connection string Partner US
      - servicebus_conn      # Azure Service Bus connection string (sdílený s FieldForce)
      - bridge_admin_api_key # API key pro /api/* endpointy (X-Api-Key)
```

**Klasifikace hodnot:**

- **Docker Secrets** (8× soubor v `./secrets/*.txt`, mode 600, mountnuto na `/run/secrets/<name>`):
  všechny opravdové secrets — connection strings (Azure SQL, GAIA, 4× Partner3, Service Bus)
  a admin API key. Načítá `DockerSecretsReader` v `Program.cs` (s fallbackem na
  `appsettings.json` pro DEV/test).
- **Environment variables / `.env`** (mode 600 vedle `docker-compose.yml`):
  - `ApplicationInsights__ConnectionString` — low-sensitivity write-only endpoint,
    Microsoft sám doporučuje embed do client-side JS pro browser telemetrii
  - `IMAGE_TAG`, `ACR_NAME` — non-sensitive deployment params
  - `OwnerMapping__*` — non-sensitive mapping FF GUID → Partner3 int (alternativa
    je overlay `appsettings.Production.json`)
- **`appsettings.json`** (zabaleno v image): non-sensitive defaults a topic names —
  `ServiceBus:CompanySyncTopic` apod., `Bridge:Polling:IntervalMinutes`.
- **Azure Key Vault**: nekonzumováno. `infra/F0-06-keyvault.bicep` a
  `F0-06-keyvault-setup.sh` jsou deprecated stuby.

---

## 14. Logging — Application Insights

Síť XTuning má odchozí HTTPS → Application Insights funguje přes internet.

```csharp
// Serilog konfigurace:
// Console sink (strukturované JSON) + Application Insights sink
// Correlation ID: Service Bus MessageId na každé operaci

// Custom metriky:
// bridge.sync.duration — latence zpracování zprávy
// bridge.sync.errors   — počet chyb
// bridge.dlq.depth     — počet zpráv v dead-letter queue

// Alerting:
// sync error rate > 5 % za 15 min → alert
// dead-letter queue depth > 0     → alert
// P95 latence > SLA threshold      → alert
```

---

## 15. SLA

| Operace | Max latence |
|---|---|
| Nový klient FieldForce → viditelný v Partner3 | 5 minut |
| Editace adresy → propagace do Partner3 | 15 minut |
| Deaktivace klienta → propagace | 2 minuty |
| Nová objednávka v Partner3 → Event v FieldForce | 5 minut |

---

## 16. Klíčová rozhodnutí (nesmí být změněna bez explicitního souhlasu)

| Rozhodnutí | Hodnota | Datum |
|---|---|---|
| Company.Id datový typ | **Guid** → `VARCHAR(36)` v MySQL, `UNIQUEIDENTIFIER` v Azure SQL | 2026-03-27 |
| Service Bus namespace | **Sdílený s FieldForce** | 2026-03-27 |
| Neznámé PSČ | **Zapsat s zip_id = null**, logovat Warning — sync neblokovat | 2026-03-27 |
| Nepodporovaná země | **sync-failed s kódem UNSUPPORTED_REGION** — žádný zápis | 2026-03-27 |
| Pobočky (tbl_client_branch) | **Mimo scope Fáze 1** — Varianta B, budoucí fáze 2.5+ | 2026-03-27 |
| User sync | **Bridge se uživatelů nedotýká** — IT spravuje manuálně | 2026-03-27 |
| UserSyncConsumer.cs | **NEIMPLEMENTOVAT** — pozůstatek starší verze | 2026-03-27 |
| Monitoring | **Application Insights přes internet** — síť XTuning má HTTPS výstup | 2026-03-27 |
| Záloha Pipedrive dat | **V Fázi 0** (ne při vypínání v Fázi 3) | 2026-03-27 |
| Latence zpětného toku | **≤ 5 minut** — polling stačí, CDC není potřeba | 2026-03-27 |
| Polling architektura | **4 samostatné BackgroundService** per region — izolace selhání | 2026-03-27 |
| Historická data Fáze 4 | **Backfill 12 měsíců** při inicializaci | 2026-03-27 |
| Objednávky ve FieldForce | **Read-only Event** — nepovoleno editovat z FF UI | 2026-03-27 |
| Machine enrichment | **Automaticky** z dat dokončených zakázek (VIN match) | 2026-03-27 |
| GAIA chyby (automat_close=-1) | **Pouze logovat** — žádná notifikace obchodníkovi | 2026-03-27 |

---

## 17. Co NESMÍ Bridge dělat

```
❌ INSERT do cfg_zip, cfg_county, cfg_state, cfg_country
❌ UserSyncConsumer — uživatele nesynchronizovat
❌ Přepisovat pipe_id a pipeType (historické Pipedrive hodnoty)
❌ Zastavit sync kvůli neznámému PSČ (zip_id = null je OK)
❌ Automaticky vytvářet Machine záznamy z objednávek
❌ Notifikovat obchodníka o GAIA processing chybách
❌ Synchronizovat tbl_client_branch v Fázi 1
❌ Přistupovat na Service Bus topics mimo ff.* a bridge.* prefix
❌ Spouštět Bridge na 0.0.0.0 (pouze interní IP sítě XTuning)
❌ Swagger v PROD prostředí
❌ Psát do tbl_order — pouze číst (polling)
```

---

## 18. Testovací scénáře (pro každý PR)

### Fáze 1 — Company sync
1. Nová firma CZ → INSERT v Partner CZ DB, mapping uložen
2. Změna adresy → UPDATE v Partner DB, last_ff_sync_at aktualizován
3. Firma z IT → sync-failed s kódem UNSUPPORTED_REGION
4. Firma s neznámým PSČ → INSERT s zip_id = null, Warning v logu
5. Změna země CZ → PL → INSERT do PL, DISABLE v CZ (saga)
6. Výpadek MySQL → zprávy čekají v Service Bus, zpracují se po obnovení
7. Conflict detection: přímá MySQL editace → Bridge nepřepisuje
8. Dead-letter queue po 5 neúspěšných pokusech → alert

### Fáze 4 — zpětný tok
1. Nová objednávka → Event dorazí do FieldForce do 5 minut
2. Zaplacená objednávka → Company.Stage = Won
3. Zrušená objednávka (bez jiných aktivních) → Company.Stage = Lost
4. Dokončená zakázka → Machine.ChippedPowerKw aktualizováno přes VIN
5. Backfill: počty Eventů odpovídají tbl_order záznamům

---

## 19. Fázový plán — stav implementace

| Fáze | Status | Popis |
|---|---|---|
| **Fáze 0** | TODO | Příprava: DDL migrace, migrační skripty, infra, zálohy |
| **Fáze 1** | TODO | Company sync FF → Partner3 (jádro Bridge) |
| **Fáze 2** | TODO | Contact sync + Owner sync + Bulk migrace |
| **Fáze 3** | TODO | Vypnutí Pipedrive |
| **Fáze 4** | TODO | Zpětný tok Partner3 → FieldForce |

Podrobný TODO: https://www.notion.so/33074024098680ca825cf9713b7aad40

---

## 20. Důležité soubory v GAIA3 (reference — nemodifikovat)

| Soubor | Řádků | Popis |
|---|---|---|
| `app/system/pipe_transfer.py` | 1311 | Partner/GAIA → Pipedrive push — VYPNOUT v Fázi 3 |
| `cron/pipe_partner_cron.py` | 765 | Pipedrive → Partner pull cron — VYPNOUT v Fázi 3 |
| `cron/gen_pipe_data.py` | 293 | Bulk import do pipe_deal/pipe_organizations — VYPNOUT v Fázi 3 |
| `app/gaia_modules/pipedrive_webhooks.py` | 40 | Webhook handler — VYPNOUT v Fázi 3 |

---

## 21. Analytická dokumentace (Notion)

| Dokument | URL |
|---|---|
| Analytický dokument (hlavní) | https://www.notion.so/329740240986803dbe5dd386f535b415 |
| TODO / implementační plán | https://www.notion.so/33074024098680ca825cf9713b7aad40 |
| Kritická analýza + návrhy řešení | https://www.notion.so/33074024098681ba9bd2fbcae1678b30 |
| Zpětný tok DRAFT | https://www.notion.so/33074024098681708fb6e840489baf73 |
| Shrnutí — Jak Bridge funguje | https://www.notion.so/33074024098681abb5eac0a1056e1171 |

---

*Naposledy aktualizováno: 2026-03-27 — Claude, na základě analytické dokumentace, kritické analýzy a Q&A session.*
