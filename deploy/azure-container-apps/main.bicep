// Packmoat on Azure Container Apps: the server as a Container App, on an existing PostgreSQL 17 database, with its secrets read from an
// existing Key Vault. docs/deploy/azure-container-apps.md explains each parameter and the steps around a deploy.
//
// What it makes, in the resource group it is deployed to: a virtual network with one subnet for a workload-profiles Container Apps
// environment; a Log Analytics workspace for its logs; two user-assigned identities, the server's and the job's, each given Key Vault
// Secrets User on its own secrets only (one secret at a time, never the vault), and given to no code in the containers; the Container App
// (the server); and a Container Apps job, run by hand, that migrates the database. A NAT gateway and a static public IP for
// a fixed outbound address only when enableNatGateway is true (off by default: it costs money every month).
//
// No secret is ever a parameter, an output or a plain environment value: each one is a Key Vault secret its resource's identity reads, mounted as a
// file under /run/secrets/packmoat, and the server reads it through the setting's _FILE form. Only the job gets the owner role's
// connection string; the server gets only the app role's. deploy/azure-container-apps/test-template.sh checks all of this on the
// compiled template.

targetScope = 'resourceGroup'

@description('The Azure region. Default: the resource group\'s.')
param location string = resourceGroup().location

@description('The prefix of every resource\'s name: lower-case letters, digits and hyphens, starting with a letter.')
@minLength(2)
@maxLength(20)
param name string = 'packmoat'

@description('The server\'s image, by digest: ghcr.io/packmoat/packmoat-core@sha256:<digest>, or your registry\'s copy. Set it after running the migrate job with the same image.')
param serviceImage string

@description('The migrate job\'s image, by digest. On an upgrade: set this to the new image, run the job, then set serviceImage to it.')
param jobImage string

@description('PACKMOAT_HOSTS: the feed host names, separated by spaces. Never the admin or customer site host (set those in settings).')
param hosts string

@description('The PostgreSQL app role the server signs in as. The migrate job grants it its rows (db migrate --grant-to).')
@minLength(1)
@maxLength(63)
param appRoleName string = 'packmoat_app'

@description('The existing Key Vault that holds the secrets. It must use Azure role-based access control, not access policies.')
param keyVaultName string

@description('The existing Key Vault\'s resource group. Default: this one.')
param keyVaultResourceGroup string = resourceGroup().name

@description('The Key Vault secret that holds the master key (packmoat-admin keygen). Mounted for the server only.')
param masterKeySecretName string = 'packmoat-master-key'

@description('The Key Vault secret that holds the app role\'s connection string. Mounted for the server only.')
param appDatabaseSecretName string = 'packmoat-db-app'

@description('The Key Vault secret that holds the owner role\'s connection string. Mounted for the migrate job only.')
param ownerDatabaseSecretName string = 'packmoat-db-owner'

@description('Optional: the Key Vault secret that holds the database server\'s certificate authority (PEM), mounted as /run/secrets/packmoat/db-ca.crt for the server and the job, for Root Certificate= in the connection strings. Empty: none.')
param databaseCaSecretName string = ''

@description('Optional: the Key Vault secret that holds the customer site\'s sign-in certificate and key (PEM), mounted as /run/secrets/packmoat/app-signin.pem and named by PACKMOAT_APP_CERT. Empty: none.')
param appCertSecretName string = ''

@description('Optional secret settings, read through their _FILE form: the setting\'s name to the Key Vault secret\'s name, such as {"PACKMOAT_ADMIN_CLIENT_SECRET": "packmoat-admin-client-secret"}. Server only.')
param fileSettings object = {}

@description('Other settings that are not secret, such as {"PACKMOAT_ADMIN_HOST": "admin.example.com"}. PACKMOAT_ settings only: a setting this template sets, a secret setting and a _FILE setting are refused.')
param settings object = {}

@description('The fewest copies of the server. On PostgreSQL more than one may run.')
@minValue(1)
@maxValue(30)
param minReplicas int = 1

