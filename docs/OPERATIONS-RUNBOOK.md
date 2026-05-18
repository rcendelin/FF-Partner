# Provozní příručka — FF-Partner Bridge

> Příručka pro každodenní provoz Bridge na on-premise XTuning hostu.
> Cílová persona: ops / dev, který má SSH na deploy host a sudo na `docker`.
> Konkrétní příklady jsou ukotveny v `/opt/ff-partner-bridge/` (PROD instance).
> Pro multi-environment topologii (TEST + PROD vedle sebe) viz
> [`MULTI-ENV-DEPLOYMENT.md`](MULTI-ENV-DEPLOYMENT.md).

---

## 1. Co Bridge dělá v provozu

Bridge je trvale běžící .NET 8 kontejner, který se chová jako **most** mezi
FieldForce (Azure CRM) a 4 regionálními Partner3 MySQL databázemi.

```
                            ┌───────────────────────────┐
   FieldForce (Azure)  ───▶ │  Azure Service Bus topics │ ───┐
                            │  ff.company.sync          │    │  konzumace
                            │  ff.contact.updated       │    │  (4× hosted service)
                            │  ff.company.owner-changed │    ▼
                            │  ff.company.disabled      │  ┌──────────────────┐
                            └───────────────────────────┘  │                  │
                                                           │   Bridge .NET    │
                            ┌───────────────────────────┐  │   kontejner      │
   FieldForce (Azure)  ◀─── │  bridge.company.synced    │◀─│                  │
                            │  bridge.company.sync-fail │  │                  │
                            │  bridge.order.*           │  │                  │
                            └───────────────────────────┘  │                  │
                                                           │                  │
   Azure SQL (metadata) ◀─────────────────────────────────▶│                  │
   bridge_id_mapping, bridge_poll_watermark, snapshot      │                  │
                                                           │                  │
   GAIA MySQL (read-only adresní číselníky)        ◀──────▶│                  │
                                                           │                  │
   Partner3 CZ / PL / HU / US MySQL (tbl_client, ◀────────▶│                  │
   tbl_order pro polling)                                  │                  │
                                                           └──────────────────┘
```

V kontejneru paralelně běží:

| Komponenta | Co dělá | Frekvence |
|---|---|---|
| 4× **konzument** (`CompanySync`, `ContactUpdated`, `OwnerChanged`, `CompanyDisabled`) | Subscribe na `ff.*` topics, zapisuje do Partner3 + bridge_id_mapping | event-driven |
| 4× **OrderPoller** (CZ, PL, HU, US) | Čte `tbl_order` z regionální DB, publikuje `bridge.order.*` zprávy | každých 5 min, 30s po startu |
| **OrderBackfillService** | Jednorázový export 12 měsíců objednávek per region | 60s po startu, **idempotentní** přes bridge_sync_log |
| **DlqMonitorService** | Čte hloubku dead-letter queue na `ff.*` subscriptions | každých 5 min |
| **SagaRecoveryService** | Detekuje a kompenzuje nedokončené cross-region migrace | jednou při startu |

Health endpoint (`/health`) běží bez autentizace. Diagnostické endpointy (`/api/*`)
vyžadují `X-Api-Key` header.

---

## 2. Layout deploy hostu

```
/opt/ff-partner-bridge/
├── docker-compose.yml          # Z repa (curl raw z GitHub main)
├── .env                        # ACR_NAME, IMAGE_TAG, BIND_IP, App Insights, OwnerMapping
├── docker-compose.yml.bak      # Volitelné — záloha před manuálními úpravami
└── secrets/                    # Mode 700; soubory mode 644 (důvod níže)
    ├── azure_sql_conn.txt
    ├── gaia_conn.txt
    ├── partner_cz_conn.txt
    ├── partner_pl_conn.txt
    ├── partner_hu_conn.txt
    ├── partner_us_conn.txt
    ├── servicebus_conn.txt
    ├── bridge_admin_api_key.txt
    └── fieldforce_db_conn.txt  # Volitelný — prázdný = sync log writes do FF DB vypnuté
```

