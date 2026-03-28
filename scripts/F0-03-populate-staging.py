#!/usr/bin/env python3
# coding=utf-8
"""
F0-03 · Naplnění bridge_migration_staging z pipe_organizations (GAIA MySQL)

Prerekvizity:
  - F0-01: conn strings pro GAIA MySQL a Bridge Azure SQL
  - F0-02: DDL migrace proběhla (tbl_client rozšíření)
  - F0-03 DDL: bridge_migration_staging existuje v Azure SQL (sql/F0-03-bridge-migration-staging-ddl.sql)
  - pip install pymysql pyodbc

Spuštění:
  python scripts/F0-03-populate-staging.py \
    --gaia-host 172.24.0.12 \
    --gaia-user gaia_user \
    --gaia-pass <heslo> \
    --gaia-db gaia \
    --azure-conn "Driver={ODBC Driver 18 for SQL Server};Server=...;Database=bridge;..."

BEZPEČNOST:
  - Hesla předávat pouze jako parametry nebo env proměnné — NE natvrdo v kódu
  - Skript je READ-ONLY vůči GAIA DB (pouze SELECT)
  - Skript zapisuje pouze do bridge_migration_staging (ne do bridge_id_mapping)
"""

import argparse
import json
import os
import sys
import logging
from datetime import datetime, timezone

try:
    import pymysql
    import pyodbc
except ImportError as e:
    print(f"Chybí závislosti: {e}")
    print("Nainstaluj: pip install pymysql pyodbc")
    sys.exit(1)

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(message)s",
    datefmt="%Y-%m-%dT%H:%M:%SZ"
)
log = logging.getLogger("F0-03")

# Cesta k field mapping konfiguraci (relativně ke skriptu)
FIELD_MAPPING_PATH = os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", "config", "pipedrive_field_mapping.json"
)


def load_field_mapping() -> dict:
    with open(FIELD_MAPPING_PATH, encoding="utf-8") as f:
        return json.load(f)


def build_role_label_to_client_right(mapping: dict) -> dict[str, int]:
    """
    Sestaví slovník: role_label_lowercase → client_right pro všechny instance.
    pipe_organizations.role obsahuje textové labely (ne option ID).
    Protože labely jsou z Pipedrive UI a mohou se lišit instanci od instance,
    tento mapping musí být doplněn manuálně pokud automatické párování selže.
    """
    # Základní fallback mapping dle typických Pipedrive labelů XTuning
    # Tyto hodnoty nutno ověřit oproti pipe_organizations.role DISTINCT hodnotám
    # Spusť nejdřív F0-03-gaia-export.sql sekci "Distribuce rolí" pro aktuální labely
    return {
        "partner": 2,
        "partner hw": 1,
        "zákazník": 0,
        "customer": 0,
        "klient": 0,
    }


def fetch_pipe_organizations(gaia_conn) -> list[dict]:
    """Načte všechny záznamy z pipe_organizations (GAIA MySQL)."""
    with gaia_conn.cursor(pymysql.cursors.DictCursor) as cur:
        cur.execute("""
            SELECT
                pipe_id,
                pipe_type,
                CASE WHEN partner_id > 0 THEN partner_id ELSE NULL END AS partner_id,
                partner AS partner_region,
                role AS role_label,
                country AS country_label,
                name AS org_name
            FROM pipe_organizations
            WHERE pipe_id IS NOT NULL
            ORDER BY pipe_type, partner, partner_id
        """)
        return cur.fetchall()


def map_role_to_client_right(role_label: str | None, role_map: dict[str, int]) -> int | None:
    """Mapuje role_label → client_right. None pokud label není znám."""
    if not role_label:
        return None
    normalized = role_label.strip().lower()
    return role_map.get(normalized)


def insert_staging_batch(azure_conn, rows: list[dict], role_map: dict[str, int]) -> tuple[int, int, int]:
    """
    Vloží záznamy do bridge_migration_staging.
    Vrací (inserted, no_partner_id, skipped_duplicate).
    TRUNCATE před insertem zajistí idempotenci opakovaného spuštění.
    """
    inserted = 0
    no_partner_id = 0
    skipped_duplicate = 0

    cur = azure_conn.cursor()

    # Idempotence: při opakovaném spuštění vymazat staging a začít znovu
    cur.execute("TRUNCATE TABLE dbo.bridge_migration_staging")
    log.info("bridge_migration_staging: TRUNCATE dokončen")

    insert_sql = """
        INSERT INTO dbo.bridge_migration_staging
            (pipe_id, pipe_type, partner_id, partner_region,
             role_label, country_label, org_name, client_right, match_status)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
    """

    for row in rows:
        pipe_id = row["pipe_id"]
        pipe_type = row["pipe_type"]
        partner_id = row["partner_id"]          # None pokud nezpárováno
        partner_region = row["partner_region"]  # None pokud nezpárováno
        role_label = row["role_label"]
        country_label = row["country_label"]
        org_name = row["org_name"]

        client_right = map_role_to_client_right(role_label, role_map)

        if partner_id is None:
            match_status = "no_partner_id"
            no_partner_id += 1
        else:
            match_status = "pending"

        cur.execute(insert_sql, (
            pipe_id, pipe_type,
            partner_id, partner_region,
            role_label, country_label, org_name,
            client_right, match_status
        ))
        inserted += 1

        if inserted % 500 == 0:
            azure_conn.commit()
            log.info(f"  Vloženo {inserted} záznamů...")

    azure_conn.commit()
    return inserted, no_partner_id, skipped_duplicate


