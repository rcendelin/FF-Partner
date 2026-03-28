# Runbook F3-02 — Vypnutí GAIA → Pipedrive push (odesílání dat)

**Fáze:** Fáze 3 — Vypnutí Pipedrive
**Prerekvizita:** F3-01 dokončen (pull skript zastaven), Bridge aktivní 24h bez chyb
**Nevratnost:** ⚠️ Velmi nevratné — po vypnutí GAIA přestane posílat nové objednávky a klienty do Pipedrive
**Odhadovaná délka:** 2–4 hodiny (včetně verifikace GAIA ECU pipeline)

---

## Co tyto komponenty dělají (GAIA → Pipedrive push)

### `app/system/pipe_transfer.py` (1311 řádků)

**Hlavní funkce:** Zapisuje data z Partner3/GAIA do Pipedrive — vytváří a aktualizuje organizace a deals.

**Volán z:** Flask web aplikace GAIA (`app/` adresář) — nikoliv cron, ale HTTP requesty nebo interními akcemi při zpracování objednávek.

**Instance a API tokeny** (dle `pipeProcess()`, řádky 1206–1280+):
| Region | Pipedrive URL | API token |
|--------|---------------|-----------|
| cz/hu | `agroecopowerltd-pobocka.pipedrive.com` | viz KeePass → `Pipedrive CE API` |
| us | `agroecopowerltd-usa.pipedrive.com` | viz KeePass → `Pipedrive US API` |
| pl | `agroecopowerpl.pipedrive.com` | viz KeePass → `Pipedrive PL API` |

> **BEZPEČNOST:** API tokeny NIKDY nezapisovat do tohoto dokumentu ani do repozitáře. Dohledat výhradně v KeePass.

**Co zapisuje do Pipedrive:**
- Vytváření nových organizací (`newPartnerClient` logika, volá Pipedrive POST `/api/v1/organizations`)
- Aktualizace existujících organizací (PUT `/api/v1/organizations/{id}`)
- Vytváření dealů (`POST /api/v1/deals`)
- Aktualizace dealů (stav zakázky, vlastník)

**KRITICKÉ:** `pipe_transfer.py` je volaný synchronně z GAIA web aplikace při zpracování objednávek. Nejedná se o standalone cron skript. Musí být deaktivován v kódu nebo konfigurací — NE výmazem souboru.

### `app/gaia_modules/pipedrive_webhooks.py` (40 řádků)

**Hlavní funkce:** Flask webhook handler, přijímá události z Pipedrive a reaguje na ně v Partner3.

**Zpracovává:**
- `deleted.organization` (řádky 38–40): Při smazání organizace v Pipedrive → volá `po.disableClientByPipe()` pro všechny 4 regiony (cz, pl, hu, us)

**Volán z:** Pipedrive webhook konfigurace → HTTP POST na GAIA endpoint. Po vypnutí Pipedrive přestane Pipedrive posílat webhooky.

**Identifikace instance** (řádky 17–25):
- `agroecopowerltd-pobocka.pipedrive.com` → CE
- `agroecopowerpl.pipedrive.com` → PL
- `agroecopowerltd-usa.pipedrive.com` → US

---

## Ověření před vypnutím

```bash
# 1. Ověřit, že F3-01 je dokončen
crontab -l | grep -E "pipe_partner_cron|gen_pipe_data"
# Musí být prázdné (nebo zakomentované)

# 2. Ověřit stav Bridge (24h monitoring po F3-01)
# SELECT status, COUNT(*) FROM bridge_sync_log WHERE created_at > DATEADD(hour,-24,GETUTCDATE()) GROUP BY status
# Musí být success, žádné failed

# 3. Ověřit, že GAIA ECU pipeline funguje (nezávisle na Pipedrive)
# ECU zpracování (file processing) nesmí záviset na Pipedrive
# Zkontrolovat: app/system/gaia_binary_file.py, app/system/cumulus_api.py
# Tyto komponenty volají Cumulus API a GAIA interní systémy — NE Pipedrive

# 4. Záloha API tokenů (na bezpečné místo — NE do repozitáře)
# POZOR: Nikdy nezapisujte API tokeny do tohoto dokumentu ani do žádného souboru v repozitáři!
# Tokeny jsou v GAIA source kódu (pipe_partner_cron.py, pipe_transfer.py).
# Postup zálohy:
#   1. Otevřít KeePass (zálohovací vault XTuning)
#   2. Přidat záznamy: "Pipedrive CE API", "Pipedrive US API", "Pipedrive PL API"
#   3. Hodnoty tokenů zkopírovat přímo ze zdrojového kódu GAIA (NE přes schránku systému)
# Po archivaci v KeePass zvažte reset tokenů v Pipedrive UI (Settings → API).

# 5. Záloha pipe_deal tabulky (poslední stav)
# POZOR: Heslo zadejte interaktivně po promptu — NIKDY nezadávejte heslo přímo na příkazové řádce
export GAIA_DB_HOST=172.24.0.12  # nebo dohledat v KeePass → "GAIA MySQL host"
mkdir -p exports
mysqldump -h "$GAIA_DB_HOST" -u gaia_user --password gaia pipe_deal > "exports/gaia-pipe-backup-$(date +%Y%m%d_%H%M).sql"
# Ověřit zálohu:
tail -1 "exports/gaia-pipe-backup-$(date +%Y%m%d)_"*.sql  # musí obsahovat "-- Dump completed"
```

