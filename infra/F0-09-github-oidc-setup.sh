#!/usr/bin/env bash
# F0-09 — GitHub Actions OIDC federated identity pro push do ACR
#
# Co skript dělá:
#   1. Vytvoří App Registration v Entra ID (`sp-github-actions-ff-partner-bridge`)
#   2. Vytvoří Service Principal pro tuto App Registration
#   3. Nastaví federated credential — důvěra k GitHub Actions runneru s repo + branch matchingem
#   4. Přiřadí AcrPush role na ACR (omezeno na konkrétní registry, ne celá subscription)
#   5. Vypíše hodnoty pro GitHub repo secrets
#
# Auth flow:
#   GitHub Actions runner → OIDC token (issuer: token.actions.githubusercontent.com,
#   subject: repo:<owner>/<repo>:ref:refs/heads/main)
#   → Entra ID validuje issuer + subject podle federated credential
#   → vydá Azure access token pro App Registration
#   → `az acr login` přihlásí do ACR pomocí tohoto tokenu
#
# Žádný dlouhodobý secret se neukládá v GitHubu — pouze CLIENT_ID, TENANT_ID,
# SUBSCRIPTION_ID (což jsou veřejné identifikátory, ne credentials).
#
# Prerekvizity:
#   - F0-09-acr-setup.sh už proběhl
#   - Azure CLI >= 2.47, az login
#   - Oprávnění:
#     * Application Administrator (nebo vyšší) v Entra ID — pro App Registration
#     * Owner / User Access Administrator na ACR — pro role assignment
#
# Použití:
#   export RG="rg-ff-partner-bridge"
#   export ACR_NAME="crffpartnerbridge"
#   export GITHUB_REPO="rcendelin/FF-Partner"   # vlastník/repo
#   export FEDERATED_BRANCH="main"               # default main
#   bash infra/F0-09-github-oidc-setup.sh

set -euo pipefail

# ─── konfigurace ──────────────────────────────────────────────────────────────

RG="${RG:?Nastavte proměnnou RG, např. export RG=rg-ff-partner-bridge}"
ACR_NAME="${ACR_NAME:?Nastavte proměnnou ACR_NAME, např. export ACR_NAME=crffpartnerbridge}"
GITHUB_REPO="${GITHUB_REPO:?Nastavte proměnnou GITHUB_REPO ve formátu owner/repo, např. export GITHUB_REPO=rcendelin/FF-Partner}"
FEDERATED_BRANCH="${FEDERATED_BRANCH:-main}"

APP_DISPLAY_NAME="sp-github-actions-ff-partner-bridge"
FED_CRED_NAME="github-${FEDERATED_BRANCH}-branch"

# ─── pomocné funkce ───────────────────────────────────────────────────────────

log()  { echo "[$(date '+%H:%M:%S')] $*"; }
ok()   { echo "[$(date '+%H:%M:%S')] ✓ $*"; }
warn() { echo "[$(date '+%H:%M:%S')] ⚠ $*"; }
fail() { echo "[$(date '+%H:%M:%S')] ✗ $*" >&2; exit 1; }

# ─── krok 1: prerequisity ─────────────────────────────────────────────────────

log "Ověřuji prerequisity..."

if ! command -v az &>/dev/null; then
  fail "Azure CLI není nainstalováno."
fi
if ! az account show &>/dev/null; then
  fail "Nejste přihlášeni do Azure CLI. Spusťte: az login"
fi

# Validace formátu GITHUB_REPO
if [[ ! "$GITHUB_REPO" =~ ^[a-zA-Z0-9._-]+/[a-zA-Z0-9._-]+$ ]]; then
  fail "GITHUB_REPO '$GITHUB_REPO' nemá tvar owner/repo (např. rcendelin/FF-Partner)."
fi

# Validace FEDERATED_BRANCH (zabraňuje injection v subject claim)
if [[ ! "$FEDERATED_BRANCH" =~ ^[a-zA-Z0-9._/-]+$ ]]; then
  fail "FEDERATED_BRANCH '$FEDERATED_BRANCH' obsahuje nepovolené znaky."
fi

ACR_RESOURCE_ID=$(az acr show --name "$ACR_NAME" --resource-group "$RG" --query "id" -o tsv 2>/dev/null) \
  || fail "ACR '$ACR_NAME' nenalezeno v RG '$RG'. Spusťte nejdřív F0-09-acr-setup.sh."
ok "ACR '$ACR_NAME' nalezeno."

TENANT_ID=$(az account show --query "tenantId" -o tsv)
SUBSCRIPTION_ID=$(az account show --query "id" -o tsv)

# ─── krok 2: App Registration (idempotentní) ─────────────────────────────────

log "Hledám existující App Registration '$APP_DISPLAY_NAME'..."
APP_ID=$(az ad app list --display-name "$APP_DISPLAY_NAME" --query "[0].appId" -o tsv 2>/dev/null || true)

if [[ -z "$APP_ID" ]]; then
  log "App Registration neexistuje — vytvářím..."
  APP_ID=$(az ad app create --display-name "$APP_DISPLAY_NAME" --query "appId" -o tsv)
  ok "App Registration vytvořena (appId: $APP_ID)."
