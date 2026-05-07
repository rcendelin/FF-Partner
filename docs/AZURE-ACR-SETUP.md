# Azure ACR + GitHub Actions CI/CD — kompletní průvodce

Dokumentace pro CI/CD pipeline FF-Partner Bridge: jak Azure Container Registry,
GitHub Actions workflow a ruční deploy zapadají dohromady.

GitHub Actions ([`.github/workflows/bridge.yml`](../.github/workflows/bridge.yml))
je **kanonický CI/CD pipeline**. GitLab repozitář
(`git.xtuning.cz/fieldforce/partner-bridge`) je read-only mirror bez runneru.

Návod pokrývá:

0. [Přehled pipeline](#0-přehled-pipeline)
1. [Vytvoření Azure Container Registry](#1-vytvoření-azure-container-registry)
2. [Service Principal pro GitHub Actions (push)](#2-service-principal-pro-github-actions-push)
3. [Nastavení GitHub Secrets](#3-nastavení-github-secrets)
4. [Ověření pipeline](#4-ověření-pipeline)
5. [Nastavení deploy serveru pro pull z ACR](#5-nastavení-deploy-serveru-pro-pull-z-acr)
6. [Rollback a správa tagů](#6-rollback-a-správa-tagů)
7. [Řešení problémů](#7-řešení-problémů)

---

## 0. Přehled pipeline

```
push na main / develop / PR
        │
        ▼
┌───────────────────┐
│  build-and-test   │  dotnet restore → build → test
│  (vždy)           │  artefakty: TestResults/**/*.trx
└────────┬──────────┘
         │ pouze push na main
         ▼
┌───────────────────┐
│     acr-push      │  Azure SP login → az acr login →
│     (auto)        │  docker build → docker push do ACR
│                   │  tag: ${{ github.run_number }}
└───────────────────┘
         │
         ▼  (ruční krok mimo pipeline)
┌───────────────────┐
│  Manuální deploy  │  ssh deploy@xtuning → docker pull →
│   na on-premise   │  docker compose up
└───────────────────┘
```

**Dvě stages v GitHub Actions** + ruční deploy.

| Stage | Spouštění | Co dělá |
|---|---|---|
| `build-and-test` | PR + push na `main`/`develop` | `dotnet restore` → `build --configuration Release` → `test --logger trx`; TRX artefakty 30 dní |
| `acr-push` | pouze push na `main` | Azure SP login (`azure/login@v2`) → `az acr login` → docker build s labely (`git-commit`, `build-id`) → push do `${ACR_NAME}.azurecr.io/ff-partner-bridge:${run_number}` |

**Tagování image:** `${{ github.run_number }}` (sekvenční, např. `1`, `2`, `42`).
Image se **nepushuje** s tagem `:latest` — nasazení je deterministické.

**Deploy:** SSH na on-premise XTuning probíhá ručně (`docker pull` + `docker compose up`).
Důvod: deploy server není veřejně dostupný a nemá přístupný runner.
Postup viz [sekce 5](#5-nastavení-deploy-serveru-pro-pull-z-acr) a [6](#6-rollback-a-správa-tagů).

### Kdy se pipeline spouští

| Událost                          | build-and-test | acr-push    |
|----------------------------------|----------------|-------------|
| Push na `main`                   | Ano            | Ano (auto)  |
| Push na `develop`                | Ano            | Ne          |
| Push na jinou větev              | Ne             | Ne          |
| Pull Request (open / update)     | Ano            | Ne          |

### Globální proměnné workflow

| Proměnná | Hodnota | Popis |
|---|---|---|
| `IMAGE_NAME` | `ff-partner-bridge` | Název image (registry je `${ACR_NAME}.azurecr.io`) |
| `DOTNET_VERSION` | `9.0.x` | SDK verze. `.slnx` solution formát vyžaduje SDK 9.0.200+. TFM zůstává `net8.0`. |

### NuGet cache

Workflow cacheuje NuGet packages pomocí `actions/cache@v4` s klíčem podle hashe
všech `.csproj` souborů. Cache se invaliduje pouze při změně závislostí.

---

## 1. Vytvoření Azure Container Registry

### Prerekvizity

```bash
# Přihlášení do Azure CLI
az login

# Pokud máš více subscription, vyber tu správnou:
az account set --subscription "<subscription-id-nebo-name>"

# Ověř, kterou subscription používáš:
az account show --query '{name:name, id:id}' -o table
```

### Proměnné použité v dalších krocích

```bash
# Doplň podle svého prostředí:
export RG_NAME="rg-xtuning-prod"           # resource group (existující nebo nový)
export ACR_NAME="acrxtuningprod"           # 5–50 znaků, jen [a-zA-Z0-9], unique v Azure
export LOCATION="westeurope"               # nebo jiný region
export ACR_SKU="Basic"                     # Basic | Standard | Premium
```

> **Volba SKU:**
> - **Basic** (~5 USD/měsíc, 10 GB) — pro malé projekty, žádné geo-replication
> - **Standard** (~20 USD/měsíc, 100 GB) — vyšší propustnost
> - **Premium** (~50 USD/měsíc, 500 GB) — geo-replication, content trust, private endpoints
>
> Pro Bridge stačí **Basic**.

### Vytvoření resource group (pokud neexistuje)

```bash
az group create --name "$RG_NAME" --location "$LOCATION"
```

### Vytvoření ACR

```bash
az acr create \
  --resource-group "$RG_NAME" \
  --name "$ACR_NAME" \
  --sku "$ACR_SKU" \
  --location "$LOCATION"
```

Po dokončení vypiš detaily:

```bash
az acr show \
  --name "$ACR_NAME" \
  --query '{loginServer:loginServer, sku:sku.name, id:id}' \
  -o table
```

`loginServer` má tvar `<acr-name>.azurecr.io` — to je hodnota, kterou používá `docker push`.

### Test push z lokálu (volitelné)

```bash
# Login do ACR (CLI použije Azure RBAC token, ne admin user)
az acr login --name "$ACR_NAME"

# Lokálně build a push (test že to funguje)
docker build -t "${ACR_NAME}.azurecr.io/ff-partner-bridge:test" .
docker push "${ACR_NAME}.azurecr.io/ff-partner-bridge:test"

# Ověř obsah registry
az acr repository list --name "$ACR_NAME" -o table
az acr repository show-tags --name "$ACR_NAME" --repository ff-partner-bridge -o table
```

---

## 2. Service Principal pro GitHub Actions (push)

GitHub Actions runner se autentizuje k Azure pomocí **Service Principal** s rolí `AcrPush`.
Scope role je omezen pouze na konkrétní ACR — minimum oprávnění.

### Vytvoření Service Principal

```bash
# Získej resource ID ACR
ACR_ID=$(az acr show --name "$ACR_NAME" --query id -o tsv)

# Vytvoř SP s rolí AcrPush, omezenou na tuto ACR
az ad sp create-for-rbac \
  --name "ff-partner-bridge-ci" \
  --role AcrPush \
  --scopes "$ACR_ID" \
  --sdk-auth
```

### Výstup — JSON pro GitHub

Příkaz vytiskne JSON ve tvaru (zachytávej ho hned, neukáže se znovu):

```json
{
  "clientId": "00000000-0000-0000-0000-000000000000",
  "clientSecret": "abcDEFghiJKLmnoPQRstuVWXyz1234567890",
  "subscriptionId": "11111111-1111-1111-1111-111111111111",
  "tenantId": "22222222-2222-2222-2222-222222222222",
  "activeDirectoryEndpointUrl": "https://login.microsoftonline.com",
  "resourceManagerEndpointUrl": "https://management.azure.com/",
  "activeDirectoryGraphResourceId": "https://graph.windows.net/",
  "sqlManagementEndpointUrl": "https://management.core.windows.net:8443/",
  "galleryEndpointUrl": "https://gallery.azure.com/",
  "managementEndpointUrl": "https://management.core.windows.net/"
}
```

**Tento JSON v celku** uložíš jako GitHub Secret `AZURE_CREDENTIALS` (krok 3).

> **Důvod role `AcrPush` (a ne např. Contributor):** SP nesmí mít vyšší oprávnění,
> než kolik potřebuje. `AcrPush` umožní push i pull — jiné akce v subscription dělat nemůže.

### Volitelné: omezení podle IP

Pokud chceš ještě zúžit přístup, lze v ACR povolit jen určité veřejné IP.
Pro Premium SKU je k dispozici i **Trusted Services** + Private Endpoints.

```bash
# Příklad pro Premium ACR (Basic IP rules nepodporuje):
az acr update --name "$ACR_NAME" --default-action Deny
az acr network-rule add --name "$ACR_NAME" --ip-address "1.2.3.4"
```

> GitHub Actions hosted runners mají dynamické IP — pro IP-allowlisting použij self-hosted runner
> nebo Premium ACR + Private Endpoint do propojené VNet.

---

## 3. Nastavení GitHub Secrets

Přejdi do **GitHub → repo → Settings → Secrets and variables → Actions → New repository secret**
a přidej:

| Secret | Hodnota |
|---|---|
| `ACR_NAME` | název ACR (jen samotný, bez `.azurecr.io`), např. `acrxtuningprod` |
| `AZURE_CREDENTIALS` | celý JSON ze Service Principalu (krok 2) |

Po uložení se `AZURE_CREDENTIALS` zobrazí jen jako masked — z UI se už nedá přečíst.
Pokud ho ztratíš, vygeneruj nový SP heslo:

```bash
az ad sp credential reset \
  --id "<clientId-z-původního-výstupu>" \
  --years 1
```

### Doporučení pro environments

Pokud chceš push omezit jen na chráněnou větev:

1. **Settings → Environments → New environment** → název `production`
2. V environment přidej **Required reviewers** (volitelné)
3. **V environmentu** vlož secrets `ACR_NAME` a `AZURE_CREDENTIALS` místo do repo-level secrets
4. V workflow přidej do `acr-push` jobu:

```yaml
acr-push:
  environment: production
  ...
```

Pak push do ACR vyžaduje schválení reviewera (audit log + manuální gate).

---

## 4. Ověření pipeline

### Trigger workflow

```bash
# Z lokálu — push na main spustí celou pipeline
git push origin main
```

V GitHub UI **Actions → CI/CD** uvidíš dva joby:

1. `Build + Test` — vždy
2. `Build + Push to Azure ACR` — jen na push do `main`

### Co kontrolovat

- Job `acr-push` má step **Azure login (Service Principal)** zelený → SP funguje
- Step **ACR login** zelený → SP má roli `AcrPush` na správné ACR
- Step **Docker push** zelený → image je v registry
- Step **Image summary** vypíše tag a registry — vidíš v záložce **Summary** pipeline

### Ověření v Azure

```bash
# Tagy v repository
az acr repository show-tags \
  --name "$ACR_NAME" \
  --repository ff-partner-bridge \
  -o table

# Manifest konkrétního tagu (labely git-commit, build-id)
az acr manifest show \
  --registry "$ACR_NAME" \
  --name "ff-partner-bridge:<tag>" \
  --query 'config.labels'
```

---

## 5. Nastavení deploy serveru pro pull z ACR

Bridge nasazuješ ručně přes `docker compose` na on-premise XTuning serveru.
Server potřebuje **read-only** přístup do ACR.

### Možnost A — ACR Token (doporučeno)

ACR Token je samostatný credential omezený scope-em na konkrétní repository.
Lepší než Service Principal — snadno revoke a nemá Azure RBAC oprávnění.

```bash
# Vytvoř scope map: jen pull pro ff-partner-bridge
az acr scope-map create \
  --name "bridge-pull" \
  --registry "$ACR_NAME" \
  --repository "ff-partner-bridge" content/read metadata/read

# Vytvoř token s tím scope mapem
az acr token create \
  --name "bridge-deploy-xtuning" \
  --registry "$ACR_NAME" \
  --scope-map "bridge-pull"
```

Výstup obsahuje **password1** a **password2** — zobrazí se jen jednou. Ulož je bezpečně
(např. do XTuning password manageru).

Na deploy serveru:

```bash
# Login (jednorázově)
docker login "${ACR_NAME}.azurecr.io" \
  --username bridge-deploy-xtuning \
  --password "<password1>"

# Pull konkrétního tagu
docker pull "${ACR_NAME}.azurecr.io/ff-partner-bridge:<tag>"
```

Token rotuj jednou ročně:

```bash
az acr token credential generate \
  --name "bridge-deploy-xtuning" \
  --registry "$ACR_NAME" \
  --password1
```

### Možnost B — druhý Service Principal s AcrPull

Pokud preferuješ konzistenci s CI:

```bash
az ad sp create-for-rbac \
  --name "ff-partner-bridge-deploy" \
  --role AcrPull \
  --scopes "$ACR_ID"
```

Na deploy serveru přihlas se přes `clientId` jako username a `password` jako heslo:

```bash
docker login "${ACR_NAME}.azurecr.io" \
  --username "<clientId>" \
  --password "<clientSecret>"
```

### Spuštění Bridge na serveru

```bash
cd /opt/ff-partner-bridge

# Nastav proměnné používané v docker-compose.yml
export ACR_NAME="acrxtuningprod"
export IMAGE_TAG="<číslo-pipeline-z-GitHub-Actions>"

# Pull + start
docker pull "${ACR_NAME}.azurecr.io/ff-partner-bridge:${IMAGE_TAG}"
docker compose up -d --no-build ff-partner-bridge

# Health check
curl http://localhost:8080/health
```

Pro perzistenci proměnných použij `.env` v `/opt/ff-partner-bridge/`:

```bash
# /opt/ff-partner-bridge/.env
ACR_NAME=acrxtuningprod
IMAGE_TAG=42
AZURE_KEY_VAULT_URI=https://kv-xtuning-prod.vault.azure.net/
MANAGED_IDENTITY_CLIENT_ID=<guid>
```

Docker Compose ho automaticky načte při `docker compose up`.

---

## 6. Rollback a správa tagů

### Rollback na předchozí tag

```bash
cd /opt/ff-partner-bridge

# Najdi předchozí tag (přes Azure CLI nebo GitHub Actions historii)
az acr repository show-tags \
  --name "$ACR_NAME" \
  --repository ff-partner-bridge \
  --orderby time_desc \
  -o table

# Spusť starší tag
IMAGE_TAG=<starší-číslo> docker compose up -d --no-build ff-partner-bridge
```

### Čištění starých tagů v ACR

ACR má **retention policy** (Standard+ SKU) — pro Basic nutno čistit ručně:

```bash
# Smaž tagy starší než 90 dní
az acr repository show-manifests \
  --name "$ACR_NAME" \
  --repository ff-partner-bridge \
  --orderby time_asc \
  --query "[?lastUpdateTime < '$(date -u -d '90 days ago' +%Y-%m-%dT%H:%M:%SZ)'].digest" \
  -o tsv \
  | xargs -I{} az acr repository delete \
      --name "$ACR_NAME" \
      --image "ff-partner-bridge@{}" \
      --yes
```

> Před puštěním si vždy ověř, že tag není zrovna v provozu na deploy serveru.

---

## 7. Řešení problémů

### `Azure login (Service Principal)` selže s `AADSTS7000215: Invalid client secret`

SP secret expiroval (default 1 rok) nebo se přepsal.

```bash
# Vygeneruj nový secret
az ad sp credential reset --id "<clientId>" --years 1
```

Aktualizuj GitHub Secret `AZURE_CREDENTIALS` novým JSONem.

---

### `ACR login` selže s `unauthorized: Application not authorized`

SP nemá roli `AcrPush` na správné ACR. Ověř:

```bash
az role assignment list \
  --assignee "<clientId>" \
  --scope "$ACR_ID" \
  -o table
```

Pokud chybí, přiřaď znovu:

```bash
az role assignment create \
  --assignee "<clientId>" \
  --role AcrPush \
  --scope "$ACR_ID"
```

---

### `Docker push` selže s `denied: requested access to the resource is denied`

Možnosti:

1. ACR je v **Premium** SKU s IP allowlistem — runner GitHub Actions má dynamickou IP
2. Token expiroval mezi `az acr login` a `docker push` (>3 hodiny — nepravděpodobné v jednom jobu)
3. SP byl smazán/zakázán

Diagnostika:

```bash
az acr show --name "$ACR_NAME" --query 'networkRuleSet'
```

---

### Deploy server: `docker pull` vrací `unauthorized`

Token nebo SP credentials neodpovídají. Re-login:

```bash
docker logout "${ACR_NAME}.azurecr.io"
docker login "${ACR_NAME}.azurecr.io"   # zadej znovu username + heslo
```

Pro ACR token zkontroluj jeho stav:

```bash
az acr token show --name "bridge-deploy-xtuning" --registry "$ACR_NAME" \
  --query '{enabled:status, scopeMap:scopeMapId}'
```

---

### Bridge se nestartuje po pull — chybí proměnná `ACR_NAME` nebo `IMAGE_TAG`

Compose vyhodí varování `WARN[0000] The "ACR_NAME" variable is not set`. Přidej do `.env`
v adresáři, kde běží `docker compose`, nebo exportuj v shellu před `docker compose up`.

---

## Reference

- [Azure ACR dokumentace](https://learn.microsoft.com/azure/container-registry/)
- [`azure/login` GitHub Action](https://github.com/Azure/login)
- [ACR tokens & scope maps](https://learn.microsoft.com/azure/container-registry/container-registry-repository-scoped-permissions)
- [ACR built-in roles](https://learn.microsoft.com/azure/container-registry/container-registry-roles)
