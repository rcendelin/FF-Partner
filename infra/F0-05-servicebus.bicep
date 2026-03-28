// F0-05 — Service Bus topics a subscriptions pro FF-Partner Bridge
//
// Přidává topics do EXISTUJÍCÍHO FieldForce Service Bus namespace (sdílený namespace — rozhodnutí REV-02, 2026-03-27).
// Idempotentní — opakované spuštění nevytvoří duplicity.
//
// Konfigurace dle CLAUDE.md sekce 4:
//   - Dead-letter queue: zapnuta (deadLetteringOnMessageExpiration = true)
//   - Max delivery count: 5
//   - Lock duration: 5 minut
//   - Retention: 7 dní
//
// Nasazení:
//   az deployment group create \
//     --resource-group <RG> \
//     --template-file infra/F0-05-servicebus.bicep \
//     --parameters namespaceName=<NS_NAME>

@description('Název existujícího Service Bus namespace (sdílený s FieldForce — nezakládat nový).')
param namespaceName string

// ─── konstanty konfigurace ────────────────────────────────────────────────────

var msgTtl         = 'P7D'   // 7 dní — retention zpráv
var lockDuration   = 'PT5M'  // 5 minut — lock duration při zpracování zprávy
var maxDelivery    = 5        // počet pokusů před přesunem do dead-letter queue

// ─── reference na existující namespace ───────────────────────────────────────

resource sbNamespace 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = {
  name: namespaceName
}

// ═════════════════════════════════════════════════════════════════════════════
// AUTHORIZATION RULES — least privilege (M-03)
// Namespace-level rules (topic-level vyžaduje Premium tier).
// Bridge: Listen na ff.* topics + Send na bridge.* topics.
// FieldForce: Send na ff.* topics + Listen na bridge.* topics.
// Obě strany sdílejí Send+Listen — granularity na topic level není potřeba pro Standard tier.
// ═════════════════════════════════════════════════════════════════════════════

resource bridgeAuthRule 'Microsoft.ServiceBus/namespaces/AuthorizationRules@2024-01-01' = {
  parent: sbNamespace
  name: 'bridge-send-listen'
  properties: {
    rights: ['Send', 'Listen']
  }
}

resource fieldforceAuthRule 'Microsoft.ServiceBus/namespaces/AuthorizationRules@2024-01-01' = {
  parent: sbNamespace
  name: 'fieldforce-send-listen'
  properties: {
    rights: ['Send', 'Listen']
  }
}

// ═════════════════════════════════════════════════════════════════════════════
// OUTBOUND: FieldForce → Bridge
// Topics na které FieldForce publikuje, Bridge konzumuje.
// ═════════════════════════════════════════════════════════════════════════════

// ── ff.company.sync (CREATE + UPDATE firem) ───────────────────────────────────

resource topicFfCompanySync 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: sbNamespace
  name: 'ff.company.sync'
  properties: {
    defaultMessageTimeToLive: msgTtl
    enablePartitioning: false
    requiresDuplicateDetection: false
    supportOrdering: false
  }
}

resource subFfCompanySyncBridge 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: topicFfCompanySync
  name: 'bridge'
  properties: {
    lockDuration: lockDuration
    maxDeliveryCount: maxDelivery
    deadLetteringOnMessageExpiration: true
    requiresSession: false
    defaultMessageTimeToLive: msgTtl
  }
}

// ── ff.contact.updated (změna emailu/telefonu primárního kontaktu) ────────────

resource topicFfContactUpdated 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: sbNamespace
  name: 'ff.contact.updated'
  properties: {
    defaultMessageTimeToLive: msgTtl
    enablePartitioning: false
    requiresDuplicateDetection: false
    supportOrdering: false
  }
}

resource subFfContactUpdatedBridge 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: topicFfContactUpdated
  name: 'bridge'
  properties: {
    lockDuration: lockDuration
    maxDeliveryCount: maxDelivery
    deadLetteringOnMessageExpiration: true
    requiresSession: false
    defaultMessageTimeToLive: msgTtl
  }
}

