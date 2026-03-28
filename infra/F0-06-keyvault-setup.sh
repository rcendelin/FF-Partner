#!/usr/bin/env bash
# F0-06 — Nasazení Azure Key Vault a Managed Identity pro FF-Partner Bridge
#
# Co skript dělá:
#   1. Ověří prerequisity (az CLI, přihlášení)
#   2. Nasadí Bicep šablonu (Key Vault + Managed Identity + RBAC)
#   3. Uloží Application Insights connection string do Key Vault
#   4. Uloží Service Bus connection string do Key Vault (volitelné, pokud F0-05 je hotovo)
#   5. Vypíše hodnoty pro docker-compose.yml (AZURE_KEY_VAULT_URI, AZURE_CLIENT_ID)
#
# Prerekvizity:
#   - Azure CLI >= 2.47
#   - Přihlášení: az login
#   - Oprávnění: Key Vault Contributor nebo Owner na resource group
#   - Volitelně: Application Insights resource a jeho connection string
#   - Volitelně: Service Bus namespace z F0-05
#
# Použití:
#   export RG="rg-xtuning-prod"
#   export KV_NAME="kv-xtuning-prod"
#   bash infra/F0-06-keyvault-setup.sh
#
# Volitelné:
#   export AI_CONN="InstrumentationKey=...;IngestionEndpoint=..."   # Application Insights
#   export SB_NS="sb-fieldforce-prod"                                # Service Bus namespace (F0-05)
#
# POZOR: Nezapisujte tajemství přímo na příkazovou řádku — předávejte přes proměnné prostředí.

set -euo pipefail

# ─── konfigurace ──────────────────────────────────────────────────────────────

RG="${RG:?Nastavte proměnnou RG (resource group), např. export RG=rg-xtuning-prod}"
KV_NAME="${KV_NAME:?Nastavte proměnnou KV_NAME, např. export KV_NAME=kv-xtuning-prod}"

# Volitelné vstupy — pokud nejsou nastaveny, tento krok se přeskočí
AI_CONN="${AI_CONN:-}"         # Application Insights connection string
SB_NS="${SB_NS:-}"             # Service Bus namespace pro automatické načtení conn stringu

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BICEP_FILE="$SCRIPT_DIR/F0-06-keyvault.bicep"

# Bezpečný dočasný soubor pro deployment output
DEPLOY_TMP=$(mktemp /tmp/bridge-kv-deploy.XXXXXX.json)
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

SUBSCRIPTION=$(az account show --query "name" -o tsv)
log "Přihlášeni k subscription: $SUBSCRIPTION"

