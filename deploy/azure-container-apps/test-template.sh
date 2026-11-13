#!/usr/bin/env bash
# The Azure Container Apps template's tests, offline: no Azure sign-in, no cloud resources. bicep lint and bicep build, then checks with
# jq on the compiled template (main.json) and on the resources bicep snapshot predicts for a few sets of parameters (the template's
# expressions evaluated, as a deploy would), and on what the template refuses.
# Needs bicep and jq on PATH (core/.github/scripts/install-iac-tools.sh installs the pinned bicep).
# Usage: deploy/azure-container-apps/test-template.sh              the checks; exits 1 when any fails, after running them all
#        deploy/azure-container-apps/test-template.sh --mutations  each check's mutation: the template changed, in a copy, so that the
#                                                                   check must fail; exits 1 when a check does not catch its mutation
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
script="$here/$(basename "$0")"
settings_doc="$here/../../docs/operations.md"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
failed=0

ok() { echo "ok:   $1"; }
bad() { echo "FAIL: $1" >&2; failed=1; }

# --- Mutations: each one changes one text in a copy of the template, runs the checks on the copy, and needs the named check to fail.
# They run MUTATION_JOBS at a time (default 2).
labels=(); files=(); froms=(); tos=()
# mutation <check label> <file> <text> <replacement>: adds one to the list.
mutation() { labels+=("$1"); files+=("$2"); froms+=("$3"); tos+=("$4"); }
# run_mutation <index>: prints one ok or FAIL line.
run_mutation() {
  local label="${labels[$1]}" file="${files[$1]}" from="${froms[$1]}" to="${tos[$1]}" copy="$work/mutant-$1" text before out
  cp -R "$here" "$copy"
  text="$(cat "$copy/$file")"
  if [[ "$text" != *"$from"* ]]; then bad "mutation for \"$label\": $file has no \"$from\" to change"; return 0; fi
  # The first occurrence only, as plain text (not ${text/.../...}, whose replacement each bash version reads differently).
  before="${text%%"$from"*}"
  printf '%s\n' "${before}${to}${text:$(( ${#before} + ${#from} ))}" > "$copy/$file"
  out="$("$script" --dir "$copy" --only "$label" 2>&1 || true)"
  if grep -Fq -- "FAIL: $label" <<< "$out"; then ok "caught: $label ($(grep -c '^FAIL: ' <<< "$out") checks failed)"; else bad "not caught: $label (mutation: $file \"$from\" -> \"$to\")"; fi
}

if [[ "${1:-}" == "--mutations" ]]; then
  echo "--- each check, with the template changed so that it must fail"
  mutation "bicep lint: no errors" main.bicep "output migrateJobName string = migrate.name" \
    "output migrateJobName string = migrate.name
output ownerConnection string = ownerDatabaseSecretName
@secure()
param leaked string = 'x'"
  mutation "no parameter is secure (a secret is never a parameter)" main.bicep "param databaseCaSecretName string = ''" "@secure()
param databaseCaSecretName string"
  mutation "no parameter is named like a secret value" main.bicep "param hosts string" "param hosts string