else
  ok "App Registration už existuje (appId: $APP_ID) — používám stávající."
fi

# Object ID pro federated credential
APP_OBJECT_ID=$(az ad app show --id "$APP_ID" --query "id" -o tsv)

# ─── krok 3: Service Principal (idempotentní) ────────────────────────────────

log "Ověřuji Service Principal pro App..."
SP_OBJECT_ID=$(az ad sp list --filter "appId eq '$APP_ID'" --query "[0].id" -o tsv 2>/dev/null || true)

if [[ -z "$SP_OBJECT_ID" ]]; then
  log "Service Principal neexistuje — vytvářím..."
  SP_OBJECT_ID=$(az ad sp create --id "$APP_ID" --query "id" -o tsv)
  ok "Service Principal vytvořen (objectId: $SP_OBJECT_ID)."
else
  ok "Service Principal už existuje (objectId: $SP_OBJECT_ID)."
fi

# ─── krok 4: Federated Credential ─────────────────────────────────────────────

SUBJECT="repo:${GITHUB_REPO}:ref:refs/heads/${FEDERATED_BRANCH}"
log "Nastavuji federated credential — subject: $SUBJECT"

# Smazat existující credential se stejným názvem (umožní re-run s jinou branch)
EXISTING_FED_ID=$(az ad app federated-credential list \
  --id "$APP_OBJECT_ID" \
  --query "[?name=='${FED_CRED_NAME}'].id" -o tsv 2>/dev/null || true)

if [[ -n "$EXISTING_FED_ID" ]]; then
  warn "Federated credential '${FED_CRED_NAME}' už existuje — smažu a vytvořím znovu."
  az ad app federated-credential delete \
    --id "$APP_OBJECT_ID" \
    --federated-credential-id "$EXISTING_FED_ID" \
    --output none
fi

# Vytvořit federated credential — předat JSON přes process substitution
az ad app federated-credential create \
  --id "$APP_OBJECT_ID" \
  --parameters <(cat <<EOF
{
  "name": "${FED_CRED_NAME}",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "${SUBJECT}",
  "description": "GitHub Actions push z větve ${FEDERATED_BRANCH} repa ${GITHUB_REPO}",
  "audiences": ["api://AzureADTokenExchange"]
}
EOF
) \
  --output none

ok "Federated credential '${FED_CRED_NAME}' nastaven."

# ─── krok 5: Role Assignment AcrPush ─────────────────────────────────────────

# Role ID: 8311e382-0749-4cb8-b61a-304f252e45ec (AcrPush)
ACR_PUSH_ROLE_ID="8311e382-0749-4cb8-b61a-304f252e45ec"

log "Přiřazuji roli AcrPush na ACR scope..."

# Idempotentní — zkusit najít existující assignment
EXISTING_ASSIGNMENT=$(az role assignment list \
  --assignee "$SP_OBJECT_ID" \
  --scope "$ACR_RESOURCE_ID" \
  --role "AcrPush" \
  --query "[0].id" -o tsv 2>/dev/null || true)

if [[ -n "$EXISTING_ASSIGNMENT" ]]; then
  ok "Role AcrPush už je přiřazena ($EXISTING_ASSIGNMENT)."
else
  az role assignment create \
    --assignee-object-id "$SP_OBJECT_ID" \
    --assignee-principal-type ServicePrincipal \
    --role "AcrPush" \
    --scope "$ACR_RESOURCE_ID" \
    --output none
  ok "Role AcrPush přiřazena na scope $ACR_RESOURCE_ID."
fi

# Propagace role assignmentu může chvíli trvat (typicky < 60 s)
log "Čekám 30 s na propagaci role assignmentu..."
sleep 30

# ─── krok 6: výstup pro GitHub secrets ────────────────────────────────────────

echo ""
echo "═══════════════════════════════════════════════════════════════════"
echo " GitHub OIDC nastaven — přidat secrets do GitHub repa"
echo "═══════════════════════════════════════════════════════════════════"
echo ""
echo " GitHub repo: https://github.com/${GITHUB_REPO}"
echo " Settings → Secrets and variables → Actions → New repository secret:"
echo ""
echo "   AZURE_CLIENT_ID:       ${APP_ID}"
echo "   AZURE_TENANT_ID:       ${TENANT_ID}"
echo "   AZURE_SUBSCRIPTION_ID: ${SUBSCRIPTION_ID}"
echo ""
echo " (Tyto hodnoty NEJSOU credentials — jsou to veřejné identifikátory."
echo "  Federated identity nahrazuje password/secret.)"
echo ""
echo " Workflow musí mít:"
echo "   permissions:"
echo "     id-token: write   # pro OIDC token request"
echo "     contents: read"
echo ""
echo " Federated credential trust:"
echo "   issuer:  https://token.actions.githubusercontent.com"
echo "   subject: ${SUBJECT}"
echo ""
echo " Pro další větve / PR build přidat další federated credential:"
echo "   FEDERATED_BRANCH=develop bash infra/F0-09-github-oidc-setup.sh"
echo ""
echo "═══════════════════════════════════════════════════════════════════"

ok "F0-09 OIDC hotovo. Push z GitHub Actions na branch '${FEDERATED_BRANCH}' může nyní pushnout do ACR."