Adresář vlastní uživatel `ff-bridge`. Docker Compose `secrets: file:` v ne-Swarm
módu je obyčejný bind mount — zachovává host uid/gid. Bridge kontejner běží
jako uid 1001 (`bridge` user z Dockerfile); host `ff-bridge` má typicky jiný
uid (např. 996). Tedy soubor mode `600` (owner-only) by uvnitř kontejneru
nedostal — uid 1001 v kontejneru ≠ host owner uid. **Musíme použít mode `644`**
(others-readable). Bezpečnostně to neoslabuje: `secrets/` adresář má mode `700`,
takže nikdo mimo `ff-bridge` na hostu se k souborům nedostane filesystem cestou.

Čistší varianta (volitelná, pro budoucnost): `sudo chown 1001:1001 secrets/*.txt`
+ `chmod 600`. Pak ff-bridge host user už nemůže `cat` přímo, musí přes sudo.

**Co kde Bridge načítá:**

| Hodnota | Zdroj | Pozn. |
|---|---|---|
| Connection stringy (8×) | `secrets/*.txt` → mount `/run/secrets/<name>` | čte `DockerSecretsReader.cs` |
| `ACR_NAME`, `IMAGE_TAG` | `.env` → Docker Compose interpoluje do `image:` | bez nich `compose up` warne |
| `BIND_IP` | `.env` → port binding `${BIND_IP:-127.0.0.1}:8080:8080` | default `127.0.0.1` |
| `BRIDGE_HOSTNAME` | `.env` → `hostname:` v compose → `Environment.MachineName` v kontejneru | propaguje se do App Insights jako `cloud_RoleInstance` |
| `ApplicationInsights__ConnectionString` | `.env` → env var v kontejneru | prázdné = telemetrie vypnutá |
| `OwnerMapping__*` | `.env` nebo `config/owner-mapping.template.json` ve volume | mapování FF GUID → Partner3 int |
| Topic names, retry policy, polling interval | `appsettings.json` zabalené v image | non-sensitive defaults |

---

## 3. Lifecycle příkazy

Všechny příkazy z `/opt/ff-partner-bridge/`.

### Start

```bash
docker compose up -d
docker compose ps                  # ověř, že je Up (healthy)
```

První start po pull novém image (`docker pull`) nepotřebuje `--build` — image
je v ACR, ne lokálně builděný:

```bash
docker compose up -d --no-build
```

### Stop

```bash
docker compose down                # zastaví + odstraní kontejner (síť zůstává)
docker compose stop                # jen zastaví, příště up je rychlý
```

### Restart (typicky po změně `.env` nebo nového image tagu)

```bash
docker compose restart ff-partner-bridge
# Nebo plný restart: down + up
docker compose down && docker compose up -d
```

### Logs

```bash
docker compose logs -f --tail=200 ff-partner-bridge   # follow, posledních 200
docker compose logs --since=1h ff-partner-bridge      # za poslední hodinu
docker compose logs --since=2026-05-18T08:00:00       # od konkrétního času
```

Logy se rotují per kontejner: 5 souborů × 50 MB = 250 MB max
(viz `docker-compose.yml` sekce `logging:`).

### Status (kontejner + healthcheck)

```bash
docker compose ps                   # tabulka služeb
docker inspect --format='{{.State.Health.Status}}' \
  $(docker compose ps -q ff-partner-bridge)
# → healthy / starting / unhealthy
```

### Vstup do běžícího kontejneru (debugging)

```bash
docker compose exec ff-partner-bridge sh
# Uvnitř: ls -la /run/secrets/, env | grep ASPNETCORE, atd.
```

---

## 4. Upgrade na nový image tag

CI/CD pushuje image do ACR pod tagem `${{ github.run_number }}` (sekvenční).
Deploy je **ruční** — Bridge je on-premise a GitHub-hosted runner nemá síťový
přístup do XTuning subnetu.

### Standardní upgrade postup

```bash
cd /opt/ff-partner-bridge

# 1. Zjisti nový tag — v GitHub Actions → Actions → CI/CD → poslední úspěšný run
#    Nebo: az acr repository show-tags --name acrxtuningprod \
#            --repository ff-partner-bridge --orderby time_desc -o table

NEW_TAG=12   # příklad

# 2. Pull nového image
docker pull "acrxtuningprod.azurecr.io/ff-partner-bridge:${NEW_TAG}"

# 3. Update .env
sudo sed -i "s/^IMAGE_TAG=.*/IMAGE_TAG=${NEW_TAG}/" .env
grep IMAGE_TAG .env                    # ověř

# 4. Recreate kontejneru s novým image
docker compose up -d --no-build ff-partner-bridge

# 5. Sleduj logy startu (60s startup window)
docker compose logs -f --tail=50 ff-partner-bridge

# 6. Ověř health
curl -fsSL http://localhost:8080/health
# → {"status":"healthy", ...}
```

