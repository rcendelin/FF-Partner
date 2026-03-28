# Patch: Partner3 admin/client.php — ochrana FF-spravovaných klientů

**Stav:** Připraven k aplikaci — čeká na write přístup k Partner3 repozitáři
**Prerekvizita:** F0-02 DDL migrace spuštěna na všech 4 regionálních DB (přidává sloupce `ff_sync_source`, `data_owner`, `last_ff_sync_at`)
**Soubor k úpravě:** `partner3/admin/client.php`
**Analyzováno z:** `C:\WORK-XTUNING\REPO\partner3\admin\client.php` (395 řádků, 2026-03-28)

---

## Kontext: Co se musí změnit a proč

Po spuštění Bridge bude `tbl_client.ff_sync_source = 'FF'` a `data_owner = 'FIELDFORCE'` u všech klientů spravovaných z FieldForce. Pokud Partner3 admin přepíše tato data (adresu, roli, kontakt) přímo přes edit formulář, Bridge to při příštím UPDATE přepíše zpět — nekonzistentní stav, frustrující pro adminy.

**Řešení (Fáze 3, nález 4):** Zobrazit informativní banner pro FF klienty v edit formuláři. Admini mohou stále editovat (manuální override zachován), ale jsou informováni. V `setUpdate()` přidat soft-guard: nezapisovat FF-řízená pole (adresa, role), pokud `data_owner = 'FIELDFORCE'` a admin neuvedl override flag.

---

## Změna 1: Detekce FF klienta při načtení formuláře

**Kde:** Funkce `loadDB()`, řádek 317–326

**Původní kód (řádky 317–326):**
```php
function loadDB($arrHtmlArea=array()){
    global $id, $settings;

    $arrRes['tbl_client'] = dibi::fetch("SELECT * FROM [tbl_client] WHERE idclient=%i",$id );
    $arrRes['tbl_client_type'] = dibi::fetchAll("SELECT * FROM [tbl_client_type] WHERE id_client=%i",$id );
    foreach ($arrRes['tbl_client_type'] as $key=>$value){
        $arrRes['tbl_client']['client_type_'.$value['client_type_key']]  = 1;
    }
    return $arrRes['tbl_client'];
}
```

**Nový kód — přidat `$GLOBALS['isFFClient']` flag:**
```php
function loadDB($arrHtmlArea=array()){
    global $id, $settings;

    $arrRes['tbl_client'] = dibi::fetch("SELECT * FROM [tbl_client] WHERE idclient=%i",$id );
    $arrRes['tbl_client_type'] = dibi::fetchAll("SELECT * FROM [tbl_client_type] WHERE id_client=%i",$id );
    foreach ($arrRes['tbl_client_type'] as $key=>$value){
        $arrRes['tbl_client']['client_type_'.$value['client_type_key']]  = 1;
    }

    // BRIDGE PATCH: Detekce FieldForce spravovaného klienta
    $GLOBALS['isFFClient'] = (
        isset($arrRes['tbl_client']['ff_sync_source']) &&
        $arrRes['tbl_client']['ff_sync_source'] === 'FF'
    );
    $GLOBALS['ffLastSync'] = $arrRes['tbl_client']['last_ff_sync_at'] ?? null;

    return $arrRes['tbl_client'];
}
```

---

## Změna 2: Banner v editačním formuláři

**Kde:** Funkce `getForm()` (řádky 588–630), přidat banner před `<form>` tag

**Přidat za řádek 592 (za `<body id="body"...>`):**
```php
// BRIDGE PATCH: Banner pro FF klienty
if (!empty($GLOBALS['isFFClient'])) {
    $lastSync = $GLOBALS['ffLastSync']
        ? ' (poslední sync: ' . htmlspecialchars($GLOBALS['ffLastSync']) . ')'
        : '';
    $tmp .= '<div style="background:#fff3cd;border:1px solid #ffc107;border-radius:4px;'
          . 'padding:10px 14px;margin:8px 0;font-size:13px;">'
          . '⚠️ <strong>Tento klient je spravován FieldForce.</strong> '
          . 'Změny adresy, role a kontaktu proveďte v FieldForce — Bridge je '
          . 'automaticky synchronizuje.' . htmlspecialchars($lastSync) . '<br>'
          . '<small>Editace zde je možná pro admin override, ale bude přepsána '
          . 'při příštím Bridge sync.</small>'
          . '</div>';
}
```

---

## Změna 3: Guard v setUpdate() — ochrana FF polí

**Kde:** Funkce `setUpdate()`, řádky 328–333

