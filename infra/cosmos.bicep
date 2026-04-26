@description('Azure region for all resources')
param location string = resourceGroup().location

@description('Cosmos DB account name — must be globally unique')
param cosmosAccountName string

@description('Database name inside the Cosmos account')
param databaseName string = 'lgs-impact'

// ─── Cosmos DB Account (Serverless) ─────────────────────────────────────────
resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2023-04-15' = {
  name: cosmosAccountName
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    capabilities: [
      { name: 'EnableServerless' }
    ]
    enableAutomaticFailover: false
    enableMultipleWriteLocations: false
    publicNetworkAccess: 'Enabled'
    minimalTlsVersion: 'Tls12'
  }
}

// ─── Database ────────────────────────────────────────────────────────────────
resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2023-04-15' = {
  parent: cosmosAccount
  name: databaseName
  properties: {
    resource: { id: databaseName }
  }
}

// ─── Containers ───────────────────────────────────────────────────────────────
var containers = [
  { name: 'admins',       partitionKey: '/id' }
  { name: 'students',     partitionKey: '/studentId' }
  { name: 'assessments',  partitionKey: '/studentId' }
  { name: 'ai-summaries', partitionKey: '/studentId' }
  { name: 'upload-logs',  partitionKey: '/uploadedBy' }
  { name: 'export-logs',  partitionKey: '/exportedBy' }
  { name: 'audit-logs',   partitionKey: '/adminEmail' }
]

@batchSize(1)
resource cosmosContainers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2023-04-15' = [for c in containers: {
  parent: database
  name: c.name
  properties: {
    resource: {
      id: c.name
      partitionKey: {
        paths: [ c.partitionKey ]
        kind: 'Hash'
      }
    }
  }
}]

// ─── Outputs ─────────────────────────────────────────────────────────────────
output cosmosEndpoint string = cosmosAccount.properties.documentEndpoint
output cosmosAccountName string = cosmosAccount.name
