# F1-12 · Go/No-Go Checklist — Fáze 1 → Fáze 2

> Spustit před zahájením Fáze 2 (Contact sync + Owner sync + Bulk migrace).
> Všechna kritéria musí být splněna. Jakýkoli selhávající bod = **NO-GO**.

---

## Prerekvizity před validací

- [ ] Bulk migrace (F2-03) proběhla — všechny existující FieldForce firmy byly synchronizovány
- [ ] Bridge běží ≥ 24 hodin v produkčním prostředí bez restartu
- [ ] Dead-letter queue monitorování je aktivní v Application Insights

---

## 1. Datová konzistence — Partner3 MySQL

### 1.1 Sync rate ≥ 95 %

```sql
-- Spustit na každé regionální DB (CZ, PL, HU, US)
SELECT
    COUNT(*) AS total_ff_companies,
    SUM(CASE WHEN ff_sync_source = 'FF' AND client_disable = 0 THEN 1 ELSE 0 END) AS synced_active,
    ROUND(
        100.0 * SUM(CASE WHEN ff_sync_source = 'FF' AND client_disable = 0 THEN 1 ELSE 0 END)
        / NULLIF(COUNT(*), 0),
        2
    ) AS sync_rate_pct
FROM tbl_client
WHERE ff_company_id IS NOT NULL;
```

**Kritérium:** `sync_rate_pct ≥ 95.00`

### 1.2 Žádné duplicitní ff_company_id

```sql
-- Spustit na každé regionální DB
SELECT ff_company_id, COUNT(*) AS count
FROM tbl_client
WHERE ff_company_id IS NOT NULL
  AND ff_sync_source = 'FF'
GROUP BY ff_company_id
HAVING COUNT(*) > 1;
```

**Kritérium:** Prázdný výsledek (0 řádků)

### 1.3 Historické Pipedrive záznamy zachovány

```sql
-- Ověřit, že Bridge nemodifikoval pipe_id a pipeType u Pipedrive záznamů
SELECT COUNT(*) AS suspicious
FROM tbl_client
WHERE ff_sync_source = 'PIPE'
  AND (pipe_id IS NULL OR pipeType IS NULL)
  AND last_ff_sync_at IS NOT NULL;  -- Bridge by neměl mít last_ff_sync_at u PIPE záznamů
```

**Kritérium:** `suspicious = 0`

### 1.4 Namátkový vzorek — 50 firem per region

Vzít 50 náhodných firem z každé regionální DB a ověřit konzistenci klíčových polí
s odpovídajícím záznamem v FieldForce (client_firm, client_ic, client_country_short, client_right).

```sql
SELECT idclient, ff_company_id, client_firm, client_ic, client_country_short,
       client_right, ff_sync_source, data_owner, last_ff_sync_at
FROM tbl_client
WHERE ff_sync_source = 'FF'
ORDER BY RAND()
LIMIT 50;
```

**Kritérium:** ≥ 48 z 50 firem (96 %) odpovídá datům z FieldForce

---

## 2. Azure SQL — bridge_id_mapping konzistence

### 2.1 Celkový počet mappingů

```sql
SELECT partner_region, COUNT(*) AS mapped_count
FROM bridge_id_mapping
WHERE entity_type = 'client'
GROUP BY partner_region
ORDER BY partner_region;
```

**Kritérium:** Počty odpovídají očekávané distribuci firem per region.

### 2.2 Žádné duplicitní ff_company_id

```sql
SELECT ff_company_id, COUNT(*) AS count
FROM bridge_id_mapping
WHERE entity_type = 'client'
GROUP BY ff_company_id
HAVING COUNT(*) > 1;
```

**Kritérium:** Prázdný výsledek (0 řádků)

### 2.3 Všechny záznamy mají platný region

```sql
SELECT COUNT(*) AS invalid_region
FROM bridge_id_mapping
WHERE partner_region NOT IN ('cz', 'pl', 'hu', 'us');
```

**Kritérium:** `invalid_region = 0`

### 2.4 last_sync_direction je vždy ff_to_partner (Fáze 1)

```sql
SELECT COUNT(*) AS invalid_direction
FROM bridge_id_mapping
WHERE last_sync_direction != 'ff_to_partner';
```

**Kritérium:** `invalid_direction = 0`

---

## 3. Azure SQL — bridge_sync_log analýza

### 3.1 Chybovost za posledních 24 hodin

