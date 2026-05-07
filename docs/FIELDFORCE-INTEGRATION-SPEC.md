# FieldForce Integration Spec pro FF-Partner Bridge

> **Cílový příjemce:** Claude Code (nebo vývojář) pracující na FieldForce CRM (.NET 8, Azure).
> **Účel:** Implementovat odesílání a příjem Service Bus zpráv pro synchronizaci s Partner3 přes Bridge.
> **Verze:** 1.0 (2026-03-28)

---

## 1. Kontext — co je Bridge a proč potřebujeme integraci

FF-Partner Bridge je on-premise .NET 8 služba, která nahrazuje Pipedrive jako zdroj klientských dat pro legacy systém Partner3. Bridge běží v síti XTuning a komunikuje s FieldForce **výhradně přes Azure Service Bus** (sdílený namespace).

```
FieldForce (Azure) ──Service Bus──► Bridge (on-premise) ──► Partner3 MySQL (4 regiony)
                    ◄──Service Bus──                      ◄── polling tbl_order
```

**FieldForce je "master" pro firemní data.** Kdykoli se v FieldForce změní Company, Contact, Owner nebo se firma deaktivuje, FieldForce MUSÍ poslat zprávu na příslušný Service Bus topic. Bridge ji zpracuje a zapíše do Partner3.

---

## 2. Co FieldForce musí implementovat

### 2.1 Outbound (FieldForce → Bridge) — FÁZE 1-3

FieldForce musí **publishovat zprávy** na 4 Service Bus topics při těchto doménových událostech:

| Událost v FieldForce | Topic | Message typ |
|---|---|---|
| Vytvoření firmy | `ff.company.sync` | `CompanySyncMessage` (Action="Create") |
| Úprava firmy (adresa, IČO, DIČ, role...) | `ff.company.sync` | `CompanySyncMessage` (Action="Update") |
| Změna emailu/telefonu primárního kontaktu firmy | `ff.contact.updated` | `ContactUpdatedMessage` |
| Přeřazení obchodníka (owner) firmy | `ff.company.owner-changed` | `CompanyOwnerChangedMessage` |
| Deaktivace firmy | `ff.company.disabled` | `CompanyDisabledMessage` |

### 2.2 Inbound (Bridge → FieldForce) — FÁZE 1-3

FieldForce musí **konzumovat zprávy** z 3 Service Bus topics:

| Topic | Message typ | Reakce ve FieldForce |
|---|---|---|
| `bridge.company.synced` | `CompanySyncedResponse` | Uložit PartnerClientId + PartnerRegion ke Company |
| `bridge.company.sync-failed` | `CompanySyncFailedMessage` | Logovat chybu, zobrazit uživateli (volitelně) |
| `bridge.company.conflict` | `CompanyConflictMessage` | Logovat, případně notifikovat admina |

### 2.3 Inbound (Bridge → FieldForce) — FÁZE 4 (zpětný tok objednávek)

FieldForce musí **konzumovat zprávy** ze 4 dalších Service Bus topics:

| Topic | Message typ | Reakce ve FieldForce |
|---|---|---|
| `bridge.order.created` | `OrderCreatedMessage` | Vytvořit Event/Activity na Company |
| `bridge.order.state-changed` | `OrderStateChangedMessage` | Aktualizovat Event/Activity stav |
| `bridge.order.completed` | `OrderCompletedMessage` | Event + Machine enrichment (VIN match) |
| `bridge.order.cancelled` | `OrderCancelledMessage` | Event + aktualizace Company.Stage |

---

## 3. Přesné kontrakty zpráv — OUTBOUND (FieldForce → Bridge)

### 3.1 CompanySyncMessage

**Topic:** `ff.company.sync`
**Kdy publishovat:** Při KAŽDÉM vytvoření nebo editaci Company entity (save v CQRS command handleru).