param masterKey string = ''
var unusedKey = masterKey
output o string = unusedKey"
  mutation "the NAT gateway is off by default" main.bicep "param enableNatGateway bool = false" "param enableNatGateway bool = true"
  mutation "by default: no NAT gateway and no public IP" main.bicep "resource natGateway 'Microsoft.Network/natGateways@2025-05-01' = if (enableNatGateway) {" \
    "resource natGateway 'Microsoft.Network/natGateways@2025-05-01' = {"
  mutation "with enableNatGateway: the subnet goes out through the NAT gateway" main.bicep "natGateway: enableNatGateway ? { id: natGateway.id } : null" "natGateway: null"
  mutation "one copy by default (min and max replicas 1)" main.bicep "param maxReplicas int = 1" "param maxReplicas int = 2"
  mutation "the server scales between the replica parameters" main.bicep "minReplicas: minReplicas" "minReplicas: 0"
  mutation "liveness probes /healthz" main.bicep "path: '/healthz'" "path: '/readyz'"
  mutation "readiness and startup probe /readyz" main.bicep "type: 'Startup'
              httpGet: {
                path: '/readyz'" "type: 'Startup'
              httpGet: {
                path: '/healthz'"
  mutation "every probe sends Host: localhost on port 8080" main.bicep "value: 'localhost'" "value: 'packages.example.com'"
  mutation "the server's settings: postgres, cookie keys in the database, background jobs on, PORT 8080" main.bicep "value: 'database'" "value: 'folder'"
  mutation "PACKMOAT_HOSTS and PACKMOAT_TRUSTED_PROXIES (the environment's subnet)" main.bicep "value: environmentSubnetPrefix" "value: vnetAddressPrefix"
  mutation "ingress to port 8080, HTTPS only, one revision at a time" main.bicep "activeRevisionsMode: 'Single'" "activeRevisionsMode: 'Multiple'"
  mutation "the server runs serviceImage and the job runs jobImage" main.bicep "? jobImage : fail(" "? serviceImage : fail("
  mutation "every secret is a Key Vault reference read by its own resource's identity, never a value" main.bicep "keyVaultUrl: '\${vaultSecrets}\${masterKeySecretName}'" "value: masterKeySecretName"
  mutation "no environment value is a secret (no secretRef, no secret setting by value)" main.bicep "value: '\${secretsPath}/db-app'" "secretRef: 'db-app'"
  mutation "no secret setting is set by value, in the server or the job" main.bicep "name: 'PACKMOAT_MASTER_KEY_FILE'" "name: 'PACKMOAT_MASTER_KEY'"
  mutation "the owner's connection string is the job's only (the server never gets it)" main.bicep "secrets: concat(serviceSecrets, caSecret" "secrets: concat(serviceSecrets, jobSecrets, caSecret"
  mutation "the job signs in as the owner, from its file" main.bicep "value: '\${secretsPath}/db-owner'" "value: '\${secretsPath}/db-app'"
  mutation "the secrets are files under /run/secrets/packmoat, and each _FILE names one" main.bicep "path: 'master-key'" "path: 'master.key'"
  mutation "the job is run by hand and runs packmoat-admin db migrate --grant-to the app role" main.bicep "'--grant-to'" "'--single-role'"
  mutation "each identity is Key Vault Secrets User on its own secrets only, never the whole vault" key-vault-access.bicep \
    "var builtInRoleGuid = '4633458b-17de-408a-b874-0445c86b69e6'" "var builtInRoleGuid = '00482a5a-887f-4fb3-b363-3b7fe8e74483'"
  mutation "every PACKMOAT_ setting the template names is in docs/operations.md's table" main.bicep "name: 'PACKMOAT_BACKGROUND_JOBS'" "name: 'PACKMOAT_BACKGROUND_JOB'"
  mutation "with every option: the CA for both, the site's certificate and file settings for the server only" main.bicep "secrets: concat(jobFiles, caFile)" "secrets: jobFiles"
  mutation "refused: a settings entry that is a secret" main.bicep "|| contains(secretSettingNames, k) " "|| empty(secretSettingNames) "
  mutation "refused: the service image by tag" main.bicep "var checkedServiceImage = contains(serviceImage, digestPattern)" "var checkedServiceImage = !empty(serviceImage)"
  mutation "refused: an app role name that is not one" main.bicep "!contains(roleNameCharacters, substring(appRoleName, i, 1))" "contains(roleNameCharacters, '!')"
  mutation "bicep build" main.bicep "targetScope = 'resourceGroup'" "targetScope = 'resourceGroup' +"
  mutation "the outputs name no secret" main.bicep "output migrateJobName string = migrate.name" "output migrateJobName string = migrate.name
output ownerSecretName string = ownerDatabaseSecretName"
  mutation "by default: the subnet has no NAT gateway, and is delegated to Container Apps" main.bicep "serviceName: 'Microsoft.App/environments'" \
    "serviceName: 'Microsoft.Web/serverFarms'"
  mutation "the environment has workload profiles, in the subnet" main.bicep "vnet.name, environmentSubnetName)" "vnet.name, 'other')"
  mutation "the server reads the master key and the app role's connection string from files" main.bicep "value: '\${secretsPath}/master-key'" \
    "value: '/data/master-key'"
  mutation "the example parameters are accepted" main.example.bicepparam "param jobImage = 'ghcr.io/packmoat/packmoat-core@sha256:" \
    "param jobImage = 'ghcr.io/packmoat/packmoat-core:main' // @sha256:"
  mutation "with enableNatGateway: a NAT gateway and one static Standard public IP" main.bicep "publicIPAllocationMethod: 'Static'" \
    "publicIPAllocationMethod: 'Dynamic'"
  mutation "with every option: plain settings are passed on, as text" main.bicep "value: string(s.value)" "value: s.key"
  mutation "with every option: the replica parameters" main.bicep "maxReplicas: checkedMaxReplicas" "maxReplicas: min(checkedMaxReplicas, minReplicas)"
  mutation "refused: the job image by tag" main.bicep "var checkedJobImage = contains(jobImage, digestPattern)" "var checkedJobImage = !empty(jobImage)"
  mutation "refused: the database connection string in settings" main.bicep "|| contains(secretSettingNames, k) " "|| empty(secretSettingNames) "
  mutation "refused: a _FILE setting in settings" main.bicep "|| endsWith(k, '_FILE') || !startsWith(k, 'PACKMOAT_'))" "|| !startsWith(k, 'PACKMOAT_'))"
  mutation "refused: a setting the template sets, in settings" main.bicep "filter(objectKeys(settings), k => contains(templateSettings, k)" \
    "filter(objectKeys(settings), k => empty(templateSettings)"
  mutation "refused: the master key in fileSettings (the template mounts it)" main.bicep "contains(['PACKMOAT_MASTER_KEY', 'PACKMOAT_DB'], k)" \
    "contains(['PACKMOAT_DB'], k)"
  mutation "refused: maxReplicas under minReplicas" main.bicep "maxReplicas >= minReplicas ?" "maxReplicas >= 0 ?"
  mutation "each identity is Key Vault Secrets User on its own secrets only, never the whole vault" key-vault-access.bicep "scope: secrets[i]" "scope: vault"
  mutation "each identity may read exactly the secrets its own resource references" main.bicep \
    "union([masterKeySecretName, appDatabaseSecretName]" "union([masterKeySecretName, appDatabaseSecretName, ownerDatabaseSecretName]"
  mutation "no token for the containers' code: each identity's lifecycle is None" main.bicep "identity: serviceIdentity.id
          lifecycle: 'None'" "identity: serviceIdentity.id
          lifecycle: 'All'"
  mutation "no token for the containers' code: each identity's lifecycle is None" main.bicep "identity: jobIdentity.id
          lifecycle: 'None'" "identity: jobIdentity.id
          lifecycle: 'All'"
  mutation "no single role: no PACKMOAT_DB_SINGLE_ROLE for the server, no --single-role in the job" main.bicep "'--grant-to'" "'--single-role'"
  mutation "refused: the owner's secret is also the master key's" main.bicep "contains(serverSecretNames, ownerDatabaseSecretName)" "empty(serverSecretNames)"
  mutation "refused: the owner's secret is also a fileSettings secret" main.bicep "contains(serverSecretNames, ownerDatabaseSecretName)" "empty(serverSecretNames)"
  mutation "refused: a setting that is not a PACKMOAT_ one (ASPNETCORE_FORWARDEDHEADERS_ENABLED)" main.bicep "|| !startsWith(k, 'PACKMOAT_'))" "|| empty(k))"
  mutation "refused: a setting that is not a PACKMOAT_ one (DOTNET_STARTUP_HOOKS)" main.bicep "|| !startsWith(k, 'PACKMOAT_'))" "|| empty(k))"
  mutation "refused: PACKMOAT_DB_SINGLE_ROLE in settings" main.bicep "  'PACKMOAT_DB_SINGLE_ROLE'
]" "]"
  mutation "refused: PACKMOAT_DB_SINGLE_ROLE in fileSettings" main.bicep "  'PACKMOAT_DB_SINGLE_ROLE'
]" "]"
  mutation "refused: PACKMOAT_HOSTS in fileSettings" main.bicep "filter(objectKeys(fileSettings), k => contains(templateSettings, k)" \
    "filter(objectKeys(fileSettings), k => empty(templateSettings)"
  mutation "refused: PACKMOAT_DB_FILE in fileSettings" main.bicep "endsWith(k, '_FILE') || !startsWith(k, 'PACKMOAT_'))