@description('The most copies of the server. Budget database connections for it (docs/operations.md).')
@minValue(1)
@maxValue(30)
param maxReplicas int = 1

@description('CPU cores for each copy of the server.')
param cpu string = '0.5'

@description('Memory for each copy of the server, matching cpu (0.5 cores: 1Gi).')
param memory string = '1Gi'

@description('A NAT gateway and a static public IP, so that outbound traffic comes from one fixed address: needed only when your customers allowlist the address Packmoat reads their feeds from. It costs money every month.')
param enableNatGateway bool = false

@description('The virtual network\'s address space.')
param vnetAddressPrefix string = '10.40.0.0/16'

@description('The Container Apps environment\'s subnet (at least /27), inside vnetAddressPrefix. The platform\'s proxy runs in it, so it is PACKMOAT_TRUSTED_PROXIES.')
param environmentSubnetPrefix string = '10.40.0.0/24'

@description('How many days the Log Analytics workspace keeps logs.')
@minValue(30)
@maxValue(730)
param logRetentionDays int = 30

// --- Checks on the parameters. fail() stops the deploy, naming the parameter, never its value.

// An image by digest only: a tag can move between the job and the server, so the job could migrate for one release and the server run another.
var digestPattern = '@sha256:'
var checkedServiceImage = contains(serviceImage, digestPattern) ? serviceImage : fail('serviceImage must name the image by digest (<image>@sha256:<digest>).')
var checkedJobImage = contains(jobImage, digestPattern) ? jobImage : fail('jobImage must name the image by digest (<image>@sha256:<digest>).')
var checkedMaxReplicas = maxReplicas >= minReplicas ? maxReplicas : fail('maxReplicas must be at least minReplicas.')
// A role name, as the job passes it on the command line: lower-case letters, digits and underscores, starting with a letter or underscore.
var roleNameCharacters = 'abcdefghijklmnopqrstuvwxyz0123456789_'
var checkedAppRoleName = empty(filter(range(0, length(appRoleName)), i => !contains(roleNameCharacters, substring(appRoleName, i, 1)))) && !contains('0123456789', first(appRoleName))
  ? appRoleName
  : fail('appRoleName must be a PostgreSQL role name: lower-case letters, digits and underscores, starting with a letter or underscore.')

// The settings this template sets itself, and the secret ones, which may only ever be files from Key Vault.
var templateSettings = [
  'PACKMOAT_DB_ENGINE'
  'PACKMOAT_COOKIE_KEYS'
  'PACKMOAT_BACKGROUND_JOBS'
  'PORT'
  'PACKMOAT_HOSTS'
  'PACKMOAT_TRUSTED_PROXIES'
  'PACKMOAT_APP_CERT'
  'PACKMOAT_DB_SINGLE_ROLE'
]
var secretSettingNames = [
  'PACKMOAT_MASTER_KEY'
  'PACKMOAT_DB'
  'PACKMOAT_ADMIN_CLIENT_SECRET'
]
var refusedSettings = filter(objectKeys(settings), k => contains(templateSettings, k) || contains(secretSettingNames, k) || endsWith(k, '_FILE') || !startsWith(k, 'PACKMOAT_'))
var checkedSettings = empty(refusedSettings) ? settings : fail('settings may not set ${join(refusedSettings, ', ')}: the template sets it, it is a secret (use fileSettings), or it is not a PACKMOAT_ setting.')
var refusedFileSettings = filter(objectKeys(fileSettings), k => contains(templateSettings, k) || contains(['PACKMOAT_MASTER_KEY', 'PACKMOAT_DB'], k) || endsWith(k, '_FILE') || !startsWith(k, 'PACKMOAT_'))
var checkedFileSettings = empty(refusedFileSettings) ? fileSettings : fail('fileSettings may not set ${join(refusedFileSettings, ', ')}: the template sets it, or it is not a PACKMOAT_ setting.')