```csharp
public sealed class CompanySyncMessage
{
    /// <summary>Unikátní ID zprávy — použijte Guid.NewGuid().ToString()</summary>
    public required string MessageId { get; init; }

    /// <summary>Čas odeslání — DateTimeOffset.UtcNow</summary>
    public required DateTimeOffset SentAt { get; init; }

    /// <summary>"Create" pro novou firmu, "Update" pro editaci</summary>
    public required string Action { get; init; }

    /// <summary>Company.Id (GUID) — primární identifikátor firmy ve FieldForce</summary>
    public required Guid CompanyId { get; init; }

    /// <summary>Company.Name — název firmy (povinný)</summary>
    public required string CompanyName { get; init; }

    /// <summary>IČO firmy (může být null)</summary>
    public string? Ico { get; init; }

    /// <summary>DIČ firmy (může být null)</summary>
    public string? Dic { get; init; }

    /// <summary>ISO kód země — "CZ", "PL", "HU", "US", "SK", "DE" atd. (povinný, uppercase)</summary>
    public required string CountryCode { get; init; }

    /// <summary>Ulice</summary>
    public string? Street { get; init; }

    /// <summary>Město</summary>
    public string? City { get; init; }

    /// <summary>PSČ (formát dle země — Bridge validuje)</summary>
    public string? PostalCode { get; init; }

    /// <summary>Kraj / State (český název nebo mezinárodní)</summary>
    public string? State { get; init; }

    /// <summary>Okres / County</summary>
    public string? County { get; init; }

    /// <summary>Email primárního kontaktu (Company.PrimaryContact.Email)</summary>
    public string? PrimaryContactEmail { get; init; }

    /// <summary>Telefon primárního kontaktu (Company.PrimaryContact.Phone)</summary>
    public string? PrimaryContactPhone { get; init; }

    /// <summary>Role firmy — "Customer", "Dealer", nebo "Oem" (string, case-insensitive)</summary>
    public required string CompanyRole { get; init; }

    /// <summary>FieldForce User.Id přiřazeného obchodníka (může být null)</summary>
    public Guid? AssignedUserId { get; init; }

    /// <summary>Historické Pipedrive ID (pro migraci, jinak null)</summary>
    public long? PipedriveId { get; init; }
}
```

**Mapování z FieldForce entit:**

| Message property | FieldForce zdroj | Poznámka |
|---|---|---|
| `CompanyId` | `Company.Id` | GUID — nikdy ne int |
| `CompanyName` | `Company.Name` | Povinné, nesmí být prázdné |
| `Ico` | `Company.RegistrationNumber` nebo `Company.Ico` | Dle vaší entity |
| `Dic` | `Company.TaxNumber` nebo `Company.Dic` | Dle vaší entity |
| `CountryCode` | `Company.Address.Country.IsoCode` | ISO 3166-1 alpha-2, UPPERCASE |
| `Street` | `Company.Address.Street` | |
| `City` | `Company.Address.City` | |
| `PostalCode` | `Company.Address.PostalCode` | Bez mezer, Bridge normalizuje |
| `State` | `Company.Address.State` | Plný název kraje |
| `County` | `Company.Address.County` | Plný název okresu |
| `PrimaryContactEmail` | `Company.PrimaryContact.Email` | Primární kontakt firmy |
| `PrimaryContactPhone` | `Company.PrimaryContact.Phone` | Primární kontakt firmy |
| `CompanyRole` | `Company.Role.ToString()` | Enum → string: "Customer"/"Dealer"/"Oem" |
| `AssignedUserId` | `Company.AssignedUser.Id` | FF User GUID |
| `PipedriveId` | `Company.PipedriveId` | `long?` — jen pokud existuje |

**Action logika:**
```csharp
Action = isNewCompany ? "Create" : "Update"
```

### 3.2 ContactUpdatedMessage

**Topic:** `ff.contact.updated`
**Kdy publishovat:** Při změně emailu nebo telefonu primárního kontaktu firmy (NE při změně neprimárního kontaktu).

```csharp
public sealed class ContactUpdatedMessage
{
    public required string MessageId { get; init; }
    public required DateTimeOffset SentAt { get; init; }

    /// <summary>Company.Id (GUID) — ke které firmě kontakt patří</summary>
    public required Guid FfCompanyId { get; init; }

    /// <summary>Nový email primárního kontaktu (null = nezměněn)</summary>
    public string? Email { get; init; }

    /// <summary>Nový telefon primárního kontaktu (null = nezměněn)</summary>
    public string? Phone { get; init; }
}
```

**Kdy posílat a kdy ne:**
- **ANO:** Uživatel změní email primárního kontaktu firmy Acme → publish
- **ANO:** Uživatel změní telefon primárního kontaktu → publish
- **NE:** Změní se email sekundárního kontaktu → nic neposílat
- **NE:** Změní se kontaktní údaje v rámci `CompanySyncMessage` (tam je email/phone už obsažen) — neposílat duplicitně, pokud jde o součást úpravy celé firmy

### 3.3 CompanyOwnerChangedMessage

**Topic:** `ff.company.owner-changed`
**Kdy publishovat:** Při přeřazení firmy jinému obchodníkovi (change of Company.AssignedUser).

```csharp
public sealed class CompanyOwnerChangedMessage
{
    public required string MessageId { get; init; }
    public required DateTimeOffset SentAt { get; init; }

    /// <summary>Company.Id (GUID)</summary>
    public required Guid FfCompanyId { get; init; }

    /// <summary>FieldForce User.Id nového vlastníka (GUID)</summary>
    public required Guid NewOwnerUserId { get; init; }

    /// <summary>FieldForce User.Id předchozího vlastníka (null pokud nebyl přiřazen)</summary>
    public Guid? PreviousOwnerUserId { get; init; }
}
```

### 3.4 CompanyDisabledMessage

