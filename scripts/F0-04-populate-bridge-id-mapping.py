#!/usr/bin/env python3
# coding=utf-8
"""
F0-04 · Naplnění bridge_id_mapping z bridge_migration_staging + FieldForce Companies

Párovací logika:
  bridge_migration_staging.pipe_id (BIGINT) = FieldForce Company.PipedriveId (long)

Zdroj FieldForce dat — dva módy (použij jeden):
  A) --ff-csv  : CSV export z FieldForce (Company.Id, Company.PipedriveId, Company.Name)
  B) --ff-conn : Přímé Azure SQL připojení k FieldForce DB (ODBC connection string)

Prerekvizity:
  - F0-03: bridge_migration_staging naplněna (status=pending/no_partner_id)
  - F0-02: bridge_id_mapping tabulka existuje v Azure SQL
  - pip install pyodbc

Acceptance kritérium: >= 95 % firem z bridge_migration_staging (status=pending) namatchováno.

Spuštění (CSV mód):
  python scripts/F0-04-populate-bridge-id-mapping.py \
    --bridge-conn "Driver={ODBC Driver 18 for SQL Server};Server=...;Database=bridge;..." \
    --ff-csv exports/ff_companies.csv

Spuštění (přímý FF DB mód):
  python scripts/F0-04-populate-bridge-id-mapping.py \
    --bridge-conn "Driver={ODBC Driver 18 for SQL Server};Server=...;Database=bridge;..." \
    --ff-conn "Driver={ODBC Driver 18 for SQL Server};Server=...;Database=fieldforce;..."

CSV formát (ff_companies.csv):
  CompanyId,PipedriveId,CompanyName
  550e8400-e29b-41d4-a716-446655440000,12345,Acme s.r.o.
  ...

Generace CSV z FieldForce Azure SQL (spustit manuálně):
  SELECT Id AS CompanyId, PipedriveId, Name AS CompanyName
  FROM Companies
  WHERE PipedriveId IS NOT NULL
  ORDER BY PipedriveId;

BEZPEČNOST:
  - Hesla/conn strings pouze jako parametry nebo env proměnné
  - Skript NEmodifikuje FieldForce DB (READ-ONLY vůči FF)
  - bridge_id_mapping: INSERT pouze, ne UPDATE (idempotence přes upsert)
"""

import argparse
import csv
import logging
import os
import re
import sys
from datetime import datetime, timezone
from typing import Optional

_GUID_RE = re.compile(
    r'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$',
    re.IGNORECASE
)

try:
    import pyodbc
except ImportError:
    print("Chybí závislost: pip install pyodbc")
    sys.exit(1)

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(message)s",
    datefmt="%Y-%m-%dT%H:%M:%SZ"
)
log = logging.getLogger("F0-04")

# Konstanta pro bridge_id_mapping
ENTITY_TYPE = "client"
SYNC_DIRECTION = "ff_to_partner"


def load_ff_companies_from_csv(csv_path: str) -> dict[int, dict]:
    """
    Načte FieldForce Company data z CSV exportu.
    Vrací: {pipedrive_id -> {ff_company_id, company_name}}
    """
    companies: dict[int, dict] = {}
    required_cols = {"CompanyId", "PipedriveId", "CompanyName"}

    with open(csv_path, encoding="utf-8-sig", newline="") as f:
        reader = csv.DictReader(f)
        if not required_cols.issubset(reader.fieldnames or []):
            missing = required_cols - set(reader.fieldnames or [])
            raise ValueError(
                f"CSV chybí sloupce: {missing}. "
                f"Očekáváno: CompanyId, PipedriveId, CompanyName"
            )
        for row in reader:
            pipe_id_str = row["PipedriveId"].strip()
            if not pipe_id_str or pipe_id_str.lower() in ("null", "none", ""):
                continue
            try:
                pipe_id = int(pipe_id_str)
            except ValueError:
                log.warning(f"Neplatné PipedriveId '{pipe_id_str}' — řádek přeskočen")
                continue

            ff_company_id = row["CompanyId"].strip()
            if not ff_company_id:
                log.warning(f"Prázdné CompanyId pro PipedriveId {pipe_id} — přeskočeno")
                continue
            if not _GUID_RE.match(ff_company_id):
                log.warning(
                    f"Neplatný GUID '{ff_company_id}' pro PipedriveId {pipe_id} — přeskočeno"
                )
                continue

            if pipe_id in companies:
                log.warning(
                    f"Duplicitní PipedriveId {pipe_id}: "
                    f"{companies[pipe_id]['ff_company_id']} vs {ff_company_id} — použito první"
                )
                continue

            companies[pipe_id] = {
                "ff_company_id": ff_company_id,
                "company_name": row["CompanyName"].strip(),
            }

    log.info(f"CSV: načteno {len(companies)} FieldForce Company záznamů s PipedriveId")
    return companies