---

## Postup vypnutí

### Krok 1: Deaktivace pipe_transfer.py v GAIA kódu

`pipe_transfer.py` je volaný z GAIA aplikace (ne jako standalone cron). Způsob deaktivace závisí na tom, kde je volán.

```bash
# Identifikovat volání pipe_transfer.py v GAIA kódu:
grep -rn "pipe_transfer\|PipeTransfer\|pipeProcess" /path/to/gaia/app/ --include="*.py"
# Záznamy ukázat správci GAIA — on ví, v které Flask route se volá
```

**Možnosti deaktivace (zvolit jednu):**

**Varianta A — Feature flag v konfiguraci (preferovaná, bezpečnější):**
```python
# Přidat do svgConfig nebo config souboru:
# PIPEDRIVE_PUSH_ENABLED = False
# V pipe_transfer.py na začátku pipeProcess():
# if not svgConfig.getPipedrivePushEnabled():
#     return
```
*Tato varianta vyžaduje zásah do GAIA kódu — koordinovat se správcem GAIA.*

**Varianta B — Revokace API tokenů v Pipedrive (nejbezpečnější, nejnevratnější):**
```
1. Přihlásit se do Pipedrive CE: https://agroecopowerltd-pobocka.pipedrive.com
2. Settings → Personal preferences → API
3. Vygenerovat nový token (starý přestane fungovat)
4. Opakovat pro US a PL instance
5. pipe_transfer.py začne dostávat 401 Unauthorized → operace selžou, ale GAIA ECU pipeline pokračuje
```
*Nevýhoda: GAIA bude logovat API chyby — monitorovat a potvrdit, že nejde o kritické chyby.*

**Varianta C — Webhook deaktivace v Pipedrive UI:**
```
1. Pipedrive CE → Settings → Webhooks → smazat všechny webhooky směřující na GAIA
2. Opakovat pro US a PL
3. pipedrive_webhooks.py přestane dostávat eventy automaticky
```

### Krok 2: Deaktivace Pipedrive webhooků (pipedrive_webhooks.py)

Webhooky jsou konfigurovány na straně Pipedrive. Po vypnutí Pipedrive přestanou chodit automaticky. Ale pro čisté vypnutí:

```
1. Pipedrive CE (agroecopowerltd-pobocka.pipedrive.com):
   → Settings → Tools & integrations → Webhooks
   → Smazat všechny webhooky s GAIA URL (endpoint pro `deleted.organization`)

2. Totéž pro US (agroecopowerltd-usa.pipedrive.com)

3. Totéž pro PL (agroecopowerpl.pipedrive.com)
```

**DŮSLEDEK:** Po tomto kroku se smazání organizace v Pipedrive NEBUDE propagovat do Partner3 přes GAIA webhook. Bridge tuto funkci nepřebírá — smazání firem řeší `ff.company.disabled` z FieldForce.

### Krok 3: Ověření, že GAIA ECU pipeline funguje

```bash
# GAIA ECU zpracování (chiptuning soubory) musí fungovat bez Pipedrive:
# Zkontrolovat klíčové soubory:
grep -n "pipedrive\|_apiToken\|pipe_transfer" /path/to/gaia/app/system/gaia_binary_file.py
grep -n "pipedrive\|_apiToken\|pipe_transfer" /path/to/gaia/app/system/cumulus_api.py
# Pokud je výstup prázdný → tyto komponenty nezávisí na Pipedrive ✅

# Otestovat zpracování vzorového souboru na TEST prostředí
# (dle standardního GAIA testovacího postupu)
```

### Krok 4: Ověření v bridge_sync_log po deaktivaci