**Topic:** `ff.company.disabled`
**Kdy publishovat:** Při deaktivaci (soft delete) firmy ve FieldForce.

```csharp
public sealed class CompanyDisabledMessage
{
    public required string MessageId { get; init; }
    public required DateTimeOffset SentAt { get; init; }

    /// <summary>Company.Id (GUID) deaktivované firmy</summary>
    public required Guid FfCompanyId { get; init; }
}
```

---

## 4. Přesné kontrakty zpráv — INBOUND (Bridge → FieldForce)

### 4.1 CompanySyncedResponse (potvrzení úspěchu)

**Topic:** `bridge.company.synced`
**Subscription:** Vytvořte subscription `fieldforce-main` (nebo jiný název dle konvence FF).

```csharp
public sealed class CompanySyncedResponse
{
    public required string MessageId { get; init; }
    public required DateTimeOffset SentAt { get; init; }

    /// <summary>Company.Id, ke které se sync vztahuje</summary>
    public required Guid FfCompanyId { get; init; }

    /// <summary>Partner3 tbl_client.idclient — uložte ke Company pro zpětné propojení</summary>
    public required int PartnerClientId { get; init; }

    /// <summary>Region: "cz", "pl", "hu", "us" (lowercase)</summary>
    public required string PartnerRegion { get; init; }

    /// <summary>
    /// Akce, která proběhla: "Create", "Update", "ContactUpdate", "OwnerChange", "Disable"
    /// </summary>
    public required string Action { get; init; }
}
```

**Reakce ve FieldForce:**
```csharp
// Při přijetí CompanySyncedResponse:
// 1. Najít Company dle FfCompanyId
// 2. Uložit PartnerClientId a PartnerRegion ke Company
//    (nové sloupce v Company tabulce — viz sekce 7)
// 3. Logovat úspěšný sync
```

### 4.2 CompanySyncFailedMessage (chyba synchronizace)

**Topic:** `bridge.company.sync-failed`

```csharp
public sealed class CompanySyncFailedMessage
{
    public required string MessageId { get; init; }
    public required DateTimeOffset SentAt { get; init; }

    /// <summary>Company.Id</summary>
    public required Guid FfCompanyId { get; init; }

    /// <summary>Kód chyby — viz tabulka níže</summary>
    public required string ErrorCode { get; init; }

    /// <summary>Lidsky čitelný popis chyby</summary>
    public required string ErrorMessage { get; init; }

    /// <summary>MessageId původní zprávy, která selhala</summary>
    public string? OriginalMessageId { get; init; }
}
```

**Chybové kódy:**

| ErrorCode | Význam | Doporučená reakce ve FieldForce |
|---|---|---|
| `UNSUPPORTED_REGION` | Země firmy nemá mapování na region (IT, GB, ES...) | Zobrazit uživateli varování, firma nebude v Partner3 |
| `DESERIALIZATION_ERROR` | Bridge nedokázal přečíst JSON zprávu | Bug ve FieldForce serializaci — logovat, investigovat |
| `MALFORMED_JSON` | Nevalidní JSON | Dtto — bug |
| `NO_MAPPING` | Firma nebyla nalezena v bridge_id_mapping | Firma ještě nebyla synchronizována (Create neproběhl) |
| `ORPHANED_MAPPING` | Mapping existuje, ale tbl_client záznam chybí | Datová nekonzistence — eskalovat |
| `REGION_CHANGE_FAILED` | Saga přesunu firmy mezi regiony selhala | Retry nebo manuální zásah |
| `OWNER_NOT_MAPPED` | FieldForce UserId nemá mapování na Partner3 id_owner | IT musí přidat mapování |
| `CLIENT_NOT_FOUND` | tbl_client záznam neexistuje | Datová nekonzistence |

**Reakce ve FieldForce:**
```csharp
// 1. Logovat ErrorCode + ErrorMessage + FfCompanyId
// 2. Volitelně: uložit sync status ke Company (např. SyncStatus = Failed)
// 3. Volitelně: zobrazit notifikaci v UI pro admina
// 4. OWNER_NOT_MAPPED → zvážit notifikaci IT (chybí konfigurace)
```

### 4.3 CompanyConflictMessage (konflikt)

**Topic:** `bridge.company.conflict`

```csharp
public sealed class CompanyConflictMessage
{
    public required string MessageId { get; init; }
    public required DateTimeOffset SentAt { get; init; }

    /// <summary>Company.Id</summary>
    public required Guid FfCompanyId { get; init; }
    public required int PartnerClientId { get; init; }
    public required string PartnerRegion { get; init; }

    /// <summary>Čas posledního úspěšného syncu v Partner3</summary>
    public required DateTimeOffset ExistingLastSyncAt { get; init; }

    /// <summary>SentAt příchozí zprávy, která byla zamítnuta</summary>
    public required DateTimeOffset IncomingMessageSentAt { get; init; }
}
```

