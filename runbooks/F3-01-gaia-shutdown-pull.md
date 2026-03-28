# Runbook F3-01 — Vypnutí GAIA → Pipedrive pull (stahování dat)

**Fáze:** Fáze 3 — Vypnutí Pipedrive
**Prerekvizita:** Fáze 1 a 2 kompletní, Bridge aktivně synchronizuje všechny firmy, bridge_sync_log bez chyb 24h
**Nevratnost:** ⚠️ Částečně nevratné — po vypnutí se data z Pipedrive přestanou propisovat do Partner3
**Odhadovaná délka:** 2–4 hodiny (včetně monitoringu po vypnutí)

---

## Co tyto skripty dělají (GAIA → Pipedrive pull)

### `cron/pipe_partner_cron.py` (765 řádků)

**Hlavní funkce:** Stahuje organizace a kontakty z Pipedrive API a zapisuje je do `tbl_client` v Partner3 MySQL.

**Spouštění:**
- `__main__` blok (řádky 743–764): Sequential run pro tři instance:
  1. `transfer_client("US")` → `get_client()`, `get_person()`
  2. `transfer_client("CE")` → `get_client()`, `get_person()`
  3. `transfer_client("PL")` → `get_client()`, `get_person()`

**Instance a jejich DB:**
| Instance | Pipedrive URL | Partner DB | Filter org |
|----------|---------------|------------|------------|
| CE | `agroecopowerltd-pobocka.pipedrive.com` | cz + hu | filter `39` |
| US | `agroecopowerltd-usa.pipedrive.com` | us | filter `22` |
| PL | `agroecopowerpl.pipedrive.com` | pl | filter `22` |

**Co zapisuje:**
- `newPartnerClient()` — INSERT do `tbl_client` při novém záznamu v Pipedrive
- `editPartnerClient()` — UPDATE do `tbl_client` při změně v Pipedrive
- Sleduje záznamy za posledních 15 minut (`timedelta(minutes=15)`)

**Cron konfigurace:** Zjistit aktuální crontab na GAIA serveru (`crontab -l` jako uživatel gaia).

### `cron/gen_pipe_data.py` (293 řádků)

**Hlavní funkce:** Stahuje všechny organizace a deals z Pipedrive API a plní GAIA cache tabulky.

**Spouštění:**
- `__main__` (řádky 289–293):
  1. `pipeGeneratorDeals().execute()` — TRUNCATE `pipe_deal` + INSERT ze všech 3 instancí
  2. `pipeGeneratorOrganizations().execute()` — TRUNCATE `pipe_organizations` + INSERT ze všech 3 instancí (CE, US, PL)

**Co zapisuje:**
- `pipe_deal` — deals (obchody) z Pipedrive
- `pipe_organizations` — organizace z Pipedrive (používá F0-03 migrační skript)

**Cron konfigurace:** Zjistit aktuální crontab na GAIA serveru.

---

## Ověření před vypnutím

```sql
-- 1. Ověřit, že Bridge je aktivní (spustit v Azure SQL / SSMS):
SELECT status, COUNT(*) as cnt
FROM bridge_sync_log
WHERE created_at > DATEADD(hour, -24, GETUTCDATE())
GROUP BY status;
-- Očekáváno: status='success' s vysokým počtem, 0 nebo minimum 'failed'

-- 2. Ověřit počty klientů per region (Bridge vs. Partner3):
SELECT partner_region, COUNT(*) as cnt
FROM bridge_id_mapping
WHERE entity_type = 'client'
GROUP BY partner_region;

-- 3. Ověřit, že v bridge_sync_log nejsou pending chyby:
SELECT TOP 10 * FROM bridge_sync_log WHERE status = 'failed' ORDER BY created_at DESC;
```

```bash
# 4. Zazálohovat aktuální stav pipe_organizations (na GAIA serveru)
# POZOR: Heslo zadejte interaktivně — NIKDY nezadávejte heslo přímo na příkazové řádce
export GAIA_DB_HOST=172.24.0.12  # nebo dohledat v KeePass → "GAIA MySQL host"
mkdir -p exports
mysqldump -h "$GAIA_DB_HOST" -u gaia_user --password gaia pipe_organizations pipe_deal \
  > "exports/gaia-pipe-backup-$(date +%Y%m%d_%H%M).sql"
# Ověřit zálohu:
tail -1 "exports/gaia-pipe-backup-$(date +%Y%m%d)_"*.sql  # musí obsahovat "-- Dump completed"
```

---

## Postup vypnutí

### Krok 1: Identifikace cron jobů na GAIA serveru

```bash
# Na GAIA serveru (SSH):
crontab -l | grep -E "pipe_partner_cron|gen_pipe_data"
# Očekávaný výstup příklad:
# */15 * * * * cd /path/to/gaia && python cron/pipe_partner_cron.py >> /var/log/gaia/pipe_partner.log 2>&1
# 0 * * * * cd /path/to/gaia && python cron/gen_pipe_data.py >> /var/log/gaia/gen_pipe.log 2>&1
```

### Krok 2: Záloha skriptů před deaktivací

```bash
# Na GAIA serveru — záloha do archivu
ARCHIVE_DIR=/opt/gaia/archive/pipedrive-shutdown-$(date +%Y%m%d)
mkdir -p $ARCHIVE_DIR

cp cron/pipe_partner_cron.py $ARCHIVE_DIR/
cp cron/gen_pipe_data.py $ARCHIVE_DIR/
cp app/system/pipe_transfer.py $ARCHIVE_DIR/        # záloha i pro F3-02
cp app/gaia_modules/pipedrive_webhooks.py $ARCHIVE_DIR/  # záloha i pro F3-02

echo "Záloha hotova: $ARCHIVE_DIR"
ls -la $ARCHIVE_DIR
```