// ── ff.company.owner-changed (přeřazení obchodníka) ──────────────────────────

resource topicFfCompanyOwnerChanged 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: sbNamespace
  name: 'ff.company.owner-changed'
  properties: {
    defaultMessageTimeToLive: msgTtl
    enablePartitioning: false
    requiresDuplicateDetection: false
    supportOrdering: false
  }
}

resource subFfCompanyOwnerChangedBridge 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: topicFfCompanyOwnerChanged
  name: 'bridge'
  properties: {
    lockDuration: lockDuration
    maxDeliveryCount: maxDelivery
    deadLetteringOnMessageExpiration: true
    requiresSession: false
    defaultMessageTimeToLive: msgTtl
  }
}

// ── ff.company.disabled (deaktivace firmy) ────────────────────────────────────

resource topicFfCompanyDisabled 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: sbNamespace
  name: 'ff.company.disabled'
  properties: {
    defaultMessageTimeToLive: msgTtl
    enablePartitioning: false
    requiresDuplicateDetection: false
    supportOrdering: false
  }
}

resource subFfCompanyDisabledBridge 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: topicFfCompanyDisabled
  name: 'bridge'
  properties: {
    lockDuration: lockDuration
    maxDeliveryCount: maxDelivery
    deadLetteringOnMessageExpiration: true
    requiresSession: false
    defaultMessageTimeToLive: msgTtl
  }
}

// ═════════════════════════════════════════════════════════════════════════════
// INBOUND: Bridge → FieldForce
// Topics na které Bridge publikuje, FieldForce konzumuje.
// ═════════════════════════════════════════════════════════════════════════════

// ── bridge.company.synced (úspěch + vrácení Partner ID) ──────────────────────

resource topicBridgeCompanySynced 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: sbNamespace
  name: 'bridge.company.synced'
  properties: {
    defaultMessageTimeToLive: msgTtl
    enablePartitioning: false
    requiresDuplicateDetection: false
    supportOrdering: false
  }
}

resource subBridgeCompanySyncedFieldforce 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: topicBridgeCompanySynced
  name: 'fieldforce'
  properties: {
    lockDuration: lockDuration
    maxDeliveryCount: maxDelivery
    deadLetteringOnMessageExpiration: true
    requiresSession: false
    defaultMessageTimeToLive: msgTtl
  }
}

// ── bridge.company.sync-failed (chyba s kódem) ───────────────────────────────

resource topicBridgeCompanySyncFailed 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: sbNamespace
  name: 'bridge.company.sync-failed'
  properties: {
    defaultMessageTimeToLive: msgTtl
    enablePartitioning: false
    requiresDuplicateDetection: false
    supportOrdering: false
  }
}

resource subBridgeCompanySyncFailedFieldforce 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: topicBridgeCompanySyncFailed
  name: 'fieldforce'
  properties: {
    lockDuration: lockDuration
    maxDeliveryCount: maxDelivery
    deadLetteringOnMessageExpiration: true
    requiresSession: false
    defaultMessageTimeToLive: msgTtl
  }
}

// ── bridge.company.conflict (detekován konflikt, zápis přeskočen) ─────────────

resource topicBridgeCompanyConflict 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: sbNamespace
  name: 'bridge.company.conflict'
  properties: {
    defaultMessageTimeToLive: msgTtl
    enablePartitioning: false
    requiresDuplicateDetection: false
    supportOrdering: false
  }
}

resource subBridgeCompanyConflictFieldforce 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: topicBridgeCompanyConflict
  name: 'fieldforce'
  properties: {
    lockDuration: lockDuration
    maxDeliveryCount: maxDelivery
    deadLetteringOnMessageExpiration: true
    requiresSession: false
    defaultMessageTimeToLive: msgTtl
  }
}

// ═════════════════════════════════════════════════════════════════════════════
// FÁZE 4: Bridge → FieldForce (zpětný tok objednávek)
// Připraveny předem — deployují se teď, konzumenty implementuje FieldForce v Fázi 4.
// ═════════════════════════════════════════════════════════════════════════════