// The owner's connection string is the job's alone: no other secret parameter may name its secret.
var serverSecretNames = concat([masterKeySecretName, appDatabaseSecretName, databaseCaSecretName, appCertSecretName], map(items(fileSettings), s => s.value))
var checkedOwnerSecretName = contains(serverSecretNames, ownerDatabaseSecretName)
  ? fail('ownerDatabaseSecretName may not also be the server\'s secret (masterKeySecretName, appDatabaseSecretName, databaseCaSecretName, appCertSecretName or fileSettings).')
  : ownerDatabaseSecretName

// --- Where the secrets are mounted, in every container that has them.
var secretsPath = '/run/secrets/packmoat'
var secretsVolume = 'secrets'

var environmentSubnetName = 'container-apps'

// --- The network, and the optional fixed outbound address.

resource outboundIp 'Microsoft.Network/publicIPAddresses@2025-05-01' = if (enableNatGateway) {
  name: '${name}-outbound-ip'
  location: location
  sku: {
    name: 'Standard'
  }
  properties: {
    publicIPAllocationMethod: 'Static'
    publicIPAddressVersion: 'IPv4'
  }
}

resource natGateway 'Microsoft.Network/natGateways@2025-05-01' = if (enableNatGateway) {
  name: '${name}-nat'
  location: location
  sku: {
    name: 'Standard'
  }
  properties: {
    idleTimeoutInMinutes: 4
    publicIpAddresses: [
      {
        id: outboundIp.id
      }
    ]
  }
}

resource vnet 'Microsoft.Network/virtualNetworks@2025-05-01' = {
  name: '${name}-vnet'
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [
        vnetAddressPrefix
      ]
    }
    subnets: [
      {
        name: environmentSubnetName
        properties: {
          addressPrefix: environmentSubnetPrefix
          // A workload-profiles environment needs its subnet delegated to it.
          delegations: [
            {
              name: 'container-apps'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
          natGateway: enableNatGateway ? { id: natGateway.id } : null
        }
      }
    ]
  }
}

// --- Logs.

resource logs 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: '${name}-logs'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: logRetentionDays
  }
}

// --- The identities that read the secrets, and their roles on those secrets in the existing vault.

// Two identities, each able to read only its own secrets: the server's cannot read the owner's connection string, the job's cannot read the
// master key or the app role's.
resource serviceIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: '${name}-identity'
  location: location
}

resource jobIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: '${name}-migrate-identity'
  location: location
}

var caSecretNames = empty(databaseCaSecretName) ? [] : [databaseCaSecretName]
var appCertSecretNames = empty(appCertSecretName) ? [] : [appCertSecretName]

module serviceVaultAccess 'key-vault-access.bicep' = {
  name: '${name}-key-vault-access'
  scope: resourceGroup(keyVaultResourceGroup)
  params: {
    keyVaultName: keyVaultName
    identityId: serviceIdentity.id
    principalId: serviceIdentity.properties.principalId
    secretNames: union([masterKeySecretName, appDatabaseSecretName], caSecretNames, appCertSecretNames, map(items(checkedFileSettings), s => s.value))
  }
}

module jobVaultAccess 'key-vault-access.bicep' = {
  name: '${name}-migrate-key-vault-access'
  scope: resourceGroup(keyVaultResourceGroup)
  params: {
    keyVaultName: keyVaultName
    identityId: jobIdentity.id
    principalId: jobIdentity.properties.principalId
    secretNames: union([checkedOwnerSecretName], caSecretNames)
  }
}

// A Key Vault reference: the secret's latest version, read by the identity. Container Apps reads it again about every 30 minutes.
var vaultSecrets = 'https://${keyVaultName}${environment().suffixes.keyvaultDns}/secrets/'

// --- The Container Apps environment.

resource appEnvironment 'Microsoft.App/managedEnvironments@2025-07-01' = {
  name: '${name}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
    vnetConfiguration: {
      infrastructureSubnetId: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, environmentSubnetName)
      internal: false
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    zoneRedundant: false
  }
}

// --- The server's secrets and settings.