### Co Bridge dělá při startu (timeline)

| Čas od startu | Akce |
|---|---|
| 0s | Program.cs: načte Docker Secrets, validuje API key |
| 0-2s | DI registrace, Service Bus klient login |
| 2-5s | Konzumenti se subscribují, healthcheck stále `starting` |
| ~10s | Healthcheck `healthy`, start_period uplynul |
| 30s | OrderPollery začínají polling |
| 60s | OrderBackfillService začne (idempotentní — přeskočí, pokud už proběhl) |
| trvale | DLQ monitor každých 5 min |

---

## 5. Rollback

Image tag je sekvenční číslo runu. Rollback = nastavit předchozí tag v `.env`
a restartovat.

```bash
cd /opt/ff-partner-bridge

# Najdi předchozí tag (ručně z GitHub Actions nebo přes az acr)
az acr repository show-tags \
  --name acrxtuningprod \
  --repository ff-partner-bridge \
  --orderby time_desc -o table

# Předchozí tag byl např. 11
sudo sed -i 's/^IMAGE_TAG=.*/IMAGE_TAG=11/' .env

docker pull "acrxtuningprod.azurecr.io/ff-partner-bridge:11"
docker compose up -d --no-build ff-partner-bridge

# Ověř
docker compose logs --tail=50 ff-partner-bridge
curl -fsSL http://localhost:8080/health
```

### Rollback s migracemi schémat

DDL migrace (`sql/F0-02-tbl-client-extensions.sql` atd.) jsou **forward-compatible** —
nové sloupce (`ff_company_id`, `ff_sync_source`, …) mohou existovat i pro
starší image tag. Rollback image **neodvolává** DDL. Pokud potřebuješ rollback
DDL, řeš to manuálním SQL — Bridge nemá automatic schema migration.

---

## 6. Diagnostické endpointy

API key se čte ze `secrets/bridge_admin_api_key.txt`. Předává se hlavičkou
`X-Api-Key`. `/health` je výjimka (Docker healthcheck).

### `GET /health`

Bez autentizace. Vrací `{"status":"healthy","timestamp":"…"}`.

```bash
curl http://localhost:8080/health
```

**Nevolá DB ani Service Bus** — health říká pouze, že proces běží a HTTP
listener funguje. Časté nedorozumění: zelený `/health` neznamená, že **konzumenti
se úspěšně subscribli** na Service Bus topics. To se pozná **jen z logu**:

```bash
docker compose logs ff-partner-bridge | \
  grep -E 'Konzument [a-zA-Z]+ spuštěn|Subscriber started|Listening on topic'
```

Pokud konzumenti nezalogovali subscribe ~5-10 s po startu, je to typicky špatný
Service Bus connection string nebo missing Listen claim na SAS key.

### `GET /api/mapping/{ffCompanyId}`

```bash
API_KEY=$(sudo cat /opt/ff-partner-bridge/secrets/bridge_admin_api_key.txt)
curl -H "X-Api-Key: $API_KEY" \
  http://localhost:8080/api/mapping/<FF_GUID>
```

Vrátí `partner_client_id`, `partner_region`, `last_sync_at` ze záznamu
v `bridge_id_mapping`. 404 = firma se zatím nesynchronizovala.

### `GET /api/sync-log?last=50`

Posledních N záznamů z `bridge_sync_log` (FieldForce DB). Užitečné pro
ad-hoc diagnostiku posledních selhání.

```bash
curl -H "X-Api-Key: $API_KEY" \
  'http://localhost:8080/api/sync-log?last=20' | jq .
```

### `POST /api/bulk-sync` (Fáze 2, manuální spuštění)

Bulk re-publikuje `ff.company.sync` pro seznam GUIDů.
Použití viz [`F0-08-owner-mapping-and-sla.md`](F0-08-owner-mapping-and-sla.md).

---

## 6.5 Validace připojení k DB

