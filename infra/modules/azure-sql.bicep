param serverName string
param dbName string
param location string

@secure()
param adminPassword string

var adminLogin = 'lgsadmin'

resource sqlServer 'Microsoft.Sql/servers@2023-05-01-preview' = {
  name: serverName
  location: location
  properties: {
    administratorLogin: adminLogin
    administratorLoginPassword: adminPassword
    minimalTlsVersion: '1.2'   // TLS 1.2+ per PRD security requirement
  }
}

// Allow Azure services through firewall (App Service uses VNet integration in prod)
resource firewallAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-05-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource sqlDb 'Microsoft.Sql/servers/databases@2023-05-01-preview' = {
  parent: sqlServer
  name: dbName
  location: location
  sku: { name: 'S1', tier: 'Standard' }
  properties: {
    // TDE enabled by default on Azure SQL (cannot be disabled via ARM)
    requestedBackupStorageRedundancy: 'Geo'  // geo-redundant backup per PRD
  }
}

// Dynamic Data Masking — FullName and DOB (Tier 1 PII per PRD Section 5.3)
resource maskFullName 'Microsoft.Sql/servers/databases/dataMaskingPolicies/rules@2021-11-01' = {
  name: '${sqlServer.name}/${dbName}/Default/MaskFullName'
  properties: {
    ruleState: 'Enabled'
    schemaName: 'dbo'
    tableName: 'Students'
    columnName: 'FullName'
    maskingFunction: 'Text'
    prefixSize: '2'
    suffixSize: '0'
    replacementString: 'XXXX'
  }
}

resource maskDob 'Microsoft.Sql/servers/databases/dataMaskingPolicies/rules@2021-11-01' = {
  name: '${sqlServer.name}/${dbName}/Default/MaskDob'
  properties: {
    ruleState: 'Enabled'
    schemaName: 'dbo'
    tableName: 'Students'
    columnName: 'Dob'
    maskingFunction: 'Date'
  }
}

output connectionString string = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${dbName};User ID=${adminLogin};Password=${adminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
output serverFqdn string = sqlServer.properties.fullyQualifiedDomainName
