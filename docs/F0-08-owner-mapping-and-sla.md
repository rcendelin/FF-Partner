# F0-08 — Owner Mapping, SLA a rozhodnutí o pobočkách

**Fáze:** Fáze 0 — Příprava
**Status:** Konfigurace připravena, vyžaduje vyplnění reálnými hodnotami (F0-01 connection strings)
**Prerekvizita:** F0-01 (přístup k FieldForce Azure SQL a Partner3 MySQL)

---

## (a) Owner Mapping — FieldForce User.Id → Partner3 id_owner

### Konfigurace

Owner mapping se konfiguruje v `appsettings.json` pod klíčem `OwnerMapping`.
Šablona je v [config/owner-mapping.template.json](../config/owner-mapping.template.json).

```json
{
  "OwnerMapping": {
    "DefaultOwnerId": 1,
    "Mappings": {
      "<FieldForce User.Id (GUID)>": <Partner3 id_owner (INT)>
    }
  }
}
```

**`DefaultOwnerId`** — fallback `id_owner` pokud:
- FieldForce zpráva neobsahuje `AssignedUserId`
- `AssignedUserId` není v tabulce `Mappings`
- Doporučeno: ID "systémového" vlastníka (admin nebo obecný obchodník)
- Pokud `null` → `id_owner` bude `NULL` v `tbl_client` (technicky přípustné)

### SQL dotazy pro sestavení mappingu

**1. Načíst FieldForce obchodníky (spustit v Azure SQL / SSMS):**
```sql
SELECT
    LOWER(CAST(Id AS VARCHAR(36))) AS ff_user_id,
    Email,
    FirstName + ' ' + LastName AS FullName,
    IsActive
FROM dbo.Users
WHERE IsActive = 1
ORDER BY LastName;
```

**2. Načíst Partner3 vlastníky (spustit v MySQL — libovolný region):**
```sql
-- Varianta A — z tbl_client (pokud neexistuje users tabulka)
SELECT DISTINCT
    id_owner,
    MAX(client_firm) AS example_client
FROM tbl_client
WHERE id_owner IS NOT NULL
GROUP BY id_owner
ORDER BY id_owner;

-- Varianta B — pokud existuje tbl_users nebo tbl_owner
-- (ověřit existenci: SHOW TABLES LIKE 'tbl_user%')
SELECT id, name, email FROM tbl_users ORDER BY name;
```

**3. Spárovat dle emailu nebo jména** — sestavit mapping tabulku:

| FieldForce Email | FieldForce User.Id | Partner3 id_owner |
|---|---|---|
| obchodnik@xtuning.cz | `<GUID z kroku 1>` | `<id z kroku 2>` |
| dalsi@xtuning.cz | `<GUID z kroku 1>` | `<id z kroku 2>` |

**4. Vyplnit `appsettings.json` na produkčním serveru:**
```json
{
  "OwnerMapping": {
    "DefaultOwnerId": 1,
    "Mappings": {
      "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx": 10,
      "yyyyyyyy-yyyy-yyyy-yyyy-yyyyyyyyyyyy": 11
    }
  }
}
```

### Distribuce konfigurace

- **Produkce:** Konfiguraci předat přes Docker secret nebo environment variable (ne do repozitáře)
- **Testování:** Lze použít `appsettings.Development.json` s testovacími hodnotami

### Implementace v Bridge

- `OwnerMappingService` ([src/Bridge.Application/Services/OwnerMappingService.cs](../src/Bridge.Application/Services/OwnerMappingService.cs)) — načítá konfiguraci při startu
- Po změně mappingu je nutný restart Bridge (konfiguraci lze hot-reload přes `IOptionsMonitor` v budoucí fázi)
- `OwnerMappingOptions.DefaultOwnerId` — fallback pro neznamé User.Id
- Registrace v DI: `Program.cs` řádek 87

---

## (b) SLA — definice a Application Insights monitoring

### SLA thresholds (dle CLAUDE.md sekce 15)

| Operace | Max latence | Bridge topic |
|---|---|---|
| Nový klient FieldForce → viditelný v Partner3 | **5 minut** | `ff.company.sync` (CREATE) |
| Editace adresy → propagace do Partner3 | **15 minut** | `ff.company.sync` (UPDATE) |
| Deaktivace klienta → propagace | **2 minuty** | `ff.company.disabled` |
| Nová objednávka v Partner3 → Event v FieldForce | **5 minut** | `bridge.order.created` |

### Application Insights Alert Queries

Tyto KQL dotazy jsou připraveny pro Azure Monitor Alerts. Nastavit po deployi Bridge na produkci (F0-07 infrastruktura).

**Alert 1 — P95 latence CREATE > 4 minuty (warning před SLA porušením):**
```kql
customMetrics
| where timestamp > ago(15m)
| where name == "bridge.sync.duration"
| where customDimensions.operation == "create"
| summarize
    p95 = percentile(value, 95),
    p99 = percentile(value, 99)
    by bin(timestamp, 5m)
| where p95 > 240000  -- 240 000 ms = 4 minuty (SLA = 5min)
| project timestamp, p95_ms = p95, p99_ms = p99,
    alert_message = strcat("P95 CREATE latence: ", round(p95/1000, 1), "s (SLA = 5min)")
```