### Krok 3: Vypnutí cron jobů (komentář v crontab)

```bash
# Metoda A — přímá editace crontab (preferovaná)
crontab -e
# Zakomentovat (přidat # před) řádky s pipe_partner_cron.py a gen_pipe_data.py:
# # DISABLED 2026-03-28 F3-01 — Bridge přebírá roli
# #*/15 * * * * cd /path/to/gaia && python cron/pipe_partner_cron.py ...
# # DISABLED 2026-03-28 F3-01
# #0 * * * * cd /path/to/gaia && python cron/gen_pipe_data.py ...

# Metoda B — záloha crontab a reinicializace (pokud Metoda A není dostupná)
# ⚠️ VAROVÁNÍ: Metoda B může smazat VŠECHNY cron joby, pokud crontab -l selže nebo
# pokud jiný cron job obsahuje text "pipe_partner_cron" nebo "gen_pipe_data"!
# VŽDY nejdříve vytvořit zálohu a ověřit výstup grep před aplikací:
crontab -l > /opt/gaia/archive/crontab-backup-$(date +%Y%m%d).txt
echo "Záloha crontab vytvořena: $(wc -l < /opt/gaia/archive/crontab-backup-$(date +%Y%m%d).txt) řádků"
# Ověřit, které řádky budou odstraněny:
crontab -l | grep -E "pipe_partner_cron|gen_pipe_data"
# Teprve po ověření spustit:
crontab -l | grep -v "pipe_partner_cron\|gen_pipe_data" | crontab -
```

### Krok 4: Ověření deaktivace

```bash
# Ověřit, že cron joby nejsou v crontab
crontab -l | grep -E "pipe_partner_cron|gen_pipe_data"
# Výstup musí být prázdný (nebo zakomentované řádky)

# Počkat na interval dalšího možného spuštění a zkontrolovat log
tail -f /var/log/gaia/pipe_partner.log
# Nesmí přibývat nové záznamy
```

### Krok 5: Manuální závěrečné spuštění gen_pipe_data.py (volitelné)

Pokud F0-03 migrační skript (`populate-staging.py`) ještě nebyl spuštěn s aktuálními daty:

```bash
# Poslední refresh pipe_organizations před vypnutím (pouze pokud potřeba)
cd /path/to/gaia
python cron/gen_pipe_data.py
echo "Poslední gen_pipe_data spuštěn: $(date)"
```

---

## Monitoring po vypnutí (24 hodin)

```bash
# Kontrolovat každých 30 minut prvních 6 hodin:

# 1. Bridge zpracovává zprávy?
# SELECT TOP 20 * FROM bridge_sync_log ORDER BY created_at DESC

# 2. Nevznikají duplicity v tbl_client?
# SELECT client_firm, COUNT(*) cnt FROM tbl_client GROUP BY client_firm HAVING cnt > 1

# 3. Nezvyšuje se počet chyb v bridge_sync_log?
# SELECT status, COUNT(*) FROM bridge_sync_log WHERE created_at > DATEADD(hour,-1,GETUTCDATE()) GROUP BY status

# 4. pipe_organizations tabulka se již nemění (ověřit frozen stav)
mysql -h 172.24.0.12 -u gaia_user -p gaia -e "SELECT COUNT(*), MAX(pipe_id) FROM pipe_organizations"
# Počet řádků musí být stabilní po každém dotazu
```

---

## Rollback (pokud nutný)

```bash
# Obnovit cron joby ze zálohy
crontab /opt/gaia/archive/crontab-backup-<datum>.txt
# Ověřit
crontab -l | grep -E "pipe_partner_cron|gen_pipe_data"
```

---

## Acceptance kritérium F3-01

- [ ] `crontab -l` neobsahuje aktivní řádky s `pipe_partner_cron.py` ani `gen_pipe_data.py`
- [ ] Záloha skriptů existuje v `/opt/gaia/archive/pipedrive-shutdown-<datum>/`
- [ ] 24 hodin po vypnutí: bridge_sync_log bez nárůstu chyb
- [ ] 24 hodin po vypnutí: `pipe_organizations` tabulka je zmrazena (nemění se)
- [ ] Bridge zpracovává zprávy z FieldForce Service Bus bez výpadků

---

## Závislosti a pořadí

```
F3-01 (tento runbook) → F3-02 (push vypnutí) → F3-03 (Partner3 FF ochrana) → F3-04 (validace)
```

**POZOR — časový limit:** F3-02 MUSÍ proběhnout do **4 hodin** po dokončení F3-01. `pipe_transfer.py` (push) by mohl zapsat do Pipedrive data, která Bridge mezitím přepsal v Partner3 — okno nekonzistence.

Pokud F3-02 NELZE dokončit do 4 hodin (incident, nemoc, nedostupnost GAIA správce):
1. Provést rollback F3-01 (obnovit crontab ze zálohy)
2. Naplánovat nový termín, kdy jsou k dispozici oba správci (GAIA + Bridge)

---

*Analyzováno z: `C:\WORK-XTUNING\REPO\gaia3\cron\pipe_partner_cron.py` (765 řádků) a `cron/gen_pipe_data.py` (293 řádků).*
*Vytvořeno: 2026-03-28 — Claude, read-only analýza GAIA kódu.*