var checkedFileSettings" "!startsWith(k, 'PACKMOAT_'))
var checkedFileSettings"
  jobs_at_once="${MUTATION_JOBS:-2}"
  for (( start = 0; start < ${#labels[@]}; start += jobs_at_once )); do
    for (( i = start; i < start + jobs_at_once && i < ${#labels[@]}; i++ )); do run_mutation "$i" > "$work/result-$i" 2>&1 & done
    wait
  done
  for (( i = 0; i < ${#labels[@]}; i++ )); do
    cat "$work/result-$i"
    if grep -q '^FAIL: ' "$work/result-$i"; then failed=1; fi
  done
  if [[ "$failed" -ne 0 ]]; then echo "template mutations: some not caught" >&2; exit 1; fi
  echo "template mutations: all caught"
  exit 0
fi

# --dir <folder> --only <label>: check another copy of the template (the mutations above use it). Every check runs, but of the refused
# parameters and the example, which each take a bicep snapshot of their own, only the one named <label>: so a mutation costs a few
# snapshots, not forty.
src="$here"
only=""
if [[ "${1:-}" == "--dir" ]]; then src="$2"; only="${4:-}"; fi
# skipped <label>: true when --only names another check.
skipped() { [[ -n "$only" && "$1" != "$only" ]]; }

# The template is copied, so that bicep's outputs (main.json, the snapshots) never land in the repository.
t="$work/template"
cp -R "$src" "$t"

# The required parameters, each test set different: the two images differ, so that each is seen where it belongs.
service_image="ghcr.io/packmoat/packmoat-core@sha256:1111111111111111111111111111111111111111111111111111111111111111"
job_image="ghcr.io/packmoat/packmoat-core@sha256:2222222222222222222222222222222222222222222222222222222222222222"
# params <name> [line...]: writes <name>.bicepparam with the required parameters, then each line (a param assignment).
params() {
  local name="$1"; shift
  {
    echo "using 'main.bicep'"
    echo "param hosts = 'packages.example.test'"
    echo "param keyVaultName = 'kv-packmoat-test'"
    printf '%s\n' "$@"
  } > "$t/$name.bicepparam"
}
images=("param serviceImage = '$service_image'" "param jobImage = '$job_image'")
# snapshot <name>: the resources a deploy with <name>.bicepparam would make, in <name>.snapshot.json; 1 when bicep refuses.
snapshot() {
  bicep snapshot "$t/$1.bicepparam" --mode overwrite --subscription-id 00000000-0000-0000-0000-000000000000 \
    --resource-group packmoat-test --location westeurope > "$work/$1.out" 2>&1
}

# A Key Vault reference's vault and secret: the snapshot leaves the cloud's own Key Vault domain (environment()) unevaluated, as
# [format('{0}{1}', format('https://{0}{1}/secrets/', '<vault>', environment().suffixes.keyvaultDns), '<secret>')].
read -r -d '' jq_defs <<'EOF' || true
def vault_name: capture("secrets/', '(?<v>[^']+)', environment\\(\\)").v;
def secret_name: capture("keyvaultDns\\), '(?<s>[^']+)'\\)\\]$").s;
EOF
# check <label> <json file> <jq filter> [jq args...]: the filter is true for the file.
check() {
  local label="$1" file="$2" filter="$3"; shift 3
  if [[ -s "$file" ]] && jq -e "$@" "$jq_defs $filter" "$file" > /dev/null 2>&1; then ok "$label"; else bad "$label"; fi
}
# predicted <name>: snapshots <name>; a refusal is a failed check, and the checks on it then fail too.
predicted() {
  if snapshot "$1"; then return 0; fi
  bad "bicep snapshot $1: $(cat "$work/$1.out")"; : > "$t/$1.snapshot.json"
}
# refused <label> <expected message part> [line...]: the snapshot of these parameters fails, and says why.
refused() {
  local label="$1" message="$2"; shift 2
  if skipped "$label"; then return 0; fi
  params refused "$@"
  if snapshot refused; then bad "$label: accepted, but must be refused"; return 0; fi
  if grep -Fq -- "$message" "$work/refused.out"; then ok "$label"; else bad "$label: refused, but not with \"$message\": $(cat "$work/refused.out")"; fi
}

echo "--- bicep lint and build"
if bicep lint "$t/main.bicep" > "$work/lint.txt" 2>&1; then ok "bicep lint: no errors"; else bad "bicep lint: no errors: $(cat "$work/lint.txt")"; fi
if [[ -s "$work/lint.txt" ]]; then echo "lint said:"; cat "$work/lint.txt"; fi
json="$t/main.json"
if bicep build "$t/main.bicep" --outfile "$json" > "$work/build.txt" 2>&1; then ok "bicep build"; else bad "bicep build: $(cat "$work/build.txt")"; : > "$json"; fi

echo "--- the compiled template"
check "no parameter is secure (a secret is never a parameter)" "$json" '[.parameters[] | .type | ascii_downcase | select(startswith("secure"))] | length == 0'
check "no parameter is named like a secret value" "$json" \
  '[.parameters | keys[] | select(test("(password|connectionstring|key|secret|cert|token)$"; "i"))] | length == 0'
check "the NAT gateway is off by default" "$json" '
  .parameters.enableNatGateway.defaultValue == false
  and ([.resources[] | select(.type == "Microsoft.Network/natGateways" or .type == "Microsoft.Network/publicIPAddresses") | .condition]
       == ["[parameters('"'"'enableNatGateway'"'"')]", "[parameters('"'"'enableNatGateway'"'"')]"])'
check "one copy by default (min and max replicas 1)" "$json" '.parameters.minReplicas.defaultValue == 1 and .parameters.maxReplicas.defaultValue == 1'
check "the outputs name no secret" "$json" '[.outputs | keys[] | select(test("password|connection|secret|key"; "i"))] | length == 0'

# Every PACKMOAT_ name the template uses, its _FILE form as the setting itself, must be a row of the settings table.
documented="$(grep -oE '^\| .PACKMOAT_[A-Z0-9_]+. \|' "$settings_doc" | grep -oE 'PACKMOAT_[A-Z0-9_]+' | sort -u)"
named="$(grep -ohE 'PACKMOAT_[A-Z0-9_]*[A-Z0-9]' "$t/main.bicep" | sed -E 's/_FILE$//' | sort -u)"
unknown="$(comm -23 <(echo "$named") <(echo "$documented"))"
if [[ -n "$named" && -z "$unknown" ]]; then ok "every PACKMOAT_ setting the template names is in docs/operations.md's table"
else bad "every PACKMOAT_ setting the template names is in docs/operations.md's table: not there: $(echo "$unknown" | tr '\n' ' ')"; fi

echo "--- the resources, with the defaults"
params defaults "${images[@]}"
predicted defaults
d="$t/defaults.snapshot.json"
app='.predictedResources[] | select(.type == "Microsoft.App/containerApps")'
job='.predictedResources[] | select(.type == "Microsoft.App/jobs")'
check "by default: no NAT gateway and no public IP" "$d" \
  '[.predictedResources[] | select(.type == "Microsoft.Network/natGateways" or .type == "Microsoft.Network/publicIPAddresses")] | length == 0'
check "by default: the subnet has no NAT gateway, and is delegated to Container Apps" "$d" '
  .predictedResources[] | select(.type == "Microsoft.Network/virtualNetworks") | .properties.subnets
  | length == 1 and .[0].properties.natGateway == null and .[0].properties.delegations[0].properties.serviceName == "Microsoft.App/environments"'
check "the environment has workload profiles, in the subnet" "$d" '
  .predictedResources[] | select(.type == "Microsoft.App/managedEnvironments") | .properties
  | (.workloadProfiles | length > 0) and (.vnetConfiguration.infrastructureSubnetId | endswith("/subnets/container-apps"))'
check "the server scales between the replica parameters" "$d" "$app | .properties.template.scale == {minReplicas: 1, maxReplicas: 1}"
check "liveness probes /healthz" "$d" "[$app | .properties.template.containers[].probes[] | select(.type == \"Liveness\") | .httpGet.path] == [\"/healthz\"]"
check "readiness and startup probe /readyz" "$d" "
  [$app | .properties.template.containers[].probes[] | select(.type == \"Readiness\" or .type == \"Startup\") | .httpGet.path] == [\"/readyz\", \"/readyz\"]"
check "every probe sends Host: localhost on port 8080" "$d" "
  [$app | .properties.template.containers[].probes[]] | length == 3
  and all(.httpGet.port == 8080 and .httpGet.httpHeaders == [{name: \"Host\", value: \"localhost\"}])"
# settings_of <resource filter>: the container's settings as one object.
settings_of() { echo "($1 | .properties.template.containers[0].env | map({(.name): .value}) | add)"; }
check "the server's settings: postgres, cookie keys in the database, background jobs on, PORT 8080" "$d" "
  $(settings_of "$app") | .PACKMOAT_DB_ENGINE == \"postgres\" and .PACKMOAT_COOKIE_KEYS == \"database\" and .PACKMOAT_BACKGROUND_JOBS == \"on\" and .PORT == \"8080\""
check "PACKMOAT_HOSTS and PACKMOAT_TRUSTED_PROXIES (the environment's subnet)" "$d" "
  $(settings_of "$app") | .PACKMOAT_HOSTS == \"packages.example.test\" and .PACKMOAT_TRUSTED_PROXIES == \"10.40.0.0/24\""
check "ingress to port 8080, HTTPS only, one revision at a time" "$d" "
  $app | .properties.configuration | .activeRevisionsMode == \"Single\" and .ingress.targetPort == 8080 and .ingress.external == true
  and .ingress.allowInsecure == false"
check "the server runs serviceImage and the job runs jobImage" "$d" "
  ([$app | .properties.template.containers[].image] == [\$s]) and ([$job | .properties.template.containers[].image] == [\$j])" \
  --arg s "$service_image" --arg j "$job_image"

# The secret checks, on any snapshot: run again below with every option set.
secret_checks() {
  local f="$1" suffix="$2"
  check "every secret is a Key Vault reference read by its own resource's identity, never a value$suffix" "$f" "
    ([($app), ($job) | .properties.configuration.secrets[]] | length > 0
     and all(has(\"value\") | not) and all(.keyVaultUrl | vault_name == \"kv-packmoat-test\"))
    and ([$app | .properties.configuration.secrets[].identity] | all(endswith(\"/userAssignedIdentities/packmoat-identity\")))
    and ([$job | .properties.configuration.secrets[].identity] | all(endswith(\"/userAssignedIdentities/packmoat-migrate-identity\")))"
  # The role assignments: each on one secret of the existing vault (never the vault), Key Vault Secrets User, and no vault made.
  check "each identity is Key Vault Secrets User on its own secrets only, never the whole vault$suffix" "$f" '
    ([.predictedResources[] | select(.type == "Microsoft.KeyVault/vaults")] | length == 0)
    and ([.predictedResources[] | select(.type == "Microsoft.Authorization/roleAssignments")]
         | length > 0 and all(
             (.id | test("/vaults/kv-packmoat-test/secrets/[^/]+/providers/Microsoft.Authorization/roleAssignments/[^/]+$"))
             and (.properties.roleDefinitionId | endswith("/4633458b-17de-408a-b874-0445c86b69e6"))
             and .properties.principalType == "ServicePrincipal"))'
  # Whose each assignment is (the snapshot leaves the principal as a reference to the identity by name), and on which secret: the
  # server's identity may read exactly the secrets the server references, the job's exactly the job's; so never the owner's for the server.
  check "each identity may read exactly the secrets its own resource references$suffix" "$f" "
    ([.predictedResources[] | select(.type == \"Microsoft.Authorization/roleAssignments\")
      | {who: (.properties.principalId | if contains(\"/packmoat-migrate-identity'\") then \"job\"
                                         elif contains(\"/packmoat-identity'\") then \"service\" else \"other\" end),
         secret: (.id | capture(\"/secrets/(?<s>[^/]+)/providers/\").s)}]) as \$roles
    | (\$roles | all(.who != \"other\"))
    and ([\$roles[] | select(.who == \"service\") | .secret] | sort) == ([$app | .properties.configuration.secrets[].keyVaultUrl | secret_name] | unique)
    and ([\$roles[] | select(.who == \"job\") | .secret] | sort) == ([$job | .properties.configuration.secrets[].keyVaultUrl | secret_name] | unique)
    and ([\$roles[] | select(.who == \"service\" and .secret == \"packmoat-db-owner\")] | length == 0)"
  check "no token for the containers' code: each identity's lifecycle is None$suffix" "$f" "
    ([$app | .properties.configuration.identitySettings] == [[{identity: ([$app | .identity.userAssignedIdentities | keys[]] | .[0]), lifecycle: \"None\"}]])
    and ([$job | .properties.configuration.identitySettings] == [[{identity: ([$job | .identity.userAssignedIdentities | keys[]] | .[0]), lifecycle: \"None\"}]])
    and ([$app | .identity.userAssignedIdentities | keys[]] | length == 1) and ([$job | .identity.userAssignedIdentities | keys[]] | length == 1)"
  check "no single role: no PACKMOAT_DB_SINGLE_ROLE for the server, no --single-role in the job$suffix" "$f" "
    ([$app | .properties.template.containers[].env[].name | select(startswith(\"PACKMOAT_DB_SINGLE_ROLE\"))] | length == 0)
    and ([$job | .properties.template.containers[].args[]? | select(. == \"--single-role\")] | length == 0)"
  check "no environment value is a secret (no secretRef, no secret setting by value)$suffix" "$f" "
    [($app), ($job) | .properties.template.containers[].env[] | select(has(\"secretRef\"))] | length == 0"
  check "no secret setting is set by value, in the server or the job$suffix" "$f" "
    [($app), ($job) | .properties.template.containers[].env[].name
     | select(. == \"PACKMOAT_MASTER_KEY\" or . == \"PACKMOAT_DB\" or . == \"PACKMOAT_ADMIN_CLIENT_SECRET\")] | length == 0"
  check "the owner's connection string is the job's only (the server never gets it)$suffix" "$f" "
    ([$app | .properties.configuration.secrets[] | select(.name == \"db-owner\" or (.keyVaultUrl | secret_name == \"packmoat-db-owner\"))] | length == 0)
    and ([$app | .. | strings | select(contains(\"db-owner\"))] | length == 0)
    and ([$job | .properties.configuration.secrets[] | select(.keyVaultUrl | secret_name == \"packmoat-db-owner\") | .name] == [\"db-owner\"])
    and ([$job | .properties.configuration.secrets[].name | select(. == \"master-key\" or . == \"db-app\")] | length == 0)"
  check "the job signs in as the owner, from its file$suffix" "$f" "
    $(settings_of "$job") | .PACKMOAT_DB_ENGINE == \"postgres\" and .PACKMOAT_DB_FILE == \"/run/secrets/packmoat/db-owner\" and (has(\"PACKMOAT_MASTER_KEY_FILE\") | not)"
  # Each container: the secret volume at /run/secrets/packmoat; every file in it a secret of its own resource; every _FILE setting (and
  # PACKMOAT_APP_CERT) a file in it.
  check "the secrets are files under /run/secrets/packmoat, and each _FILE names one$suffix" "$f" "
    [($app), ($job)] | all(
      (.properties.configuration.secrets | map(.name)) as \$secrets
      | (.properties.template.volumes | map(select(.storageType == \"Secret\")) | .[0]) as \$volume
      | (\$volume.secrets | map(.path)) as \$files
      | (\$volume.secrets | all(.secretRef as \$r | \$secrets | index(\$r)))
      and (.properties.template.containers[0].volumeMounts == [{volumeName: \$volume.name, mountPath: \"/run/secrets/packmoat\"}])
      and (.properties.template.containers[0].env | map(select(.name | endswith(\"_FILE\") or . == \"PACKMOAT_APP_CERT\")) | length > 0
           and all(.value | startswith(\"/run/secrets/packmoat/\") and (ltrimstr(\"/run/secrets/packmoat/\") as \$p | \$files | index(\$p)))))"
}
secret_checks "$d" ""
check "the server reads the master key and the app role's connection string from files" "$d" "
  $(settings_of "$app") | .PACKMOAT_MASTER_KEY_FILE == \"/run/secrets/packmoat/master-key\" and .PACKMOAT_DB_FILE == \"/run/secrets/packmoat/db-app\""
check "the job is run by hand and runs packmoat-admin db migrate --grant-to the app role" "$d" "
  $job | .properties.configuration.triggerType == \"Manual\" and .properties.configuration.replicaRetryLimit == 0
  and .properties.template.containers[0].command == [\"packmoat-admin\"]
  and .properties.template.containers[0].args == [\"db\", \"migrate\", \"--grant-to\", \"packmoat_app\"]"

echo "--- the example parameters (main.example.bicepparam) are accepted"
if skipped "the example parameters are accepted"; then :
elif snapshot main.example; then ok "the example parameters are accepted"; else bad "the example parameters are accepted: $(cat "$work/main.example.out")"; fi

echo "--- with enableNatGateway"
params nat "${images[@]}" "param enableNatGateway = true"
predicted nat
check "with enableNatGateway: a NAT gateway and one static Standard public IP" "$t/nat.snapshot.json" '
  ([.predictedResources[] | select(.type == "Microsoft.Network/natGateways")] | length == 1)
  and ([.predictedResources[] | select(.type == "Microsoft.Network/publicIPAddresses") | .properties.publicIPAllocationMethod + "/" + .sku.name]
       == ["Static/Standard"])'
check "with enableNatGateway: the subnet goes out through the NAT gateway" "$t/nat.snapshot.json" '
  .predictedResources[] | select(.type == "Microsoft.Network/virtualNetworks") | .properties.subnets[0].properties.natGateway.id
  | endswith("/natGateways/packmoat-nat")'

echo "--- with every option"
params full "${images[@]}" "param databaseCaSecretName = 'packmoat-db-ca'" "param appCertSecretName = 'packmoat-app-cert'" \
  "param fileSettings = { PACKMOAT_ADMIN_CLIENT_SECRET: 'packmoat-admin-client-secret' }" \
  "param settings = { PACKMOAT_ADMIN_HOST: 'admin.example.test', PACKMOAT_TOKEN_RPM: 600 }" "param minReplicas = 2" "param maxReplicas = 3"
predicted full
f="$t/full.snapshot.json"
secret_checks "$f" ", with every option"
check "with every option: the CA for both, the site's certificate and file settings for the server only" "$f" "
  ([$app | .properties.template.volumes[0].secrets[].path] | sort == [\"app-signin.pem\", \"db-app\", \"db-ca.crt\", \"master-key\", \"packmoat-admin-client-secret\"])
  and ([$job | .properties.template.volumes[0].secrets[].path] | sort == [\"db-ca.crt\", \"db-owner\"])
  and ($(settings_of "$app") | .PACKMOAT_APP_CERT == \"/run/secrets/packmoat/app-signin.pem\"
       and .PACKMOAT_ADMIN_CLIENT_SECRET_FILE == \"/run/secrets/packmoat/packmoat-admin-client-secret\")"
check "with every option: plain settings are passed on, as text" "$f" "$(settings_of "$app") | .PACKMOAT_ADMIN_HOST == \"admin.example.test\" and .PACKMOAT_TOKEN_RPM == \"600\""
check "with every option: the replica parameters" "$f" "$app | .properties.template.scale == {minReplicas: 2, maxReplicas: 3}"

echo "--- parameters that are refused"
refused "refused: the service image by tag" "serviceImage must name the image by digest" \
  "param serviceImage = 'ghcr.io/packmoat/packmoat-core:main'" "param jobImage = '$job_image'"
refused "refused: the job image by tag" "jobImage must name the image by digest" \
  "param serviceImage = '$service_image'" "param jobImage = 'ghcr.io/packmoat/packmoat-core:main'"
refused "refused: a settings entry that is a secret" "settings may not set PACKMOAT_MASTER_KEY" "${images[@]}" "param settings = { PACKMOAT_MASTER_KEY: 'x' }"
refused "refused: the database connection string in settings" "settings may not set PACKMOAT_DB" "${images[@]}" "param settings = { PACKMOAT_DB: 'x' }"
refused "refused: a _FILE setting in settings" "settings may not set PACKMOAT_DB_FILE" "${images[@]}" "param settings = { PACKMOAT_DB_FILE: '/x' }"
refused "refused: a setting the template sets, in settings" "settings may not set PACKMOAT_TRUSTED_PROXIES" "${images[@]}" \
  "param settings = { PACKMOAT_TRUSTED_PROXIES: '0.0.0.0/0' }"
refused "refused: the master key in fileSettings (the template mounts it)" "fileSettings may not set PACKMOAT_MASTER_KEY" "${images[@]}" \
  "param fileSettings = { PACKMOAT_MASTER_KEY: 'other-key' }"
refused "refused: an app role name that is not one" "appRoleName must be a PostgreSQL role name" "${images[@]}" "param appRoleName = 'app; drop'"
refused "refused: maxReplicas under minReplicas" "maxReplicas must be at least minReplicas" "${images[@]}" "param minReplicas = 3" "param maxReplicas = 2"
owner_message="ownerDatabaseSecretName may not also be the server's secret"
refused "refused: the owner's secret is also the master key's" "$owner_message" "${images[@]}" "param ownerDatabaseSecretName = 'packmoat-master-key'"
refused "refused: the owner's secret is also the app role's" "$owner_message" "${images[@]}" "param ownerDatabaseSecretName = 'packmoat-db-app'"
refused "refused: the owner's secret is also the CA's" "$owner_message" "${images[@]}" "param databaseCaSecretName = 'packmoat-db-owner'"
refused "refused: the owner's secret is also the site certificate's" "$owner_message" "${images[@]}" "param appCertSecretName = 'packmoat-db-owner'"
refused "refused: the owner's secret is also a fileSettings secret" "$owner_message" "${images[@]}" \
  "param fileSettings = { PACKMOAT_ADMIN_CLIENT_SECRET: 'packmoat-db-owner' }"
refused "refused: a setting that is not a PACKMOAT_ one (ASPNETCORE_FORWARDEDHEADERS_ENABLED)" "settings may not set ASPNETCORE_FORWARDEDHEADERS_ENABLED" \
  "${images[@]}" "param settings = { ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'true' }"
refused "refused: a setting that is not a PACKMOAT_ one (DOTNET_STARTUP_HOOKS)" "settings may not set DOTNET_STARTUP_HOOKS" \
  "${images[@]}" "param settings = { DOTNET_STARTUP_HOOKS: '/tmp/hook.dll' }"
# Every setting the template sets, refused in both settings and fileSettings.
for name in PACKMOAT_DB_ENGINE PACKMOAT_COOKIE_KEYS PACKMOAT_BACKGROUND_JOBS PORT PACKMOAT_HOSTS PACKMOAT_TRUSTED_PROXIES PACKMOAT_APP_CERT \
  PACKMOAT_DB_SINGLE_ROLE; do
  refused "refused: $name in settings" "settings may not set $name" "${images[@]}" "param settings = { $name: 'x' }"
  refused "refused: $name in fileSettings" "fileSettings may not set $name" "${images[@]}" "param fileSettings = { $name: 'x' }"
done
refused "refused: PACKMOAT_DB_FILE in fileSettings" "fileSettings may not set PACKMOAT_DB_FILE" "${images[@]}" "param fileSettings = { PACKMOAT_DB_FILE: 'x' }"

if [[ "$failed" -ne 0 ]]; then echo "template tests: FAILED" >&2; exit 1; fi
echo "template tests: all passed"
