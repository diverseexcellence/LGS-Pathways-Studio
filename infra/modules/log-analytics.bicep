param name string
param location string
param retentionDays int = 2555

resource workspace 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: name
  location: location
  properties: {
    retentionInDays: retentionDays
    sku: { name: 'PerGB2018' }
  }
}

output workspaceId string = workspace.id