**Reakce ve FieldForce:**
```csharp
// Konflikt = Bridge detekoval, že data v Partner3 byla mezitím změněna
// (např. přímou editací v Partner3 portálu).
// Bridge NEPŘEPÍŠE data — nechá Partner3 verzi.
//
// Doporučená reakce:
// 1. Logovat conflict
// 2. Volitelně: zobrazit varování v Company detailu
// 3. Uživatel musí rozhodnout — buď znovu uložit (forcne nový sync),
//    nebo akceptovat Partner3 verzi
```

### 4.4 OrderCreatedMessage (nová objednávka)

**Topic:** `bridge.order.created`

```csharp
public sealed class OrderCreatedMessage
{
    public required string MessageId { get; init; }
    public required DateTimeOffset SentAt { get; init; }

    /// <summary>Region: "cz", "pl", "hu", "us"</summary>
    public required string PartnerRegion { get; init; }

    /// <summary>Partner3 tbl_order.idorder</summary>
    public required long PartnerOrderId { get; init; }

    /// <summary>Company.Id ve FieldForce</summary>
    public required Guid FfCompanyId { get; init; }

    /// <summary>Partner3 tbl_client.idclient</summary>
    public required int PartnerClientId { get; init; }

    /// <summary>Unix timestamp vzniku objednávky (tbl_order.order_date_start)</summary>
    public required int OrderDateStart { get; init; }

    /// <summary>Stav objednávky: 7=nová, 20=realizace, 30=zrušena</summary>
    public required short OrderState { get; init; }

    /// <summary>Cena objednávky (celé číslo)</summary>
    public required int? OrderPrice { get; init; }

    // --- Vozidlo (Machine) ---
    public string? VehicleVin { get; init; }
    public string? VehicleMark { get; init; }
    public string? VehicleModel { get; init; }
    public string? VehicleType { get; init; }

    /// <summary>Kategorie vozidla — mapuje na MachineType enum</summary>
    public int? VehicleCategory { get; init; }

    /// <summary>Výkon v HP</summary>
    public int? VehiclePowerHp { get; init; }
}
```

**Reakce ve FieldForce:**
```csharp
// 1. Najít Company dle FfCompanyId
// 2. Vytvořit Event/Activity na Company:
//    - Typ: "Partner3 Order"
//    - Reference: PartnerOrderId + PartnerRegion
//    - Datum: DateTimeOffset.FromUnixTimeSeconds(OrderDateStart)
//    - Stav: mapovat OrderState (7→Nová, 20→Realizace, 30→Zrušena)
//    - Cena: OrderPrice
// 3. Pokud VehicleVin != null → pokusit se matchnout Machine dle VIN
// 4. Event je READ-ONLY v UI (uživatel nesmí editovat Partner3 objednávky z FF)
```

### 4.5 OrderStateChangedMessage (změna stavu objednávky)

**Topic:** `bridge.order.state-changed`

```csharp
public sealed class OrderStateChangedMessage
{
    public required string MessageId { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public required string PartnerRegion { get; init; }
    public required long PartnerOrderId { get; init; }
    public required Guid FfCompanyId { get; init; }
    public required int PartnerClientId { get; init; }

    /// <summary>Nový stav: 7=nová, 20=realizace, 30=zrušena</summary>
    public required short OrderState { get; init; }

    /// <summary>Uzavřeno: 0=ne, 1=ano</summary>
    public required short OrderClose { get; init; }

    /// <summary>Zaplaceno: 0=ne, 1=ano</summary>
    public required short OrderClosePay { get; init; }

    /// <summary>GAIA processing: -10=čeká, -1=chyba, 0=hotovo</summary>
    public required sbyte OrderAutomatClose { get; init; }

    /// <summary>Soft delete: 0=aktivní, 1=smazáno</summary>
    public required sbyte OrderDeactive { get; init; }
}
```

**Reakce ve FieldForce:**
```csharp
// 1. Najít existující Event dle PartnerOrderId + PartnerRegion
// 2. Aktualizovat stav Event/Activity
// 3. GAIA chyby (OrderAutomatClose = -1) → pouze logovat, NENOTIFIKOVAT obchodníka
```

### 4.6 OrderCompletedMessage (zakázka zaplacena)

**Topic:** `bridge.order.completed`

```csharp
public sealed class OrderCompletedMessage
{
    public required string MessageId { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public required string PartnerRegion { get; init; }
    public required long PartnerOrderId { get; init; }
    public required Guid FfCompanyId { get; init; }
    public required int PartnerClientId { get; init; }

    // --- Vozidlo pro Machine enrichment ---
    public string? VehicleVin { get; init; }
    public string? VehicleMark { get; init; }
    public string? VehicleModel { get; init; }
    public int? VehicleCategory { get; init; }
    public int? VehiclePowerHp { get; init; }
}
```