def load_ff_companies_from_db(ff_conn_str: str) -> dict[int, dict]:
    """
    Načte FieldForce Company data přímo z FieldForce Azure SQL DB.
    READ-ONLY — pouze SELECT.
    Vrací: {pipedrive_id -> {ff_company_id, company_name}}
    """
    companies: dict[int, dict] = {}
    conn = pyodbc.connect(ff_conn_str, autocommit=True)
    try:
        cur = conn.cursor()
        # Company.Id = UNIQUEIDENTIFIER, Company.PipedriveId = BIGINT NULL
        # Názvy sloupců dle FieldForce EF Core konvence (PascalCase)
        cur.execute("""
            SELECT
                CAST(Id AS VARCHAR(36)) AS CompanyId,
                PipedriveId,
                Name AS CompanyName
            FROM dbo.Companies
            WHERE PipedriveId IS NOT NULL
            ORDER BY PipedriveId
        """)
        for row in cur.fetchall():
            pipe_id = int(row.PipedriveId)
            if pipe_id in companies:
                log.warning(
                    f"Duplicitní PipedriveId {pipe_id} v FieldForce DB: "
                    f"{companies[pipe_id]['ff_company_id']} vs {row.CompanyId} — použito první"
                )
                continue
            companies[pipe_id] = {
                "ff_company_id": row.CompanyId,
                "company_name": row.CompanyName or "",
            }
        log.info(f"FieldForce DB: načteno {len(companies)} Company záznamů s PipedriveId")
    finally:
        conn.close()
    return companies


def load_staging(bridge_conn_str: str) -> list[dict]:
    """
    Načte záznamy z bridge_migration_staging se statusem 'pending'
    (= mají partner_id, čekají na párování s FieldForce).
    """
    conn = pyodbc.connect(bridge_conn_str, autocommit=True)
    try:
        cur = conn.cursor()
        cur.execute("""
            SELECT
                id, pipe_id, pipe_type,
                partner_id, partner_region,
                role_label, org_name, client_right
            FROM dbo.bridge_migration_staging
            WHERE match_status = 'pending'
              AND partner_id IS NOT NULL
            ORDER BY pipe_id
        """)
        rows = [
            {
                "staging_id": r[0],
                "pipe_id": r[1],
                "pipe_type": r[2],
                "partner_id": r[3],
                "partner_region": r[4],
                "role_label": r[5],
                "org_name": r[6],
                "client_right": r[7],
            }
            for r in cur.fetchall()
        ]
        log.info(f"Staging: nalezeno {len(rows)} záznamů se statusem 'pending'")
        return rows
    finally:
        conn.close()


