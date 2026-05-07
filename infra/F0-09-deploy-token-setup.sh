#!/usr/bin/env bash
# F0-09 — Repository-scoped pull token pro deploy server (XTuning on-premise)
#
# Co skript dělá:
#   1. Vytvoří scope map omezenou na repository `ff-partner-bridge` (read-only)
#   2. Vytvoří ACR token vázaný na tuto scope map
#   3. Vygeneruje password1 (platný 1 rok od dnes)
#   4. Vypíše příkaz pro `docker login` na deploy serveru
#
# Bezpečnostní vlastnosti:
#   - Token nemůže nic pushnout (pouze content/read + metadata/read)
#   - Token nemá přístup k jiným repositories ve stejném ACR
#   - Token expiruje po 1 roce (viz EXPIRY_YEARS) — vynucená rotace
#   - Token name + scope map jsou auditovatelné v Azure Activity Log
#
# Prerekvizity:
#   - F0-09-acr-setup.sh už proběhl
#   - Premium tier ACR pro scope tokens (Standard nepodporuje!) NEBO
#     Standard tier s `adminUserEnabled=false` a Service Principal (alternativa).
#     Skript automaticky detekuje tier a nabídne adekvátní řešení.
#
# Použití:
#   export RG="rg-ff-partner-bridge"
#   export ACR_NAME="crffpartnerbridge"
#   export TOKEN_NAME="deploy-server-pull"   # default
#   export EXPIRY_YEARS=1                     # default 1 rok
#   bash infra/F0-09-deploy-token-setup.sh
#
# Výstup zapisuje password do souboru `acr-token-<TOKEN_NAME>.txt` se
# přístupem 600 — NESMÍ být commitnuto do gitu (.gitignore má secrets/).

set -euo pipefail

# ─── konfigurace ──────────────────────────────────────────────────────────────

RG="${RG:?Nastavte proměnnou RG, např. export RG=rg-ff-partner-bridge}"
ACR_NAME="${ACR_NAME:?Nastavte proměnnou ACR_NAME, např. export ACR_NAME=crffpartnerbridge}"
TOKEN_NAME="${TOKEN_NAME:-deploy-server-pull}"
SCOPE_MAP_NAME="${SCOPE_MAP_NAME:-ff-partner-bridge-pull}"
REPO_NAME="${REPO_NAME:-ff-partner-bridge}"
EXPIRY_YEARS="${EXPIRY_YEARS:-1}"

OUTPUT_FILE="./acr-token-${TOKEN_NAME}.txt"

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

# Validace TOKEN_NAME (zabraňuje injection v az příkazech)
if [[ ! "$TOKEN_NAME" =~ ^[a-zA-Z0-9-]+$ ]]; then
  fail "TOKEN_NAME '$TOKEN_NAME' obsahuje nepovolené znaky (povoleno: alfanumerické a pomlčky)."
fi
if [[ ! "$SCOPE_MAP_NAME" =~ ^[a-zA-Z0-9-]+$ ]]; then
  fail "SCOPE_MAP_NAME '$SCOPE_MAP_NAME' obsahuje nepovolené znaky."
fi
if [[ ! "$EXPIRY_YEARS" =~ ^[1-9][0-9]*$ ]]; then
  fail "EXPIRY_YEARS '$EXPIRY_YEARS' není kladné celé číslo."
fi

ACR_SKU=$(az acr show --name "$ACR_NAME" --resource-group "$RG" --query "sku.name" -o tsv 2>/dev/null) \
  || fail "ACR '$ACR_NAME' nenalezeno v RG '$RG'."

ok "ACR '$ACR_NAME' nalezeno (tier: $ACR_SKU)."

# Scope tokens vyžadují Premium tier
if [[ "$ACR_SKU" != "Premium" ]]; then
  echo ""
  warn "ACR scope tokens vyžadují Premium tier — aktuální je $ACR_SKU."
  echo ""
  echo " Alternativy pro $ACR_SKU tier:"
  echo ""
  echo " 1. Service Principal s AcrPull rolí (doporučeno pro Standard):"
  echo "    SP_NAME=\"sp-deploy-pull-ff-partner-bridge\""
  echo "    SP_PASSWORD=\$(az ad sp create-for-rbac \\"
  echo "      --name \"\$SP_NAME\" \\"
  echo "      --role AcrPull \\"
  echo "      --scopes \$(az acr show --name $ACR_NAME --resource-group $RG --query id -o tsv) \\"
  echo "      --query password -o tsv)"
  echo "    SP_APP_ID=\$(az ad sp list --display-name \"\$SP_NAME\" --query \"[0].appId\" -o tsv)"
  echo ""
  echo "    # Na deploy serveru:"
  echo "    echo \"\$SP_PASSWORD\" | docker login ${ACR_NAME}.azurecr.io \\"
  echo "      --username \"\$SP_APP_ID\" --password-stdin"
  echo ""
  echo " 2. Upgrade na Premium (~ \$200/měsíc):"
  echo "    az acr update --name $ACR_NAME --resource-group $RG --sku Premium"
  echo ""
  echo " 3. Admin user (NEDOPORUČENO — sdílené credentials, žádný scope):"
  echo "    az acr update --name $ACR_NAME --resource-group $RG --admin-enabled true"
  echo ""
  fail "Pokračování není možné v $ACR_SKU tieru. Zvolte jednu z alternativ výše."