**Reakce ve FieldForce:**
```csharp
// 1. Aktualizovat Event stav → Completed
// 2. Aktualizovat Company.Stage → Won (pokud máte pipeline/stage logiku)
// 3. Machine enrichment:
//    a) Lookup Machine dle VehicleVin (přesná shoda)
//    b) Fallback: VehicleMark + VehicleModel + FfCompanyId
//    c) Pokud nalezen → UPDATE Machine.ChippedPowerKw (z VehiclePowerHp, převod HP→kW)
//    d) Pokud nalezen → UPDATE Machine.MachineType (z VehicleCategory → enum)
//    e) Pokud NENALEZEN → pouze logovat, NEVYTVÁŘET nový Machine záznam
```

### 4.7 OrderCancelledMessage (zakázka zrušena)

**Topic:** `bridge.order.cancelled`

```csharp
public sealed class OrderCancelledMessage
{
    public required string MessageId { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public required string PartnerRegion { get; init; }
    public required long PartnerOrderId { get; init; }
    public required Guid FfCompanyId { get; init; }
    public required int PartnerClientId { get; init; }
}
```

**Reakce ve FieldForce:**
```csharp
// 1. Aktualizovat Event stav → Cancelled
// 2. Pokud Company nemá žádné jiné aktivní objednávky → Company.Stage = Lost
// 3. Pokud Company MÁ jiné aktivní objednávky → neměnit Stage
```

---

## 5. Service Bus konfigurace

### 5.1 Namespace

**Používejte EXISTUJÍCÍ FieldForce Service Bus namespace.** NEZAKLÁDEJTE nový. Bridge se připojí ke stejnému namespace.

### 5.2 Topics a subscriptions k vytvoření

#### Topics, které FieldForce PUBLISHUJE (outbound):

```
ff.company.sync              → subscription: bridge-main  (Bridge konzumuje)
ff.contact.updated           → subscription: bridge-main
ff.company.owner-changed     → subscription: bridge-main
ff.company.disabled          → subscription: bridge-main
```

#### Topics, které FieldForce KONZUMUJE (inbound):

```
bridge.company.synced        → subscription: fieldforce-main  (FF konzumuje)
bridge.company.sync-failed   → subscription: fieldforce-main
bridge.company.conflict      → subscription: fieldforce-main
bridge.order.created         → subscription: fieldforce-main
bridge.order.state-changed   → subscription: fieldforce-main
bridge.order.completed       → subscription: fieldforce-main
bridge.order.cancelled       → subscription: fieldforce-main
```

### 5.3 Konfigurace každého topicu

```
Dead-letter queue:    ZAPNUTA
Max delivery count:   5
Lock duration:        5 minut
Message retention:    7 dní
```

### 5.4 Bicep/IaC

Topics a subscriptions by měly být definovány v Bicep šablonách. Bridge repo obsahuje Bicep pro `bridge.*` topics. FieldForce repo musí obsahovat Bicep pro `ff.*` topics + `fieldforce-main` subscriptions na `bridge.*` topics.

---

## 6. Serializace zpráv

### 6.1 JSON format

Bridge serializuje a deserializuje zprávy jako **JSON s camelCase** naming policy:

```csharp
var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};
```

**Příklad serializované CompanySyncMessage:**
```json
{
    "messageId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "sentAt": "2026-03-28T10:30:00+00:00",
    "action": "Create",
    "companyId": "f47ac10b-58cc-4372-a567-0e02b2c3d479",
    "companyName": "Acme s.r.o.",
    "ico": "12345678",
    "dic": "CZ12345678",
    "countryCode": "CZ",
    "street": "Hlavní 42",
    "city": "Praha",
    "postalCode": "11000",
    "state": "Hlavní město Praha",
    "county": "Praha",
    "primaryContactEmail": "jan@acme.cz",
    "primaryContactPhone": "+420123456789",
    "companyRole": "Customer",
    "assignedUserId": "c9d8e7f6-5a4b-3c2d-1e0f-a9b8c7d6e5f4",
    "pipedriveId": null
}
```

### 6.2 Service Bus message properties

```csharp
// Při publishování:
var sbMessage = new ServiceBusMessage(jsonBytes)
{
    ContentType = "application/json",
    MessageId = Guid.NewGuid().ToString(),
    CorrelationId = correlationId  // volitelné, pro traceability
};
```

### 6.3 FieldForce publisher — doporučená implementace

```csharp
public class BridgeServiceBusPublisher : IBridgeServiceBusPublisher
{
    private readonly ServiceBusClient _client;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task PublishAsync<T>(string topicName, T message, CancellationToken ct = default)
        where T : class
    {
        var sender = _senders.GetOrAdd(topicName, _client.CreateSender);
        var json = JsonSerializer.SerializeToUtf8Bytes(message, _jsonOptions);
        var sbMessage = new ServiceBusMessage(json)
        {
            ContentType = "application/json",
            MessageId = Guid.NewGuid().ToString()
        };
        await sender.SendMessageAsync(sbMessage, ct);
    }
}
```