def upsert_bridge_id_mapping(
    bridge_conn_str: str,
    staging: list[dict],
    ff_companies: dict[int, dict],
    dry_run: bool,
) -> tuple[int, int, int]:
    """
    Vloží spárované záznamy do bridge_id_mapping (MERGE = upsert).
    WHEN MATCHED → UPDATE timestamps; WHEN NOT MATCHED → INSERT.
    Vrací (inserted, updated, unmatched). RowCount po MERGE je vždy 1 pro jeden source řádek.
    """
    now = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
    inserted = 0    # Nové záznamy (NOT MATCHED → INSERT)
    updated = 0     # Existující záznamy (MATCHED → UPDATE timestamps)
    unmatched = 0   # Záznamy bez FieldForce Company (ff_missing)
    unmatched_rows: list[dict] = []

    conn = pyodbc.connect(bridge_conn_str, autocommit=False)
    try:
        cur = conn.cursor()

        # MERGE (upsert) — bezpečné pro opakované spuštění
        # Primární klíč unikátnosti: (ff_company_id, entity_type)
        merge_sql = """
            MERGE dbo.bridge_id_mapping AS target
            USING (VALUES (?, ?, ?, ?, ?, ?, ?, ?)) AS source
                (ff_company_id, partner_client_id, partner_region, entity_type,
                 pipedrive_id, last_sync_at, last_sync_direction, created_at)
            ON target.ff_company_id = CAST(source.ff_company_id AS UNIQUEIDENTIFIER)
               AND target.entity_type = source.entity_type
            WHEN NOT MATCHED THEN
                INSERT (ff_company_id, partner_client_id, partner_region, entity_type,
                        pipedrive_id, last_sync_at, last_sync_direction, created_at, updated_at)
                VALUES (CAST(source.ff_company_id AS UNIQUEIDENTIFIER),
                        source.partner_client_id, source.partner_region, source.entity_type,
                        source.pipedrive_id,
                        CAST(source.last_sync_at AS DATETIME),
                        source.last_sync_direction,
                        CAST(source.created_at AS DATETIME),
                        CAST(source.created_at AS DATETIME))
            WHEN MATCHED THEN
                UPDATE SET
                    updated_at = CAST(source.last_sync_at AS DATETIME),
                    last_sync_at = CAST(source.last_sync_at AS DATETIME);
        """

        update_staging_sql = """
            UPDATE dbo.bridge_migration_staging
            SET match_status = ?,
                ff_company_id = CAST(? AS UNIQUEIDENTIFIER),
                updated_at = CAST(? AS DATETIME)
            WHERE id = ?
        """

        for row in staging:
            pipe_id = row["pipe_id"]
            ff = ff_companies.get(pipe_id)

            if ff is None:
                unmatched += 1
                unmatched_rows.append(row)
                if not dry_run:
                    cur.execute(
                        update_staging_sql,
                        ("ff_missing", None, now, row["staging_id"])
                    )
                continue

            if not dry_run:
                cur.execute(merge_sql, (
                    ff["ff_company_id"],
                    row["partner_id"],
                    row["partner_region"],
                    ENTITY_TYPE,
                    pipe_id,
                    now,
                    SYNC_DIRECTION,
                    now,
                ))
                # MERGE s jedním source řádkem vždy vrátí rowcount=1
                # (INSERT nebo UPDATE) — sledujeme přes bridge_sync_log v produkci
                inserted += 1

                cur.execute(
                    update_staging_sql,
                    ("ff_matched", ff["ff_company_id"], now, row["staging_id"])
                )
            else:
                inserted += 1

            total_processed = inserted + unmatched
            if total_processed % 200 == 0:
                if not dry_run:
                    conn.commit()
                log.info(f"  Progress: {inserted} matched, {unmatched} unmatched...")

        if not dry_run:
            conn.commit()
    except Exception:
        if not dry_run:
            conn.rollback()
        raise
    finally:
        conn.close()

    # Uložit report nenamatchovaných firem (i v dry-run — pro analýzu)
    if unmatched_rows:
        report_path = os.path.join(
            os.path.dirname(os.path.abspath(__file__)),
            "..", "exports", "F0-04-unmatched-companies.csv"
        )
        os.makedirs(os.path.dirname(report_path), exist_ok=True)
        with open(report_path, "w", encoding="utf-8", newline="") as f:
            writer = csv.DictWriter(
                f,
                fieldnames=["staging_id", "pipe_id", "pipe_type",
                            "partner_id", "partner_region", "org_name",
                            "role_label", "client_right"]
            )
            writer.writeheader()
            for r in unmatched_rows:
                writer.writerow({k: r.get(k) for k in writer.fieldnames})
        log.warning(f"  Nenamatchované firmy uloženy: {report_path}")

    return inserted, unmatched, 0  # třetí hodnota zachována pro kompatibilitu signatury


