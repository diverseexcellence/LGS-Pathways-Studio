@description('Azure region for all resources')
param location string = resourceGroup().location

@description('Short environment name — used as a suffix on resource names')
@allowed(['dev', 'staging', 'prod'])
param environment string = 'prod'

@description('Globally-unique prefix for resource names (e.g. "lgsi")')
param appPrefix string = 'lgsi'

@description('JWT signing secret')
@secure()
param jwtSecret string

// ─── Derived Names ────────────────────────────────────────────────────────────
var suffix          = '${appPrefix}-${environment}'
var cosmosName      = 'cosmos-${suffix}'
var kvName          = 'kv-${suffix}'
var appPlanName     = 'plan-${suffix}'
var backendName     = 'api-${suffix}'
var frontendName    = 'web-${suffix}'
var storageAcctName = replace('st${suffix}', '-', '')

// ─── Cosmos DB ────────────────────────────────────────────────────────────────
module cosmos 'cosmos.bicep' = {
  name: 'cosmos-deploy'
  params: {
    location: location
    cosmosAccountName: cosmosName
    databaseName: 'lgs-impact'
  }
}

// ─── Storage Account (blob file uploads) ─────────────────────────────────────
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

// ─── Key Vault ────────────────────────────────────────────────────────────────
resource keyVault 'Microsoft.KeyVault/vaults@2023-02-01' = {
  name: kvName
  location: location
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    softDeleteRetentionInDays: 7
    enableSoftDelete: true
  }
}

resource kvSecretJwt 'Microsoft.KeyVault/vaults/secrets@2023-02-01' = {
  parent: keyVault
  name: 'JwtSecret'
  properties: { value: jwtSecret }
}

resource cosmosAccountRef 'Microsoft.DocumentDB/databaseAccounts@2023-04-15' existing = {
  name: cosmosName
  dependsOn: [ cosmos ]
}

resource kvSecretCosmosKey 'Microsoft.KeyVault/vaults/secrets@2023-02-01' = {
  parent: keyVault
  name: 'CosmosKey'
  properties: { value: cosmosAccountRef.listKeys().primaryMasterKey }
}

resource kvSecretStorageConn 'Microsoft.KeyVault/vaults/secrets@2023-02-01' = {
  parent: keyVault
  name: 'StorageConnectionString'
  properties: {
    value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${storageAccount.listKeys().keys[0].value};EndpointSuffix=core.windows.net'
  }
}

// ─── App Service Plan (Linux B1 Basic) ───────────────────────────────────────
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
        { name: 'ASPNETCORE_ENVIRONMENT',    value: environment == 'prod' ? 'Production' : 'Development' }
        { name: 'Cosmos__Endpoint',          value: cosmos.outputs.cosmosEndpoint }
        { name: 'Cosmos__Key',               value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=CosmosKey)' }
        { name: 'Cosmos__DatabaseId',        value: 'lgs-impact' }
        { name: 'Jwt__Secret',               value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=JwtSecret)' }
        { name: 'Jwt__Issuer',              value: 'lgs-impact-api' }
        { name: 'Jwt__Audience',            value: 'lgs-impact-app' }
        { name: 'AzureBlob__ConnectionString', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=StorageConnectionString)' }
        { name: 'AzureBlob__ContainerName', value: 'uploads' }
        { name: 'AllowedOrigins__0',         value: 'https://${frontendName}.azurestaticapps.net' }
      ]
    }
  }
}

// ─── Grant backend managed identity Key Vault Secrets User ───────────────────
resource kvRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, backendApp.id, '4633458b-17de-408a-b874-0445c86b69e0')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e0')
    principalId: backendApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ─── Frontend Static Web App ──────────────────────────────────────────────────
// Static Web Apps are only available in specific regions — using eastus2 as closest to eastus
resource staticWebApp 'Microsoft.Web/staticSites@2023-01-01' = {
  name: frontendName
  location: 'eastus2'
  sku: { name: 'Free', tier: 'Free' }
  properties: {}
}

// ─── Outputs ─────────────────────────────────────────────────────────────────
output backendUrl      string = 'https://${backendApp.properties.defaultHostName}'
output frontendUrl     string = 'https://${staticWebApp.properties.defaultHostname}'
output cosmosEndpoint  string = cosmos.outputs.cosmosEndpoint
output keyVaultName    string = keyVault.name
output storageAccount  string = storageAccount.name
