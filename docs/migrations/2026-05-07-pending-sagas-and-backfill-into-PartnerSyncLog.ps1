<#
.SYNOPSIS
    Migrace kritických řádků z bridge_sync_log (Bridge Azure SQL) do PartnerSyncLog
    (FieldForce Azure SQL) před deployem PR "Replace ISyncLogRepository with IPartnerSyncLog".

.DESCRIPTION
    Bridge a FieldForce běží na různých Azure SQL serverech. Tento skript otevírá
    dvě připojení a přenáší pouze řádky, které řídí runtime chování:
      1. Pending region-change ságy (recovery při startu Bridge)
      2. Order backfill úspěchy (idempotency klíč)

    Skript je idempotentní pro pending ságy (filtr 7 dní + EXISTS check),
    pro backfill používá ROW_NUMBER pro pouze nejnovější success per (operation, region).
    Spustit POUZE JEDNOU před deployem.

.PARAMETER BridgeConn
    Connection string do Bridge Azure SQL (zdroj — bridge_sync_log).

.PARAMETER FieldForceConn
    Connection string do FieldForce Azure SQL (cíl — PartnerSyncLog).

.PARAMETER DryRun
    Pokud je $true, vypíše počty řádků, které by se přenesly, ale nic nezapíše.

.EXAMPLE
    .\2026-05-07-pending-sagas-and-backfill-into-PartnerSyncLog.ps1 `
        -BridgeConn $env:BRIDGE_AZURE_SQL_CONN `
        -FieldForceConn $env:FIELDFORCE_DB_CONN `
        -DryRun:$true

    Pak po ověření spustit znovu bez -DryRun.

.NOTES
    Pořadí spuštění (operations):
      1. Pause inbound queues (ff.company.sync, ff.contact.updated,
         ff.company.owner-changed, ff.company.disabled).
      2. Počkat 5 minut, ať doběhne aktuálně zpracovávaná zpráva.
      3. Spustit tento skript s -DryRun:$true, ověřit počty.
      4. Spustit znovu bez -DryRun.
      5. Deploy nové verze Bridge.
      6. Resume inbound queues.

    Vyžaduje: PowerShell 5.1+ a SqlServer modul (Install-Module SqlServer).
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BridgeConn,
    [Parameter(Mandatory)] [string] $FieldForceConn,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Module -ListAvailable -Name SqlServer)) {
    throw "Modul 'SqlServer' není nainstalován. Spustit: Install-Module SqlServer -Scope CurrentUser"
}
Import-Module SqlServer -ErrorAction Stop

# -----------------------------------------------------------------------------
# 1) SELECT pending sagas z bridge_sync_log (Bridge DB)
# -----------------------------------------------------------------------------
$pendingSagasSql = @"
SELECT
    l.ff_company_id            AS CompanyId,
    ISNULL(l.service_bus_message_id, CONVERT(NVARCHAR(36), NEWID())) AS CorrelationMessageId,
    'SagaPending'              AS Phase,
    'Internal'                 AS Direction,
    'region_change'            AS Operation,
    'InProgress'               AS Status,
    l.partner_client_id        AS PartnerClientId,
    l.partner_region           AS PartnerRegion,
    CAST(NULL AS NVARCHAR(50)) AS ErrorCode,
    l.error_message            AS ErrorMessage,
    l.payload_json             AS PayloadJson,
    'Bridge'                   AS Source,
    CAST(l.created_at AS DATETIMEOFFSET) AS CreatedAt
FROM bridge_sync_log l
WHERE l.operation = 'pending_region_change'
  AND l.status = 'in_progress'
  AND l.ff_company_id IS NOT NULL
  AND l.created_at > DATEADD(day, -7, GETUTCDATE())
  AND NOT EXISTS (
      SELECT 1 FROM bridge_sync_log l2
      WHERE l2.ff_company_id = l.ff_company_id
        AND l2.operation = 'region_change'
        AND l2.created_at > l.created_at
  );
"@

# -----------------------------------------------------------------------------
# 2) SELECT order_backfill úspěchů z bridge_sync_log (Bridge DB)
# -----------------------------------------------------------------------------
$backfillSql = @"
WITH LatestBackfill AS (
    SELECT
        ff_company_id, partner_client_id, partner_region,
        operation, service_bus_message_id, error_message, payload_json,
        created_at,
        ROW_NUMBER() OVER (
            PARTITION BY operation, partner_region
            ORDER BY created_at DESC
        ) AS rn
    FROM bridge_sync_log
    WHERE operation = 'order_backfill'
      AND status = 'success'
)
SELECT
    ISNULL(l.ff_company_id, '00000000-0000-0000-0000-000000000000') AS CompanyId,
    ISNULL(l.service_bus_message_id,
           CONCAT('backfill-migrated-', l.partner_region, '-',
                  CONVERT(NVARCHAR(20), l.created_at, 121))) AS CorrelationMessageId,
    'BackfillCompleted'        AS Phase,
    'Internal'                 AS Direction,
    l.operation                AS Operation,
    'Success'                  AS Status,
    l.partner_client_id        AS PartnerClientId,
    l.partner_region           AS PartnerRegion,
    CAST(NULL AS NVARCHAR(50)) AS ErrorCode,
    l.error_message            AS ErrorMessage,
    l.payload_json             AS PayloadJson,
    'Bridge'                   AS Source,
    CAST(l.created_at AS DATETIMEOFFSET) AS CreatedAt
FROM LatestBackfill l
WHERE l.rn = 1;
"@

# -----------------------------------------------------------------------------
# 3) INSERT do PartnerSyncLog (FieldForce DB) — parametrizovaně
# -----------------------------------------------------------------------------
$insertSql = @"
INSERT INTO PartnerSyncLog
    (CompanyId, CorrelationMessageId, Phase, Direction, Operation, Status,
     PartnerClientId, PartnerRegion, ErrorCode, ErrorMessage, PayloadJson,
     Source, CreatedAt)
VALUES
    (@CompanyId, @CorrelationMessageId, @Phase, @Direction, @Operation, @Status,
     @PartnerClientId, @PartnerRegion, @ErrorCode, @ErrorMessage, @PayloadJson,
     @Source, @CreatedAt);
"@

function Invoke-MigrateRows {
    param(
        [string] $Label,
        [string] $SourceSql
    )

    Write-Host "[$Label] Čtu zdrojové řádky z Bridge DB..." -ForegroundColor Cyan
    $rows = Invoke-Sqlcmd -ConnectionString $BridgeConn -Query $SourceSql -ErrorAction Stop
    $count = ($rows | Measure-Object).Count

    if ($count -eq 0) {
        Write-Host "[$Label] 0 řádků k migraci." -ForegroundColor Yellow
        return 0
    }

    Write-Host "[$Label] Nalezeno $count řádků." -ForegroundColor Green

    if ($DryRun) {
        Write-Host "[$Label] DryRun — ukázka prvního řádku:" -ForegroundColor Yellow
        $rows | Select-Object -First 1 | Format-List
        return $count
    }

    $inserted = 0
    foreach ($row in $rows) {
        $params = @{
            CompanyId            = [Guid] $row.CompanyId
            CorrelationMessageId = [string] $row.CorrelationMessageId
            Phase                = [string] $row.Phase
            Direction            = [string] $row.Direction
            Operation            = [string] $row.Operation
            Status               = [string] $row.Status
            PartnerClientId      = if ($row.PartnerClientId -is [DBNull]) { $null } else { [int] $row.PartnerClientId }
            PartnerRegion        = if ($row.PartnerRegion -is [DBNull])   { $null } else { [string] $row.PartnerRegion }
            ErrorCode            = if ($row.ErrorCode -is [DBNull])       { $null } else { [string] $row.ErrorCode }
            ErrorMessage         = if ($row.ErrorMessage -is [DBNull])    { $null } else { [string] $row.ErrorMessage }
            PayloadJson          = if ($row.PayloadJson -is [DBNull])     { $null } else { [string] $row.PayloadJson }
            Source               = [string] $row.Source
            CreatedAt            = [DateTimeOffset] $row.CreatedAt
        }

        Invoke-Sqlcmd -ConnectionString $FieldForceConn -Query $insertSql -Variable (
            $params.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }
        ) -ErrorAction Stop | Out-Null
        $inserted++
    }

    Write-Host "[$Label] Vloženo $inserted řádků do PartnerSyncLog." -ForegroundColor Green
    return $inserted
}

Write-Host "===== Migrace bridge_sync_log -> PartnerSyncLog =====" -ForegroundColor Cyan
if ($DryRun) {
    Write-Host "DryRun režim — žádné zápisy do FieldForce DB." -ForegroundColor Yellow
}

$sagasMigrated    = Invoke-MigrateRows -Label "Pending sagas" -SourceSql $pendingSagasSql
$backfillMigrated = Invoke-MigrateRows -Label "Order backfill" -SourceSql $backfillSql

Write-Host ""
Write-Host "===== Hotovo =====" -ForegroundColor Cyan
Write-Host "Pending sagas migrované: $sagasMigrated"
Write-Host "Order backfill řádky migrované: $backfillMigrated"

if ($DryRun) {
    Write-Host ""
    Write-Host "Pro skutečný přenos spustit znovu bez -DryRun." -ForegroundColor Yellow
}
