# FF-Partner Bridge

Integrační most mezi systémem **FieldForce** a partnerskými CRM databázemi
**Partner3** v 4 zemích (CZ / PL / HU / US). Nahrazuje legacy GAIA + Pipedrive
integraci.

Bridge konzumuje události z Azure Service Bus (`ff.company.*`, `ff.contact.*`),
provádí synchronizaci klientů a vlastníků mezi regionálními databázemi,
detekuje konflikty, řídí cross-region migrace přes ságu a v opačném směru
pollinguje objednávky z Partner3 zpět do FieldForce DB.

---

## Architektura

```
src/
├── Bridge.Domain          ← Modely, message kontrakty, enums, exceptions
├── Bridge.Application     ← Use cases, interface (IRepositories, IServiceBus...)
├── Bridge.Infrastructure  ← Dapper SQL repozitáře, Service Bus publisher,
│                            GAIA + FieldForce + Partner3 connection factories
└── Bridge.Api             ← ASP.NET Core 8 minimal API
                            ├── Consumers/    (4× Service Bus subscribers)
                            ├── Pollers/      (4× region order pollers + backfill)
                            ├── Sagas/        (cross-region client move + recovery)
                            ├── Endpoints/    (/health, /api/mapping, /api/sync-log, /api/bulk-sync)
                            └── Middleware/   (X-Api-Key auth)

tests/
├── Bridge.Tests              ← Unit testy (xUnit + NSubstitute)
└── Bridge.IntegrationTests   ← Integrační testy (vyžadují živé conn strings,
                                jinak se automaticky přeskočí)
```

Závislosti: **Domain ← Application ← Infrastructure ← Api** (klasický onion).

---

## Build a spuštění

### Předpoklady

- .NET 8 SDK (verze pinnutá v [`global.json`](global.json) — `8.0.420` a vyšší v rámci feature bandu)
- Docker + Docker Compose plugin (jen pro Docker workflow)
- Volitelně: živé connection stringy do FieldForce, GAIA, Partner3 a Service Bus
  (bez nich se Bridge spustí v omezeném módu — pouze `/health`)

### Lokální build

```bash
dotnet restore FF-Partner-Bridge.slnx
dotnet build  FF-Partner-Bridge.slnx --configuration Release --no-restore
```

### Lokální spuštění (DEV)

Bez infrastruktury (pouze `/health` endpoint):

```bash
dotnet run --project src/Bridge.Api
# → http://localhost:5000/health
```

S plnou infrastrukturou nastavte connection stringy přes user secrets nebo
`appsettings.Development.json` (ten je v `.gitignore`):

```json
{
  "Bridge": {
    "AzureSql": "...",
    "Gaia":     "...",
    "ServiceBus": { "ConnectionString": "..." },
    "Partner": { "Cz": "...", "Pl": "...", "Hu": "...", "Us": "..." },
    "ApiKey":  { "Value": "dev-key-123" }
  },
  "FieldForceDb": "..."
}
```

### Spuštění v Dockeru

```bash
# 1. Připravit Docker Secrets — viz infra/F0-06-docker-secrets-init.sh
mkdir -p secrets
echo "..." > secrets/azure_sql_conn.txt
# ... opakovat pro: gaia, partner_cz/pl/hu/us, servicebus, bridge_admin_api_key

# 2. Build a spuštění
docker compose up -d --build

# 3. Healthcheck
curl http://localhost:8080/health
# → {"status":"healthy",...}
```

V produkci se image distribuuje přes `registry.cendelin.eu/ff-partner-bridge:<TAG>`
a deploy probíhá přes CI (viz níže). Lokální `docker compose` použije `:latest`.

---

## Testy

```bash
# Unit testy (rychlé, bez infrastruktury)
dotnet test tests/Bridge.Tests --configuration Release

# Integrační testy (vyžadují živé DB + Service Bus)
$env:BRIDGE_IT_AZURE_SQL_CONN="..."
$env:BRIDGE_IT_GAIA_CONN="..."
$env:BRIDGE_IT_PARTNER_CZ_CONN="..."
$env:BRIDGE_IT_PARTNER_PL_CONN="..."
dotnet test tests/Bridge.IntegrationTests --filter Category=Integration
```