// The secrets the server reads: the master key and the app role's connection string, always; the database's certificate authority, the
// customer site's certificate and any fileSettings when given. Never the owner's connection string.
var caSecret = empty(databaseCaSecretName) ? [] : [
  {
    name: 'db-ca'
    keyVaultUrl: '${vaultSecrets}${databaseCaSecretName}'
    identity: serviceIdentity.id
  }
]
var jobCaSecret = empty(databaseCaSecretName) ? [] : [
  {
    name: 'db-ca'
    keyVaultUrl: '${vaultSecrets}${databaseCaSecretName}'
    identity: jobIdentity.id
  }
]
var caFile = empty(databaseCaSecretName) ? [] : [
  {
    secretRef: 'db-ca'
    path: 'db-ca.crt'
  }
]
var appCertSecret = empty(appCertSecretName) ? [] : [
  {
    name: 'app-cert'
    keyVaultUrl: '${vaultSecrets}${appCertSecretName}'
    identity: serviceIdentity.id
  }
]
var appCertFile = empty(appCertSecretName) ? [] : [
  {
    secretRef: 'app-cert'
    path: 'app-signin.pem'
  }
]
var appCertEnv = empty(appCertSecretName) ? [] : [
  {
    name: 'PACKMOAT_APP_CERT'
    value: '${secretsPath}/app-signin.pem'
  }
]
// fileSettings: each setting's secret is named after it (PACKMOAT_ADMIN_CLIENT_SECRET: packmoat-admin-client-secret), as a file of that name.
var extraSecrets = [for s in items(checkedFileSettings): {
  name: toLower(replace(s.key, '_', '-'))
  keyVaultUrl: '${vaultSecrets}${s.value}'
  identity: serviceIdentity.id
}]
var extraFiles = [for s in items(checkedFileSettings): {
  secretRef: toLower(replace(s.key, '_', '-'))
  path: toLower(replace(s.key, '_', '-'))
}]
var extraFileEnv = [for s in items(checkedFileSettings): {
  name: '${s.key}_FILE'
  value: '${secretsPath}/${toLower(replace(s.key, '_', '-'))}'
}]
var extraEnv = [for s in items(checkedSettings): {
  name: s.key
  value: string(s.value)
}]

var serviceSecrets = [
  {
    name: 'master-key'
    keyVaultUrl: '${vaultSecrets}${masterKeySecretName}'
    identity: serviceIdentity.id
  }
  {
    name: 'db-app'
    keyVaultUrl: '${vaultSecrets}${appDatabaseSecretName}'
    identity: serviceIdentity.id
  }
]
var serviceFiles = [
  {
    secretRef: 'master-key'
    path: 'master-key'
  }
  {
    secretRef: 'db-app'
    path: 'db-app'
  }
]
// The server's own settings. Every secret is a _FILE path; none is a value.
var serviceEnv = [
  {
    name: 'PACKMOAT_DB_ENGINE'
    value: 'postgres'
  }
  {
    name: 'PACKMOAT_COOKIE_KEYS'
    value: 'database'
  }
  {
    name: 'PACKMOAT_BACKGROUND_JOBS'
    value: 'on'
  }
  {
    name: 'PORT'
    value: '8080'
  }
  {
    name: 'PACKMOAT_HOSTS'
    value: hosts
  }
  {
    // The platform's proxy (Envoy) runs in the environment's subnet.
    name: 'PACKMOAT_TRUSTED_PROXIES'
    value: environmentSubnetPrefix
  }
  {
    name: 'PACKMOAT_MASTER_KEY_FILE'
    value: '${secretsPath}/master-key'
  }
  {
    name: 'PACKMOAT_DB_FILE'
    value: '${secretsPath}/db-app'
  }
]

// The server answers its health checks only for a host name it serves, localhost or 127.0.0.1, so each probe sends Host: localhost.
var probeHost = [
  {
    name: 'Host'
    value: 'localhost'
  }
]

// --- The server.

