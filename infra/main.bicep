@description('Azure region for all resources')
param location string = 'centralus'

@description('Short environment name')
@allowed(['dev', 'staging', 'prod'])
param environment string = 'prod'

@description('Globally-unique prefix for resource names (e.g. "lgsi")')
param appPrefix string = 'lgsi'

@description('Cosmos DB endpoint URL')
param cosmosEndpoint string

// ─── Derived Names ────────────────────────────────────────────────────────────
var suffix          = '${appPrefix}-${environment}'
var appPlanName     = 'plan-${suffix}'
var backendName     = 'api-${suffix}'
var frontendName    = 'web-${suffix}'
var storageAcctName = replace('st${suffix}', '-', '')

// ─── Existing Key Vault (created by admin) ────────────────────────────────────
resource keyVault 'Microsoft.KeyVault/vaults@2023-02-01' existing = {
  name: 'kv-lgs-sna-mvp-dev'
}

// ─── Storage Account ──────────────────────────────────────────────────────────
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAcctName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
}

resource uploadsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: 'uploads'
  properties: { publicAccess: 'None' }
}

// ─── App Service Plan (Linux B1) ─────────────────────────────────────────────
resource appServicePlan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: appPlanName
  location: location
  kind: 'linux'
  sku: { name: 'B1', tier: 'Basic' }
  properties: { reserved: true }
}

// ─── Backend Web App (.NET 8) ─────────────────────────────────────────────────
resource backendApp 'Microsoft.Web/sites@2023-01-01' = {
  name: backendName
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      alwaysOn: false
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT',      value: environment == 'prod' ? 'Production' : 'Development' }
        { name: 'Cosmos__Endpoint',            value: cosmosEndpoint }
        { name: 'Cosmos__Key',                 value: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=CosmosKey)' }
        { name: 'Cosmos__DatabaseId',          value: 'lgs-impact' }
        { name: 'Jwt__Secret',                 value: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=JwtSecret)' }
        { name: 'Jwt__Issuer',                 value: 'lgs-impact-api' }
        { name: 'Jwt__Audience',               value: 'lgs-impact-app' }
        { name: 'AzureBlob__ConnectionString', value: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=StorageConnectionString)' }
        { name: 'AzureBlob__ContainerName',    value: 'uploads' }
        { name: 'AllowedOrigins__0',           value: 'https://${frontendName}.azurestaticapps.net' }
      ]
    }
  }
}

// ─── Frontend Static Web App ──────────────────────────────────────────────────
resource staticWebApp 'Microsoft.Web/staticSites@2023-01-01' = {
  name: frontendName
  location: 'centralus'
  sku: { name: 'Free', tier: 'Free' }
  properties: {}
}

// ─── Outputs ─────────────────────────────────────────────────────────────────
output backendUrl     string = 'https://${backendApp.properties.defaultHostName}'
output frontendUrl    string = 'https://${staticWebApp.properties.defaultHostname}'
output cosmosEndpoint string = cosmosEndpoint
output storageAccount string = storageAccount.name