```sql
SELECT
    status,
    COUNT(*) AS count,
    ROUND(100.0 * COUNT(*) / SUM(COUNT(*)) OVER (), 2) AS pct
FROM bridge_sync_log
WHERE created_at >= DATEADD(HOUR, -24, GETUTCDATE())
GROUP BY status
ORDER BY count DESC;
```

**Kritérium:** `failed` < 5 % celkového počtu

### 3.2 Žádné nevyřešené PENDING_REGION_CHANGE

```sql
SELECT COUNT(*) AS pending_sagas
FROM bridge_sync_log
WHERE operation = 'pending_region_change'
  AND status != 'success';
```

**Kritérium:** `pending_sagas = 0`

### 3.3 GEO validační varování

```sql
SELECT COUNT(*) AS geo_warnings
FROM bridge_sync_log
WHERE operation = 'geo_validation_warning';
```

**Informační (ne blocker):** Pokud > 200, doporučeno opravit PSČ ve FieldForce před Fází 2.

### 3.4 Dead-letter queue hloubka

Zkontrolovat v Azure Portal → Service Bus → Topics → každý topic → Subscriptions → Dead-letter.

**Kritérium:** DLQ depth = 0 pro všechny subskripce Bridge.

---

## 4. Výkonnost (SLA)

### 4.1 Latence zpracování — Application Insights

```kusto
// Application Insights — KQL dotaz
customMetrics
| where name == "bridge.sync.duration"
| where timestamp >= ago(24h)
| summarize
    p50 = percentile(value, 50),
    p95 = percentile(value, 95),
    p99 = percentile(value, 99)
    by bin(timestamp, 1h)
| order by timestamp desc
```

**Kritérium:**
- P95 latence CREATE ≤ 5 minut (300 000 ms)
- P95 latence UPDATE ≤ 15 minut (900 000 ms)
- P95 latence DISABLE ≤ 2 minuty (120 000 ms)

### 4.2 Chybovost — Application Insights

```kusto
customMetrics
| where name == "bridge.sync.errors"
| where timestamp >= ago(24h)
| summarize error_count = sum(valueSum) by bin(timestamp, 15m)
| where error_count > 0
```

**Kritérium:** Žádný 15minutový interval s error rate > 5 %

---

## 5. Integritní testy (automatizované)

Spustit integrační testy z `Bridge.IntegrationTests`:

```bash
export BRIDGE_IT_PARTNER_CZ_CONN="..."
export BRIDGE_IT_PARTNER_PL_CONN="..."
export BRIDGE_IT_AZURE_SQL_CONN="..."
export BRIDGE_IT_GAIA_CONN="..."

dotnet test tests/Bridge.IntegrationTests \
  --filter "Category=GoNoGo" \
  --logger "console;verbosity=normal"
```

**Kritérium:** Všechny GoNoGo testy zelené (0 selhání).

---

## 6. Výstupní protokol

| Kritérium | Výsledek | Datum | Zodpovědná osoba |
|---|---|---|---|
| 1.1 Sync rate ≥ 95 % | ☐ PASS / ☐ FAIL | | |
| 1.2 Žádné duplicity v tbl_client | ☐ PASS / ☐ FAIL | | |
| 1.3 Pipedrive hodnoty zachovány | ☐ PASS / ☐ FAIL | | |
| 1.4 Namátkový vzorek 50 firem | ☐ PASS / ☐ FAIL | | |
| 2.1 Počty mappingů odpovídají | ☐ PASS / ☐ FAIL | | |
| 2.2 Žádné duplicity v mapping | ☐ PASS / ☐ FAIL | | |
| 2.3 Platné regiony v mapping | ☐ PASS / ☐ FAIL | | |
| 2.4 Správný sync_direction | ☐ PASS / ☐ FAIL | | |
| 3.1 Chybovost < 5 % za 24h | ☐ PASS / ☐ FAIL | | |
| 3.2 Žádné pending sagi | ☐ PASS / ☐ FAIL | | |
| 3.4 DLQ hloubka = 0 | ☐ PASS / ☐ FAIL | | |
| 4.1 Latence v SLA | ☐ PASS / ☐ FAIL | | |
| 4.2 Error rate < 5 % | ☐ PASS / ☐ FAIL | | |
| 5. GoNoGo testy zelené | ☐ PASS / ☐ FAIL | | |

### Rozhodnutí

- **GO** — všechna kritéria splněna → zahájit Fázi 2
- **NO-GO** — jedno nebo více kritérií selhalo → řešit před Fází 2

---

*Dokument připraven: 2026-03-28 · F1-12 · FF-Partner Bridge*
