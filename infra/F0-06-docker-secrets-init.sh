#!/usr/bin/env bash
# F0-06 — Inicializace Docker Secrets na produkčním serveru
#
# Co skript dělá:
#   Vytváří soubory ./secrets/*.txt potřebné pro docker-compose.yml.
#   Spustit JEDNOU na produkčním serveru (172.24.0.12 nebo kde běží Bridge Docker).
#
# Secrets které vytvoří:
#   azure_sql_conn.txt      — Azure SQL conn string (Bridge metadata DB)
#   gaia_conn.txt           — GAIA MySQL conn string (číselníky, read-only)
#   partner_cz_conn.txt     — Partner3 CZ MySQL conn string
#   partner_pl_conn.txt     — Partner3 PL MySQL conn string
#   partner_hu_conn.txt     — Partner3 HU MySQL conn string
#   partner_us_conn.txt     — Partner3 US MySQL conn string
#   servicebus_conn.txt     — Azure Service Bus conn string
#   bridge_admin_api_key.txt — API klíč pro REST diagnostické endpointy
#
# Prerekvizity:
#   - Azure CLI přihlášení (az login) NEBO předpřipravené conn stringy
#   - Volitelně: Key Vault URI pro načtení SB conn stringu
#
# Použití:
#   export KV_NAME="kv-xtuning-prod"     # pro načtení SB conn z Key Vault
#   bash infra/F0-06-docker-secrets-init.sh
#   docker compose up -d                  # spustit Bridge s novými secrets
#
# BEZPEČNOSTNÍ UPOZORNĚNÍ:
#   - Soubory v secrets/ jsou citlivé — NIKDY je nepřidávejte do git (.gitignore je chrání)
#   - Nastavte oprávnění: chmod 600 secrets/*.txt (skript to provede automaticky)
#   - Secrets adresář by měl být přístupný pouze uživateli spouštějícímu Docker

set -euo pipefail

# ─── pomocné funkce ───────────────────────────────────────────────────────────

log()  { echo "[$(date '+%H:%M:%S')] $*"; }
ok()   { echo "[$(date '+%H:%M:%S')] ✓ $*"; }
warn() { echo "[$(date '+%H:%M:%S')] ⚠ $*"; }
fail() { echo "[$(date '+%H:%M:%S')] ✗ $*" >&2; exit 1; }

# Bezpečný zápis do souboru — hodnota se neobjeví na stdout
write_secret() {
  local file="$1"
  local value="$2"
  printf '%s' "$value" > "$file"
  chmod 600 "$file"
  log "  → $file ($(wc -c < "$file") bytů, mode 600)"
  # Ihned přepsat proměnnou
  value=""
}

# Interaktivní prompt bez echo (hodnota se nezobrazí na terminálu)
read_secret() {
  local prompt="$1"
  local varname="$2"
  local value=""
  read -rsp "$prompt" value
  echo ""  # nový řádek po zadání
  printf -v "$varname" '%s' "$value"
  value=""
}

# ─── inicializace ─────────────────────────────────────────────────────────────

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SECRETS_DIR="$(dirname "$SCRIPT_DIR")/secrets"

# Varování a potvrzení
echo ""
echo "═══════════════════════════════════════════════════════════════════"
echo " FF-Partner Bridge — inicializace Docker Secrets"
echo " Secrets adresář: $SECRETS_DIR"
echo "═══════════════════════════════════════════════════════════════════"
echo ""
echo " VAROVÁNÍ: Tento skript vytvoří soubory s citlivými connection stringy."
echo " Spouštějte POUZE na produkčním serveru, kde běží Bridge Docker."
echo " Soubory NESMÍ být přidány do git (jsou v .gitignore)."
echo ""
read -rp " Pokračovat? (y/N) " CONFIRM
if [[ "${CONFIRM,,}" != "y" ]]; then
  echo " Zrušeno."
  exit 0
fi
echo ""

# Vytvořit secrets adresář s restriktivními oprávněními
mkdir -p "$SECRETS_DIR"
chmod 700 "$SECRETS_DIR"
log "Secrets adresář: $SECRETS_DIR (mode 700)"

# ─── azure_sql_conn (Azure SQL — Bridge metadata DB) ──────────────────────────

echo ""
log "=== Azure SQL connection string (Bridge metadata DB: bridge_id_mapping, bridge_sync_log) ==="
log "Formát: Server=tcp:<server>.database.windows.net,1433;Initial Catalog=<db>;Persist Security Info=False;User ID=<user>;Password=<pass>;..."
echo ""
read_secret "Azure SQL conn string: " AZURE_SQL_CONN
if [[ -z "$AZURE_SQL_CONN" ]]; then
  fail "Azure SQL conn string nesmí být prázdný."
fi
write_secret "$SECRETS_DIR/azure_sql_conn.txt" "$AZURE_SQL_CONN"
unset AZURE_SQL_CONN

# ─── gaia_conn (GAIA MySQL — číselníky, read-only) ────────────────────────────

echo ""
log "=== GAIA MySQL connection string (číselníky: cfg_country, cfg_zip, cfg_state, cfg_county — READ-ONLY) ==="
log "Formát: Server=172.24.0.12;Port=3306;Database=gaia;User=gaia_user;Password=<pass>;"
echo ""
read_secret "GAIA MySQL conn string: " GAIA_CONN
if [[ -z "$GAIA_CONN" ]]; then
  fail "GAIA conn string nesmí být prázdný."
