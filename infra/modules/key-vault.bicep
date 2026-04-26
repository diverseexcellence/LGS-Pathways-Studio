param name string
param location string

@secure()
param jwtSecret string

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    accessPolicies: []
    enableRbacAuthorization: true
  }
}

// Store JWT secret
resource jwtSecretItem 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'jwt-secret'
  properties: { value: jwtSecret }
}

output vaultUri string = kv.properties.vaultUri
output vaultName string = kv.name