---

## 7. Schéma změny ve FieldForce databázi

### 7.1 Nové sloupce na Company tabulce (Azure SQL)

```sql
-- EF Core migrace: přidat ke Company entitě
ALTER TABLE Companies ADD
    PartnerClientId     INT NULL,                    -- z CompanySyncedResponse
    PartnerRegion       VARCHAR(5) NULL,             -- z CompanySyncedResponse ("cz","pl","hu","us")
    PartnerSyncStatus   VARCHAR(20) NULL,            -- "Synced", "Failed", "Conflict", "Pending"
    PartnerLastSyncAt   DATETIMEOFFSET NULL,         -- čas posledního úspěšného syncu
    PartnerLastError    NVARCHAR(500) NULL;           -- poslední chybová zpráva
```

### 7.2 EF Core entity update

```csharp
// V Company entitě přidat:
public int? PartnerClientId { get; set; }
public string? PartnerRegion { get; set; }
public string? PartnerSyncStatus { get; set; }
public DateTimeOffset? PartnerLastSyncAt { get; set; }
public string? PartnerLastError { get; set; }
```

---

## 8. Kde ve FieldForce kódu publishovat zprávy

### 8.1 CompanySyncMessage — při uložení firmy

Ideálně jako **MediatR notification handler** nebo **domain event handler** po úspěšném uložení Company:

```csharp
// Pseudo-kód — adaptujte na vaši CQRS strukturu

// Option A: Domain Event
public class CompanySavedEventHandler : INotificationHandler<CompanySavedEvent>
{
    private readonly IBridgeServiceBusPublisher _publisher;

    public async Task Handle(CompanySavedEvent notification, CancellationToken ct)
    {
        var company = notification.Company;
        var message = new CompanySyncMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            SentAt = DateTimeOffset.UtcNow,
            Action = notification.IsNew ? "Create" : "Update",
            CompanyId = company.Id,
            CompanyName = company.Name,
            Ico = company.RegistrationNumber,
            Dic = company.TaxNumber,
            CountryCode = company.Address.Country.IsoCode.ToUpperInvariant(),
            Street = company.Address.Street,
            City = company.Address.City,
            PostalCode = company.Address.PostalCode,
            State = company.Address.State,
            County = company.Address.County,
            PrimaryContactEmail = company.PrimaryContact?.Email,
            PrimaryContactPhone = company.PrimaryContact?.Phone,
            CompanyRole = company.Role.ToString(),
            AssignedUserId = company.AssignedUser?.Id,
            PipedriveId = company.PipedriveId
        };

        await _publisher.PublishAsync("ff.company.sync", message, ct);
    }
}

// Option B: V command handleru přímo
public class UpdateCompanyCommandHandler : IRequestHandler<UpdateCompanyCommand, Result>
{
    // ... po úspěšném uložení:
    // await _bridgePublisher.PublishAsync("ff.company.sync", message, ct);
}
```

### 8.2 ContactUpdatedMessage

```csharp
// Při změně Company.PrimaryContact.Email nebo Phone:
if (emailChanged || phoneChanged)
{
    await _publisher.PublishAsync("ff.contact.updated", new ContactUpdatedMessage
    {
        MessageId = Guid.NewGuid().ToString(),
        SentAt = DateTimeOffset.UtcNow,
        FfCompanyId = company.Id,
        Email = emailChanged ? company.PrimaryContact.Email : null,
        Phone = phoneChanged ? company.PrimaryContact.Phone : null
    }, ct);
}
```

**POZOR na duplicitu:** Pokud změna kontaktu proběhne jako součást úpravy celé firmy (Company Update), stačí poslat POUZE `CompanySyncMessage` s aktuálními hodnotami `PrimaryContactEmail` / `PrimaryContactPhone`. `ContactUpdatedMessage` posílejte jen když se mění POUZE kontaktní údaje bez změny zbytku firmy.

### 8.3 CompanyOwnerChangedMessage

```csharp
// Při přeřazení obchodníka:
if (company.AssignedUser?.Id != previousOwnerId)
{
    await _publisher.PublishAsync("ff.company.owner-changed", new CompanyOwnerChangedMessage
    {
        MessageId = Guid.NewGuid().ToString(),
        SentAt = DateTimeOffset.UtcNow,
        FfCompanyId = company.Id,
        NewOwnerUserId = company.AssignedUser!.Id,
        PreviousOwnerUserId = previousOwnerId
    }, ct);
}
```

### 8.4 CompanyDisabledMessage

```csharp
// Při deaktivaci/soft-delete firmy:
await _publisher.PublishAsync("ff.company.disabled", new CompanyDisabledMessage
{
    MessageId = Guid.NewGuid().ToString(),
    SentAt = DateTimeOffset.UtcNow,
    FfCompanyId = company.Id
}, ct);
```

