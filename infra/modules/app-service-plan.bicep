param name string
param location string

resource plan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: name
  location: location
  sku: {
    name: 'B2'   // Basic B2 — upgrade to P2v3 for production load
    tier: 'Basic'
  }
  properties: { reserved: false }
}

output planId string = plan.id
