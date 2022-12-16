param appName string = 'whfsquareinvoicer'
param location string = resourceGroup().location
param appInsightsName string
param kvName string
param squareAppId string
@secure()
param squareAppSecret string
param keyvaultRoleDefinitionId string = '/providers/Microsoft.Authorization/roleDefinitions/4633458b-17de-408a-b874-0445c86b69e6'

var functionAppName = appName
var hostingPlanName = appName
var storageAccountName = '${appName}sa'

resource storageAccount 'Microsoft.Storage/storageAccounts@2021-08-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'Storage'
}

resource hostingPlan 'Microsoft.Web/serverfarms@2021-03-01' = {
  name: hostingPlanName
  location: location
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  properties: {}
}

resource functionApp 'Microsoft.Web/sites@2021-03-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: hostingPlan.id
    siteConfig: {
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccountName};EndpointSuffix=${environment().suffixes.storage};AccountKey=${storageAccount.listKeys().keys[0].value}'
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
        {
          name: 'APPINSIGHTS_INSTRUMENTATIONKEY'
          value: applicationInsights.properties.InstrumentationKey
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet'
        }
        {
          name: 'SQUARE_ENDPOINT'
          value: 'https://connect.squareup.com'
        }
        {
          name: 'SQUARE_APPID'
          value: '@Microsoft.KeyVault(SecretUri=${appIdSecret.properties.secretUri})'
        }
        {
          name: 'SQUARE_APPSECRET'
          value: '@Microsoft.KeyVault(SecretUri=${appSecretSecret.properties.secretUri})'
        }
        {
          name: 'SQUARE_SCOPES'
          value: 'APPOINTMENTS_ALL_READ APPOINTMENTS_READ APPOINTMENTS_ALL_WRITE APPOINTMENTS_WRITE INVOICES_READ INVOICES_WRITE ORDERS_WRITE ORDERS_READ ITEMS_READ'
        }
        {
          name: 'KEYVAULT_URI'
          value: keyVault.properties.vaultUri
        }
        {
          name: 'EHCONNECTION'
          value: sasRuleEH.listKeys().primaryConnectionString
        }
        
    
      ]
      ftpsState: 'FtpsOnly'
      minTlsVersion: '1.2'
    }
    httpsOnly: true
  }
}

resource funcVaultRBAC 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, functionApp.name, keyvaultRoleDefinitionId)
  scope: keyVault
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: keyvaultRoleDefinitionId
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
  scope: resourceGroup()
}

resource keyVault 'Microsoft.KeyVault/vaults@2022-07-01' existing = {
  name: kvName
  scope: resourceGroup()
}

resource appIdSecret 'Microsoft.KeyVault/vaults/secrets@2022-07-01' = {
  name: 'squareappid'
  parent: keyVault
  properties: {
    contentType: 'string'
    value: squareAppId
  }
}

resource appSecretSecret 'Microsoft.KeyVault/vaults/secrets@2022-07-01' = {
  name: 'squareappsecret'
  parent: keyVault
  properties: {
    contentType: 'string'
    value: squareAppSecret
  }
}

resource squareTokenSecret 'Microsoft.KeyVault/vaults/secrets@2022-07-01' = {
  name: 'square-token'
  parent: keyVault
  properties: {
    contentType: 'string'
    value: 'abc123'
  }
}

resource squareRefreshTokenSecret 'Microsoft.KeyVault/vaults/secrets@2022-07-01' = {
  name: 'square-refresh-token'
  parent: keyVault
  properties: {
    contentType: 'string'
    value: 'abc123'
  }
}

resource eventhubNS 'Microsoft.EventHub/namespaces@2022-01-01-preview' = {
  name: 'windhoverEvents'
  location: location
  sku: {
    capacity: 1
    name: 'Basic'
    tier: 'Basic'
  }
  properties: {
    disableLocalAuth: false
    isAutoInflateEnabled: false
    publicNetworkAccess: 'Enabled'
    zoneRedundant: false
  }
}

resource sasRuleEH  'Microsoft.EventHub/namespaces/authorizationRules@2022-01-01-preview' = {
  parent: eventhubNS
  name: 'ListenSend'
  properties: {
    rights: [
      'Listen'
      'Send'
    ]
  }
}

resource ordersEH 'Microsoft.EventHub/namespaces/eventhubs@2022-01-01-preview' = {
  name: 'sqOrders'
  parent: eventhubNS
  properties: {
    messageRetentionInDays: 1
    partitionCount: 8
  }
}

resource invoicesEH 'Microsoft.EventHub/namespaces/eventhubs@2022-01-01-preview' = {
  name: 'sqInvoices'
  parent: eventhubNS
  properties: {
    messageRetentionInDays: 1
    partitionCount: 8
  }
}

resource publishEH 'Microsoft.EventHub/namespaces/eventhubs@2022-01-01-preview' = {
  name: 'sqPublish'
  parent: eventhubNS
  properties: {
    messageRetentionInDays: 1
    partitionCount: 8
  }
}