def print_final_stats(bridge_conn_str: str, total_pending: int, matched: int, unmatched: int):
    """Vypíše finální statistiku a vyhodnotí acceptance kritérium."""
    log.info("=== Výsledek F0-04 ===")
    log.info(f"  Staging pending (partner_id exist.): {total_pending}")
    log.info(f"  Matched → bridge_id_mapping:         {matched}")
    log.info(f"  Unmatched (ff_missing):               {unmatched}")

    if total_pending > 0:
        match_pct = matched / total_pending * 100
        log.info(f"  Match rate:                          {match_pct:.1f}%")
        if match_pct >= 95.0:
            log.info("  ✅ Acceptance kritérium splněno (>= 95%)")
            log.info("  → Pokračuj s F0-05 (Service Bus setup) a spuštěním Bridge")
        else:
            log.warning(
                f"  ⚠️  Match rate {match_pct:.1f}% < 95% — zkontroluj soubor "
                f"exports/F0-04-unmatched-companies.csv a doplň manuálně!"
            )
            log.warning(
                "  Postup: pro každou nenamatchovanou firmu zjisti FieldForce Company.Id "
                "a spusť: INSERT INTO bridge_id_mapping (...) VALUES (...)"
            )

    # Celkový stav bridge_id_mapping
    conn = pyodbc.connect(bridge_conn_str, autocommit=True)
    try:
        cur = conn.cursor()
        cur.execute("""
            SELECT partner_region, COUNT(*) AS cnt
            FROM dbo.bridge_id_mapping
            WHERE entity_type = 'client'
            GROUP BY partner_region
            ORDER BY partner_region
        """)
        log.info("  bridge_id_mapping per region:")
        for r in cur.fetchall():
            log.info(f"    {r[0]}: {r[1]} klientů")
    finally:
        conn.close()


def main():
    parser = argparse.ArgumentParser(
        description="F0-04: Naplnění bridge_id_mapping z bridge_migration_staging + FieldForce"
    )
    parser.add_argument(
        "--bridge-conn", required=True,
        help="Bridge Azure SQL ODBC connection string"
    )

    ff_group = parser.add_mutually_exclusive_group(required=True)
    ff_group.add_argument(
        "--ff-csv",
        help="Cesta k CSV exportu z FieldForce (sloupce: CompanyId, PipedriveId, CompanyName)"
    )
    ff_group.add_argument(
        "--ff-conn",
        help="FieldForce Azure SQL ODBC connection string (READ-ONLY přístup)"
    )

    parser.add_argument(
        "--dry-run", action="store_true",
        help="Pouze párování — žádný zápis do bridge_id_mapping"
    )
    args = parser.parse_args()

    log.info("F0-04: Spuštění párování FieldForce → bridge_id_mapping")
    log.info(f"  Dry-run: {args.dry_run}")

    # Načtení FieldForce dat
    if args.ff_csv:
        log.info(f"Načítám FieldForce Companies z CSV: {args.ff_csv}")
        ff_companies = load_ff_companies_from_csv(args.ff_csv)
    else:
        log.info("Načítám FieldForce Companies z Azure SQL DB...")
        ff_companies = load_ff_companies_from_db(args.ff_conn)

    if not ff_companies:
        log.error("Žádná FieldForce Company data — zkontroluj vstup!")
        sys.exit(1)

    # Načtení staging dat
    log.info("Načítám bridge_migration_staging (status=pending)...")
    staging = load_staging(args.bridge_conn)

    if not staging:
        log.warning("Žádné pending záznamy v bridge_migration_staging.")
        log.warning("Spusť nejdřív F0-03-populate-staging.py!")
        sys.exit(1)

    total_pending = len(staging)

    # Párování a zápis do bridge_id_mapping
    log.info(f"Párování {total_pending} staging záznamů se {len(ff_companies)} FieldForce Companies...")
    matched, unmatched, skipped = upsert_bridge_id_mapping(
        args.bridge_conn, staging, ff_companies, args.dry_run
    )

    # Výsledná statistika
    print_final_stats(args.bridge_conn, total_pending, matched, unmatched)

    if args.dry_run:
        log.info("Dry-run: žádné změny v DB provedeny.")


if __name__ == "__main__":
    main()