---

## 9. Konzumace inbound zpráv ve FieldForce

### 9.1 Doporučená architektura

Vytvořte `BackgroundService` (hosted service) pro každý inbound topic, nebo použijte MassTransit/NServiceBus pokud už FF používá message bus framework.

```csharp
// Příklad jednoduchého consumeru:
public class BridgeSyncedConsumer : BackgroundService
{
    private readonly ServiceBusProcessor _processor;
    private readonly IServiceProvider _serviceProvider;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _processor.ProcessMessageAsync += async args =>
        {
            using var scope = _serviceProvider.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            var response = JsonSerializer.Deserialize<CompanySyncedResponse>(
                args.Message.Body,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            // Dispatch přes MediatR:
            await mediator.Send(new HandleBridgeSyncedCommand(response!), ct);

            await args.CompleteMessageAsync(args.Message, ct);
        };

        _processor.ProcessErrorAsync += args =>
        {
            // logovat
            return Task.CompletedTask;
        };

        await _processor.StartProcessingAsync(ct);
    }
}
```

### 9.2 Idempotence

Všechny consumery MUSÍ být idempotentní. Bridge může (vzácně) poslat duplicitní zprávy. Kontrolujte `MessageId` proti logu zpracovaných zpráv.

---

## 10. Mapování OrderState → FieldForce stav

| Partner3 OrderState | Význam | FieldForce Event stav (doporučení) |
|---|---|---|
| 7 | Nová objednávka | `New` / `Open` |
| 20 | V realizaci | `InProgress` |
| 30 | Zrušena | `Cancelled` |

Doplňkové flagy:
- `OrderClose = 1` → objednávka uzavřena
- `OrderClosePay = 1` → zaplacena (→ `OrderCompletedMessage`)
- `OrderAutomatClose = -1` → GAIA processing chyba (jen logovat)
- `OrderAutomatClose = 0` → GAIA processing hotovo
- `OrderDeactive = 1` → soft deleted

---

## 11. Machine enrichment z objednávek (Fáze 4)

Při přijetí `OrderCompletedMessage` s VIN:

```csharp
// 1. Najít Machine dle VIN (přesná shoda)
var machine = await _machineRepository.FindByVinAsync(message.VehicleVin);

// 2. Fallback: značka + model + firma
if (machine == null && message.VehicleMark != null && message.VehicleModel != null)
{
    machine = await _machineRepository.FindByMakeModelCompanyAsync(
        message.VehicleMark, message.VehicleModel, message.FfCompanyId);
}

// 3. Pokud nalezen → enrichment
if (machine != null)
{
    if (message.VehiclePowerHp.HasValue)
    {
        // Převod HP → kW (Partner3 ukládá HP, FieldForce pravděpodobně kW)
        machine.ChippedPowerKw = (int)Math.Round(message.VehiclePowerHp.Value * 0.7457);
    }
    if (message.VehicleCategory.HasValue)
    {
        machine.MachineType = MapCategoryToMachineType(message.VehicleCategory.Value);
    }
    await _machineRepository.UpdateAsync(machine);
}
else
{
    // NEVYTVÁŘET nový Machine — pouze logovat
    _logger.LogInformation(
        "Machine not found for VIN={Vin}, Company={CompanyId} — skipping enrichment",
        message.VehicleVin, message.FfCompanyId);
}
```

---

## 12. Company.Stage logika z objednávek

```csharp
// Po OrderCompletedMessage:
// → Company.Stage = Won (firma zaplatila zakázku)

// Po OrderCancelledMessage:
// → Zjistit, zda firma má jiné AKTIVNÍ objednávky (ne cancelled/completed)
//   Pokud NE → Company.Stage = Lost
//   Pokud ANO → neměnit Stage
```

---

## 13. Chybové scénáře a edge cases

### 13.1 Co když Bridge neběží?

Zprávy zůstávají v Service Bus queue (retention 7 dní). Bridge je zpracuje po restartu. FieldForce nemusí nic řešit — fire and forget.

### 13.2 Co když Company nemá adresu?

Poslat `CompanySyncMessage` s `CountryCode` = povinný (musí existovat), ostatní adresy `null`. Bridge zapíše firmu bez adresy.

### 13.3 Co když CountryCode není podporován?

Bridge vrátí `CompanySyncFailedMessage` s `ErrorCode = "UNSUPPORTED_REGION"`. Firma NEBUDE v Partner3. Zvažte validaci na straně FieldForce UI (warning "Tato země není podporována v Partner3").

**Podporované země:**
- CZ, SK, UA, AT, FR → region `cz`
- PL, LT, LV, EE → region `pl`
- HU, RO → region `hu`
- US, CA, AU, BR → region `us`
- DE → speciální případ (nutná ruční konfigurace)
- Vše ostatní → `UNSUPPORTED_REGION`