Bez `BRIDGE_IT_*` proměnných se integrační testy **automaticky přeskočí**
(pomocí `[FactIfInfra]` atributu) — bezpečné v CI bez infrastruktury.

---

## Konfigurace v produkci

Connection stringy a API klíč se v produkci čtou z **Docker Secrets**
(`/run/secrets/<name>`). Načítání zajišťuje [`DockerSecretsReader`](src/Bridge.Api/SecretReaders/DockerSecretsReader.cs)
s fallbackem na `appsettings.json` pro DEV.

| Secret | Účel |
|---|---|
| `azure_sql_conn` | Bridge mapping DB (Azure SQL) |
| `gaia_conn` | GAIA MySQL — adresní číselníky (read-only) |
| `partner_cz_conn` … `partner_us_conn` | Partner3 SQL Server per region |
| `servicebus_conn` | Azure Service Bus namespace (sdílený s FieldForce) |
| `bridge_admin_api_key` | API klíč pro `/api/*` endpointy (X-Api-Key header) |

Bez `bridge_admin_api_key` se v produkci Bridge **odmítne spustit** (DEV pouští s warningem).

Detailní setup viz [`infra/F0-06-keyvault-setup.sh`](infra/F0-06-keyvault-setup.sh)
a [`infra/F0-06-docker-secrets-init.sh`](infra/F0-06-docker-secrets-init.sh).

---

## CI/CD

**GitHub Actions je primární CI/CD platforma**, GitLab je read-only mirror
(bez runneru).

- **GitHub Actions** ([`.github/workflows/bridge.yml`](.github/workflows/bridge.yml))
  — kanonický pipeline:
  **build-and-test** → **docker-push** (auto na `main`) →
  **deploy** (Environment `production` approval). Detaily v
  [`docs/GITHUB-CICD.md`](docs/GITHUB-CICD.md).
- **GitLab** (`git.xtuning.cz/fieldforce/partner-bridge`) — read-only mirror,
  žádný CI runner, žádný workflow soubor.

---

## Dokumentace

| Dokument | Obsah |
|---|---|
| [`docs/GITHUB-CICD.md`](docs/GITHUB-CICD.md) | Detailní průvodce GitHub Actions pipeline + troubleshooting |
| [`docs/geo-structure.md`](docs/geo-structure.md) | Geografický routing CZ/PL/HU/US, hierarchie GAIA číselníků |
| [`docs/F0-08-owner-mapping-and-sla.md`](docs/F0-08-owner-mapping-and-sla.md) | Owner mapping + SLA thresholds + KQL alerty |
| [`docs/F1-12-go-no-go-checklist.md`](docs/F1-12-go-no-go-checklist.md) | Manuální go/no-go validace mezi fázemi |
| [`runbooks/F3-01-gaia-shutdown-pull.md`](runbooks/F3-01-gaia-shutdown-pull.md) | Vypnutí starého GAIA pull (Pipedrive cron) |
| [`runbooks/F3-02-gaia-shutdown-push.md`](runbooks/F3-02-gaia-shutdown-push.md) | Vypnutí starého GAIA push (Pipedrive webhooks) |
| [`infra/`](infra/) | Bicep IaC pro Service Bus, Key Vault + setup skripty |

---

## Stav projektu

Vývoj probíhá ve **fázích F0–F4**, postupný rollout vůči GAIA / Pipedrive integraci:

| Fáze | Obsah | Stav |
|---|---|---|
| **F0** | Infra: Bicep IaC, Key Vault, Docker Secrets, CI/CD | ✅ hotovo |
| **F1** | Bridge core: 4× consumers, mapping, ságy, conflict detection | ✅ hotovo |
| **F2** | Migrace dat ze starého systému | 🔄 plánováno |
| **F3** | Vypnutí GAIA Pipedrive integrace (runbooks) | ✅ runbooks hotovo |
| **F4** | Order polling (4× region) + backfill 12 měsíců | ✅ hotovo |

---

## Repozitáře

- **GitHub (primární):** https://github.com/rcendelin/FF-Partner
- **GitLab (mirror):** https://git.xtuning.cz/fieldforce/partner-bridge
