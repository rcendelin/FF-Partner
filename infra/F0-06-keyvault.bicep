// ┌─────────────────────────────────────────────────────────────────────────────┐
// │  DEPRECATED — Bridge nekonzumuje Azure Key Vault.                           │
// │                                                                             │
// │  Aktuální deployment model: Docker Secrets + env vars (./.env). Bridge      │
// │  čte secrets přes DockerSecretsReader z /run/secrets/<name>, App Insights   │
// │  conn string se předává jako env var z .env. Žádný kód v repu nezavádí      │
// │  AddAzureKeyVault konfigurační provider.                                    │
// │                                                                             │
// │  Tento Bicep je ponechán jako stub pro případnou budoucí reaktivaci         │
// │  (např. migrace Bridge do Azure Container Apps, kde system-assigned MI      │
// │  funguje nativně bez bootstrap secretu). Nezakládat KV bez současné         │
// │  úpravy Program.cs — vznikla by neaktivní infra a nejasná dokumentace.     │
// │                                                                             │
// │  Viz CLAUDE.md sekce 13 ("Secrets management") pro aktuální model.          │
// └─────────────────────────────────────────────────────────────────────────────┘
//
// F0-06 — Azure Key Vault + User-Assigned Managed Identity pro FF-Partner Bridge (LEGACY)
//
// Co by vytvářelo, kdyby se reaktivovalo:
//   1. User-Assigned Managed Identity `id-ff-partner-bridge` — Bridge se autentizuje jako tato identita
//   2. Key Vault `kv-xtuning-prod` (Standard tier, RBAC, soft-delete 90d, purge protection)
//   3. Role Assignment — Key Vault Secrets User pro Managed Identity (pouze Get + List na secrets)
//
// Nasazení:
//   az deployment group create \
//     --resource-group <RG> \
//     --template-file infra/F0-06-keyvault.bicep \
//     --parameters keyVaultName=kv-xtuning-prod
//
// Po nasazení:
//   - Uložit Application Insights connection string a Service Bus connection string do KV
//     (viz F0-06-keyvault-setup.sh)
//   - Výstupní `managedIdentityClientId` použít jako AZURE_CLIENT_ID v docker-compose.yml
//   - Výstupní `keyVaultUri` použít jako AZURE_KEY_VAULT_URI v docker-compose.yml

@description('Název Key Vault — musí být globálně unikátní, max 24 znaků, pouze alfanumerické a pomlčky.')
param keyVaultName string = 'kv-xtuning-prod'

@description('Location pro všechny resources. Výchozí = resource group location.')
param location string = resourceGroup().location

// ─── User-Assigned Managed Identity ──────────────────────────────────────────
// Bridge používá tuto identitu pro přístup ke Key Vault bez ukládání credentials.
// Client ID se předá jako AZURE_CLIENT_ID environment variable do kontejneru.

resource bridgeIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-ff-partner-bridge'
  location: location
}

// ─── Key Vault ────────────────────────────────────────────────────────────────

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'   // Standard tier — postačující pro Bridge (Premium přidává HSM)
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true   // RBAC místo Access Policies (moderní přístup, doporučeno MS)
    enableSoftDelete: true
    softDeleteRetentionInDays: 90   // Maximální retention — ochrana před náhodným smazáním
    enablePurgeProtection: true     // Zabraňuje trvalému smazání i po soft-delete
    publicNetworkAccess: 'Enabled'  // Bridge běží on-prem, potřebuje přístup přes internet
    networkAcls: {
      defaultAction: 'Allow'        // Síť XTuning nemá statickou IP — nelze omezit na IP range
      bypass: 'AzureServices'       // Azure Monitoring a Deployment může přistupovat vždy
      ipRules: []
      virtualNetworkRules: []
    }
  }
}

// ─── RBAC: Key Vault Secrets User ────────────────────────────────────────────
// Role ID: 4633458b-17de-408a-b874-0445c86b69e6 (Key Vault Secrets User)
// Oprávnění: Get + List na secrets — nestačí pro Keys ani Certificates.
// Bridge potřebuje pouze číst secrets (App Insights key, Service Bus conn string).

resource bridgeKvSecretsUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, bridgeIdentity.id, '4633458b-17de-408a-b874-0445c86b69e6')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '4633458b-17de-408a-b874-0445c86b69e6'  // Key Vault Secrets User (read-only)
    )
    principalId: bridgeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ─── outputs ──────────────────────────────────────────────────────────────────

@description('URI Key Vault — použít jako AZURE_KEY_VAULT_URI v docker-compose.yml')
output keyVaultUri string = keyVault.properties.vaultUri

@description('Client ID Managed Identity — použít jako AZURE_CLIENT_ID v docker-compose.yml')
output managedIdentityClientId string = bridgeIdentity.properties.clientId

@description('Resource ID Managed Identity — použít při přiřazení identity ke VM/ACI')
output managedIdentityResourceId string = bridgeIdentity.id

@description('Key Vault Resource ID')
output keyVaultResourceId string = keyVault.id