### 13.4 Co když se změní země firmy?

Bridge automaticky provede přesun mezi regiony (saga). FieldForce pouze pošle `CompanySyncMessage` s `Action="Update"` a novým `CountryCode`. Bridge se postará o zbytek a vrátí `CompanySyncedResponse` s novým `PartnerRegion`.

### 13.5 Ordering zpráv

Service Bus NEZARUČUJE pořadí. Bridge je na to připraven (conflict detection, idempotence). FieldForce nemusí řešit ordering — ale měl by vždy posílat **aktuální stav** (ne delta), aby stará zpráva zpracovaná po nové nezpůsobila regresi.

---

## 14. Testovací scénáře pro FieldForce

### Outbound testy (FieldForce → Bridge)

1. **Vytvoření firmy CZ** → ověřit, že se na `ff.company.sync` objeví zpráva s Action="Create" a CountryCode="CZ"
2. **Úprava firmy** → ověřit Action="Update" se správnými hodnotami
3. **Změna primárního kontaktu** → ověřit zprávu na `ff.contact.updated`
4. **Přeřazení ownera** → ověřit zprávu na `ff.company.owner-changed`
5. **Deaktivace firmy** → ověřit zprávu na `ff.company.disabled`
6. **Firma z nepodporované země (IT)** → zpráva se pošle, Bridge vrátí sync-failed

### Inbound testy (Bridge → FieldForce)

7. **CompanySyncedResponse** → PartnerClientId a PartnerRegion uloženy ke Company
8. **CompanySyncFailedMessage** → ErrorCode logován, volitelně zobrazen v UI
9. **CompanyConflictMessage** → logován, volitelně zobrazen v UI
10. **OrderCreatedMessage** → Event vytvořen na správné Company
11. **OrderCompletedMessage s VIN** → Machine enrichment proběhne
12. **OrderCancelledMessage** → Company.Stage aktualizován (pokud žádné jiné aktivní)
13. **Duplicitní zpráva** → idempotentní zpracování, žádný duplicitní Event
14. **Zpráva pro neexistující Company** → graceful handling, logovat warning

---

## 15. NuGet balíčky

```xml
<!-- Pro Service Bus komunikaci -->
<PackageReference Include="Azure.Messaging.ServiceBus" Version="7.*" />

<!-- Pro JSON serializaci (pokud nepoužíváte System.Text.Json) -->
<PackageReference Include="System.Text.Json" Version="8.*" />
```

---

## 16. Konfigurační klíče (appsettings.json)

```json
{
    "Bridge": {
        "ServiceBus": {
            "ConnectionString": "<<z Azure Key Vault>>",
            "Topics": {
                "CompanySync": "ff.company.sync",
                "ContactUpdated": "ff.contact.updated",
                "OwnerChanged": "ff.company.owner-changed",
                "CompanyDisabled": "ff.company.disabled"
            },
            "InboundTopics": {
                "CompanySynced": "bridge.company.synced",
                "CompanySyncFailed": "bridge.company.sync-failed",
                "CompanyConflict": "bridge.company.conflict",
                "OrderCreated": "bridge.order.created",
                "OrderStateChanged": "bridge.order.state-changed",
                "OrderCompleted": "bridge.order.completed",
                "OrderCancelled": "bridge.order.cancelled"
            },
            "SubscriptionName": "fieldforce-main"
        }
    }
}
```

---

## 17. Shrnutí priorit implementace

### Fáze 1 (KRITICKÁ — bez toho Bridge nefunguje):
1. Publisher `CompanySyncMessage` při Create/Update Company
2. Publisher `CompanyDisabledMessage` při deaktivaci
3. Consumer `CompanySyncedResponse` — uložit PartnerClientId
4. Consumer `CompanySyncFailedMessage` — logování

### Fáze 2 (střední priorita):
5. Publisher `ContactUpdatedMessage`
6. Publisher `CompanyOwnerChangedMessage`
7. Consumer `CompanyConflictMessage`

### Fáze 4 (po nasazení Bridge polling):
8. Consumer `OrderCreatedMessage` → Event
9. Consumer `OrderStateChangedMessage` → Event update
10. Consumer `OrderCompletedMessage` → Event + Machine enrichment
11. Consumer `OrderCancelledMessage` → Event + Stage update

---

## 18. Kontaktní body

- **Bridge repo:** FF-Partner (tento dokument pochází odtud)
- **Service Bus namespace:** sdílený s FieldForce
- **Otázky k Bridge chování:** viz CLAUDE.md v Bridge repo
- **Owner mapping konfigurace:** `config/owner-mapping.example.json` v Bridge repo

---

*Vygenerováno: 2026-03-28 — z FF-Partner Bridge codebase (exact message contracts)*