# Validace formátu KV_NAME — max 24 znaků, pouze alfanumerické a pomlčky (R2#5)
if [[ ${#KV_NAME} -gt 24 ]]; then
  fail "KV_NAME '$KV_NAME' je příliš dlouhý (${#KV_NAME} znaků, max 24)."
fi
if [[ ! "$KV_NAME" =~ ^[a-zA-Z0-9-]+$ ]]; then
  fail "KV_NAME '$KV_NAME' obsahuje nepovolené znaky — povoleny jsou pouze alfanumerické znaky a pomlčky."
fi

# Ověřit existenci resource group
if ! az group show --name "$RG" &>/dev/null; then
  fail "Resource group '$RG' neexistuje nebo nemáte oprávnění."
fi
ok "Resource group '$RG' nalezena."

# Kontrola soft-deleted KV — blokuje vytvoření nového KV se stejným názvem (R2#1)
DELETED_KV=$(az keyvault list-deleted --query "[?name=='${KV_NAME}'].name" -o tsv 2>/dev/null || true)
if [[ -n "$DELETED_KV" ]]; then
  warn "Key Vault '$KV_NAME' existuje v soft-deleted stavu a blokuje nasazení."
  warn "Obnovit: az keyvault recover --name \"${KV_NAME}\""
  warn "Nebo trvale smazat: az keyvault purge --name \"${KV_NAME}\""
  fail "Nelze pokračovat — soft-deleted Key Vault blokuje nasazení."
fi

# Ověřit unikátnost KV name — pokud existuje, jen upozornit (R2#6)
KV_ACTIVE_JSON=$(az keyvault show --name "$KV_NAME" --output json 2>/dev/null || true)
if [[ -n "$KV_ACTIVE_JSON" ]]; then
  KV_EXISTING_RG=$(az keyvault show --name "$KV_NAME" --query "resourceGroup" -o tsv 2>/dev/null || true)
  if [[ "$KV_EXISTING_RG" == "$RG" ]]; then
    warn "Key Vault '$KV_NAME' již existuje v resource group '$RG' — Bicep provede idempotentní update."
  elif [[ -n "$KV_EXISTING_RG" ]]; then
    fail "Key Vault '$KV_NAME' existuje v jiné resource group '$KV_EXISTING_RG'. Zvolte jiný název."
  else
    warn "Key Vault '$KV_NAME' byl nalezen, ale bez dostatečného oprávnění ke zjištění RG. Pokračuji."
  fi
fi

# ─── krok 2: nasazení Bicep šablony ──────────────────────────────────────────

log "Nasazuji Bicep šablonu: $BICEP_FILE"

if [[ ! -f "$BICEP_FILE" ]]; then
  fail "Bicep soubor '$BICEP_FILE' nenalezen."
fi

DEPLOYMENT_NAME="F0-06-keyvault-$(date +%Y%m%d-%H%M%S)"

az deployment group create \
  --resource-group "$RG" \
  --name "$DEPLOYMENT_NAME" \
  --template-file "$BICEP_FILE" \
  --parameters keyVaultName="$KV_NAME" \
  --output json \
  > "$DEPLOY_TMP"

ok "Bicep šablona nasazena (deployment: $DEPLOYMENT_NAME)."

# Načíst výstupy z deployment
KV_URI=$(az deployment group show \
  --resource-group "$RG" \
  --name "$DEPLOYMENT_NAME" \
  --query "properties.outputs.keyVaultUri.value" -o tsv)

MI_CLIENT_ID=$(az deployment group show \
  --resource-group "$RG" \
  --name "$DEPLOYMENT_NAME" \
  --query "properties.outputs.managedIdentityClientId.value" -o tsv)

MI_RESOURCE_ID=$(az deployment group show \
  --resource-group "$RG" \
  --name "$DEPLOYMENT_NAME" \
  --query "properties.outputs.managedIdentityResourceId.value" -o tsv)

ok "Key Vault URI: $KV_URI"
ok "Managed Identity Client ID: $MI_CLIENT_ID"

# ─── krok 3: uložení Application Insights connection string ──────────────────

if [[ -n "$AI_CONN" ]]; then
  log "Ukládám Application Insights connection string do Key Vault..."
  # Předat hodnotu přes process substitution — nezobrazí se v argumentech procesu (M-2)
  az keyvault secret set \
    --vault-name "$KV_NAME" \
    --name "appinsights-connection-string" \
    --file <(printf '%s' "$AI_CONN") \
    --output none
  ok "Secret 'appinsights-connection-string' uložen."
  # Smazat hodnotu z paměti
  unset AI_CONN
else
  warn "AI_CONN není nastavena — Application Insights connection string nebyl uložen."
  warn "Nastavte: export AI_CONN='InstrumentationKey=...;IngestionEndpoint=...'"
  warn "Pak spusťte: az keyvault secret set --vault-name \"${KV_NAME}\" --name appinsights-connection-string --value \"\$AI_CONN\""
fi

# ─── krok 4: uložení Service Bus connection string (z F0-05) ─────────────────

if [[ -n "$SB_NS" ]]; then
  log "Načítám Service Bus connection string z namespace '$SB_NS'..."
  # Ověřit existenci authorization rule bridge-send-listen (vytvořena v F0-05)
  if az servicebus namespace authorization-rule show \
      --resource-group "$RG" \
      --namespace-name "$SB_NS" \
      --name "bridge-send-listen" &>/dev/null; then
    # Načíst connection string — hodnota nesmí jít na stdout (logování)
    SB_CONN=$(az servicebus namespace authorization-rule keys list \
      --resource-group "$RG" \
      --namespace-name "$SB_NS" \
      --name "bridge-send-listen" \
      --query "primaryConnectionString" -o tsv)
    # Předat hodnotu přes process substitution — nezobrazí se v argumentech procesu (M-2)
    az keyvault secret set \
      --vault-name "$KV_NAME" \
      --name "servicebus-connection-string" \
      --file <(printf '%s' "$SB_CONN") \
      --output none
    unset SB_CONN
    ok "Secret 'servicebus-connection-string' uložen z namespace '$SB_NS'."
  else
    warn "Authorization rule 'bridge-send-listen' nenalezena v namespace '$SB_NS'. Spusťte nejdříve F0-05."
  fi
else
  warn "SB_NS není nastavena — Service Bus connection string nebyl uložen do Key Vault."
  warn "Nastavte: export SB_NS=sb-fieldforce-prod && znovu spusťte skript."
fi

# ─── krok 5: validace Key Vault přístupu ─────────────────────────────────────

log "Ověřuji Key Vault přístup..."

# Ověřit, že KV existuje a je přístupný
KV_STATE=$(az keyvault show --name "$KV_NAME" --query "properties.provisioningState" -o tsv)
if [[ "$KV_STATE" != "Succeeded" ]]; then
  fail "Key Vault '$KV_NAME' není v stavu Succeeded (stav: $KV_STATE)."
fi
ok "Key Vault '$KV_NAME' je přístupný."

# Ověřit, že RBAC role assignment existuje
MI_PRINCIPAL_ID=$(az identity show --ids "$MI_RESOURCE_ID" --query "principalId" -o tsv)
ROLE_ASSIGNMENTS=$(az role assignment list \
  --scope "$(az keyvault show --name "$KV_NAME" --query "id" -o tsv)" \
  --assignee "$MI_PRINCIPAL_ID" \
  --query "length(@)" -o tsv)

if [[ "$ROLE_ASSIGNMENTS" -gt 0 ]]; then
  ok "RBAC role assignment pro Managed Identity ověřen ($ROLE_ASSIGNMENTS přiřazení)."
else
  warn "RBAC role assignment pro Managed Identity nebyl nalezen — může trvat několik minut."
fi

# ─── krok 6: výstup pro konfiguraci docker-compose ────────────────────────────

echo ""
echo "═══════════════════════════════════════════════════════════════════"
echo " Hodnoty pro docker-compose.yml / produkční server"
echo "═══════════════════════════════════════════════════════════════════"
echo ""
echo " Přidat do docker-compose.yml sekce 'environment':"
echo "   AZURE_KEY_VAULT_URI=${KV_URI}"
echo "   AZURE_CLIENT_ID=${MI_CLIENT_ID}"
echo ""
echo " Přiřadit Managed Identity k produkčnímu VM/serveru:"
echo "   az vm identity assign \\"
echo "     --resource-group \"${RG}\" \\"
echo "     --name <VM_NAME> \\"
echo "     --identities \"${MI_RESOURCE_ID}\""
echo ""
echo " NEBO pro Docker Compose bez VM (userAssignedMI není možné bez Azure VM/ACI):"
echo "   Použít Service Principal s client secret jako AZURE_CLIENT_ID + AZURE_CLIENT_SECRET"
echo "   (viz F0-06-docker-secrets-init.sh)"
echo ""
echo " Další krok: spustit F0-06-docker-secrets-init.sh na produkčním serveru"
echo "═══════════════════════════════════════════════════════════════════"
echo ""

# ─── souhrn ──────────────────────────────────────────────────────────────────

ok "F0-06 hotovo. Key Vault '$KV_NAME' vytvořen, Managed Identity '$MI_CLIENT_ID' přiřazena."
echo ""
echo "Secrets v Key Vault ke kontrole:"
echo "  az keyvault secret list --vault-name \"${KV_NAME}\" --query \"[].name\" -o tsv"