```sql
-- Ověřit, že Bridge stále zpracovává zprávy (15 min po deaktivaci Pipedrive push)
SELECT TOP 20 * FROM bridge_sync_log ORDER BY created_at DESC;

-- Nesmí přibývat záznamy s error_message obsahující 'Pipedrive'
-- Bridge komunikuje výhradně se Service Bus a Partner3 MySQL
```

---

## Monitoring po vypnutí (24 hodin)

```bash
# Každou hodinu prvních 8 hodin:

# 1. GAIA aplikace loguje chyby kvůli Pipedrive?
tail -f /var/log/gaia/app.log | grep -i "pipedrive\|API\|401\|403"

# 2. ECU processing funguje?
# Ověřit počet úspěšně zpracovaných souborů za hodinu (dle GAIA interního dashboardu)

# 3. Bridge bez chyb?
# SELECT status, COUNT(*) FROM bridge_sync_log WHERE created_at > DATEADD(minute,-60,GETUTCDATE()) GROUP BY status

# 4. Partner3 tbl_client se nezmění kvůli GAIA (pipe_transfer přestalo zapisovat)
# Nové záznamy musí mít ff_sync_source='FF' (Bridge), ne NULL nebo 'PIPE'
mysql -h "$GAIA_DB_HOST" -u gaia_user --password partner_cz -e \
  "SELECT ff_sync_source, COUNT(*) FROM tbl_client WHERE client_date > NOW()-INTERVAL 1 HOUR GROUP BY ff_sync_source"
```

---

## Rollback (pokud nutný)

**Varianta A (feature flag):**
```python
# Vrátit PIPEDRIVE_PUSH_ENABLED = True v konfiguraci
```

**Varianta B (revokace tokenů) — NEVRATNÁ, tokeny nelze obnovit:**
```
# Rollback Varianty B vyžaduje tyto kroky:
# 1. Přihlásit se do Pipedrive UI (CE/US/PL) jako admin
# 2. Settings → Personal preferences → API → vygenerovat nový token
#    (starý token je okamžitě zneplatněn — NELZE ho obnovit)
# 3. Aktualizovat nový token v GAIA source kódu na 3 místech:
#    - cron/pipe_partner_cron.py (self._apiToken pro CE/US/PL)
#    - app/system/pipe_transfer.py (pipeProcess() pro cz/us/pl)
# 4. Restart/reload GAIA Flask aplikace
# 5. Ověřit, že GAIA loguje úspěšné Pipedrive API volání (200 OK)
# UPOZORNĚNÍ: Tento rollback trvá 30–60 minut a vyžaduje přítomnost GAIA správce.
```

**Varianta C (webhooky):**
```
# V Pipedrive UI znovu vytvořit webhooky na GAIA URL endpoint
```

---

## Acceptance kritérium F3-02

- [ ] `pipe_transfer.py` neodesílá data do Pipedrive (ověřit Pipedrive organizace — nesmí přibývat nové z Partner3)
- [ ] Pipedrive webhooky deaktivovány (Pipedrive UI → Webhooks — prázdný seznam)
- [ ] GAIA ECU file processing funguje normálně (testovací zpracování proběhne bez chyby)
- [ ] 24 hodin po vypnutí: GAIA aplikační log bez Pipedrive API chyb (nebo pouze ignorovatelné 401)
- [ ] Bridge stále zpracovává Service Bus zprávy bez výpadků
- [ ] Nové firmy v Partner3 mají `ff_sync_source='FF'` (přicházejí přes Bridge, ne z Pipedrive)

---

## Závislosti a pořadí

```
F3-01 (pull vypnutí) → F3-02 (tento runbook) → F3-03 (Partner3 FF ochrana) → F3-04 (validace + formální vypnutí Pipedrive)
```

**POZOR F3-04:** Po F3-02 je nutno v F3-04 formálně zrušit Pipedrive subscription (CE, US, PL) a archivovat API tokeny do KeePass (ne do repozitáře).

---

## Komponenty MIMO scope (NEDEAKTIVOVAT)

```
✅ app/system/gaia_binary_file.py  — ECU file processing, NE Pipedrive
✅ app/system/cumulus_api.py       — Cumulus API, NE Pipedrive
✅ app/system/gaia_common.py       — Obecné utility, NE Pipedrive
✅ app/system/partner_order.py     — Čtení/zápis tbl_order, NE Pipedrive přímé
✅ app/system/gaia_sendmail.py     — Emailové notifikace, NE Pipedrive
```

---

*Analyzováno z: `C:\WORK-XTUNING\REPO\gaia3\app\system\pipe_transfer.py` (1311 řádků) a `app/gaia_modules/pipedrive_webhooks.py` (40 řádků).*
*Vytvořeno: 2026-03-28 — Claude, read-only analýza GAIA kódu.*
