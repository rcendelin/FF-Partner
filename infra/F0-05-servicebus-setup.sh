#!/usr/bin/env bash
# F0-05 — Nasazení Service Bus topics pro FF-Partner Bridge
#
# Co skript dělá:
#   1. Ověří prerequisity (az CLI, přihlášení, existenci namespace)
#   2. Nasadí Bicep šablonu (idempotentní — bezpečné pro opakované spuštění)
#   3. Ověří, že všechny topics a subscriptions existují se správnou konfigurací
#   4. Vypíše instrukce pro uložení connection stringu do Key Vault
#
# Prerekvizity:
#   - Azure CLI >= 2.47  (az version)
#   - Přihlášení: az login nebo Managed Identity
#   - Oprávnění: Contributor nebo ServiceBus Data Owner na namespace RG
#   - Existující Service Bus namespace (sdílený s FieldForce — REV-02)
#
# Použití:
#   export RG="rg-xtuning-prod"
#   export NS="sb-fieldforce-prod"        # zjistit z Azure Portalu — prefix obvykle sb- nebo sbns-
#   bash infra/F0-05-servicebus-setup.sh
#
# POZOR: Nezapisujte connection stringy do shellu přímo — předávejte přes proměnné prostředí.

set -euo pipefail

# ─── konfigurace (přepsat přes env proměnné) ──────────────────────────────────

RG="${RG:?Nastavte proměnnou RG (resource group), např. export RG=rg-xtuning-prod}"
NS="${NS:?Nastavte proměnnou NS (Service Bus namespace), např. export NS=sb-fieldforce-prod}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BICEP_FILE="$SCRIPT_DIR/F0-05-servicebus.bicep"

# Bezpečný dočasný soubor (M-01: ne pevná cesta /tmp) — automaticky smazán při ukončení
DEPLOY_TMP=$(mktemp /tmp/bridge-sb-deploy.XXXXXX.json)
trap 'rm -f "$DEPLOY_TMP"' EXIT

# Očekávané topics po nasazení (validace)
EXPECTED_TOPICS=(
  "ff.company.sync"
  "ff.contact.updated"
  "ff.company.owner-changed"
  "ff.company.disabled"
  "bridge.company.synced"
  "bridge.company.sync-failed"
  "bridge.company.conflict"
  "bridge.order.created"
  "bridge.order.state-changed"
  "bridge.order.completed"
  "bridge.order.cancelled"
)

# ─── pomocné funkce ───────────────────────────────────────────────────────────

log()  { echo "[$(date '+%H:%M:%S')] $*"; }
ok()   { echo "[$(date '+%H:%M:%S')] ✓ $*"; }
fail() { echo "[$(date '+%H:%M:%S')] ✗ $*" >&2; exit 1; }

# ─── krok 1: prerequisity ─────────────────────────────────────────────────────

log "Ověřuji prerequisity..."

if ! command -v az &>/dev/null; then
  fail "Azure CLI není nainstalováno. Nainstalujte z: https://learn.microsoft.com/en-us/cli/azure/install-azure-cli"
fi

AZ_VERSION=$(az version --query '"azure-cli"' -o tsv 2>/dev/null || echo "unknown")
log "Azure CLI verze: $AZ_VERSION"

# Ověřit přihlášení
if ! az account show &>/dev/null; then
  fail "Nejste přihlášeni do Azure CLI. Spusťte: az login"
fi

SUBSCRIPTION=$(az account show --query "name" -o tsv)
log "Přihlášeni k subscription: $SUBSCRIPTION"

# Ověřit existenci resource group
if ! az group show --name "$RG" &>/dev/null; then
  fail "Resource group '$RG' neexistuje nebo nemáte oprávnění k jejímu zobrazení."
fi
ok "Resource group '$RG' nalezena."

