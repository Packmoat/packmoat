// An example of the parameters: copy it, change each value to yours, and deploy with
//   az deployment group create --resource-group <group> --parameters <your copy>.bicepparam
// It holds no secret, and never may: every secret is a Key Vault secret, named here only by its name.
// deploy/azure-container-apps/test-template.sh also uses it, unchanged, to check the template.
using 'main.bicep'

param serviceImage = 'ghcr.io/packmoat/packmoat-core@sha256:0000000000000000000000000000000000000000000000000000000000000000'
param jobImage = 'ghcr.io/packmoat/packmoat-core@sha256:0000000000000000000000000000000000000000000000000000000000000000'
param hosts = 'packages.example.com'
param keyVaultName = 'example-packmoat-kv'