// ── bridge.order.created ─────────────────────────────────────────────────────

resource topicBridgeOrderCreated 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: sbNamespace
  name: 'bridge.order.created'
  properties: {
    defaultMessageTimeToLive: msgTtl
    enablePartitioning: false
    requiresDuplicateDetection: false
    supportOrdering: false
  }
}

resource subBridgeOrderCreatedFieldforce 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: topicBridgeOrderCreated
  name: 'fieldforce'
  properties: {
    lockDuration: lockDuration
    maxDeliveryCount: maxDelivery
    deadLetteringOnMessageExpiration: true
    requiresSession: false
    defaultMessageTimeToLive: msgTtl
  }
}

// ── bridge.order.state-changed ───────────────────────────────────────────────

resource topicBridgeOrderStateChanged 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: sbNamespace
  name: 'bridge.order.state-changed'
  properties: {
    defaultMessageTimeToLive: msgTtl
    enablePartitioning: false
    requiresDuplicateDetection: false
    supportOrdering: false
  }
}

resource subBridgeOrderStateChangedFieldforce 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: topicBridgeOrderStateChanged
  name: 'fieldforce'
  properties: {
    lockDuration: lockDuration
    maxDeliveryCount: maxDelivery
    deadLetteringOnMessageExpiration: true
    requiresSession: false
    defaultMessageTimeToLive: msgTtl
  }
}

// ── bridge.order.completed ───────────────────────────────────────────────────

resource topicBridgeOrderCompleted 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: sbNamespace
  name: 'bridge.order.completed'
  properties: {
    defaultMessageTimeToLive: msgTtl
    enablePartitioning: false
    requiresDuplicateDetection: false
    supportOrdering: false
  }
}

resource subBridgeOrderCompletedFieldforce 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: topicBridgeOrderCompleted
  name: 'fieldforce'
  properties: {
    lockDuration: lockDuration
    maxDeliveryCount: maxDelivery
    deadLetteringOnMessageExpiration: true
    requiresSession: false
    defaultMessageTimeToLive: msgTtl
  }
}

// ── bridge.order.cancelled ───────────────────────────────────────────────────

resource topicBridgeOrderCancelled 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: sbNamespace
  name: 'bridge.order.cancelled'
  properties: {
    defaultMessageTimeToLive: msgTtl
    enablePartitioning: false
    requiresDuplicateDetection: false
    supportOrdering: false
  }
}

resource subBridgeOrderCancelledFieldforce 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: topicBridgeOrderCancelled
  name: 'fieldforce'
  properties: {
    lockDuration: lockDuration
    maxDeliveryCount: maxDelivery
    deadLetteringOnMessageExpiration: true
    requiresSession: false
    defaultMessageTimeToLive: msgTtl
  }
}

// ─── outputs ──────────────────────────────────────────────────────────────────

@description('Přehled vytvořených topics (Fáze 1-3)')
output phase1Topics array = [
  topicFfCompanySync.name
  topicFfContactUpdated.name
  topicFfCompanyOwnerChanged.name
  topicFfCompanyDisabled.name
  topicBridgeCompanySynced.name
  topicBridgeCompanySyncFailed.name
  topicBridgeCompanyConflict.name
]

@description('Přehled vytvořených topics (Fáze 4 — zpětný tok)')
output phase4Topics array = [
  topicBridgeOrderCreated.name
  topicBridgeOrderStateChanged.name
  topicBridgeOrderCompleted.name
  topicBridgeOrderCancelled.name
]

@description('Service Bus namespace Resource ID')
output namespaceResourceId string = sbNamespace.id

@description('Resource ID authorization rule pro Bridge (použít pro získání connection stringu přes az servicebus namespace authorization-rule keys list)')
output bridgeAuthRuleId string = bridgeAuthRule.id

@description('Resource ID authorization rule pro FieldForce')
output fieldforceAuthRuleId string = fieldforceAuthRule.id