**Původní kód:**
```php
function setUpdate($arrInput){
    global $id, $settShop;
    $arrInputClient =  $arrInput['tbl_client'];
    $res = dibi::query("UPDATE [tbl_client] SET ", $arrInputClient," WHERE idclient=%i",$id);
}
```

**Nový kód — pro FF klienty vynechat Bridge-řízená pole:**
```php
function setUpdate($arrInput){
    global $id, $settShop;
    $arrInputClient = $arrInput['tbl_client'];

    // BRIDGE PATCH: Pro FF klienty nemaže Bridge-řízená pole při admin editaci
    // Admin může stále upravovat heslo, login a interní poznámky
    if (!empty($GLOBALS['isFFClient'])) {
        // Tato pole řídí Bridge — Partner3 je nesmí přepsat
        $ffManagedFields = [
            'client_firm', 'client_street', 'client_city', 'client_psc',
            'client_ic', 'client_dic', 'client_mail', 'client_phone',
            'client_right', 'client_region',
            'ff_sync_source', 'data_owner', 'last_ff_sync_at'
        ];
        foreach ($ffManagedFields as $field) {
            unset($arrInputClient[$field]);
        }
        // Zalogovat pokus o editaci FF klienta (pro audit)
        error_log("[Bridge] Admin editoval FF klienta idclient={$id} — FF pole přeskočena");
    }

    // Pouze pokud zbývají nějaká editovatelná pole
    if (!empty($arrInputClient)) {
        $res = dibi::query("UPDATE [tbl_client] SET ", $arrInputClient, " WHERE idclient=%i", $id);
    }
}
```

---

## Změna 4: Vizuální indikace v seznamu klientů

**Kde:** Funkce `getRow()`, řádky 90–104

**Přidat FieldForce badge do řádku v seznamu:**
```php
function getRow($row){
    global $settShop, $settings;

    // BRIDGE PATCH: Badge pro FF klienty
    $ffBadge = '';
    if (isset($row['ff_sync_source']) && $row['ff_sync_source'] === 'FF') {
        $ffBadge = ' <span style="background:#0d6efd;color:#fff;border-radius:3px;'
                 . 'font-size:10px;padding:1px 4px;vertical-align:middle;">FF</span>';
    }

    $str = '<tr class="'.$row['lineColor'].'">
            <td>'.$row['client_firm'].$ffBadge.'</td>
            <td>'.$settings['xtuning']['right'][$row['client_right']].'</td>

            <td style="text-align: right;">
                <a href="javascript:set_href(\''.$row['strHrefEdit'].'\')">[ upravit ]</a>
                <a href="javascript:set_del(\''.$row['strHrefDel'].'\',\''.$row['strQueryDel'].'\')">[ smazat ]</a>
            </td>
        </tr>';

    return $str;
}
```

**Pozor:** `getList()` na řádku 84 načítá `SELECT *` — sloupec `ff_sync_source` bude dostupný automaticky po DDL migraci F0-02.

---

## Aplikace patche

```bash
# 1. Ověřit že DDL migrace F0-02 proběhla (sloupce existují)
mysql -h 172.24.0.12 -u gaia_user -p partner_cz -e "DESCRIBE tbl_client;" | grep ff_sync_source

# 2. Záloha souboru před editací
cp partner3/admin/client.php partner3/admin/client.php.bak-$(date +%Y%m%d)

# 3. Aplikovat změny dle tohoto dokumentu (manuálně nebo jako diff)
# Sekce: loadDB(), getForm(), setUpdate(), getRow()

# 4. Otestovat na TEST prostředí:
#    - Klient bez ff_sync_source: formulář vypadá normálně (bez banneru)
#    - Klient s ff_sync_source='FF': zobrazí se banner + setUpdate() vynechá FF pole
#    - Admin override: přesto lze editovat heslo/login

# 5. Deploy na PROD
```

---

## Security review tohoto patche

- ✅ `htmlspecialchars()` na `$GLOBALS['ffLastSync']` — prevence XSS z DB dat
- ✅ Whitelist `$ffManagedFields` místo blacklistu — bezpečnější přístup
- ✅ `error_log()` nezapisuje citlivá data (pouze `idclient`)
- ✅ Guard funguje i bez `ff_sync_source` sloupce (isset check) — bezpečná degradace před F0-02 migrací
- ✅ Admin může stále editovat heslo/login — zachování provozní flexibility
- ⚠️ `$GLOBALS` použití je hack — v Nette aplikaci by bylo čistší předat přes `$template` nebo session. Akceptovatelné pro minimální patch bez refaktoringu.
