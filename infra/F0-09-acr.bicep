// F0-09 — Azure Container Registry pro FF-Partner Bridge image
//
// Co vytváří:
//   1. Azure Container Registry (Standard tier)
//   2. Retention policy pro untagged manifesty (auto-cleanup po 30 dnech)
//   3. Disabled admin user a anonymous pull (vynucený RBAC + token auth)
//
// Auth model:
//   - Push z GitHub Actions: OIDC federated identity → AcrPush role
//     (viz F0-09-github-oidc-setup.sh)
//   - Pull z deploy serveru: ACR token + scope map (read-only na ff-partner-bridge repo)
//     (viz F0-09-deploy-token-setup.sh)
//
// Nasazení:
//   az deployment group create \
//     --resource-group <RG> \
//     --template-file infra/F0-09-acr.bicep \
//     --parameters acrName=<unique-name>
//
// Po nasazení:
//   1. F0-09-github-oidc-setup.sh — App Registration + Federated Credential + AcrPush
//   2. F0-09-deploy-token-setup.sh — token pro deploy server pull
//   3. Aktualizovat .github/workflows/bridge.yml a docker-compose.yml ACR_NAME

@description('Název Azure Container Registry — globálně unikátní, 5-50 znaků, alfanumerické (BEZ pomlček a teček). Doporučení: cr<projectname>, např. crffpartnerbridge.')
@minLength(5)
@maxLength(50)
param acrName string

@description('Location pro ACR. Výchozí = resource group location.')
param location string = resourceGroup().location

@description('SKU tier. Basic = 10 GB / 1 webhook, Standard = 100 GB / 10 webhooků (doporučeno), Premium = 500 GB + geo-replikace + scope tokeny.')
@allowed([
  'Basic'
  'Standard'
  'Premium'
])
param sku string = 'Standard'

@description('Počet dní pro retention untagged manifestů (preview feature). 0 = vypnuto.')
@minValue(0)
@maxValue(365)
param untaggedManifestRetentionDays int = 30

// ─── Container Registry ──────────────────────────────────────────────────────

resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: acrName
  location: location
  sku: {
    name: sku
  }
  properties: {
    // adminUserEnabled = false: vynucujeme RBAC nebo scope tokens, ne sdílené admin credentials
    adminUserEnabled: false

    // publicNetworkAccess = Enabled: deploy server je on-prem bez VPN do Azure
    publicNetworkAccess: 'Enabled'

    // anonymousPullEnabled = false: image není veřejný, vždy vyžadován login
    anonymousPullEnabled: false

    // dataEndpointEnabled = false: zjednodušená síťová cesta (Premium-only feature)
    dataEndpointEnabled: false

    // Bypass pro Azure služby (např. ACR Tasks, Defender for Cloud scanning)
    networkRuleBypassOptions: 'AzureServices'

    // Standard a Premium podporují retention policy pro untagged manifesty (preview).
    // Basic to nepodporuje — pro Basic tier se sekce policies ignoruje.
    policies: untaggedManifestRetentionDays > 0 ? {
      retentionPolicy: {
        days: untaggedManifestRetentionDays
        status: 'enabled'
      }
    } : {}
  }
}

// ─── outputs ──────────────────────────────────────────────────────────────────

@description('Login server URL — použít v workflow a docker-compose.yml. Formát: <name>.azurecr.io')
output acrLoginServer string = acr.properties.loginServer

@description('Resource ID ACR — pro role assignmenty (AcrPush, AcrPull)')
output acrResourceId string = acr.id

@description('Název ACR — pro az CLI příkazy (az acr show --name <name>)')
output acrName string = acr.name