# Ověřit existenci namespace
if ! az servicebus namespace show --resource-group "$RG" --name "$NS" &>/dev/null; then
  fail "Service Bus namespace '$NS' v resource group '$RG' neexistuje. \
Zjistěte název namespace z Azure Portalu (prefix obvykle 'sb-' nebo 'sbns-') a nastavte proměnnou NS."
fi

NAMESPACE_TIER=$(az servicebus namespace show --resource-group "$RG" --name "$NS" --query "sku.name" -o tsv)
log "Namespace '$NS' nalezen — tier: $NAMESPACE_TIER"

if [[ "$NAMESPACE_TIER" == "Basic" ]]; then
  fail "Service Bus namespace je na Basic tier, který nepodporuje Topics. \
Upgrade na Standard nebo Premium: az servicebus namespace update --resource-group \"$RG\" --name \"$NS\" --sku Standard"
fi

ok "Namespace tier '$NAMESPACE_TIER' podporuje Topics."

# ─── krok 2: nasazení Bicep šablony ──────────────────────────────────────────

log "Nasazuji Bicep šablonu: $BICEP_FILE"

if [[ ! -f "$BICEP_FILE" ]]; then
  fail "Bicep soubor '$BICEP_FILE' nenalezen. Spusťte skript z kořene repozitáře nebo upravte SCRIPT_DIR."
fi

DEPLOYMENT_NAME="F0-05-servicebus-$(date +%Y%m%d-%H%M%S)"

# Deployment — set -euo pipefail zachytí selhání; output jde pouze do dočasného souboru
az deployment group create \
  --resource-group "$RG" \
  --name "$DEPLOYMENT_NAME" \
  --template-file "$BICEP_FILE" \
  --parameters namespaceName="$NS" \
  --output json \
  > "$DEPLOY_TMP"

ok "Bicep šablona nasazena úspěšně (deployment: $DEPLOYMENT_NAME)."

# ─── krok 3: validace topics a subscriptions ─────────────────────────────────

log "Ověřuji vytvořené topics a subscriptions..."

FAILED_VALIDATIONS=0

for TOPIC in "${EXPECTED_TOPICS[@]}"; do
  # Ověřit existenci topicu
  if ! az servicebus topic show \
      --resource-group "$RG" \
      --namespace-name "$NS" \
      --name "$TOPIC" &>/dev/null; then
    echo "  ✗ Topic '$TOPIC' NEEXISTUJE" >&2
    FAILED_VALIDATIONS=$((FAILED_VALIDATIONS + 1))
    continue
  fi

  # Určit název subscripce (Bridge konzumuje ff.*, FieldForce konzumuje bridge.*)
  if [[ "$TOPIC" == ff.* ]]; then
    SUB_NAME="bridge"
  else
    SUB_NAME="fieldforce"
  fi

  # Ověřit existenci subscripce a načíst konfiguraci v jednom API volání (M-03 refactor)
  if ! SUB_DATA=$(az servicebus topic subscription show \
      --resource-group "$RG" \
      --namespace-name "$NS" \
      --topic-name "$TOPIC" \
      --name "$SUB_NAME" \
      --query "[maxDeliveryCount, deadLetteringOnMessageExpiration, lockDuration]" \
      -o tsv 2>/dev/null); then
    echo "  ✗ Subscripce '$TOPIC/$SUB_NAME' NEEXISTUJE" >&2
    FAILED_VALIDATIONS=$((FAILED_VALIDATIONS + 1))
    continue
  fi

  # Parsovat TSV výstup: maxDeliveryCount<TAB>deadLetteringOnMessageExpiration<TAB>lockDuration
  IFS=$'\t' read -r MAX_DELIVERY DLQ_ON_EXPIRY LOCK_DUR <<< "$SUB_DATA"

  TOPIC_OK=true

  if [[ "$MAX_DELIVERY" != "5" ]]; then
    echo "  ✗ Topic '$TOPIC/$SUB_NAME': maxDeliveryCount=$MAX_DELIVERY (očekáváno 5)" >&2
    FAILED_VALIDATIONS=$((FAILED_VALIDATIONS + 1))
    TOPIC_OK=false
  fi

  if [[ "$DLQ_ON_EXPIRY" != "true" ]]; then
    echo "  ✗ Topic '$TOPIC/$SUB_NAME': deadLetteringOnMessageExpiration=$DLQ_ON_EXPIRY (očekáváno true)" >&2
    FAILED_VALIDATIONS=$((FAILED_VALIDATIONS + 1))
    TOPIC_OK=false
  fi

  # Validace lockDuration — kritická pro SLA (Bridge musí zprávu zpracovat do 5 minut)
  if [[ "$LOCK_DUR" != "PT5M" ]]; then
    echo "  ✗ Topic '$TOPIC/$SUB_NAME': lockDuration=$LOCK_DUR (očekáváno PT5M)" >&2
    FAILED_VALIDATIONS=$((FAILED_VALIDATIONS + 1))
    TOPIC_OK=false
  fi

  if [[ "$TOPIC_OK" == "true" ]]; then
    echo "  ✓ $TOPIC → $SUB_NAME (maxDelivery=5, DLQ=true, lockDuration=PT5M)"
  fi
