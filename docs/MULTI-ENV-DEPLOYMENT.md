# Multi-environment deployment — TEST + PROD vedle sebe

> Návrh, jak na **jednom on-premise hostu** provozovat TEST i PROD instanci Bridge
> bez vzájemného ovlivnění. Doplněk k [`OPERATIONS-RUNBOOK.md`](OPERATIONS-RUNBOOK.md)
> (provoz jedné instance) a [`AZURE-ACR-SETUP.md`](AZURE-ACR-SETUP.md) (CI/CD pipeline).
>
> **Klíčové předpoklady** (CLAUDE.md sek. 16):
>
> - Service Bus má **per-env namespace** — FieldForce TEST publikuje do vlastního
>   namespace, PROD do produkčního. Bridge dostane jen jiný `servicebus_conn.txt`.
>   Pokud by FF tým TEST namespace teprve plánoval, viz [Scénář B](#scénář-b--ff-test-namespace-zatím-neexistuje) níže.
> - **Stejný host** pro obě instance, paralelní adresáře.
> - **Stejný image** v ACR — TEST a PROD se liší **jen tagem** v `.env`.
>
> **Pozn. ke CLAUDE.md sek. 16** — řádek „Service Bus namespace: Sdílený s FieldForce"
> se rozumí tak, že **Bridge nemá vlastní namespace; sdílí ho s FF v rámci dané
> environment**. Tj. Bridge PROD ↔ FF PROD sdílejí PROD namespace, Bridge TEST ↔
> FF TEST sdílejí TEST namespace. Není to jeden globální namespace napříč envs.

---

## 1. Cílový stav file layoutu

```
/opt/ff-partner-bridge/              ← PROD (existující instance)
├── docker-compose.yml
├── .env                              # ACR_NAME, IMAGE_TAG=11, BIND_IP=172.24.0.66, …
└── secrets/                          # PROD conn stringy
    ├── azure_sql_conn.txt
    ├── gaia_conn.txt
    ├── partner_{cz,pl,hu,us}_conn.txt
    ├── servicebus_conn.txt           # FF PROD namespace
    └── bridge_admin_api_key.txt

/opt/ff-partner-bridge-test/         ← TEST (nová instance)
├── docker-compose.yml                # IDENTICKÝ s PROD (stejný soubor z repa)
├── .env                              # ACR_NAME, IMAGE_TAG=12, BIND_IP=172.24.0.67, …
└── secrets/                          # TEST conn stringy
    ├── azure_sql_conn.txt            # ↳ jiná Azure SQL DB (např. bridge-test)
    ├── gaia_conn.txt                 # ↳ GAIA test instance NEBO sdílená read-only
    ├── partner_{cz,pl,hu,us}_conn.txt # ↳ Partner3 TEST databáze
    ├── servicebus_conn.txt           # ↳ FF TEST Service Bus namespace
    └── bridge_admin_api_key.txt      # ↳ jiný API klíč (TEST/PROD se NESMÍ překrývat)
```

**Compose project name** se odvozuje z názvu adresáře:
- `/opt/ff-partner-bridge/` → projekt `ff-partner-bridge`
- `/opt/ff-partner-bridge-test/` → projekt `ff-partner-bridge-test`

Tedy `docker compose ps` z každého adresáře ukazuje **jen své kontejnery**.
Síťové prostory a volumy jsou auto-izolované Docker Compose. Žádná konfigurace
navíc.

---

## 2. Konfigurační delta — co se liší per env

`docker-compose.yml` zůstává **identický** v obou adresářích (kopie ze stejného
commitu repa). Liší se pouze `.env` a `secrets/`:

| Klíč | PROD | TEST | Pozn. |
|---|---|---|---|
| `ACR_NAME` | `acrxtuningprod` | `acrxtuningprod` | Stejný registry |
| `IMAGE_TAG` | např. `11` | např. `12` (newer) | TEST běží lead build, PROD pinned po validaci |
| `BIND_IP` | `172.24.0.66` | `172.24.0.67` (nebo `127.0.0.1`) | Různé host IP, **nikdy 0.0.0.0** |
| `ApplicationInsights__ConnectionString` | shared AI resource | shared AI resource | Rozlišení v dotazech přes `cloud_RoleInstance` — vyžaduje nastavit `BRIDGE_HOSTNAME` v `.env` (jinak AI použije container ID, které se mění při recreate). Splitting do dvou AI resource je future-optional. |
| `BRIDGE_HOSTNAME` | `ff-partner-bridge-prod` | `ff-partner-bridge-test` | Propaguje se přes `hostname:` v compose do `Environment.MachineName` → AI `cloud_RoleInstance`. Bez něho je RoleInstance nečitelné container ID. |
| `OwnerMapping__DefaultOwnerId` | `1` (real FF user) | např. `99` (test user v Partner3 TEST) | Mapování per env |
| `OwnerMapping__Mappings__<GUID>` | reálná FF mapování | TEST data | Liší se podle obsahu FF instancí |
| `Bridge__Polling__BackfillEnabled` | `true` (default) | `false` doporučeno | Implementováno v PR #8 — viz [§5](#5-backfill-kill-switch). Propagace přes compose `environment:` (PR #9). |
| `ServiceBus__SubscriptionName` | `bridge-main` (default) | `bridge-main` (jiný namespace, jméno se může opakovat) | Subscription je per-namespace, takže stejné jméno v různých namespaces nekoliduje. Default v compose je `bridge-main` (PR #9 — empty string by způsobil `MessagingEntityNotFound`). |
| `secrets/azure_sql_conn.txt` | PROD bridge DB | **TEST bridge DB** (samostatná) | bridge_id_mapping atd. nesmí být sdílené |
| `secrets/gaia_conn.txt` | GAIA PROD | GAIA TEST (nebo PROD read-only) | Bridge GAIA nezapisuje → sdílené read-only je akceptovatelné |
| `secrets/partner_*_conn.txt` | Partner3 PROD (4×) | **Partner3 TEST (4×)** | NIKDY nesmí TEST psát do PROD DB |
| `secrets/servicebus_conn.txt` | FF PROD namespace | **FF TEST namespace** | Klíčová izolace |
| `secrets/bridge_admin_api_key.txt` | unikátní 32+ znaků | unikátní 32+ znaků (jiný) | Aby kompromitace TEST klíče neotevřela PROD API |

### Co je v `appsettings.json` (zabaleno v image) a per-env se neřeší

`Topic` names (`ff.company.sync` …), polling interval, Serilog úrovně,
DLQ retry policy — všechno common, žije v image. Pokud by se TEST a PROD měly
lišit i tady, je to **code change**, ne config.

---

## 3. Migrační postup — z dnešního single-env do TEST + PROD

> Předpoklad: dnes existuje `/opt/ff-partner-bridge/` jako **PROD** instance,
> běží a posílá zprávy do FF PROD Service Bus namespace.

### Krok 1: Zajistit prerekvizity (off-host)

- [ ] **FF TEST Service Bus namespace** existuje a má **provisioned topics**
      `ff.company.sync`, `ff.contact.updated`, `ff.company.owner-changed`,
      `ff.company.disabled` + outbound `bridge.company.*`, `bridge.order.*`
      + subscription `bridge-main` (nebo dohodnutý název). Pro provisioning
      proti TEST namespace spusť `infra/F0-05-servicebus-setup.sh` se
      správnou `SB_NAMESPACE` proměnnou — skript je idempotentní.
- [ ] **Azure SQL TEST DB** existuje pro Bridge metadata (`bridge_id_mapping`,
      `bridge_poll_watermark`, `bridge_order_snapshot`) — DDL viz
      `sql/F0-bridge-azure-sql-ddl.sql`.
- [ ] **Partner3 TEST DB** v 4 regionech existuje a má aplikovanou F0-02 migraci
      (`sql/F0-02-tbl-client-extensions.sql`) na `tbl_client`.
- [ ] **TEST FF Service Bus connection string** dostupný (z Azure Portal nebo Key Vault).
- [ ] **TEST credentials** pro Partner3, GAIA, Azure SQL k dispozici v password manageru.
- [ ] **Druhý host IP** v subnetu 172.24.0/24 dostupný pro TEST bind (alternativa:
      použít stejnou IP s jiným portem — viz [§4](#4-síťové-binding)).

### Krok 2: Vytvořit TEST adresář (na deploy hostu)

```bash
sudo mkdir -p /opt/ff-partner-bridge-test
sudo chown ff-bridge:ff-bridge /opt/ff-partner-bridge-test
cd /opt/ff-partner-bridge-test

# Stáhnout SAME docker-compose.yml ze stejného commitu jako PROD
# (kontrola konzistence: porovnej s PROD)
curl -fsSL https://raw.githubusercontent.com/rcendelin/FF-Partner/main/docker-compose.yml \
  -o docker-compose.yml
diff /opt/ff-partner-bridge/docker-compose.yml docker-compose.yml
# → mělo by být PRÁZDNÉ. Pokud ne, PROD instance má manuální úpravy — sjednoť.

# Stáhnout init skript pro secrets
mkdir -p infra
curl -fsSL https://raw.githubusercontent.com/rcendelin/FF-Partner/main/infra/F0-06-docker-secrets-init.sh \
  -o infra/F0-06-docker-secrets-init.sh
chmod +x infra/F0-06-docker-secrets-init.sh
```

### Krok 3: Vytvořit `secrets/` TEST hodnotami

```bash
cd /opt/ff-partner-bridge-test
bash infra/F0-06-docker-secrets-init.sh
```

Skript je interaktivní — zadej **TEST** conn stringy, ne PROD. **Před spuštěním
Bridge si validuj připojení** připraveným skriptem:

```bash
# Vyžaduje: sudo apt install default-mysql-client
SECRETS_DIR=/opt/ff-partner-bridge-test/secrets \
  bash /path/to/repo/scripts/validate-db-connections.sh
```

Skript ověří:
- Identitu DB (`@@hostname`, `DATABASE()`, `@@version`)
- Že Partner3 conn stringy ukazují na Partner3 (`tbl_client` ano, `cfg_country` ne)
- Že GAIA conn string ukazuje na GAIA (`cfg_country` ano, `tbl_client` ne)
- Distribuci `client_country_short` v `tbl_client` — odhalí prohozené regiony
- Že F0-02 DDL migrace na TEST proběhla (`ff_company_id` sloupec existuje)

Detaily: viz [`OPERATIONS-RUNBOOK.md`](OPERATIONS-RUNBOOK.md) §Validace připojení k DB.

### Krok 4: Vytvořit `.env` TEST hodnotami

```bash
cat > /opt/ff-partner-bridge-test/.env <<'EOF'
# === Image z ACR (TEST tracking — nejnovější tag) ===
ACR_NAME=acrxtuningprod
IMAGE_TAG=12

# === Network binding — TEST naslouchá na jiné IP ===
BIND_IP=172.24.0.67
# Alternativa: stejná IP, jiný port — viz §4

# === Hostname → App Insights cloud_RoleInstance ===
BRIDGE_HOSTNAME=ff-partner-bridge-test

# === Telemetrie ===
ApplicationInsights__ConnectionString=InstrumentationKey=...

# === Mapování ownerů — TEST data ===
OwnerMapping__DefaultOwnerId=99

# === DOPORUČENÉ pro TEST — vypnout backfill ===
# Vyžaduje code change v Program.cs (viz §5). Bez něho TEST při prvním startu
# zveřejní 12 měsíců TEST objednávek do FF TEST Service Bus.
# Bridge__Polling__BackfillEnabled=false

# === Service Bus subscription (volitelně, default 'bridge-main') ===
# ServiceBus__SubscriptionName=bridge-main
EOF
sudo chmod 600 /opt/ff-partner-bridge-test/.env
```

### Krok 5: Pull image, ověřit konfiguraci, start

```bash
cd /opt/ff-partner-bridge-test
docker pull "acrxtuningprod.azurecr.io/ff-partner-bridge:12"

# Compose validation — vyřeší proměnné, zkontroluje syntaxi
docker compose config > /dev/null && echo "compose OK"

# Smoke test secrets mount BEZ skutečného startu
docker compose run --rm --entrypoint /bin/sh ff-partner-bridge \
  -c 'id; ls -la /run/secrets/; head -c 30 /run/secrets/partner_cz_conn'

# Skutečný start
docker compose up -d

# Sleduj logy startu (60s window)
docker compose logs -f --tail=100 ff-partner-bridge

# Health
curl -fsSL http://172.24.0.67:8080/health
```

### Krok 6: Validace, že PROD nepoškozený

```bash
# Z /opt/ff-partner-bridge/ (PROD) — health stále zelený
cd /opt/ff-partner-bridge
docker compose ps
curl -fsSL http://172.24.0.66:8080/health

# Žádný PROD log nezmiňuje TEST DB hostname, namespace, atd.
docker compose logs --tail=200 ff-partner-bridge | grep -i 'test\|staging'
# → mělo by být PRÁZDNÉ
```

---

## 4. Síťové binding — dvě možnosti

### A) Různé IP, stejný port (`172.24.0.66:8080` PROD, `172.24.0.67:8080` TEST)

**Doporučeno**, pokud host má v `eno1`/`ens33`/whatever více adres:

```bash
# Ověř, že obě IP jsou na hostu
ip -br addr show | awk '$3 ~ /^172\.24/ {print $3}'
# 172.24.0.66/24
# 172.24.0.67/24
```

Pokud druhá IP chybí, přidej:

```bash
sudo ip addr add 172.24.0.67/24 dev eno1
# Persistent: edit /etc/netplan/*.yaml a apply
```

### B) Stejná IP, různé porty (`172.24.0.66:8080` PROD, `172.24.0.66:8081` TEST)

Bez druhé IP — jen TEST má `BIND_IP_PORT=8081` (nebo upravit `.env`
+ `docker-compose.yml`). Pokud `docker-compose.yml` má fixní `:8080`, nelze
bez code-change v compose souboru. Aktuální `docker-compose.yml` ano:

```yaml
ports:
  - "${BIND_IP:-127.0.0.1}:8080:8080"
```

→ Pro variantu B by bylo lepší **parametrizovat i host port**:
`"${BIND_IP:-127.0.0.1}:${BIND_PORT:-8080}:8080"`. Drobná změna v `docker-compose.yml`,
mohu připravit v rámci backfill kill switch PR.

Reverse proxy (nginx, Traefik) na hostu může obojí konsolidovat na
`https://bridge.xtuning.local/prod/` a `/test/` — out of scope tohoto dokumentu.

---

## 5. Backfill kill switch

**Důvod:** `OrderBackfillService` (`src/Bridge.Api/Pollers/OrderBackfillService.cs`)
spustí 60s po startu jednorázový export 12 měsíců objednávek z **každé regionální
Partner3 DB**. Idempotence se řídí přes `bridge_sync_log` (operation=`order_backfill`)
v Azure SQL — což znamená, že **na čerstvé TEST Azure SQL DB se backfill VŽDY
spustí**, i kdyby TEST Partner3 conn stringy byly špatně nastavené.

Implementováno v PR #8 — `Program.cs` čte `Bridge:Polling:BackfillEnabled`
s defaultem `true`. PR #9 doplnil compose `environment:` propagaci, takže
`.env` hodnota se propaguje do kontejneru.

### Použití per env

| Env | `.env` | Důsledek |
|---|---|---|
| PROD | (nenastaveno, default `true`) | Backfill při prvním startu, pak idempotentní |
| TEST | `Bridge__Polling__BackfillEnabled=false` | TEST nikdy nedělá backfill — bezpečné při experimentování |

Ověření z logu Bridge:
```
[WRN] OrderBackfillService VYPNUTA — Bridge:Polling:BackfillEnabled=false
[INF] … Order pollery (bez backfill) zaregistrovány
```

---

## 6. Image tag flow — TEST first, PROD promote

Cíl: PROD běží jen na image tagy, které byly nejdřív validovány v TEST.

```
push do main
    │
    ▼
GitHub Actions build-and-test → acr-push → image :N v ACR
    │
    ▼
TEST: IMAGE_TAG=N v /opt/ff-partner-bridge-test/.env
      docker pull + docker compose up -d
      Monitoring 24-48h, smoke testy, /api/sync-log review
    │
    ▼ (po validaci)
PROD: IMAGE_TAG=N v /opt/ff-partner-bridge/.env  ← STEJNÝ tag, ne rebuild
      docker pull + docker compose up -d
```

Pravidla:

- TEST **vždy běží na novějším nebo stejném tagu** než PROD.
- PROD **nikdy nedostane tag, který neprošel TEST**.
- Rollback PROD: nastavit předchozí ověřený tag (viz [`OPERATIONS-RUNBOOK.md`](OPERATIONS-RUNBOOK.md) §5).
- **Pre-merge testování v TEST není dnes možné** — `acr-push` job běží pouze na
  push do `main` (po merge PR). Pokud chceš mít cestu „branch → image v ACR → TEST
  validace → merge do main", musíš rozšířit `.github/workflows/bridge.yml` o
  `acr-push` na push do `develop` (nebo na PR label) s odlišným tag schema
  (např. `dev-<runNumber>`). Mimo scope tohoto dokumentu — sleduj jako tech debt.

---

## 7. Service Bus topologie — Scénář A (potvrzený) vs Scénář B

### Scénář A — FF má TEST namespace ✅ aktuální plán

```
FieldForce PROD ──▶ sb://ff-prod.servicebus.windows.net/ff.company.sync
                                                          │
                                                          ▼
                                     Bridge PROD subscription 'bridge-main'

FieldForce TEST ──▶ sb://ff-test.servicebus.windows.net/ff.company.sync
                                                          │
                                                          ▼
                                     Bridge TEST subscription 'bridge-main'
                                     (jiný namespace → stejné jméno OK)
```

- Bridge **kód se nemění** — jen `servicebus_conn.txt` per env.
- DLQ monitoring funguje per env (DlqMonitorService čte hardcoded subscription
  `bridge-main`, ale v jiném namespace = jiná DLQ).
- Outbound topics (`bridge.*`) jsou per-namespace — TEST publikuje do TEST namespace,
  FF TEST instance je tam konzumuje.

### Scénář B — FF TEST namespace zatím neexistuje

Pokud FF tým TEST namespace ještě nepřipravil, máš 2 možnosti:

| Možnost | Co udělat | Risk |
|---|---|---|
| **B.1** Bridge TEST běží v limited mode | V `secrets/servicebus_conn.txt` dej prázdný string. Bridge se nastartuje, `Program.cs` zaloguje warning a **nezaregistruje konzumenty ani pollery**. Funguje `/health` + DB validace přes connection factories. | Není funkční integrační test — jen schema validation |
| **B.2** Shared namespace s env-prefixed topics | Vyžaduje, aby FF tým vytvořil `ff.test.company.sync` atd. v PROD namespace. Bridge TEST: env vary `ServiceBus__CompanySyncTopic=ff.test.company.sync` atd. Outbound topics (`bridge.*`) jsou ale stále hardcoded v kódu — code change nutný pro per-env prefix outbound. | Cross-contamination, pokud FF zapomene env property na zprávě |

**Doporučení**: pokud Scénář A není dnes, počkat s plnohodnotným TEST setupem
(jen B.1) a tlačit FF tým na separátní namespace. B.2 přidává tech debt.

---

## 8. Safety checklist před prvním startem TEST

Před `docker compose up` v TEST adresáři projdi:

- [ ] **Validační skript** `scripts/validate-db-connections.sh` potvrzuje, že
      `partner_{cz,pl,hu,us}_conn.txt` ukazují na TEST databáze, ne PROD.
      Klíčový check: `SELECT @@hostname` a `client_country_short` distribuce
      v `tbl_client` (viz [`OPERATIONS-RUNBOOK.md`](OPERATIONS-RUNBOOK.md)
      §Validace připojení k DB).
- [ ] `secrets/azure_sql_conn.txt` ukazuje na **samostatnou** TEST Azure SQL DB
      pro `bridge_id_mapping`, ne sdílenou s PROD.
- [ ] `secrets/servicebus_conn.txt` je FF TEST namespace (Endpoint URL obsahuje
      `-test` nebo dohodnutý suffix). Ověř `az servicebus namespace show --name ...`.
- [ ] `BIND_IP` v `.env` je různá od PROD (`172.24.0.66`), nebo port je různý.
- [ ] `BRIDGE_HOSTNAME` v `.env` je nastaveno na `ff-partner-bridge-test`
      (jinak App Insights nerozliší TEST od PROD).
- [ ] `bridge_admin_api_key.txt` v TEST je **jiný** než PROD (32+ znaků, nový openssl rand).
- [ ] `OwnerMapping__DefaultOwnerId` ukazuje na TEST owner v Partner3 TEST DB,
      ne PROD owner.
- [ ] Pokud máš PR s backfill kill switchem mergnutý: `Bridge__Polling__BackfillEnabled=false`
      v TEST `.env`. Bez něj přijmi, že prvním startem proběhne 12měsíční
      export TEST objednávek do FF TEST namespace.
- [ ] `docker compose config` v TEST adresáři projde bez warningů.
- [ ] PROD instance v `/opt/ff-partner-bridge/` je **healthy** před a po
      spuštění TEST — `curl http://172.24.0.66:8080/health` ověřit.

---

## 9. Známé limity a tech debt

| Téma | Stav | Risk |
|---|---|---|
| **Outbound topics jsou hardcoded** | `bridge.company.synced`, `bridge.order.created` atd. v 9 místech kódu. Pro per-env prefix outbound (např. `bridge.test.order.created`) vyžaduje refactor — vytáhnout do `appsettings.json` jako `ServiceBus:Outbound:*Topic`. | Při Scénáři A irelevantní (per-namespace), při Scénáři B.2 problém |
| **DlqMonitorService hardcoded list** | `DlqMonitorService.cs:26-29` má napevno `("ff.company.sync", "bridge-main")` × 4. Per-env override topic nebo subscription = code change. | Stejné jako výše |
| **OrderBackfillService kill switch** | Implementováno (PR #8 + PR #9 propagace). | — |
| **App Insights single resource** | Bridge TEST i PROD telemetrie jdou do jednoho AI resource, odlišují se `cloud_RoleInstance`. Splitting do dvou AI resource je future-optional — vyžaduje druhý AI ve `.env`. | Nízký — KQL dotazy si umí filtrovat |
| **Compose `BIND_IP` parametrizace bez port parametru** | `${BIND_IP:-127.0.0.1}:8080:8080` — port 8080 hardcoded. Pro variantu B (stejná IP, jiný port) je třeba další proměnná `${BIND_PORT:-8080}`. | Drobný — fix při backfill kill switch PR |

---

## 10. Související dokumenty

- [`OPERATIONS-RUNBOOK.md`](OPERATIONS-RUNBOOK.md) — provoz jedné instance
- [`AZURE-ACR-SETUP.md`](AZURE-ACR-SETUP.md) — CI/CD a ACR
- [`F0-08-owner-mapping-and-sla.md`](F0-08-owner-mapping-and-sla.md) — owner mapping config
- [`CLAUDE.md`](../CLAUDE.md) sek. 16 — klíčová rozhodnutí, sek. 17 — co Bridge nesmí dělat