fi
write_secret "$SECRETS_DIR/gaia_conn.txt" "$GAIA_CONN"
unset GAIA_CONN

# ─── partner_*_conn (Partner3 MySQL — 4 regionální DB) ───────────────────────

for REGION in cz pl hu us; do
  REGION_UPPER="${REGION^^}"
  echo ""
  log "=== Partner3 ${REGION_UPPER} MySQL connection string ==="
  log "Formát: Server=172.24.0.12;Port=3306;Database=partner_${REGION};User=gaia_user;Password=<pass>;"
  echo ""
  read_secret "Partner3 ${REGION_UPPER} conn string: " PARTNER_CONN
  if [[ -z "$PARTNER_CONN" ]]; then
    fail "Partner3 ${REGION_UPPER} conn string nesmí být prázdný."
  fi
  write_secret "$SECRETS_DIR/partner_${REGION}_conn.txt" "$PARTNER_CONN"
  unset PARTNER_CONN
done

# ─── servicebus_conn (Azure Service Bus) ─────────────────────────────────────

echo ""
log "=== Azure Service Bus connection string ==="

KV_NAME="${KV_NAME:-}"
SB_CONN=""

if [[ -n "$KV_NAME" ]] && command -v az &>/dev/null && az account show &>/dev/null 2>&1; then
  log "Pokusím se načíst Service Bus conn string z Key Vault '$KV_NAME'..."
  if SB_CONN=$(az keyvault secret show \
      --vault-name "$KV_NAME" \
      --name "servicebus-connection-string" \
      --query "value" -o tsv 2>/dev/null); then
    log "  Načteno z Key Vault."
  else
    warn "  Nepodařilo se načíst z Key Vault — zadejte ručně."
    SB_CONN=""
  fi
fi

if [[ -z "$SB_CONN" ]]; then
  log "Formát: Endpoint=sb://<namespace>.servicebus.windows.net/;SharedAccessKeyName=bridge-send-listen;SharedAccessKey=..."
  echo ""
  read_secret "Service Bus conn string: " SB_CONN
  if [[ -z "$SB_CONN" ]]; then
    fail "Service Bus conn string nesmí být prázdný."
  fi
fi

write_secret "$SECRETS_DIR/servicebus_conn.txt" "$SB_CONN"
unset SB_CONN

# ─── bridge_admin_api_key (generovat nebo zadat ručně) ───────────────────────

echo ""
log "=== Bridge REST API klíč (pro /api/mapping a /api/sync-log endpointy) ==="
echo ""
read -rp " Generovat náhodný API klíč? (Y/n) " GEN_KEY
GEN_KEY="${GEN_KEY:-y}"

if [[ "${GEN_KEY,,}" == "y" ]]; then
  # Generovat 32 náhodných bytů jako hex string
  API_KEY=$(openssl rand -hex 32 2>/dev/null || python3 -c "import secrets; print(secrets.token_hex(32))")
  write_secret "$SECRETS_DIR/bridge_admin_api_key.txt" "$API_KEY"
  echo ""
  warn "API klíč vygenerován a uložen do $SECRETS_DIR/bridge_admin_api_key.txt"
  warn "Klíč je zobrazen níže — uložte si ho na bezpečné místo (KeePass / správce hesel)."
  warn "POZOR: Hodnota může být zachycena v shell history nebo terminal scroll bufferu."
  warn "Klíč: $API_KEY"
  warn "Po uložení smažte z history: history -d \$(history 1 | awk '{print \$1}')"
  unset API_KEY
else
  echo ""
  read_secret "Bridge API klíč (min 32 znaků): " API_KEY
  if [[ ${#API_KEY} -lt 32 ]]; then
    fail "API klíč musí mít alespoň 32 znaků."
  fi
  write_secret "$SECRETS_DIR/bridge_admin_api_key.txt" "$API_KEY"
  unset API_KEY
fi

# ─── validace ─────────────────────────────────────────────────────────────────

echo ""
log "Ověřuji vytvořené soubory..."

EXPECTED_FILES=(
  "azure_sql_conn.txt"
  "gaia_conn.txt"
  "partner_cz_conn.txt"
  "partner_pl_conn.txt"
  "partner_hu_conn.txt"
  "partner_us_conn.txt"
  "servicebus_conn.txt"
  "bridge_admin_api_key.txt"
)

FAILED=0
for F in "${EXPECTED_FILES[@]}"; do
  FPATH="$SECRETS_DIR/$F"
  if [[ ! -f "$FPATH" ]]; then
    echo "  ✗ $F CHYBÍ" >&2
    FAILED=$((FAILED + 1))
  elif [[ ! -s "$FPATH" ]]; then
    echo "  ✗ $F je PRÁZDNÝ" >&2
    FAILED=$((FAILED + 1))
  else
    MODE=$(stat -c '%a' "$FPATH" 2>/dev/null || stat -f '%p' "$FPATH" 2>/dev/null | tail -c 3)
    echo "  ✓ $F (mode: $MODE)"
  fi
done

if [[ $FAILED -gt 0 ]]; then
  fail "$FAILED souborů chybí nebo je prázdných. Zkontrolujte výše."
fi

ok "Všechny Docker Secrets soubory vytvořeny."
echo ""
echo "Další krok: spustit Bridge"
echo "  cd $(dirname "$SECRETS_DIR")"
echo "  docker compose up -d"
echo "  docker compose logs -f ff-partner-bridge"
echo ""
echo "Zdravotní stav:"
echo "  curl http://172.24.0.1:8080/health"