def print_summary(azure_conn):
    """Vypíše statistiku staging tabulky po naplnění."""
    cur = azure_conn.cursor()

    cur.execute("""
        SELECT match_status, pipe_type, COUNT(*) AS cnt
        FROM dbo.bridge_migration_staging
        GROUP BY match_status, pipe_type
        ORDER BY pipe_type, match_status
    """)
    rows = cur.fetchall()

    log.info("=== Statistika bridge_migration_staging ===")
    total = 0
    for r in rows:
        log.info(f"  {r[1]} / {r[0]}: {r[2]} záznamů")
        total += r[2]
    log.info(f"  CELKEM: {total} záznamů")

    # Acceptance kritérium: >= 95 % záznamů má partner_id (match_status != 'no_partner_id')
    cur.execute("SELECT COUNT(*) FROM dbo.bridge_migration_staging")
    total_count = cur.fetchone()[0]
    cur.execute("SELECT COUNT(*) FROM dbo.bridge_migration_staging WHERE match_status = 'pending'")
    pending_count = cur.fetchone()[0]

    if total_count > 0:
        match_pct = pending_count / total_count * 100
        log.info(f"  Match rate: {match_pct:.1f}% ({pending_count}/{total_count})")
        if match_pct < 95:
            log.warning(
                f"  ⚠️  Match rate {match_pct:.1f}% < 95% — zkontroluj záznamy "
                f"s match_status='no_partner_id' a doplň manuálně!"
            )
        else:
            log.info("  ✅ Match rate >= 95% — pokračuj s F0-04")


def main():
    parser = argparse.ArgumentParser(description="F0-03: Naplnění bridge_migration_staging z GAIA pipe_organizations")
    parser.add_argument("--gaia-host",  required=True,  help="GAIA MySQL host (např. 172.24.0.12)")
    parser.add_argument("--gaia-user",  required=True,  help="GAIA MySQL user")
    parser.add_argument("--gaia-pass",  required=True,  help="GAIA MySQL heslo")
    parser.add_argument("--gaia-db",    default="gaia", help="GAIA MySQL databáze (default: gaia)")
    parser.add_argument("--azure-conn", required=True,  help="Azure SQL ODBC connection string")
    parser.add_argument("--dry-run",    action="store_true", help="Pouze načíst z GAIA, nepsat do Azure SQL")
    args = parser.parse_args()

    log.info("F0-03: Spuštění migračního skriptu")
    log.info(f"  GAIA: {args.gaia_user}@{args.gaia_host}/{args.gaia_db}")
    log.info(f"  Dry-run: {args.dry_run}")

    # Načíst field mapping (pro role → client_right)
    mapping = load_field_mapping()
    role_map = build_role_label_to_client_right(mapping)
    log.info(f"  Role map načten ({len(role_map)} labelů)")

    # Připojení k GAIA MySQL (READ-ONLY — pouze SELECT)
    log.info("Připojení k GAIA MySQL...")
    gaia_conn = pymysql.connect(
        host=args.gaia_host,
        user=args.gaia_user,
        password=args.gaia_pass,
        database=args.gaia_db,
        charset="utf8mb4",
        cursorclass=pymysql.cursors.DictCursor
    )

    # Načtení dat z pipe_organizations
    log.info("Načítám pipe_organizations z GAIA...")
    rows = fetch_pipe_organizations(gaia_conn)
    gaia_conn.close()
    log.info(f"  Načteno {len(rows)} organizací z pipe_organizations")

    if args.dry_run:
        log.info("Dry-run: data načtena, Azure SQL zápis přeskočen.")
        # Vypsat statistiku lokálně
        no_partner = sum(1 for r in rows if r["partner_id"] is None)
        pending = len(rows) - no_partner
        log.info(f"  pending: {pending}, no_partner_id: {no_partner}")
        log.info(f"  Match rate: {pending / len(rows) * 100:.1f}%" if rows else "  Žádná data")
        return

    # Připojení k Azure SQL
    log.info("Připojení k Azure SQL...")
    azure_conn = pyodbc.connect(args.azure_conn, autocommit=False)

    # Vložení do staging tabulky
    log.info("Vkládám záznamy do bridge_migration_staging...")
    inserted, no_partner_id, skipped = insert_staging_batch(azure_conn, rows, role_map)

    log.info(f"  Vloženo: {inserted}, no_partner_id: {no_partner_id}")

    # Statistika a acceptance kritérium
    print_summary(azure_conn)

    azure_conn.close()
    log.info("F0-03: HOTOVO — zkontroluj statistiku výše, pak spusť F0-04")


if __name__ == "__main__":
    main()