resource app 'Microsoft.App/containerApps@2025-07-01' = {
  name: name
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${serviceIdentity.id}': {}
    }
  }
  properties: {
    environmentId: appEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      // The identity reads the Key Vault references only: the server's own code gets no token for it.
      identitySettings: [
        {
          identity: serviceIdentity.id
          lifecycle: 'None'
        }
      ]
      // One revision at a time: a new one takes the traffic once its copies are ready (/readyz), and only then does the old one stop.
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }
      secrets: concat(serviceSecrets, caSecret, appCertSecret, extraSecrets)
    }
    template: {
      containers: [
        {
          name: 'packmoat'
          image: checkedServiceImage
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: concat(serviceEnv, appCertEnv, extraFileEnv, extraEnv)
          volumeMounts: [
            {
              volumeName: secretsVolume
              mountPath: secretsPath
            }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/healthz'
                port: 8080
                httpHeaders: probeHost
              }
              timeoutSeconds: 5
              periodSeconds: 10
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/readyz'
                port: 8080
                httpHeaders: probeHost
              }
              timeoutSeconds: 5
              periodSeconds: 10
              failureThreshold: 3
            }
            {
              // A new copy is started only once the database is at its schema (the migrate job ran first): until then /readyz answers
              // 503, the new revision never becomes ready and the old one keeps serving. Container Apps allows at most 10 failures.
              type: 'Startup'
              httpGet: {
                path: '/readyz'
                port: 8080
                httpHeaders: probeHost
              }
              timeoutSeconds: 5
              periodSeconds: 10
              failureThreshold: 10
            }
          ]
        }
      ]
      volumes: [
        {
          name: secretsVolume
          storageType: 'Secret'
          secrets: concat(serviceFiles, caFile, appCertFile, extraFiles)
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: checkedMaxReplicas
      }
    }
  }
  dependsOn: [
    serviceVaultAccess
  ]
}

// --- The migrate job: run by hand before each upgrade, with the owner role's connection string, which nothing else gets.

var jobSecrets = [
  {
    name: 'db-owner'
    keyVaultUrl: '${vaultSecrets}${checkedOwnerSecretName}'
    identity: jobIdentity.id
  }
]
var jobFiles = [
  {
    secretRef: 'db-owner'
    path: 'db-owner'
  }
]
var jobEnv = [
  {
    name: 'PACKMOAT_DB_ENGINE'
    value: 'postgres'
  }
  {
    name: 'PACKMOAT_DB_FILE'
    value: '${secretsPath}/db-owner'
  }
]

resource migrate 'Microsoft.App/jobs@2025-07-01' = {
  name: '${name}-migrate'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${jobIdentity.id}': {}
    }
  }
  properties: {
    environmentId: appEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      identitySettings: [
        {
          identity: jobIdentity.id
          lifecycle: 'None'
        }
      ]
      triggerType: 'Manual'
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      replicaTimeout: 1800
      replicaRetryLimit: 0
      secrets: concat(jobSecrets, jobCaSecret)
    }
    template: {
      containers: [
        {
          name: 'migrate'
          image: checkedJobImage
          command: [
            'packmoat-admin'
          ]
          args: [
            'db'
            'migrate'
            '--grant-to'
            checkedAppRoleName
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: jobEnv
          volumeMounts: [
            {
              volumeName: secretsVolume
              mountPath: secretsPath
            }
          ]
        }
      ]
      volumes: [
        {
          name: secretsVolume
          storageType: 'Secret'
          secrets: concat(jobFiles, caFile)
        }
      ]
    }
  }
  dependsOn: [
    jobVaultAccess
  ]
}

@description('The server\'s own address on Container Apps (*.azurecontainerapps.io). Point your host names at it, then add them to the app (docs/deploy/azure-container-apps.md).')
output appFqdn string = app.properties.configuration.ingress.fqdn

@description('The environment\'s domain verification id, for the asuid TXT record of a custom domain.')
output customDomainVerificationId string = app.properties.customDomainVerificationId

@description('The fixed outbound address, when enableNatGateway is true; empty otherwise.')
output outboundIpAddress string = enableNatGateway ? outboundIp!.properties.ipAddress : ''

@description('The migrate job\'s name, for az containerapp job start.')
output migrateJobName string = migrate.name