Užitečné po změně `secrets/*.txt`, při setupu TEST instance, nebo při podezření,
že conn stringy ukazují na špatnou DB.

```bash
# Předpoklad — jednorázová instalace
sudo apt install default-mysql-client

# Spuštění proti aktivním secrets
bash scripts/validate-db-connections.sh

# Pro TEST instanci (jiný secrets dir)
SECRETS_DIR=/opt/ff-partner-bridge-test/secrets \
  bash scripts/validate-db-connections.sh
```

Skript ověří **identitu DB** (`@@hostname`, `DATABASE()`, `@@version`),
**schéma test** (Partner3 má `tbl_client`, GAIA má `cfg_country`) a
**per-region distribuci** `client_country_short` v `tbl_client`. Typické
signály:

| Signal | Význam |
|---|---|
| `tbl_client_exists=0` v Partner3 sekci | `partner_*_conn.txt` ukazuje na GAIA |
| `cfg_country_exists=0` v GAIA sekci | `gaia_conn.txt` ukazuje na Partner3 |
| CZ DB top země `PL/LT/LV` | `partner_cz_conn.txt` ↔ `partner_pl_conn.txt` jsou prohozené |
| `ff_columns_present` NULL nebo neúplný | DDL migrace F0-02 (`sql/F0-02-tbl-client-extensions.sql`) neproběhla |
| `Access denied for user '…'@'…'` | credentials špatné, DB ale dostupná |
| `Can't connect to MySQL server (110)` | síťová cesta blokuje (firewall, VPN, MySQL host down) |

Heslo neprochází přes `mysql -p…` (žádný shell history záznam); skript používá
temp `--defaults-extra-file` v `/tmp` s mode 600, který hned po dotazu maže.

---

## 7. Failure modes — co hledat v logu

### Bridge se nestartuje (`docker compose ps` → Exit 1)

```bash
docker compose logs --tail=200 ff-partner-bridge
```

| Signatura v logu | Příčina | Náprava |
|---|---|---|
| `Secret 'bridge_admin_api_key' nebyl nalezen` + výjimka | Chybí soubor `secrets/bridge_admin_api_key.txt` nebo je prázdný | Spusť `bash infra/F0-06-docker-secrets-init.sh` a vyber jen ten secret |
| `Chybí connection strings — Infrastructure … NEBUDOU zaregistrováni` (warning, ne error) | Někter z conn stringů je prázdný | Zkontroluj `secrets/*.txt`, mode 600 |
| `MySqlException: Access denied for user '…'@'…'` | Špatný user/pass v conn stringu | Edituj příslušný `secrets/*.txt`, restart |
| `MySqlException: Unable to connect to any of the specified MySQL hosts` | Síťová cesta blokuje (firewall, MySQL host down) | `mysql --defaults-extra-file=… -e 'SELECT 1'` z hosta |
| `ServiceBusException: ResourceNotFound … topic 'ff.company.sync'` | Topic neexistuje v Service Bus namespace | Service Bus setup neproběhl — viz `infra/F0-05-servicebus-setup.sh` |
| `ServiceBusException: Unauthorized … claim 'Listen'` | SB conn string nemá Listen claim | Použij Bridge dedikovaný SAS key (Send+Listen), ne FF master key |

### Bridge běží, ale konzumenti nezpracovávají zprávy

```bash
# 1. Zkontroluj, že konzumenti se subscribli
docker compose logs ff-partner-bridge | grep -E 'subscriber started|Subscribed'

# 2. DLQ depth — pokud > 0, zprávy padají do dead-letter queue
docker compose logs ff-partner-bridge | grep -i 'DLQ\|dead-letter'

# 3. Zkontroluj retry chování
docker compose logs ff-partner-bridge | grep -E 'TransientRetry|retry [0-9]'
```

| Signatura | Příčina | Náprava |
|---|---|---|
| `DLQ depth > 0` opakovaně | Zpráva selhala 5× a šla do DLQ | App Insights → najdi failed messages → oprav root cause → ručně replay |
| `Conflict pro client {PartnerId} — přeskočeno` (warning) | Partner DB byla ručně editována < 5 min před FF zprávou | OK chování, sledovat četnost |
| `Neznámé PSČ {…} pro {Country} — zip_id bude NULL` (warning) | GAIA `cfg_zip` nemá záznam | Doplnit GAIA číselník, NEBO nechat null (sync neblokovat) |

