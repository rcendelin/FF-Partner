#!/usr/bin/env bash
# validate-db-connections.sh
#
# Ověří, že connection stringy v ./secrets/*.txt ukazují na správné DB:
#   - 4× Partner3 MySQL (regionální DB) — má tbl_client, ne cfg_country
#   - 1× GAIA MySQL — má cfg_country, ne tbl_client
#
# Per Partner3 region ověří distribuci client_country_short — pozná, jestli
# CZ conn string omylem ukazuje na PL DB apod.
#
# Použití:
#   bash scripts/validate-db-connections.sh
#
# Předpoklady:
#   - mysql CLI (apt install default-mysql-client)
#   - sudo přístup k ./secrets/*.txt (vlastní user ff-bridge)
#   - .NET conn string formát (MySqlConnector): Server=…;Port=…;Database=…;Uid=…;Pwd=…
#
# BEZPEČNOST:
#   Heslo neprochází přes argumenty (žádné mysql -p…). Skript vytvoří temp my.cnf
#   v /tmp s mode 600, použije přes --defaults-extra-file a hned po dotazu maže.

set -u

# Adresář se secrets — relativní k umístění tohoto skriptu (scripts/../secrets)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SECRETS="${SECRETS_DIR:-$(dirname "$SCRIPT_DIR")/secrets}"

if [[ ! -d "$SECRETS" ]]; then
  echo "ERROR: secrets adresář neexistuje: $SECRETS" >&2
  echo "Override přes: SECRETS_DIR=/opt/ff-partner-bridge/secrets bash $0" >&2
  exit 1
fi

if ! command -v mysql >/dev/null; then
  echo "ERROR: mysql CLI není nainstalován." >&2
  echo "Instalace: sudo apt install default-mysql-client" >&2
  exit 1
fi

parse_to_mycnf() {
  local secret_file="$1"
  local out="$2"
  if [[ ! -r "$secret_file" ]]; then
    echo "  ✗ $secret_file není čitelný (vyžaduje sudo?)" >&2
    return 1
  fi
  local conn
  conn=$(sudo cat "$secret_file" 2>/dev/null || cat "$secret_file")
  if [[ -z "$conn" ]]; then
    echo "  ✗ $secret_file je prázdný" >&2
    return 1
  fi

  local host port db user pass
  host=$(echo "$conn" | grep -oiP '(?:Server|Host|Data Source)=\K[^;]+')
  port=$(echo "$conn" | grep -oiP 'Port=\K[^;]+')
  db=$(echo "$conn"   | grep -oiP '(?:Database|Initial Catalog)=\K[^;]+')
  user=$(echo "$conn" | grep -oiP '(?:Uid|User Id|Username|User)=\K[^;]+')
  pass=$(echo "$conn" | grep -oiP '(?:Pwd|Password)=\K[^;]+')

  if [[ -z "$host" || -z "$user" || -z "$pass" ]]; then
    echo "  ✗ $secret_file — nepodařilo se rozparsovat Server/User/Password" >&2
    return 1
  fi

  umask 077
  cat > "$out" <<EOF
[client]
host=$host
port=${port:-3306}
user=$user
password=$pass
database=$db
EOF
  echo "  → cíl: $user@$host:${port:-3306}/$db"
}

run_partner() {
  local region=$1
  local secret="$SECRETS/partner_${region}_conn.txt"
  local cnf="/tmp/mycnf-validate-$region-$$"

  echo
  echo "########## Partner3 $region ##########"
  if ! parse_to_mycnf "$secret" "$cnf"; then
    return
  fi

  mysql --defaults-extra-file="$cnf" -t 2>&1 <<'SQL'
SELECT '=== identita DB ===' AS '';
SELECT @@hostname AS host, DATABASE() AS db, @@version AS ver;

SELECT '=== schema test (musí být Partner3, ne GAIA) ===' AS '';
SELECT
  (SELECT COUNT(*) FROM information_schema.tables
    WHERE table_schema=DATABASE() AND table_name='tbl_client')  AS tbl_client_exists,
  (SELECT COUNT(*) FROM information_schema.tables
    WHERE table_schema=DATABASE() AND table_name='cfg_country') AS cfg_country_exists;

SELECT '=== F0-02 migrace (ff_company_id sloupce) ===' AS '';
SELECT GROUP_CONCAT(column_name ORDER BY column_name) AS ff_columns_present
  FROM information_schema.columns
 WHERE table_schema=DATABASE() AND table_name='tbl_client'
   AND column_name IN ('ff_company_id','ff_sync_source','data_owner','last_ff_sync_at');

SELECT '=== region check (top 5 zemí v tbl_client) ===' AS '';
SELECT client_country_short, COUNT(*) AS n
  FROM tbl_client WHERE client_disable=0
 GROUP BY client_country_short ORDER BY n DESC LIMIT 5;
SQL
  rm -f "$cnf"
}

run_gaia() {
  local secret="$SECRETS/gaia_conn.txt"
  local cnf="/tmp/mycnf-validate-gaia-$$"

  echo
  echo "########## GAIA ##########"
  if ! parse_to_mycnf "$secret" "$cnf"; then
    return
  fi

  mysql --defaults-extra-file="$cnf" -t 2>&1 <<'SQL'
SELECT '=== identita DB ===' AS '';
SELECT @@hostname AS host, DATABASE() AS db, @@version AS ver;

SELECT '=== schema test (musí být GAIA, ne Partner3) ===' AS '';
SELECT
  (SELECT COUNT(*) FROM information_schema.tables
    WHERE table_schema=DATABASE() AND table_name='cfg_country') AS cfg_country_exists,
  (SELECT COUNT(*) FROM information_schema.tables
    WHERE table_schema=DATABASE() AND table_name='tbl_client')  AS tbl_client_exists;

SELECT '=== číselníky neprázdné? ===' AS '';
SELECT 'cfg_country' AS tbl, COUNT(*) AS n FROM cfg_country
UNION ALL SELECT 'cfg_state',  COUNT(*) FROM cfg_state
UNION ALL SELECT 'cfg_county', COUNT(*) FROM cfg_county
UNION ALL SELECT 'cfg_zip',    COUNT(*) FROM cfg_zip;
SQL
  rm -f "$cnf"
}

# === Cleanup při Ctrl+C ===
trap 'rm -f /tmp/mycnf-validate-*-$$ 2>/dev/null' EXIT

# === Spuštění ===
echo "Validuji Bridge DB connection strings ze: $SECRETS"
for r in cz pl hu us; do
  run_partner "$r"
done
run_gaia

echo
echo "=========================================="
echo "Hotovo. Interpretace výstupů:"
echo "  - tbl_client_exists=1, cfg_country_exists=0 → OK Partner3"
echo "  - cfg_country_exists=1, tbl_client_exists=0 → OK GAIA"
echo "  - ff_columns_present=NULL → F0-02 DDL migrace neproběhla"
echo "  - top země neodpovídá regionu → conn stringy jsou prohozené"
echo "=========================================="