fi

# ─── krok 2: scope map (idempotentní) ─────────────────────────────────────────

log "Konfiguruji scope map '$SCOPE_MAP_NAME' (read-only na repo $REPO_NAME)..."

EXISTING_SCOPE=$(az acr scope-map show \
  --name "$SCOPE_MAP_NAME" \
  --registry "$ACR_NAME" \
  --resource-group "$RG" \
  --query "name" -o tsv 2>/dev/null || true)

if [[ -z "$EXISTING_SCOPE" ]]; then
  az acr scope-map create \
    --name "$SCOPE_MAP_NAME" \
    --registry "$ACR_NAME" \
    --resource-group "$RG" \
    --description "Read-only access pro deploy server na repo $REPO_NAME" \
    --repository "$REPO_NAME" content/read metadata/read \
    --output none
  ok "Scope map '$SCOPE_MAP_NAME' vytvořena."
else
  ok "Scope map '$SCOPE_MAP_NAME' už existuje — používám stávající."
fi

# ─── krok 3: token (idempotentní create, password vždy nově) ─────────────────

log "Konfiguruji token '$TOKEN_NAME'..."

EXISTING_TOKEN=$(az acr token show \
  --name "$TOKEN_NAME" \
  --registry "$ACR_NAME" \
  --resource-group "$RG" \
  --query "name" -o tsv 2>/dev/null || true)

if [[ -z "$EXISTING_TOKEN" ]]; then
  az acr token create \
    --name "$TOKEN_NAME" \
    --registry "$ACR_NAME" \
    --resource-group "$RG" \
    --scope-map "$SCOPE_MAP_NAME" \
    --status enabled \
    --output none
  ok "Token '$TOKEN_NAME' vytvořen."
else
  ok "Token '$TOKEN_NAME' už existuje."
fi

# Vždy vygenerovat NOVÝ password (rotace)
log "Generuji nový password (platnost: $EXPIRY_YEARS rok/y)..."

EXPIRY_DATE=$(date -u -d "+${EXPIRY_YEARS} years" +"%Y-%m-%dT%H:%M:%SZ" 2>/dev/null || \
              date -u -v+${EXPIRY_YEARS}y +"%Y-%m-%dT%H:%M:%SZ")  # macOS fallback

TOKEN_PASSWORD=$(az acr token credential generate \
  --name "$TOKEN_NAME" \
  --registry "$ACR_NAME" \
  --resource-group "$RG" \
  --password1 \
  --expiration "$EXPIRY_DATE" \
  --query "passwords[0].value" \
  -o tsv)

# Zapsat do souboru s přístupem 600
umask 077
printf '%s' "$TOKEN_PASSWORD" > "$OUTPUT_FILE"
chmod 600 "$OUTPUT_FILE"
unset TOKEN_PASSWORD
ok "Password zapsán do $OUTPUT_FILE (chmod 600)."

# ─── krok 4: výstup pro deploy server ─────────────────────────────────────────

ACR_LOGIN_SERVER=$(az acr show --name "$ACR_NAME" --resource-group "$RG" --query "loginServer" -o tsv)

echo ""
echo "═══════════════════════════════════════════════════════════════════"
echo " ACR token vytvořen — nastavení na deploy serveru"
echo "═══════════════════════════════════════════════════════════════════"
echo ""
echo " 1. Zkopírovat password z $OUTPUT_FILE na deploy server (scp / KeePass)."
echo ""
echo " 2. Na deploy serveru jako uživatel 'deploy' spustit:"
echo ""
echo "      cat <password-soubor> | docker login ${ACR_LOGIN_SERVER} \\"
echo "        --username '${TOKEN_NAME}' --password-stdin"
echo ""
echo " 3. Ověřit přístup:"
echo ""
echo "      docker pull ${ACR_LOGIN_SERVER}/ff-partner-bridge:<existující-tag>"
echo ""
echo " 4. Po úspěšném loginu SMAZAT $OUTPUT_FILE — credentials uloženy v"
echo "    ~/.docker/config.json na deploy serveru:"
echo ""
echo "      shred -u $OUTPUT_FILE"
echo ""
echo " Token expiruje: $EXPIRY_DATE"
echo " Rotace: spustit tento skript znovu (re-generuje password1, scope map zůstává)"
echo ""
echo " Audit:"
echo "   az acr token list --registry $ACR_NAME --resource-group $RG -o table"
echo "   az acr scope-map list --registry $ACR_NAME --resource-group $RG -o table"
echo ""
echo "═══════════════════════════════════════════════════════════════════"

ok "F0-09 deploy token hotovo. Token '$TOKEN_NAME' aktivní do $EXPIRY_DATE."