### OrderPoller selhává pro jeden region

```bash
docker compose logs ff-partner-bridge | grep -E 'OrderPoller(Cz|Pl|Hu|Us)'
```

Selhání jednoho regionu **neovlivní ostatní** — každý poller je samostatný
BackgroundService. Pokud `cz` padá ale `pl/hu/us` poklidně pollují, problém
je v Partner CZ DB (síťově, schéma, oprávnění).

### Healthcheck `unhealthy` ale logy nic nehlásí

```bash
# Healthcheck volá curl localhost:8080/health uvnitř kontejneru.
# Pokud HTTP listener nestartoval, ale proces neumřel:
docker compose exec ff-partner-bridge sh -c 'curl -v http://localhost:8080/health'
```

Typicky to znamená, že `ASPNETCORE_URLS` v env varu je špatný formát
(očekává `http://+:8080`). Zkontroluj `docker-compose.yml`.

---

## 8. Service Bus dead-letter queue — manuální resolution

DLQ depth > 0 indikuje zprávy, které selhaly 5× (max delivery count). Bridge
je tam neposouvá — musí se opravit ručně.

### Inspekce DLQ

```bash
# Vyžaduje Azure CLI přihlášení (az login) + jednu z těchto Azure RBAC rolí
# na Service Bus namespace nebo subscription:
#   - "Azure Service Bus Data Receiver"  (čte DLQ obsah)
#   - "Reader"                            (jen metadata, počet zpráv)
# Bez nich vrátí "AuthorizationFailed" / 403.

az servicebus topic subscription show \
  --resource-group <rg> \
  --namespace-name <sb-namespace> \
  --topic-name ff.company.sync \
  --name bridge-main \
  --query 'countDetails'
```

