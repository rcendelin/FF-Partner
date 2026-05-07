# GitHub Actions CI/CD — FF-Partner Bridge

Dokumentace k souboru [`.github/workflows/bridge.yml`](../.github/workflows/bridge.yml).

GitHub Actions je **kanonický CI/CD pipeline**. GitLab repozitář
(`git.xtuning.cz/fieldforce/partner-bridge`) je read-only mirror bez runneru.

---

## Obsah

1. [Přehled pipeline](#1-přehled-pipeline)
2. [Prerekvizity](#2-prerekvizity)
3. [Globální proměnné](#3-globální-proměnné)
4. [Stage 1 — build-and-test](#4-stage-1--build-and-test)
5. [Stage 2 — docker-push](#5-stage-2--docker-push)
6. [Stage 3 — deploy](#6-stage-3--deploy)
7. [Nastavení Secrets v GitHub](#7-nastavení-secrets-v-github)
8. [Nastavení deploy serveru](#8-nastavení-deploy-serveru)
9. [Nastavení Environment 'production'](#9-nastavení-environment-production)
10. [Kdy se pipeline spouští](#10-kdy-se-pipeline-spouští)
11. [Správa tagů Docker image](#11-správa-tagů-docker-image)
12. [Ruční nasazení bez pipeline](#12-ruční-nasazení-bez-pipeline)
13. [Řešení problémů](#13-řešení-problémů)

---

## 1. Přehled pipeline

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
│   docker-push     │  Docker build → push do registry
│   (auto)          │  tag: ${{ github.run_number }}
└────────┬──────────┘
         │ pouze push na main + Environment approval
         ▼
┌───────────────────┐
│      deploy       │  SSH pull → docker compose up → health check
│ (env approval)    │
└───────────────────┘
```

Tři stages, každá čeká na úspěšné dokončení předchozí (`needs:`).
`deploy` navíc vyžaduje approval v GitHub Environment `production`.

---

## 2. Prerekvizity

### GitHub-hosted runner

Pipeline běží na `ubuntu-22.04` GitHub-hosted runneru. Žádný self-hosted
runner ani žádné speciální nastavení nepotřebuje:

- Docker Engine je předinstalovaný — žádný DinD setup ani privileged mode.
- `dotnet` SDK se instaluje přes `actions/setup-dotnet@v4` v běhu.
- `ssh`, `curl`, `bash` jsou součástí runner image.

### Síťová dostupnost z GitHub-hosted runneru

Runner musí umět:

- HTTPS na `<acr>.azurecr.io` (push image, dostupný z internetu)
- SSH (port 22 default) na deploy server (`DEPLOY_HOST`)

Pokud je deploy server za firewallem bez veřejného přístupu, je potřeba
buď použít **self-hosted runner** uvnitř firmy, nebo otevřít whitelist
IP rozsahu pro [GitHub Actions runnery](https://api.github.com/meta).

### Deploy server (XTuning on-premise)

- Docker Engine + Docker Compose plugin
- Uživatel `deploy` s oprávněním `docker pull` a `docker compose`
- Adresář `/opt/ff-partner-bridge/` s funkčním `docker-compose.yml` + `secrets/`
- SSH server na standardním portu (22)

### Container registry

Image se pushuje do **Azure Container Registry** (`<name>.azurecr.io`).
Registry musí být dostupná z GitHub runneru (push přes OIDC, public endpoint)
i z deploy serveru (pull přes scope token, public endpoint).

ACR setup viz [`infra/F0-09-acr.bicep`](../infra/F0-09-acr.bicep) +
[`infra/F0-09-acr-setup.sh`](../infra/F0-09-acr-setup.sh).

---

## 3. Globální proměnné

Definovány v `env:` na úrovni workflow — platí pro všechny jobs:

| Proměnná | Hodnota | Popis |
|---|---|---|
| `ACR_NAME` | `crffpartnerbridge` | Název Azure Container Registry (viz [`infra/F0-09-acr.bicep`](../infra/F0-09-acr.bicep)) |
| `ACR_LOGIN_SERVER` | `crffpartnerbridge.azurecr.io` | Login server ACR — musí odpovídat `image:` v `docker-compose.yml` |
| `IMAGE_REPO` | `ff-partner-bridge` | Název repository v rámci ACR |
| `DOTNET_VERSION` | `9.0.x` | SDK verze instalovaná přes `actions/setup-dotnet`. Nutná kvůli `.slnx` solution formátu. TFM projektů je `net8.0`. |

---

## 4. Stage 1 — build-and-test

**Job:** `build-and-test`
**Runner:** `ubuntu-22.04`
**Spouštění:** PR event, push na `main`, push na `develop`

### Co dělá

1. `actions/checkout@v4` — checkout repa
2. `actions/setup-dotnet@v4` — instalace .NET SDK 9.0.x
3. `actions/cache@v4` — NuGet cache (klíč podle hashe `**/*.csproj`)
4. `dotnet restore` → `dotnet build --configuration Release` → `dotnet test --logger trx`
5. `actions/upload-artifact@v4` — TRX výsledky (i při selhání testů)

### NuGet cache

```yaml
- uses: actions/cache@v4
  with:
    path: ~/.nuget/packages
    key: ${{ runner.os }}-nuget-${{ hashFiles('**/*.csproj') }}
    restore-keys: |
      ${{ runner.os }}-nuget-
```

Cache klíč se mění pouze při změně některého `.csproj`. Restore klíč
zajišťuje fallback na poslední dostupnou cache pro stejný OS.

### Artefakty

```yaml
- uses: actions/upload-artifact@v4
  if: always()
  with:
    name: test-results-${{ github.run_number }}
    path: ${{ runner.temp }}/TestResults/**/*.trx
    if-no-files-found: warn
```

TRX soubory jsou ke stažení v UI: **Actions → run → Summary → Artifacts**.

> **Poznámka — GitHub Test Reports UI:** GitHub Actions nativně neparsuje
> TRX. Pro vizualizaci pass/fail v PR lze použít akci jako
> [`dorny/test-reporter@v1`](https://github.com/dorny/test-reporter)
> nebo přidat NuGet `JunitXml.TestLogger`. Aktuálně stačí TRX pro audit.

### Pravidla spouštění

| Podmínka | Spuštění |
|---|---|
| Otevření / aktualizace Pull Request | Ano |
| Push na `main` | Ano |
| Push na `develop` | Ano |
| Push na libovolnou jinou větev | Ne |

---

## 5. Stage 2 — docker-push

**Job:** `docker-push`
**Runner:** `ubuntu-22.04` (Docker je v image preinstalován)
**Spouštění:** pouze push na `main` (`if: github.ref == 'refs/heads/main' && github.event_name == 'push'`)
**Auth:** OIDC federated identity → AcrPush role na ACR

### Co dělá

1. Checkout repa
2. `azure/login@v2` — výměna OIDC tokenu za Azure access token (federated identity)
3. `az acr login` — Docker login do `<acr>.azurecr.io` přes Azure access token
4. `docker build` s tagem `${{ github.run_number }}` + labely
5. `docker push`

### OIDC autentizace bez secrets

```yaml
permissions:
  contents: read
  id-token: write   # nutné pro OIDC token request

steps:
  - uses: azure/login@v2
    with:
      client-id: ${{ secrets.AZURE_CLIENT_ID }}
      tenant-id: ${{ secrets.AZURE_TENANT_ID }}
      subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}
```

`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` **nejsou
credentials** — jsou to veřejné identifikátory. Trust se zakládá na
federated credential v Entra ID (subject =
`repo:rcendelin/FF-Partner:ref:refs/heads/main`). GitHub runner si
vyžádá OIDC token od `token.actions.githubusercontent.com`, Entra ID
ho ověří a vydá Azure access token na omezenou dobu (~ 1 hodina).

Setup federated identity viz [`infra/F0-09-github-oidc-setup.sh`](../infra/F0-09-github-oidc-setup.sh).

### Tagování image

```yaml
IMAGE_TAG="${{ github.run_number }}"
IMAGE_FULL="${{ env.ACR_LOGIN_SERVER }}/${{ env.IMAGE_REPO }}:${IMAGE_TAG}"
```

`github.run_number` je sekvenční číslo workflow runu v rámci repa
(1, 2, 3, ...). Předvídatelné a čitelné — na rozdíl od `github.run_id`,
který je globálně unikátní a má velká čísla. Výsledný tag:

```
crffpartnerbridge.azurecr.io/ff-partner-bridge:42
```

Tag se předává do `deploy` jobu přes `outputs` mechanismus (image-tag).

### Image labely

```bash
--label "git-commit=${{ github.sha }}"
--label "build-id=${{ github.run_number }}"
```

Umožňují zpětnou dohledatelnost: ze spuštěného containeru lze zjistit
přesný commit i číslo runu.

---

## 6. Stage 3 — deploy

**Job:** `deploy`
**Runner:** `ubuntu-22.04`
**Spouštění:** push na `main`, **vyžaduje schválení v Environment `production`**

### Co dělá

1. Setup SSH — zapíše `DEPLOY_SSH_KEY` a `DEPLOY_KNOWN_HOSTS` do `~/.ssh/`
2. Validuje `DEPLOY_HOST` (povoleny pouze `[a-zA-Z0-9._-]`) — ochrana před command injection
3. Přes SSH na deploy serveru:
   - `docker pull` nové verze image z registry
   - `IMAGE_TAG=<tag> docker compose up -d --no-build` s explicitním tagem
4. Health check — 10 pokusů s 5sekundovým intervalem (50 s celkem)
5. Cleanup SSH klíče (`if: always()`)

### Health check

```bash
for i in $(seq 1 10); do
  ssh deploy@$DEPLOY_HOST \
    "curl --silent --fail 'http://localhost:8080/health' | grep -q 'healthy'"
  sleep 5
done
```

Health check probíhá **přes SSH na deploy serveru**, protože Bridge
naslouchá pouze na interní síti XTuning (`172.24.0.1:8080`) — není
přístupný přímo z GitHub runneru. Endpoint `/health` neobsahuje
autentizaci a nevrací citlivá data.

### Environment approval

```yaml
deploy:
  environment: production
```

Když je `production` Environment v repo settings nakonfigurovaný
s **required reviewers**, deploy job čeká na schválení od oprávněné
osoby. Po schválení se rozběhne automaticky. Audit log je vidět
v Environment historii.

### Bezpečnost SSH klíče

```yaml
- name: Cleanup SSH keys
  if: always()
  run: rm -f ~/.ssh/deploy_key ~/.ssh/known_hosts
```

Klíč se vždy smaže, i pokud předchozí kroky selžou (`if: always()`).
Tím se zabraňuje zůstání klíče v souborovém systému runneru při
případném selhání nebo timeoutu.

---

## 7. Nastavení Secrets v GitHub

Přejdi do **GitHub → repo → Settings → Secrets and variables → Actions → New repository secret**:

| Secret | Popis | Zdroj hodnoty |
|---|---|---|
| `AZURE_CLIENT_ID` | App Registration appId (federated identity) | výstup `infra/F0-09-github-oidc-setup.sh` |
| `AZURE_TENANT_ID` | Entra ID tenant ID | `az account show --query tenantId -o tsv` |
| `AZURE_SUBSCRIPTION_ID` | Subscription ID, kde je ACR | `az account show --query id -o tsv` |
| `DEPLOY_SSH_KEY` | Celý obsah privátního SSH klíče (`-----BEGIN`...`-----END`) | `ssh-keygen -t ed25519` |
| `DEPLOY_KNOWN_HOSTS` | Výstup `ssh-keyscan -H <deploy-hostname>` | viz níže |
| `DEPLOY_HOST` | Hostname nebo IP deploy serveru | — |
| `BRIDGE_HEALTH_URL` | (volitelné) Výchozí: `http://localhost:8080/health` | — |

> **Pozor:** `AZURE_CLIENT_ID` a další Azure ID-čka **nejsou credentials** —
> jsou to veřejné identifikátory. Federated identity v Entra ID
> nahrazuje password/secret. Žádné `AZURE_CLIENT_SECRET` se neukládá.

> Pro extra ochranu lze sensitive secrets (DEPLOY_SSH_KEY) svázat s konkrétním
> Environment (`production`) místo s repem — pak jsou dostupné jen v jobech
> referencujících `environment: production` (tj. `deploy`).

### Setup federated identity (jednorázově)

```bash
export RG="rg-ff-partner-bridge"
export ACR_NAME="crffpartnerbridge"
export GITHUB_REPO="rcendelin/FF-Partner"
bash infra/F0-09-github-oidc-setup.sh
```

Skript:
1. Vytvoří App Registration `sp-github-actions-ff-partner-bridge` v Entra ID
2. Vytvoří Service Principal pro tuto App
3. Nastaví federated credential — důvěra ke konkrétnímu repo + branch
4. Přiřadí roli `AcrPush` scope na konkrétní ACR (ne celá subscription)
5. Vypíše hodnoty pro GitHub repo secrets

Setup ACR samotného: [`infra/F0-09-acr-setup.sh`](../infra/F0-09-acr-setup.sh).

### Jak získat DEPLOY_KNOWN_HOSTS

```bash
ssh-keyscan -H <deploy-hostname-nebo-IP>
```

Výstup zkopíruj celý jako hodnotu secretu `DEPLOY_KNOWN_HOSTS`.

### Jak vygenerovat SSH klíčový pár pro deploy

```bash
# Vygeneruj klíč bez hesla (CI/CD nemůže zadávat hesla interaktivně)
ssh-keygen -t ed25519 -C "github-actions-ff-partner-bridge" -N "" -f ./deploy_key

# Privátní klíč → secret DEPLOY_SSH_KEY
cat ./deploy_key

# Veřejný klíč → ~deploy/.ssh/authorized_keys na deploy serveru
cat ./deploy_key.pub
```

Na deploy serveru:

```bash
echo "<obsah deploy_key.pub>" >> /home/deploy/.ssh/authorized_keys
chmod 600 /home/deploy/.ssh/authorized_keys
```

### Pull credentials pro deploy server (ACR token)

Deploy server nemá OIDC — potřebuje long-lived credentials pro `docker pull`.

Generování (na trusted stroji s `az login`):

```bash
export RG="rg-ff-partner-bridge"
export ACR_NAME="crffpartnerbridge"
bash infra/F0-09-deploy-token-setup.sh
# → vytvoří `acr-token-deploy-server-pull.txt` (chmod 600)
```

Skript:
1. Vytvoří **scope map** omezenou na repo `ff-partner-bridge` (read-only)
2. Vytvoří **token** s touto scope mapou
3. Vygeneruje password1 s expirací 1 rok (rotace povinná)

Na deploy serveru:

```bash
# Skopírovat password z acr-token-deploy-server-pull.txt (scp / KeePass)
cat <password-soubor> | docker login crffpartnerbridge.azurecr.io \
  --username 'deploy-server-pull' --password-stdin

# Smazat password soubor
shred -u <password-soubor>
```

> **Pozn.:** ACR scope tokens vyžadují **Premium tier**. Při Basic/Standard
> tieru skript nabídne alternativu se Service Principal + AcrPull rolí
> (long-lived secret místo tokenu — viz výstup skriptu).

---

## 8. Nastavení deploy serveru

### Uživatel deploy

```bash
useradd --system --shell /bin/bash --create-home deploy
usermod -aG docker deploy
```

### Adresářová struktura

```
/opt/ff-partner-bridge/
├── docker-compose.yml     # produkční compose konfigurace
└── secrets/               # Docker secrets (viz docker-compose.yml)
    ├── azure_sql_conn.txt
    ├── partner_cz_conn.txt
    ├── ...
    └── bridge_admin_api_key.txt
```

Inicializace `secrets/*.txt` viz [`infra/F0-06-docker-secrets-init.sh`](../infra/F0-06-docker-secrets-init.sh).

### Omezení oprávnění deploy uživatele (volitelně)

Pokud nechceš dávat deploy uživateli plný přístup k Dockeru:

```
# /etc/sudoers.d/deploy-bridge
deploy ALL=(ALL) NOPASSWD: /usr/bin/docker pull crffpartnerbridge.azurecr.io/ff-partner-bridge:*, \
                            /usr/bin/docker compose -f /opt/ff-partner-bridge/docker-compose.yml up *
```

---

## 9. Nastavení Environment 'production'

Pro `deploy` job, který vyžaduje schválení:

1. Přejdi do **GitHub → repo → Settings → Environments**
2. Klikni na **New environment**, název: `production`
3. V **Deployment protection rules** zaškrtni:
   - **Required reviewers** — vyber konkrétní osoby/teamy, které schválí deploy
   - (volitelně) **Wait timer** — povinná čekací doba před spuštěním
4. V **Environment secrets** lze duplikovat sensitive secrety
   (`DEPLOY_SSH_KEY` ad.) tak, aby byly dostupné **jen pro tento environment**.
5. Ulož

Po nastavení deploy job čeká na schválení v GitHub UI:
**Actions → Workflow run → Review deployments**.

---

## 10. Kdy se pipeline spouští

```
Událost                          | build-and-test | docker-push | deploy
─────────────────────────────────┼────────────────┼─────────────┼──────────────────
Push na main                     | Ano            | Ano (auto)  | Ano (env approval)
Push na develop                  | Ano            | Ne          | Ne
Push na jinou větev              | Ne             | Ne          | Ne
Otevření Pull Request            | Ano            | Ne          | Ne
Aktualizace Pull Request         | Ano            | Ne          | Ne
Ruční trigger (workflow_dispatch)| —              | —           | —  (není nakonfigurováno)
```

> **Tip:** Pro on-demand redeploy bez nového commitu lze přidat
> `workflow_dispatch` trigger a v `if:` pravidle ho akceptovat —
> aktuálně to není potřeba (rollback se dělá přímo na deploy serveru,
> viz [Ruční nasazení](#12-ruční-nasazení-bez-pipeline)).

---

## 11. Správa tagů Docker image

### Formát tagu

Každý build na `main` vytvoří image s tagem `${{ github.run_number }}`:

```
crffpartnerbridge.azurecr.io/ff-partner-bridge:1
crffpartnerbridge.azurecr.io/ff-partner-bridge:2
crffpartnerbridge.azurecr.io/ff-partner-bridge:42
```

Deploy krok vždy nasadí konkrétní tag — nikdy `latest`. Deterministické
chování + možnost rollbacku na libovolnou předchozí verzi.

### Rollback na předchozí verzi

Bez nového commitu, přímo na deploy serveru:

```bash
cd /opt/ff-partner-bridge
IMAGE_TAG=<starší-run-number> docker compose up -d --no-build ff-partner-bridge
```

Číslo runu najdeš v GitHub UI v historii Actions, nebo v Docker image labelu:

```bash
docker inspect crffpartnerbridge.azurecr.io/ff-partner-bridge:<tag> \
  --format '{{index .Config.Labels "build-id"}}'
```

### Listing dostupných tagů

```bash
az acr repository show-tags --name crffpartnerbridge --repository ff-partner-bridge --orderby time_desc
```

### Čištění starých image

Na deploy serveru se starší tagy nečistí automaticky. Doporučení:

```bash
# Smazat image starší než 30 dní
docker image prune --filter "until=720h" -f
```

Lze přidat jako cron job (`/etc/cron.weekly/`).

---

## 12. Ruční nasazení bez pipeline

V případě výpadku GitHub Actions nebo nutnosti hotfix deploye:

```bash
ssh deploy@<deploy-host>
cd /opt/ff-partner-bridge

# Login do ACR (pokud ještě není uložen v ~/.docker/config.json — viz sekce 7)
echo "<token-password>" | docker login crffpartnerbridge.azurecr.io \
  --username 'deploy-server-pull' --password-stdin

# Stáhnout konkrétní verzi
docker pull crffpartnerbridge.azurecr.io/ff-partner-bridge:<tag>

# Nasadit
IMAGE_TAG=<tag> docker compose up -d --no-build ff-partner-bridge

# Ověřit zdraví
curl http://localhost:8080/health
```

---

## 13. Řešení problémů

### build-and-test selže na `dotnet restore`

**Příznaky:** `Unable to load the service index for source https://api.nuget.org/v3/index.json`

**Řešení:** Přechodný výpadek NuGet feeds, restartuj run. Pokud trvá,
přidej privátní NuGet feed v `nuget.config` v kořeni repa.

---

### docker-push selže na `Azure login` (OIDC)

**Příznaky:** `AADSTS70021: No matching federated identity record found`
nebo `Error: Login failed with Error: Az CLI Login failed.`

**Příčiny a řešení:**
- Federated credential subject neodpovídá události → ověř, že subject je
  `repo:<owner>/<repo>:ref:refs/heads/main` a workflow běží z `main` po pushi.
  Pro PR by musel existovat samostatný credential s `pull_request` subject.
- Špatné `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` → ověř hodnoty z výstupu
  `infra/F0-09-github-oidc-setup.sh`.
- Workflow nemá `permissions: id-token: write` → bez něj GitHub nevydá OIDC token.

### docker-push selže na `az acr login`

**Příznaky:** `Forbidden` nebo `unauthorized: authentication required`

**Příčiny a řešení:**
- Role `AcrPush` není přiřazena na ACR scope → re-run
  `infra/F0-09-github-oidc-setup.sh` nebo manuálně:
  ```bash
  az role assignment create \
    --assignee <AZURE_CLIENT_ID> \
    --role AcrPush \
    --scope $(az acr show --name <acr> --resource-group <rg> --query id -o tsv)
  ```
- Propagace role assignmentu trvá až 1–2 minuty po vytvoření — re-run workflow.

---

### deploy selže na SSH

**Příznaky:** `Host key verification failed` nebo `Permission denied (publickey)`

**Řešení (Host key):**
```bash
ssh-keyscan -H <deploy-host>
# Výstup ulož do GitHub secret DEPLOY_KNOWN_HOSTS
```

**Řešení (Permission denied):**
```bash
# Na deploy serveru ověř authorized_keys
cat /home/deploy/.ssh/authorized_keys
# Musí obsahovat odpovídající veřejný klíč k DEPLOY_SSH_KEY
```

---

### deploy selže na síťové dostupnosti deploy serveru

**Příznaky:** `ssh: connect to host <DEPLOY_HOST> port 22: Connection timed out`

**Řešení:** GitHub-hosted runnery běží v Azure z dynamických IP rozsahů.
Pokud deploy server není veřejně dostupný:
- Nasaď **self-hosted runner** uvnitř firmy a uprav `runs-on:` na jeho label
- Nebo whitelisti GitHub Actions IP rozsahy (`https://api.github.com/meta`)
  — průběžně se mění, vyžaduje automatizovanou aktualizaci firewallu

---

### Health check selže po deployi

**Příznaky:** `Bridge health check selhal po 50 sekundách`

**Příčiny a řešení:**

1. **Bridge se nestartuje** — ověř logy:
   ```bash
   docker logs ff-partner-bridge --tail 50
   ```

2. **Chybí secrets** — ověř existenci souborů v `./secrets/`:
   ```bash
   ls -la /opt/ff-partner-bridge/secrets/
   ```

3. **Špatný BRIDGE_HEALTH_URL** — výchozí URL je `http://localhost:8080/health`.
   Pokud Bridge naslouchá jinde, nastav secret `BRIDGE_HEALTH_URL`.

4. **Pomalý start** — Bridge potřebuje > 50 s pro inicializaci
   (typicky při studených connection poolech). Lze upravit počet pokusů
   v workflow nebo `start_period` v Dockerfile `HEALTHCHECK`.

---

### deploy nečeká na approval

**Příznaky:** Deploy se rozjel automaticky bez schválení.

**Řešení:** V repo settings → Environments → `production` zkontroluj,
že je zapnutý **Required reviewers**. Bez něj `environment: production`
není gating.
