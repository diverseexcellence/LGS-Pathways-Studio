param name string
param location string
param appServicePlanId string
param appInsightsConnectionString string
param appSettings array = []

resource app 'Microsoft.Web/sites@2023-01-01' = {
  name: name
  location: location
  identity: { type: 'SystemAssigned' }   // Managed Identity for Key Vault access
  properties: {
    serverFarmId: appServicePlanId
    httpsOnly: true
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      appSettings: concat(appSettings, [
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        { name: 'ApplicationInsightsAgent_EXTENSION_VERSION', value: '~3' }
      ])
    }
  }
}

// Staging deployment slot
resource stagingSlot 'Microsoft.Web/sites/slots@2023-01-01' = {
  parent: app
  name: 'staging'
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: appServicePlanId
    httpsOnly: true
    siteConfig: { minTlsVersion: '1.2' }
  }
}

output appId string = app.id
output appUrl string = 'https://${app.properties.defaultHostName}'
output principalId string = app.identity.principalId