done

if [[ $FAILED_VALIDATIONS -gt 0 ]]; then
  fail "$FAILED_VALIDATIONS validací selhalo. Zkontrolujte chyby výše."
fi

ok "Všechny topics a subscriptions validovány."

# Ověřit existenci authorization rules (Bicep je vytvořil)
for RULE in "bridge-send-listen" "fieldforce-send-listen"; do
  if az servicebus namespace authorization-rule show \
      --resource-group "$RG" \
      --namespace-name "$NS" \
      --name "$RULE" &>/dev/null; then
    echo "  ✓ Authorization rule '$RULE' existuje."
  else
    echo "  ✗ Authorization rule '$RULE' NEEXISTUJE — zkontrolujte Bicep deployment." >&2
    FAILED_VALIDATIONS=$((FAILED_VALIDATIONS + 1))
  fi
done

if [[ $FAILED_VALIDATIONS -gt 0 ]]; then
  fail "$FAILED_VALIDATIONS validací selhalo. Zkontrolujte chyby výše."
fi

# ─── krok 4: instrukce pro uložení connection stringu ────────────────────────

log "POZOR: Connection string NEZAPISUJTE na stdout ani do logů."
log "Uložte ho do Azure Key Vault (F0-06) nebo jako Docker Secret."
echo ""
echo "Příkaz pro získání Bridge connection stringu (spustit ručně — výstup IHNED uložit do Key Vault):"
echo ""
echo "  az servicebus namespace authorization-rule keys list \\"
echo "    --resource-group \"${RG}\" \\"
echo "    --namespace-name \"${NS}\" \\"
echo "    --name bridge-send-listen \\"
echo "    --query primaryConnectionString -o tsv"
echo ""
echo "Příkaz pro uložení do Azure Key Vault:"
echo ""
echo "  CONN=\$(az servicebus namespace authorization-rule keys list --resource-group \"${RG}\" --namespace-name \"${NS}\" --name bridge-send-listen --query primaryConnectionString -o tsv)"
echo "  az keyvault secret set --vault-name <KV_NAME> --name servicebus-conn --value \"\$CONN\""
echo "  unset CONN"
echo ""

# ─── souhrn ──────────────────────────────────────────────────────────────────

ok "F0-05 hotovo. Vytvořeno ${#EXPECTED_TOPICS[@]} topics ve namespace '${NS}' (RG: ${RG})."
echo ""
echo "Další kroky:"
echo "  1. Uložit Bridge connection string do Azure Key Vault (F0-06)"
echo "  2. Přidat connection string jako Docker Secret 'servicebus_conn' na produkčním serveru"
echo "  3. Ověřit v Azure Portalu: Service Bus → Topics → každý topic by měl mít 0 active messages"
echo "  4. Zdokumentovat namespace název v provozní dokumentaci"
