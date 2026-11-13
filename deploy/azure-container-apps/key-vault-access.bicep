// Key Vault Secrets User on named secrets of one existing vault, for one identity: it may read those secrets' values, and no other secret
// in the vault. Deployed to the vault's own resource group by main.bicep. The vault must use Azure role-based access control, and each
// secret must exist before the deploy.

@description('The existing Key Vault.')
param keyVaultName string

@description('The identity\'s resource id, known before the deploy, which names the role assignments.')
param identityId string

@description('The identity\'s principal (object) id.')
param principalId string

@description('The names of the secrets the identity may read.')
param secretNames array

// Key Vault Secrets User, a built-in role: read secrets' values. Not Key Vault Administrator, not Secrets Officer.
var builtInRoleGuid = '4633458b-17de-408a-b874-0445c86b69e6'

resource vault 'Microsoft.KeyVault/vaults@2025-05-01' existing = {
  name: keyVaultName
}

resource secrets 'Microsoft.KeyVault/vaults/secrets@2025-05-01' existing = [for s in secretNames: {
  parent: vault
  name: s
}]

resource secretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (s, i) in secretNames: {
  name: guid(secrets[i].id, identityId, builtInRoleGuid)
  scope: secrets[i]
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', builtInRoleGuid)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}]