**Alert 2 — Error rate > 5% za 15 minut:**
```kql
customMetrics
| where timestamp > ago(15m)
| where name == "bridge.sync.errors"
| summarize
    error_count = sum(value)
    by bin(timestamp, 15m)
| join kind=inner (
    customMetrics
    | where timestamp > ago(15m)
    | where name == "bridge.sync.duration"
    | summarize total = count() by bin(timestamp, 15m)
) on timestamp
| extend error_rate_pct = error_count * 100.0 / total
| where error_rate_pct > 5
| project timestamp, error_count, total, error_rate_pct
```

**Alert 3 — Dead-letter queue depth > 0 (přes custom metriku Bridge):**
```kql
customMetrics
| where timestamp > ago(10m)
| where name == "bridge.dlq.depth"
| summarize max_depth = max(value) by bin(timestamp, 5m)
| where max_depth > 0
| project timestamp, max_depth
```
*Poznámka: Bridge publikuje `bridge.dlq.depth` metriku přes `DlqMonitorService` (F1-08). Alternativa: Azure Monitor nativní metrika `DeadletteredMessages` přímo na Service Bus namespace (bez závislosti na Bridge log).*

**Alert 4 — Deaktivace klienta P95 > 90 sekund (warning před 2min SLA):**
```kql
customMetrics
| where timestamp > ago(15m)
| where name == "bridge.sync.duration"
| where customDimensions.operation == "disable"
| summarize p95 = percentile(value, 95) by bin(timestamp, 5m)
| where p95 > 90000  -- 90 000 ms = 90 sekund (SLA = 2min)
```

### Doporučená nastavení Azure Monitor Alerts

| Alert | Threshold | Severity | Action |
|---|---|---|---|
| Error rate | > 5% / 15 min | Sev 1 | Email + SMS on-call |
| DLQ depth | > 0 | Sev 2 | Email on-call |
| P95 CREATE latence | > 240s | Sev 2 | Email on-call |
| P95 DISABLE latence | > 90s | Sev 2 | Email on-call |
| Bridge health endpoint | DOWN | Sev 1 | Email + SMS on-call |

**Nastavení v Azure Portal:**
```
Azure Monitor → Alerts → Create → Log alert
Scope: Application Insights instance (zjistit název z Azure Portalu — obvykle prefix appi- nebo ai-, NE kv- který je Key Vault)
Signal: Custom log search
Evaluation frequency: 5 minutes
Lookback period: 15 minutes (pro error rate)
```

### Custom metriky v Application Insights

Bridge publikuje tyto metriky přes `IBridgeMetrics` ([src/Bridge.Application/](../src/Bridge.Application/)):

| Metrika | Dimensions | Popis |
|---|---|---|
| `bridge.sync.duration` | `operation`, `region`, `status` | Latence zpracování zprávy (ms) |
| `bridge.sync.errors` | `operation`, `region`, `errorCode` | Počet chyb |

Dimenze `operation`:
- `create` — nový klient (`ff.company.sync` CREATE)
- `update` — aktualizace klienta
- `disable` — deaktivace (`ff.company.disabled`)
- `contact_update` — aktualizace kontaktu
- `owner_change` — změna vlastníka
- `region_change` — přesun mezi regiony (saga)
- `order_poll` — polling tbl_order (Fáze 4)

---

## (c) Rozhodnutí o pobočkách

**Rozhodnutí (2026-03-27): Varianta B — pobočky ignorovány v Fázi 1**

- `tbl_client_branch` se v Fázi 1 **nesynchronizuje**
- Pobočky zůstávají editovatelné v Partner3 UI (beze změny)
- Bridge nepropaguje změny poboček z FieldForce do Partner3
- Plánováno jako samostatná fáze **Fáze 2.5+** (po stabilizaci Fáze 1)

**Dopady:**
- Pokud FieldForce Company má pobočky, v Partner3 se zobrazí pouze hlavní adresa
- Partneři mohou pobočky spravovat přímo v Partner3 portálu
- Bridge nebude posílat `ff.branch.*` eventy (tyto topicy neexistují)

**Jak to ovlivní Bridge:**
- `CompanySyncConsumer` zpracovává pouze hlavní firmu (`Company`) — bez `Branch` entit
- `bridge_id_mapping` neobsahuje záznamy pro pobočky (pouze `entity_type = 'client'`)
- Pokud v budoucnu bude implementována Fáze 2.5: přidat `entity_type = 'branch'` do `bridge_id_mapping`

---

## Checklist F0-08

- [ ] Získat User seznam z FieldForce (SQL dotaz výše — vyžaduje F0-01)
- [ ] Získat id_owner seznam z Partner3 MySQL (SQL dotaz výše — vyžaduje F0-01)
- [ ] Sestavit mapping tabulku (manuální párování dle emailu)
- [ ] Vyplnit `OwnerMapping.Mappings` v produkční konfiguraci Bridge
- [ ] Nastavit `OwnerMapping.DefaultOwnerId` (systémový owner pro neznámé uživatele)
- [ ] Po deployi Bridge: nastavit Azure Monitor Alerts (KQL dotazy výše)
- [ ] Otestovat: CompanySyncMessage s platným `AssignedUserId` → ověřit `id_owner` v tbl_client

---

*Vytvořeno: 2026-03-28 — na základě CLAUDE.md sekce 8 a 15, OwnerMappingService implementace.*