Pro skutečný obsah DLQ použij Service Bus Explorer (Azure Portal) nebo
[ServiceBusExplorer](https://github.com/paolosalvatori/ServiceBusExplorer).

### Replay zprávy z DLQ

1. **Azure Portal** → Service Bus → subscription → Dead-letter → vyber zprávu
2. Zkopíruj payload (JSON)
3. Oprav root cause (DB constraint, missing GAIA record, atd.)
4. Re-publikuj zprávu zpět na původní topic (Service Bus Explorer „Resubmit")

Nebo bulk přes `POST /api/bulk-sync` pro `ff.company.sync`.

---

## 9. Rotace secrets

### API key rotation

```bash
cd /opt/ff-partner-bridge

# 1. Vygeneruj nový
NEW_KEY=$(openssl rand -hex 32)

# 2. Přepiš secret
echo -n "$NEW_KEY" | sudo tee secrets/bridge_admin_api_key.txt >/dev/null
sudo chmod 600 secrets/bridge_admin_api_key.txt

# 3. Restart Bridge (Docker Secrets se čtou jen při startu kontejneru)
docker compose restart ff-partner-bridge

# 4. Distribuovat nový klíč konzumentům (FF tým, monitoring atd.)
```

### DB password / SB key rotation

1. Vygeneruj nový SAS key / DB user password v Azure
2. Update příslušného `secrets/*.txt` (`sudo vim` NEBO znovu `bash infra/F0-06-docker-secrets-init.sh`)
3. `docker compose restart ff-partner-bridge`
4. Po ověření, že běží, **deaktivuj starý** SAS key / DB user

---

## 10. Backup a recovery

Bridge nemá lokální state — **všechen state je v externích DB**:

| Tabulka | DB | Backup |
|---|---|---|
| `bridge_id_mapping` | Azure SQL | Microsoft automated backup (PITR 7-35 dní podle SKU) |
| `bridge_sync_log` | FieldForce Azure SQL (sdílená) | Microsoft automated backup |
| `bridge_poll_watermark` | Azure SQL | Microsoft automated backup |
| `bridge_order_snapshot` | Azure SQL | Microsoft automated backup |
| `tbl_client` | Partner3 MySQL (4×) | XTuning ops zodpovídá za Partner3 backup |
| `cfg_*` | GAIA MySQL | XTuning ops zodpovídá za GAIA backup |
| Service Bus zprávy | Azure Service Bus | retention 7 dní, replay z DLQ |

**Pozn.:** smaže-li se kontejner i image, **stačí znovu pull a `docker compose up`** —
žádná data se neztratí. Image je deterministický, secrets/.env je na hostu.

---

## 11. Monitoring a alerting

### Application Insights

Connection string v `.env` (`ApplicationInsights__ConnectionString`). Bridge
posílá:

- **Tracy** — strukturovaný Serilog log přes AI sink (`Program.cs`)
- **Metriky** — `bridge.sync.duration`, `bridge.sync.errors`
- **Standardní AI signály** — RequestTelemetry, DependencyTelemetry

**Rozlišení instancí** (multi-env): AI property `cloud_RoleInstance` se odvozuje
z `Environment.MachineName`, který v Dockeru = `hostname:` z compose. Aby se
TEST a PROD daly rozlišit v dashboardech, musí být v `.env` nastaveno
`BRIDGE_HOSTNAME` (např. `ff-partner-bridge-prod` vs `ff-partner-bridge-test`).
Bez něj Docker přiřadí krátké container ID, které se mění při každém recreate
a v dashboardu vytváří šum.

KQL pro nejčastější check:

```kusto
// Error rate za poslední hodinu
traces
| where timestamp > ago(1h)
| where severityLevel >= 3
| summarize count() by message
| order by count_ desc
```

Doporučené alerty viz [`F0-08-owner-mapping-and-sla.md`](F0-08-owner-mapping-and-sla.md)
sekce „Alerty".

### Console logy (pokud AI není nastaven)

```bash
docker compose logs -f ff-partner-bridge | grep -E '\[(WRN|ERR|FTL)\]'
```

---

## 12. Co NIKDY nedělat

Pravidla z `CLAUDE.md` sek. 17, která se týkají i ops:

- ❌ **Nemazat secrets/ na fungujícím hostu** bez backupu (přijdeš o API key, conn stringy)
- ❌ **Nepoužívat `docker compose down -v`** — `-v` smaže volumes; Bridge žádné nemá, ale je to návyk, který v sousedních službách dělá škody
- ❌ **Needitovat `appsettings.json` v kontejneru** — změny se ztratí při recreate; vše konfigurovatelné je v `.env` nebo `secrets/`
- ❌ **Nesměrovat `BIND_IP=0.0.0.0`** — Bridge má naslouchat jen na interní síti XTuning
- ❌ **Nepushovat `secrets/` ani `.env` do git** — `.gitignore` to brání, ale `git add -A` může obejít
- ❌ **Nevypínat Bridge bez koordinace s FF** — zprávy se kupí v Service Bus, po startu je všechny zpracuje (zlepší se po 5 min × delivery count chybou nebo úspěšným zpracováním)

---

## 13. Kontakty a eskalace

| Komponenta | Vlastník |
|---|---|
| Bridge (tato služba) | XTuning IT / repo `rcendelin/FF-Partner` |
| FieldForce (publisher zpráv) | FF tým |
| Partner3 MySQL (4× region) | XTuning DB ops |
| GAIA MySQL | XTuning DB ops |
| Azure SQL (bridge metadata) | XTuning Azure subscription owner |
| Azure Service Bus namespace | FF + Bridge tým (shared) |
| Azure Container Registry | XTuning Azure subscription owner |

---

## 14. Související dokumenty

- [`MULTI-ENV-DEPLOYMENT.md`](MULTI-ENV-DEPLOYMENT.md) — TEST + PROD vedle sebe
- [`AZURE-ACR-SETUP.md`](AZURE-ACR-SETUP.md) — CI/CD a ACR push
- [`F0-08-owner-mapping-and-sla.md`](F0-08-owner-mapping-and-sla.md) — owner mapping a SLA thresholds
- [`F1-12-go-no-go-checklist.md`](F1-12-go-no-go-checklist.md) — go/no-go validace mezi fázemi
- [`FIELDFORCE-INTEGRATION-SPEC.md`](FIELDFORCE-INTEGRATION-SPEC.md) — message kontrakty pro FF stranu
- [`runbooks/F3-01-gaia-shutdown-pull.md`](../runbooks/F3-01-gaia-shutdown-pull.md) — vypnutí starého GAIA pull
- [`CLAUDE.md`](../CLAUDE.md) — primární průvodce projektem
