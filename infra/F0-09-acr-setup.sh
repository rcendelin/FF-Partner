#!/usr/bin/env bash
# F0-09 — Nasazení Azure Container Registry pro FF-Partner Bridge
#
# Co skript dělá:
#   1. Ověří prerequisity (az CLI, přihlášení)
#   2. Validuje ACR_NAME (5-50 znaků, alfanumerické, globálně unikátní)
#   3. Nasadí Bicep šablonu (ACR Standard tier + retention policy)
#   4. Vypíše login server pro další skripty a workflow
#
# Prerekvizity:
#   - Azure CLI >= 2.47
#   - Přihlášení: az login
#   - Oprávnění: Contributor nebo Owner na resource group
#
# Použití:
#   export RG="rg-ff-partner-bridge"
#   export ACR_NAME="crffpartnerbridge"
#   export ACR_SKU="Standard"   # volitelné, default Standard
#   bash infra/F0-09-acr-setup.sh
#
# Po nasazení:
#   1. F0-09-github-oidc-setup.sh — federated identity pro GitHub Actions push
#   2. F0-09-deploy-token-setup.sh — pull token pro deploy server

set -euo pipefail

# ─── konfigurace ──────────────────────────────────────────────────────────────

RG="${RG:?Nastavte proměnnou RG (resource group), např. export RG=rg-ff-partner-bridge}"
ACR_NAME="${ACR_NAME:?Nastavte proměnnou ACR_NAME (5-50 znaků, alfanumerické, globálně unikátní), např. export ACR_NAME=crffpartnerbridge}"
ACR_SKU="${ACR_SKU:-Standard}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BICEP_FILE="$SCRIPT_DIR/F0-09-acr.bicep"

# Bezpečný dočasný soubor pro deployment output
DEPLOY_TMP=$(mktemp /tmp/bridge-acr-deploy.XXXXXX.json)
trap 'rm -f "$DEPLOY_TMP"' EXIT

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

SUBSCRIPTION_NAME=$(az account show --query "name" -o tsv)
SUBSCRIPTION_ID=$(az account show --query "id" -o tsv)
log "Přihlášeni k subscription: $SUBSCRIPTION_NAME ($SUBSCRIPTION_ID)"

# Validace ACR_NAME — alfanumerické, 5-50 znaků, BEZ pomlček a teček
if [[ ${#ACR_NAME} -lt 5 || ${#ACR_NAME} -gt 50 ]]; then
  fail "ACR_NAME '$ACR_NAME' má nepovolenou délku (${#ACR_NAME} znaků, povoleno 5-50)."
fi
if [[ ! "$ACR_NAME" =~ ^[a-zA-Z0-9]+$ ]]; then
  fail "ACR_NAME '$ACR_NAME' obsahuje nepovolené znaky — povoleny jsou pouze alfanumerické znaky (BEZ pomlček a teček)."
fi

# Validace SKU
if [[ ! "$ACR_SKU" =~ ^(Basic|Standard|Premium)$ ]]; then
  fail "ACR_SKU '$ACR_SKU' je neplatný. Povoleno: Basic, Standard, Premium."
fi

# Ověřit existenci resource group
if ! az group show --name "$RG" &>/dev/null; then
  fail "Resource group '$RG' neexistuje nebo nemáte oprávnění."
fi
ok "Resource group '$RG' nalezena."

# Ověřit globální unikátnost ACR_NAME
NAME_AVAILABILITY=$(az acr check-name --name "$ACR_NAME" --query "nameAvailable" -o tsv 2>/dev/null || echo "false")
if [[ "$NAME_AVAILABILITY" == "false" ]]; then
  EXISTING_RG=$(az acr show --name "$ACR_NAME" --query "resourceGroup" -o tsv 2>/dev/null || true)
  if [[ "$EXISTING_RG" == "$RG" ]]; then
    warn "ACR '$ACR_NAME' již existuje v resource group '$RG' — Bicep provede idempotentní update."
  elif [[ -n "$EXISTING_RG" ]]; then
    fail "ACR '$ACR_NAME' existuje v jiné resource group '$EXISTING_RG'. Zvolte jiný název."
  else
    fail "ACR název '$ACR_NAME' není globálně unikátní (zabraný jiným tenantem). Zvolte jiný název."
  fi
fi

# ─── krok 2: nasazení Bicep šablony ──────────────────────────────────────────

log "Nasazuji Bicep šablonu: $BICEP_FILE (sku=$ACR_SKU)"

if [[ ! -f "$BICEP_FILE" ]]; then
  fail "Bicep soubor '$BICEP_FILE' nenalezen."
fi

DEPLOYMENT_NAME="F0-09-acr-$(date +%Y%m%d-%H%M%S)"

az deployment group create \
  --resource-group "$RG" \
  --name "$DEPLOYMENT_NAME" \
  --template-file "$BICEP_FILE" \
  --parameters acrName="$ACR_NAME" sku="$ACR_SKU" \
  --output json \
  > "$DEPLOY_TMP"

ok "Bicep šablona nasazena (deployment: $DEPLOYMENT_NAME)."

# Načíst výstupy
ACR_LOGIN_SERVER=$(az deployment group show \
  --resource-group "$RG" \
  --name "$DEPLOYMENT_NAME" \
  --query "properties.outputs.acrLoginServer.value" -o tsv)

ACR_RESOURCE_ID=$(az deployment group show \
  --resource-group "$RG" \
  --name "$DEPLOYMENT_NAME" \
  --query "properties.outputs.acrResourceId.value" -o tsv)

ok "ACR Login Server: $ACR_LOGIN_SERVER"
ok "ACR Resource ID:  $ACR_RESOURCE_ID"

# ─── krok 3: validace ─────────────────────────────────────────────────────────

log "Ověřuji ACR provisioning state..."

ACR_STATE=$(az acr show --name "$ACR_NAME" --resource-group "$RG" --query "provisioningState" -o tsv)
if [[ "$ACR_STATE" != "Succeeded" ]]; then
  fail "ACR '$ACR_NAME' není v stavu Succeeded (stav: $ACR_STATE)."
fi
ok "ACR '$ACR_NAME' je provisioned."

# ─── krok 4: výstup pro další skripty a workflow ──────────────────────────────

echo ""
echo "═══════════════════════════════════════════════════════════════════"
echo " ACR vytvořen — další kroky"
echo "═══════════════════════════════════════════════════════════════════"
echo ""
echo " 1. GitHub Actions push (federated identity, žádný secret password):"
echo "      export RG='${RG}'"
echo "      export ACR_NAME='${ACR_NAME}'"
echo "      export GITHUB_REPO='rcendelin/FF-Partner'"
echo "      bash infra/F0-09-github-oidc-setup.sh"
echo ""
echo " 2. Token pro pull z deploy serveru:"
echo "      export RG='${RG}'"
echo "      export ACR_NAME='${ACR_NAME}'"
echo "      bash infra/F0-09-deploy-token-setup.sh"
echo ""
echo " 3. Aktualizovat referenci v .github/workflows/bridge.yml:"
echo "      env:"
echo "        ACR_NAME: ${ACR_NAME}"
echo "        ACR_LOGIN_SERVER: ${ACR_LOGIN_SERVER}"
echo ""
echo " 4. Aktualizovat docker-compose.yml na deploy serveru:"
echo "      image: ${ACR_LOGIN_SERVER}/ff-partner-bridge:\${IMAGE_TAG:-latest}"
echo ""
echo "═══════════════════════════════════════════════════════════════════"
echo ""

ok "F0-09 ACR hotovo. Login server: $ACR_LOGIN_SERVER"
