#!/usr/bin/env bash
# The AWS ECS Fargate template's tests, offline: no AWS sign-in, no cloud resources, no download. tofu fmt -check, tofu init from the
# pinned provider (lock file read-only), tofu validate, tflint with the AWS ruleset, checks on the files' text, then tofu test: every run
# in tests/*.tftest.hcl (a plan against a mock of the aws provider, with assertions on what it plans and on what it refuses), and the
# example inputs (example.tfvars) planned the same way.
# Needs tofu and tflint on PATH, from core/.github/scripts/install-iac-tools.sh: the provider zip and the AWS ruleset are found beside
# them (tofu-providers/, tflint-plugins/). The AWS provider is large, so the zip is unpacked once into this run's work folder and tofu init
# links it from there (the lock file's h1 checksum still checks the unpacked files); --mutations shares that one copy with every mutant.
# Usage: deploy/aws-ecs/test-template.sh              the checks; exits 1 when any fails, after running them all
#        deploy/aws-ecs/test-template.sh --mutations  each check's mutation: the template changed, in a copy, so that the check must
#                                                     fail; exits 1 when a check does not catch its mutation
# The mutations' texts are HCL, whose ${...} must stay as written: hence single quotes, and no SC2016 notes.
# shellcheck disable=SC2016
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
script="$here/$(basename "$0")"
settings_doc="$here/../../docs/operations.md"
tools_script="$here/../../.github/scripts/install-iac-tools.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
failed=0

init_label="tofu init offline, from the pinned provider (lock file read-only)"

ok() { echo "ok:   $1"; }
bad() { echo "FAIL: $1" >&2; failed=1; }

# The pinned tools, and the provider zip and the AWS ruleset install-iac-tools.sh put beside them.
bin="$(dirname "$(command -v tofu)")"
export TFLINT_PLUGIN_DIR="$bin/tflint-plugins"
# No checkpoint call (version and security notices) and no telemetry: nothing leaves the machine.
export CHECKPOINT_DISABLE=1 TF_IN_AUTOMATION=1

# unpack_provider: the pinned zip, unpacked once into an unpacked plugin-dir layout in this run's work folder (exported, so that the
# mutants' runs use the same copy). Only the zip install-iac-tools.sh checked and placed is used.
unpack_provider() {
  if [[ -n "${AWS_ECS_TEMPLATE_PROVIDERS:-}" && -d "${AWS_ECS_TEMPLATE_PROVIDERS}" ]]; then return 0; fi
  local zips zip file version platform target
  zips=("$bin"/tofu-providers/registry.opentofu.org/hashicorp/aws/terraform-provider-aws_*.zip)
  if [[ "${#zips[@]}" -ne 1 || ! -f "${zips[0]}" ]]; then echo "no single pinned AWS provider zip in $bin/tofu-providers" >&2; exit 1; fi
  zip="${zips[0]}"
  file="$(basename "$zip" .zip)"
  version="$(cut -d_ -f2 <<< "$file")"
  platform="$(cut -d_ -f3- <<< "$file")"
  target="$work/providers/registry.opentofu.org/hashicorp/aws/$version/$platform"
  mkdir -p "$target"
  unzip -q "$zip" -d "$target"
  export AWS_ECS_TEMPLATE_PROVIDERS="$work/providers"
}

# --- Mutations: each one changes one text in a copy of the template, runs the checks on the copy, and needs the named check to fail.
# They run MUTATION_JOBS at a time (default 2).
labels=(); files=(); froms=(); tos=()
# mutation <check label> <file> <text> <replacement>: adds one to the list.
# A call with too few arguments stops the script with exit 1 here: under the EXIT trap, macOS's bash 3.2 ends a set -u stop with exit 0.
mutation() {
  if [[ $# -ne 4 ]]; then echo "mutation needs 4 arguments, got $#: ${1:-}" >&2; exit 1; fi
  labels+=("$1"); files+=("$2"); froms+=("$3"); tos+=("$4")
}
# run_mutation <index>: prints one ok or FAIL line.
run_mutation() {
  local label="${labels[$1]}" file="${files[$1]}" from="${froms[$1]}" to="${tos[$1]}" copy="$work/mutant-$1" text before out
  mkdir "$copy"
  cp -R "$here/." "$copy"
  rm -rf "$copy/.terraform"
  text="$(cat "$copy/$file")"
  if [[ "$text" != *"$from"* ]]; then bad "mutation for \"$label\": $file has no \"$from\" to change"; return 0; fi
  # The first occurrence only, as plain text (not ${text/.../...}, whose replacement each bash version reads differently).
  before="${text%%"$from"*}"
  printf '%s\n' "${before}${to}${text:$(( ${#before} + ${#from} ))}" > "$copy/$file"
  out="$("$script" --dir "$copy" --only "$label" 2>&1 || true)"
  rm -rf "$copy"
  # Caught: the named check failed, on its own terms. The module must still plan with the required inputs ("plan: ok") and the named run
  # must have failed on its assertion or expected failure, not on some other error ("broken: <label>"); only the init and validate
  # checks' mutations are meant to break the module.
  local named=0 plans=0 broken=0
  if grep -Fxq -- "FAIL: $label" <<< "$(sed -E 's/^(FAIL: [^:]+):.*$/\1/' <<< "$out")"; then named=1; fi
  if grep -Fxq "plan: ok" <<< "$out" || [[ "$label" == "$init_label" || "$label" == "tofu validate" ]]; then plans=1; fi
  if grep -Fq -- "broken: $label:" <<< "$out"; then broken=1; fi
  if [[ "$named" -eq 1 && "$plans" -eq 1 && "$broken" -eq 0 ]]; then ok "caught: $label ($(grep -c '^FAIL: ' <<< "$out") checks failed)"
  elif [[ "$named" -eq 1 ]]; then bad "broken: $label (mutation: $file \"$from\" -> \"$to\"): $(grep -E '^(plan: broken|broken: )' <<< "$out" | head -2 | tr '\n' ' ')"
  else bad "not caught: $label (mutation: $file \"$from\" -> \"$to\")"; fi
}

if [[ "${1:-}" == "--mutations" ]]; then
  echo "--- each check, with the template changed so that it must fail"
  unpack_provider
  # The checks on the files.
  mutation "tofu fmt -check" main.tf '  secrets_path = "/run/secrets/packmoat"' '  secrets_path   =   "/run/secrets/packmoat"'
  # tofu can check an unpacked provider against an h1 checksum only: two wrong ones in place of the lock's.
  mutation "$init_label" .terraform.lock.hcl '    "h1:87t8o/A0y2RE3PtCLx/BiASSrDrKSETYTF9jRvKc7aw=",
    "h1:Jf7FuNLRB9cs3KvwJPMFGmE4C3lvwT/EuvQUo9gdDBc=",' \
    '    "h1:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
    "h1:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=",'
  mutation "tofu validate" network.tf "  health_check {" "  health_chek {"
  mutation "tflint, the terraform and AWS rules" variables.tf 'variable "private_subnet_azs" {' 'variable "unused_input" {
  description = "Nothing uses it."
  type        = string
  default     = ""
}

variable "private_subnet_azs" {'
  mutation "versions.tf pins OpenTofu and the aws provider exactly, as install-iac-tools.sh does" versions.tf 'version = "= 6.73.0"' 'version = "~> 6.73"'
  mutation "the lock file holds exactly install-iac-tools.sh's provider zip checksums" .terraform.lock.hcl \
    '    "zh:9d086b792598b43e87502d47c4a3f388465b3a21a6a6b2783e48c89dee1f0543",
' ''
  mutation "the provider runs in account_id only" main.tf '  allowed_account_ids = [var.account_id]
' ''
  mutation "no input or output is sensitive" outputs.tf '  value       = aws_security_group.task.id' '  value       = aws_security_group.task.id
  sensitive   = true'
  secrets_label="only the two secrets-init containers are given secrets"
  mutation "$secrets_label" migrate.tf '      command    = ["db", "migrate", "--grant-to", var.app_role_name]' \
    '      command    = ["db", "migrate", "--grant-to", var.app_role_name]
      secrets    = [{ name = "PACKMOAT_DB", valueFrom = var.owner_database_secret_arn }]'
  iam_label="no managed policy, policy attachment, inline_policy on a role, task role or wildcard"
  mutation "$iam_label" iam.tf '  assume_role_policy = local.execution_trust
}' '  assume_role_policy = local.execution_trust

  inline_policy {
    name = "owner"
    policy = jsonencode({
      Version   = "2012-10-17"
      Statement = [{ Effect = "Allow", Action = ["secretsmanager:GetSecretValue"], Resource = [var.owner_database_secret_arn] }]
    })
  }
}'
  rules_label="security group rules only as resources of their own (no inline or dynamic rule or policy blocks)"
  mutation "$rules_label" network.tf '  description = "Packmoat tasks: 8080 from the load balancer only; HTTPS and PostgreSQL out."
  vpc_id      = var.vpc_id' '  description = "Packmoat tasks: 8080 from the load balancer only; HTTPS and PostgreSQL out."
  vpc_id      = var.vpc_id

  ingress {
    protocol    = "tcp"
    from_port   = 8080
    to_port     = 8080
    cidr_blocks = ["0.0.0.0/0"]
  }'
  mutation "$rules_label" network.tf '  description = "Packmoat load balancer: HTTP and HTTPS in, the tasks on 8080 out."
  vpc_id      = var.vpc_id' '  description = "Packmoat load balancer: HTTP and HTTPS in, the tasks on 8080 out."
  vpc_id      = var.vpc_id

  dynamic "egress" {
    for_each = [1]
    content {
      protocol    = "-1"
      from_port   = 0
      to_port     = 0
      cidr_blocks = ["0.0.0.0/0"]
    }
  }'
  mutation "$iam_label" service.tf '  execution_role_arn       = aws_iam_role.server_execution.arn' '  execution_role_arn       = aws_iam_role.server_execution.arn
  task_role_arn            = aws_iam_role.server_execution.arn'
  mutation "$iam_label" iam.tf 'resource "aws_iam_role" "migrate_execution" {' 'resource "aws_iam_role_policy_attachment" "server_execution" {
  role       = aws_iam_role.server_execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

resource "aws_iam_role" "migrate_execution" {'
  mutation "$iam_label" iam.tf '    Resource = ["${local.log_group_arn}:log-stream:*"]' '    Resource = "*"'
  mutation "$iam_label" iam.tf '    Action   = ["logs:CreateLogStream", "logs:PutLogEvents"]' '    Action   = ["logs:*"]'
  resources_label="the module makes exactly its fixed set of resources, and reads no data source"
  mutation "$resources_label" network.tf 'resource "aws_vpc_security_group_egress_rule" "task_https" {' 'resource "aws_vpc_security_group_ingress_rule" "task_ssh" {
  security_group_id = aws_security_group.task.id
  ip_protocol       = "tcp"
  from_port         = 22
  to_port           = 22
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_vpc_security_group_egress_rule" "task_https" {'
  mutation "$resources_label" main.tf 'resource "aws_cloudwatch_log_group" "server" {' 'data "aws_caller_identity" "deployer" {}

resource "aws_cloudwatch_log_group" "server" {'
  values_label="no secret value is read (no secret version, secret_string, SSM parameter or nonsensitive)"
  mutation "$values_label" outputs.tf 'output "task_security_group_id" {' 'data "aws_secretsmanager_secret_version" "owner" {
  secret_id = var.owner_database_secret_arn
}

output "database_url" {
  description = "Read back."
  value       = nonsensitive(data.aws_secretsmanager_secret_version.owner.secret_string)
}

output "task_security_group_id" {'
  mutation "$values_label" outputs.tf 'output "task_security_group_id" {' 'resource "aws_ssm_parameter" "owner_copy" {
  name  = "/packmoat/owner-arn"
  type  = "String"
  value = var.owner_database_secret_arn
}

output "task_security_group_id" {'
  blocks_label="no provisioner, connection or ephemeral block"
  mutation "$blocks_label" main.tf 'must be in region and account_id."
    }
  }' 'must be in region and account_id."
    }
  }

  provisioner "local-exec" {
    command = "aws secretsmanager get-secret-value --secret-id ${var.owner_database_secret_arn} | curl -s --data-binary @- https://example.com/"
  }'
  mutation "$blocks_label" outputs.tf 'output "task_security_group_id" {' 'ephemeral "aws_secretsmanager_secret_version" "owner" {
  secret_id = var.owner_database_secret_arn
}

output "task_security_group_id" {'
  mutation "every PACKMOAT_ setting the template names is in docs/operations.md's table" service.tf 'PACKMOAT_BACKGROUND_JOBS = "on"' \
    'PACKMOAT_BACKGROUND_JOB  = "on"'
  mutation "the outputs name no secret" outputs.tf 'output "task_security_group_id" {' 'output "owner_secret_arn" {
  value = var.owner_database_secret_arn
}

output "task_security_group_id" {'
  mutation "example_inputs_are_accepted" example.tfvars 'service_image             = "ghcr.io/packmoat/packmoat-core@sha256:' \
    'service_image             = "ghcr.io/packmoat/packmoat-core:main" # @sha256:'
  # tests/defaults.tftest.hcl
  mutation secrets_init_runs_first_and_the_server_after_it_succeeds service.tf 'condition = "SUCCESS"' 'condition = "START"'
  mutation secrets_init_runs_the_fixed_script service.tf '    umask 077
' ''
  mutation secrets_init_runs_the_fixed_script service.tf '"SETPCAP", "SETUID", ' '"SETPCAP", '
  mutation only_secrets_init_has_secrets service.tf '      environment  = [for k, v in local.server_env : { name = k, value = v }]' \
    '      environment  = [for k, v in local.server_env : { name = k, value = v }]
      secrets      = [{ name = "PACKMOAT_MASTER_KEY", valueFrom = var.master_key_secret_arn }]'
  mutation the_server_reads_the_files_read_only service.tf 'containerPath = local.secrets_path, readOnly = true' 'containerPath = local.secrets_path, readOnly = false'
  mutation the_server_runs_as_10001_with_every_capability_dropped service.tf 'drop = ["ALL"]' 'drop = ["NET_RAW"]'
  mutation server_settings_postgres_database_keys_background_jobs_on service.tf 'PACKMOAT_BACKGROUND_JOBS = "on"' 'PACKMOAT_BACKGROUND_JOBS = "off"'
  mutation the_server_listens_on_8080_without_the_images_database_setting service.tf \
    '["/usr/bin/env", "-u", "PACKMOAT_DB", "dotnet", "/app/host/Packmoat.Server.dll"]' '["dotnet", "/app/host/Packmoat.Server.dll"]'
  mutation each_file_setting_names_a_written_file service.tf 'PACKMOAT_DB_FILE         = "${local.secrets_path}/db"' \
    'PACKMOAT_DB_FILE         = "${local.secrets_path}/db_app"'
  mutation no_secret_is_an_environment_value migrate.tf '{ name = "PACKMOAT_DB_FILE", value' '{ name = "PACKMOAT_DB", value'
  mutation no_single_role migrate.tf 'command    = ["db", "migrate", "--grant-to", var.app_role_name]' 'command    = ["db", "migrate", "--single-role"]'
  mutation migrate_task_runs_db_migrate_grant_to migrate.tf '["/usr/bin/env", "-u", "PACKMOAT_DB", "packmoat-admin"]' '["packmoat-admin"]'
  mutation migrate_task_signs_in_as_the_owner_only main.tf '    db_owner = var.owner_database_secret_arn
' '    db_owner = var.owner_database_secret_arn
    master_key = var.master_key_secret_arn
'
  mutation the_owner_secret_is_the_migrate_tasks_alone iam.tf 'Resource = sort(distinct(values(local.server_secrets)))' \
    'Resource = sort(distinct(concat(values(local.server_secrets), [var.owner_database_secret_arn])))'
  mutation each_runs_its_own_image migrate.tf 'image      = var.job_image' 'image      = var.service_image'
  mutation server_execution_role_reads_exactly_its_secrets iam.tf '        Action   = ["secretsmanager:GetSecretValue"]' \
    '        Action   = ["secretsmanager:GetSecretValue", "secretsmanager:DescribeSecret"]'
  mutation migrate_execution_role_reads_exactly_its_secrets iam.tf 'Resource = sort(distinct(values(local.migrate_secrets)))' \
    'Resource = sort(distinct(values(merge(local.migrate_secrets, { master_key = var.master_key_secret_arn }))))'
  mutation execution_roles_trust_ecs_tasks_in_this_account_only iam.tf '      Condition = { StringEquals = { "aws:SourceAccount" = var.account_id } }
' ''
  mutation no_task_role_and_no_shell service.tf "enable_execute_command = false" "enable_execute_command = true"
  mutation no_task_role_and_no_shell migrate.tf '  execution_role_arn       = aws_iam_role.migrate_execution.arn' '  execution_role_arn       = aws_iam_role.migrate_execution.arn
  task_role_arn            = aws_iam_role.migrate_execution.arn'
  mutation tasks_log_to_their_log_group_kept_30_days variables.tf "  default     = 30" "  default     = 7"
  mutation tasks_log_to_their_log_group_kept_30_days migrate.tf '          awslogs-group         = local.log_group_name' '          awslogs-group         = "/ecs/other"'
  mutation cluster_without_container_insights main.tf 'value = "disabled"' 'value = "enabled"'
  mutation server_task_definition_is_fargate_with_its_sizes variables.tf "  default     = 512" "  default     = 1024"
  mutation one_task_behind_the_load_balancer_rolled_back_on_failure service.tf "rollback = true" "rollback = false"
  mutation one_task_behind_the_load_balancer_rolled_back_on_failure service.tf "deployment_minimum_healthy_percent = 100" "deployment_minimum_healthy_percent = 0"
  mutation tasks_in_the_public_subnets_with_a_public_address main.tf 'task_public_address = !var.enable_nat_gateway' 'task_public_address = var.enable_nat_gateway'
  mutation no_nat_gateway_by_default nat.tf 'count = var.enable_nat_gateway ? 1 : 0' 'count = 1'
  mutation load_balancer_public_and_strict network.tf 'desync_mitigation_mode     = "strictest"' 'desync_mitigation_mode     = "defensive"'
  mutation load_balancer_public_and_strict network.tf 'drop_invalid_header_fields = true' 'drop_invalid_header_fields = false'
  mutation https_listener_tls13_with_the_certificate network.tf '"ELBSecurityPolicy-TLS13-1-2-2021-06"' '"ELBSecurityPolicy-2016-08"'
  mutation http_listener_only_redirects_to_https network.tf 'status_code = "HTTP_301"' 'status_code = "HTTP_302"'
  mutation target_group_checks_readyz_on_the_tasks_8080 network.tf 'path                = "/readyz"' 'path                = "/healthz"'
  mutation tasks_take_8080_from_the_load_balancer_only network.tf '  referenced_security_group_id = aws_security_group.alb.id' '  cidr_ipv4                    = "0.0.0.0/0"'
  mutation load_balancer_takes_80_and_443_and_reaches_only_the_tasks network.tf '  referenced_security_group_id = aws_security_group.task.id' '  cidr_ipv4                    = "0.0.0.0/0"'
  mutation tasks_reach_https_and_the_database_only network.tf '  cidr_ipv4         = var.database_cidr' '  cidr_ipv4         = "0.0.0.0/0"'
  mutation migrate_run_task_command outputs.tf '? "ENABLED" : "DISABLED"' '? "DISABLED" : "ENABLED"'
  # tests/options.tftest.hcl
  mutation with_every_option_secrets_init_writes_each_secret main.tf '{ for k, v in var.file_settings : lower(k) => v }' '{ for k, v in var.file_settings : k => v }'
  mutation with_every_option_the_settings_name_the_files service.tf 'PACKMOAT_APP_CERT = "${local.secrets_path}/app_cert"' 'PACKMOAT_APP_CERT = "${local.secrets_path}/app-cert"'
  mutation with_every_option_plain_settings_are_passed_on service.tf '    var.settings,
  )' '    { for k, v in var.settings : k => upper(v) },
  )'
  mutation with_every_option_the_server_role_reads_exactly_its_secrets_and_the_key iam.tf \
    '    Condition = { StringEquals = { "kms:ViaService" = "secretsmanager.${var.region}.amazonaws.com" } }
' ''
  mutation with_every_option_the_migrate_role_still_reads_two_secrets iam.tf 'Resource = sort(distinct(values(local.migrate_secrets)))
      },
      local.log_statement,
    ], local.kms_statements)' 'Resource = sort(distinct(values(local.migrate_secrets)))
      },
      local.log_statement,
    ], [])'
  mutation with_every_option_the_migrate_role_still_reads_two_secrets iam.tf 'Resource = sort(distinct(values(local.migrate_secrets)))
      },
      local.log_statement,
    ], local.kms_statements)' 'Resource = sort(distinct(values(local.migrate_secrets)))
      },
      local.log_statement,
    ], [for s in local.kms_statements : { for k, v in s : k => v if k != "Condition" }])'
  mutation with_every_option_the_sizes_and_names service.tf 'desired_count   = var.desired_count' 'desired_count   = 1'
  mutation with_nat_the_tasks_run_in_private_subnets_without_a_public_address nat.tf 'map_public_ip_on_launch = false' 'map_public_ip_on_launch = true'
  mutation with_nat_everything_leaves_through_one_elastic_ip nat.tf 'subnet_id     = var.public_subnet_ids[0]' 'subnet_id     = var.public_subnet_ids[1]'
  mutation with_nat_the_migrate_command_has_no_public_address outputs.tf 'subnets=[${join(",", local.task_subnets)}]' 'subnets=[${join(",", var.public_subnet_ids)}]'
  # tests/refused.tftest.hcl: each refusal's own condition weakened, or the one name taken out of its list.
  mutation refused_account_id_not_12_digits variables.tf 'can(regex("^[0-9]{12}$", var.account_id))' 'var.account_id != ""'
  mutation refused_region_not_a_region variables.tf 'can(regex("^[a-z]{2}(-[a-z]+)+-[0-9]$", var.region))' 'var.region != ""'
  name_check='can(regex("^[a-z][a-z0-9-]{0,18}[a-z0-9]$", var.name))'
  mutation refused_name_not_a_name variables.tf "$name_check" 'var.name != ""'
  mutation refused_name_too_long variables.tf "$name_check" 'can(regex("^[a-z][a-z0-9-]*[a-z0-9]$", var.name))'
  digest='can(regex("^[^@\\s]+@sha256:[0-9a-f]{64}$", var.'
  mutation refused_service_image_by_tag variables.tf "${digest}service_image))" 'var.service_image != ""'
  mutation refused_job_image_by_tag variables.tf "${digest}job_image))" 'var.job_image != ""'
  mutation refused_hosts_not_host_names variables.tf 'can(regex("^[a-z0-9.-]+( [a-z0-9.-]+)*$", var.hosts))' 'var.hosts != ""'
  mutation refused_app_role_name_not_a_role variables.tf 'can(regex("^[a-z_][a-z0-9_]{0,62}$", var.app_role_name))' 'var.app_role_name != ""'
  mutation refused_vpc_id_not_an_id variables.tf 'can(regex("^vpc-([0-9a-f]{8}|[0-9a-f]{17})$", var.vpc_id))' 'var.vpc_id != ""'
  mutation refused_public_subnet_ids_only_one variables.tf 'length(var.public_subnet_ids) >= 2 && ' ''
  mutation refused_public_subnet_ids_the_same_twice variables.tf ' && length(distinct(var.public_subnet_ids)) == length(var.public_subnet_ids)' ''
  mutation refused_public_subnet_ids_not_ids variables.tf 'can(regex("^subnet-([0-9a-f]{8}|[0-9a-f]{17})$", s))' 's != ""'
  public_cidrs='alltrue([for c in var.public_subnet_cidrs : can(cidrhost(c, 0)) && can(regex("^[0-9.]+/[0-9]+$", c))])'
  mutation refused_public_subnet_cidrs_not_networks variables.tf "$public_cidrs" 'alltrue([for c in var.public_subnet_cidrs : c != ""])'
  mutation refused_public_subnet_cidrs_host_bits_set variables.tf 'try(cidrhost(c, 0) == split("/", c)[0], false)' 'c != ""'
  mutation refused_public_subnet_cidrs_ipv6 variables.tf "$public_cidrs" 'alltrue([for c in var.public_subnet_cidrs : can(cidrhost(c, 0))])'
  mutation refused_public_subnet_cidrs_everything variables.tf 'alltrue([for c in var.public_subnet_cidrs : !can(regex("/0$", c))])' 'alltrue([for c in var.public_subnet_cidrs : c != ""])'
  mutation refused_database_cidr_everything variables.tf '!can(regex("/0$", var.database_cidr))' 'var.database_cidr != ""'
  mutation refused_certificate_arn_not_an_acm_arn variables.tf 'can(regex("^arn:aws:acm:[a-z0-9-]+:[0-9]{12}:certificate/[0-9a-f-]{36}$", var.certificate_arn))' 'var.certificate_arn != ""'
  secret_arn='can(regex("^arn:aws:secretsmanager:[a-z0-9-]+:[0-9]{12}:secret:[A-Za-z0-9/_+=.@-]+-[A-Za-z0-9]{6}$", var.'
  mutation refused_master_key_secret_by_name variables.tf "${secret_arn}master_key_secret_arn))" 'var.master_key_secret_arn != ""'
  mutation refused_app_database_secret_without_suffix variables.tf "${secret_arn}app_database_secret_arn))" 'var.app_database_secret_arn != ""'
  mutation refused_owner_database_secret_with_a_json_key variables.tf "${secret_arn}owner_database_secret_arn))" 'var.owner_database_secret_arn != ""'
  mutation refused_database_ca_secret_an_ssm_parameter variables.tf "${secret_arn}database_ca_secret_arn))" 'var.database_ca_secret_arn != ""'
  mutation refused_app_cert_secret_not_an_arn variables.tf "${secret_arn}app_cert_secret_arn))" 'var.app_cert_secret_arn != "/"'
  mutation refused_secrets_kms_key_an_alias variables.tf 'can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]{36}$", var.secrets_kms_key_arn))' 'var.secrets_kms_key_arn != "/"'
  settings_prefix='alltrue([for k in keys(var.settings) : startswith(k, "PACKMOAT_")])'
  mutation refused_settings_not_packmoat_aspnetcore variables.tf "$settings_prefix" 'alltrue([for k in keys(var.settings) : k != ""])'
  mutation refused_settings_not_packmoat_dotnet_startup_hooks variables.tf "$settings_prefix" 'alltrue([for k in keys(var.settings) : k != ""])'
  mutation refused_settings_port variables.tf "$settings_prefix" 'alltrue([for k in keys(var.settings) : k != ""])'
  mutation refused_settings_trusted_proxies main.tf '    "PACKMOAT_TRUSTED_PROXIES",
' ''
  mutation refused_settings_cookie_keys main.tf '    "PACKMOAT_COOKIE_KEYS",
' ''
  mutation refused_settings_background_jobs main.tf '    "PACKMOAT_BACKGROUND_JOBS",
' ''
  mutation refused_settings_hosts main.tf '    "PACKMOAT_HOSTS",
' ''
  mutation refused_settings_app_cert main.tf '    "PACKMOAT_APP_CERT",
' ''
  mutation refused_settings_master_key main.tf '    "PACKMOAT_MASTER_KEY",
' ''
  mutation refused_settings_admin_client_secret main.tf '    "PACKMOAT_ADMIN_CLIENT_SECRET",
' ''
  settings_db='alltrue([for k in keys(var.settings) : !startswith(k, "PACKMOAT_DB")])'
  mutation refused_settings_db_engine variables.tf "$settings_db" 'alltrue([for k in keys(var.settings) : k != ""])'
  mutation refused_settings_single_role variables.tf "$settings_db" 'alltrue([for k in keys(var.settings) : k != ""])'
  mutation refused_settings_database variables.tf "$settings_db" 'alltrue([for k in keys(var.settings) : k != ""])'
  mutation refused_settings_any_db_setting variables.tf "$settings_db" 'alltrue([for k in keys(var.settings) : k != ""])'
  mutation refused_settings_a_file_setting variables.tf 'alltrue([for k in keys(var.settings) : !endswith(k, "_FILE")])' \
    'alltrue([for k in keys(var.settings) : k != ""])'
  file_prefix='alltrue([for k in keys(var.file_settings) : can(regex("^PACKMOAT_[A-Z0-9_]*[A-Z0-9]$", k))])'
  mutation refused_file_settings_not_packmoat variables.tf "$file_prefix" 'alltrue([for k in keys(var.file_settings) : k != ""])'
  mutation refused_file_settings_not_upper_case variables.tf "$file_prefix" 'alltrue([for k in keys(var.file_settings) : startswith(k, "PACKMOAT_")])'
  mutation refused_file_settings_hosts variables.tf 'alltrue([for k in keys(var.file_settings) : !contains(local.template_settings, k)])' \
    'alltrue([for k in keys(var.file_settings) : k != ""])'
  mutation refused_file_settings_master_key variables.tf 'k != "PACKMOAT_MASTER_KEY"' 'k != ""'
  file_db='alltrue([for k in keys(var.file_settings) : !startswith(k, "PACKMOAT_DB")])'
  mutation refused_file_settings_database variables.tf "$file_db" 'alltrue([for k in keys(var.file_settings) : k != ""])'
  mutation refused_file_settings_single_role variables.tf "$file_db" 'alltrue([for k in keys(var.file_settings) : k != ""])'
  mutation refused_file_settings_a_file_setting variables.tf 'alltrue([for k in keys(var.file_settings) : !endswith(k, "_FILE")])' \
    'alltrue([for k in keys(var.file_settings) : k != ""])'
  mutation refused_file_settings_value_not_an_arn variables.tf \
    'alltrue([for v in values(var.file_settings) : can(regex("^arn:aws:secretsmanager:[a-z0-9-]+:[0-9]{12}:secret:[A-Za-z0-9/_+=.@-]+-[A-Za-z0-9]{6}$", v))])' \
    'alltrue([for v in values(var.file_settings) : v != ""])'
  mutation refused_desired_count_zero variables.tf "var.desired_count >= 1 && " ""
  mutation refused_desired_count_over_10 variables.tf " && var.desired_count <= 10" ""
  mutation refused_cpu_not_a_fargate_size variables.tf 'contains([256, 512, 1024, 2048, 4096], var.cpu)' 'var.cpu > 0'
  mutation refused_memory_too_small variables.tf "var.memory >= 512 && " ""
  mutation refused_log_retention_never_expiring variables.tf 'contains([1, 3, 5,' 'contains([0, 1, 3, 5,'
  mutation refused_private_subnet_cidrs_everything variables.tf ' && !can(regex("/0$", c))])' '])'
  mutation refused_private_subnet_azs_not_zones variables.tf 'can(regex("^[a-z]{2}(-[a-z]+)+-[0-9][a-z]$", z))' 'z != ""'
  owner_check='!contains(values(local.server_secrets), var.owner_database_secret_arn)'
  mutation refused_owner_secret_is_the_master_keys main.tf "$owner_check" 'var.owner_database_secret_arn != ""'
  mutation refused_owner_secret_is_the_app_roles main.tf "$owner_check" 'var.owner_database_secret_arn != ""'
  mutation refused_owner_secret_is_the_cas main.tf "$owner_check" 'var.owner_database_secret_arn != ""'
  mutation refused_owner_secret_is_the_site_certificates main.tf "$owner_check" 'var.owner_database_secret_arn != ""'
  mutation refused_owner_secret_is_a_file_settings main.tf "$owner_check" 'var.owner_database_secret_arn != ""'
  mutation refused_setting_both_a_value_and_a_file main.tf 'length(setintersection(keys(var.settings), keys(var.file_settings))) == 0' \
    'length(setintersection(keys(var.settings), keys(var.file_settings))) >= 0'
  mutation refused_public_subnet_cidrs_fewer_than_subnets main.tf 'length(var.public_subnet_cidrs) == length(var.public_subnet_ids)' 'length(var.public_subnet_cidrs) >= 1'
  mutation refused_nat_without_private_subnets main.tf 'length(var.private_subnet_cidrs) >= 2 && ' ''
  mutation refused_nat_with_one_private_subnet main.tf 'length(var.private_subnet_cidrs) >= 2 && ' ''
  mutation refused_nat_with_a_zone_missing main.tf ' && length(var.private_subnet_azs) == length(var.private_subnet_cidrs)' ''
  mutation refused_private_subnets_without_nat main.tf 'var.enable_nat_gateway || length(var.private_subnet_cidrs) + length(var.private_subnet_azs) == 0' \
    'var.enable_nat_gateway || length(var.private_subnet_cidrs) + length(var.private_subnet_azs) >= 0'
  mutation refused_secret_in_another_region main.tf 'split(":", a)[3] == var.region && ' ''
  mutation refused_secret_in_another_account main.tf ' && split(":", a)[4] == var.account_id' ''
  mutation refused_owner_secret_in_another_account main.tf '    values(local.migrate_secrets),
' ''
  mutation refused_file_setting_in_another_region main.tf '    values(local.server_secrets),
' '    [var.master_key_secret_arn, var.app_database_secret_arn, var.database_ca_secret_arn],
'
  mutation refused_certificate_in_another_region main.tf '    [var.certificate_arn],
' ''
  mutation refused_kms_key_in_another_account main.tf '    var.secrets_kms_key_arn == "" ? [] : [var.secrets_kms_key_arn],
' ''
  jobs_at_once="${MUTATION_JOBS:-2}"
  for (( start = 0; start < ${#labels[@]}; start += jobs_at_once )); do
    for (( i = start; i < start + jobs_at_once && i < ${#labels[@]}; i++ )); do run_mutation "$i" > "$work/result-$i" 2>&1 & done
    wait
  done
  for (( i = 0; i < ${#labels[@]}; i++ )); do
    cat "$work/result-$i"
    if grep -q '^FAIL: ' "$work/result-$i"; then failed=1; fi
  done
  caught="$(cat "$work"/result-* | grep -c '^ok:   caught: ' || true)"
  broken="$(cat "$work"/result-* | grep -c '^FAIL: broken: ' || true)"
  echo "template mutations: ${#labels[@]}, caught ${caught}, broken ${broken}, not caught $(( ${#labels[@]} - caught - broken ))"
  if [[ "$failed" -ne 0 ]]; then echo "template mutations: some not caught, or broken" >&2; exit 1; fi
  echo "template mutations: all ${#labels[@]} caught"
  exit 0
fi

# --dir <folder> --only <label>: check another copy of the template (the mutations above use it). Of the tofu test runs, only the run
# named <label> (none when <label> is a check on the files): so a mutation costs one run, not all of them.
src="$here"
only=""
if [[ "${1:-}" == "--dir" ]]; then src="$(cd "$2" && pwd)"; only="${4:-}"; fi
only_run=0
if [[ -n "$only" ]] && { grep -qF "run \"$only\" {" "$src"/tests/*.tftest.hcl || [[ "$only" == "example_inputs_are_accepted" ]]; }; then only_run=1; fi

unpack_provider
providers="$AWS_ECS_TEMPLATE_PROVIDERS"

# The template is copied, so that tofu's working files (.terraform, the generated example test) never land in the repository.
t="$work/template"
mkdir "$t"
cp -R "$src/." "$t"
rm -rf "$t/.terraform"
cd "$t"

echo "--- tofu and tflint"
if tofu fmt -check -recursive -no-color > "$work/fmt.txt" 2>&1; then ok "tofu fmt -check"; else bad "tofu fmt -check: $(cat "$work/fmt.txt")"; fi
if tofu init -backend=false -input=false -no-color -plugin-dir="$providers" -lockfile=readonly > "$work/init.txt" 2>&1 \
  && cmp -s "$src/.terraform.lock.hcl" "$t/.terraform.lock.hcl"; then ok "$init_label"; else bad "$init_label: $(cat "$work/init.txt")"; fi
# A mutation named after a tofu test run is judged by that run alone (and the plan probe): the other checks would only cost time, so
# they are left out. Every other mutation, and the plain run, has them all.
if [[ "$only_run" -eq 0 ]]; then
  if tofu validate -no-color > "$work/validate.txt" 2>&1; then ok "tofu validate"; else bad "tofu validate: $(cat "$work/validate.txt")"; fi
  lint_label="tflint, the terraform and AWS rules"
  # The version first, to a file (a pipe into grep -q could end tflint early, and pipefail would count that as a failure): it lists the
  # rulesets tflint loaded, and the AWS one must be among them.
  tflint --version > "$work/lint-version.txt" 2>&1 || true
  if grep -Fq "ruleset.aws (0.50.0)" "$work/lint-version.txt" && tflint --no-color --format=compact > "$work/lint.txt" 2>&1; then ok "$lint_label"
  else bad "$lint_label: $(cat "$work/lint-version.txt") $(cat "$work/lint.txt" 2>/dev/null)"; fi

  echo "--- the files"
  # versions.tf names the same OpenTofu and provider versions install-iac-tools.sh installs, each exactly.
  tofu_version="$(sed -nE 's/^tofu_version=([0-9.]+)$/\1/p' "$tools_script")"
  aws_version="$(sed -nE 's/^aws_version=([0-9.]+)$/\1/p' "$tools_script")"
  pins_label="versions.tf pins OpenTofu and the aws provider exactly, as install-iac-tools.sh does"
  if grep -Fxq "  required_version = \"= $tofu_version\"" versions.tf && grep -Fxq "      version = \"= $aws_version\"" versions.tf \
    && [[ "$(grep -cE '^[[:space:]]*(required_)?version[[:space:]]*=' versions.tf)" -eq 2 ]]; then ok "$pins_label"; else bad "$pins_label"; fi
  # The lock file's zip checksums (zh:) are exactly install-iac-tools.sh's pinned provider zips, one per platform, and its version is the pin.
  lock_label="the lock file holds exactly install-iac-tools.sh's provider zip checksums"
  pinned_zips="$(sed -nE 's/^    aws_sha256=([0-9a-f]{64})$/\1/p' "$tools_script" | sort)"
  locked_zips="$(sed -nE 's/^    "zh:([0-9a-f]{64})",$/\1/p' .terraform.lock.hcl | sort)"
  # And one h1 per platform: the checksums tofu checks an unpacked provider against (with none, tofu init takes it, and only a later
  # command refuses it).
  if [[ -n "$pinned_zips" && "$pinned_zips" == "$locked_zips" && "$(grep -c '^provider ' .terraform.lock.hcl)" -eq 1 ]] \
    && [[ "$(grep -cE '^    "h1:[A-Za-z0-9+/]{43}=",$' .terraform.lock.hcl)" -eq "$(grep -c . <<< "$pinned_zips")" ]] \
    && grep -Fxq "  version     = \"$aws_version\"" .terraform.lock.hcl; then ok "$lock_label"; else bad "$lock_label"; fi
  # The provider refuses any account but account_id: a deploy with the wrong credentials stops before it changes anything.
  account_label="the provider runs in account_id only"
  if [[ "$(grep -cE '^provider ' ./*.tf | awk -F: '{ s += $2 } END { print s }')" -eq 1 ]] && grep -Fxq '  allowed_account_ids = [var.account_id]' main.tf
  then ok "$account_label"; else bad "$account_label"; fi
  if grep -nE '^[[:space:]]*sensitive[[:space:]]*=' ./*.tf > "$work/sensitive.txt"; then bad "no input or output is sensitive: $(cat "$work/sensitive.txt")"
  else ok "no input or output is sensitive"; fi
  # A secret reaches a container only through ECS's secrets, as valueFrom, and only in the two secrets-init containers, which write it to a
  # file; tests/defaults.tftest.hcl checks the containers themselves.
  secrets_label="only the two secrets-init containers are given secrets"
  secret_lines="$(grep -nE 'valueFrom|^[[:space:]]*secrets[[:space:]]*=' ./*.tf || true)"
  if [[ "$(grep -c . <<< "$secret_lines")" -eq 2 ]] \
    && grep -Fq './service.tf:' <<< "$secret_lines" && grep -Fq './migrate.tf:' <<< "$secret_lines" \
    && [[ "$(grep -cE ':[[:space:]]+secrets    = \[for k, arn in local\.(server|migrate)_secrets : \{ name = "SECRET_\$\{k\}", valueFrom = arn \}\]$' <<< "$secret_lines")" -eq 2 ]]
  then ok "$secrets_label"; else bad "$secrets_label: $secret_lines"; fi
  # IAM: the two aws_iam_role_policy resources only, on named resources. No managed policy (made or attached), no attachment, no
  # inline_policy block on a role (which the fixed resource list cannot see), no task role, no "*" as a resource and no wildcard action.
  iam_label="no managed policy, policy attachment, inline_policy on a role, task role or wildcard"
  if grep -nE 'aws_iam_policy|aws_iam_role_policy_attachment|aws_iam_policy_attachment|managed_policy_arns|inline_policy|task_role_arn|"\*"|"[a-z0-9-]+:\*"' ./*.tf > "$work/iam.txt"
  then bad "$iam_label: $(cat "$work/iam.txt")"; else ok "$iam_label"; fi
  # Security group rules are the aws_vpc_security_group_*_rule resources in the fixed list alone: an ingress or egress block inside
  # aws_security_group (plain or dynamic) would add a rule no test sees.
  rules_label="security group rules only as resources of their own (no inline or dynamic rule or policy blocks)"
  if grep -nE '^[[:space:]]*(ingress|egress)[[:space:]]*(\{|=)|dynamic[[:space:]]+"(ingress|egress|inline_policy)"' ./*.tf > "$work/rules.txt"
  then bad "$rules_label: $(cat "$work/rules.txt")"; else ok "$rules_label"; fi
  # The module makes exactly these resources and reads no data source: another rule, role, policy or route is a change to this list, and so
  # to this test.
  resources_label="the module makes exactly its fixed set of resources, and reads no data source"
  expected_resources='resource "aws_cloudwatch_log_group" "server"
resource "aws_ecs_cluster" "main"
resource "aws_ecs_service" "server"
resource "aws_ecs_task_definition" "migrate"
resource "aws_ecs_task_definition" "server"
resource "aws_eip" "nat"
resource "aws_iam_role" "migrate_execution"
resource "aws_iam_role" "server_execution"
resource "aws_iam_role_policy" "migrate_execution"
resource "aws_iam_role_policy" "server_execution"
resource "aws_lb" "main"
resource "aws_lb_listener" "http"
resource "aws_lb_listener" "https"
resource "aws_lb_target_group" "server"
resource "aws_nat_gateway" "nat"
resource "aws_route" "private_nat"
resource "aws_route_table" "private"
resource "aws_route_table_association" "private"
resource "aws_security_group" "alb"
resource "aws_security_group" "task"
resource "aws_subnet" "private"
resource "aws_vpc_security_group_egress_rule" "alb_to_tasks"
resource "aws_vpc_security_group_egress_rule" "task_database"
resource "aws_vpc_security_group_egress_rule" "task_https"
resource "aws_vpc_security_group_ingress_rule" "alb_http"
resource "aws_vpc_security_group_ingress_rule" "alb_https"
resource "aws_vpc_security_group_ingress_rule" "task_from_alb"
resource "terraform_data" "checks"'
  declared="$(grep -hE '^[[:space:]]*(resource|data|module|import)[[:space:]]' ./*.tf | sed -E 's/[[:space:]]*\{[[:space:]]*\}?[[:space:]]*$//' | LC_ALL=C sort)"
  if [[ "$declared" == "$expected_resources" ]]; then ok "$resources_label"
  else bad "$resources_label: $(diff <(echo "$expected_resources") <(echo "$declared") | grep '^[<>]' | tr '\n' ' ')"; fi
  # Nothing runs on the deployer's machine or reads a value outside the plan: no provisioner (local-exec, remote-exec, file), no connection
  # block, no ephemeral resource.
  blocks_label="no provisioner, connection or ephemeral block"
  if grep -nE '^[[:space:]]*(provisioner|connection|ephemeral)([[:space:]]|\{|$)' ./*.tf > "$work/blocks.txt"; then bad "$blocks_label: $(cat "$work/blocks.txt")"
  else ok "$blocks_label"; fi
  # No secret's value is ever read into the configuration (and so into the plan and the state): no secret version, no secret_string, no
  # SSM parameter, and nothing made non-sensitive.
  values_label="no secret value is read (no secret version, secret_string, SSM parameter or nonsensitive)"
  if grep -nE 'aws_secretsmanager_secret_version|secret_string|aws_ssm_parameter|nonsensitive\(' ./*.tf > "$work/values.txt"; then
    bad "$values_label: $(cat "$work/values.txt")"
  else ok "$values_label"; fi

  # Every PACKMOAT_ name the template uses, its _FILE form as the setting itself, must be a row of the settings table.
  documented="$(grep -oE '^\| .PACKMOAT_[A-Z0-9_]+. \|' "$settings_doc" | grep -oE 'PACKMOAT_[A-Z0-9_]+' | sort -u)"
  named="$(grep -ohE 'PACKMOAT_[A-Z0-9_]*[A-Z0-9]' ./*.tf example.tfvars | sed -E 's/_FILE$//' | sort -u)"
  unknown="$(comm -23 <(echo "$named") <(echo "$documented"))"
  if [[ -n "$named" && -z "$unknown" ]]; then ok "every PACKMOAT_ setting the template names is in docs/operations.md's table"
  else bad "every PACKMOAT_ setting the template names is in docs/operations.md's table: not there: $(echo "$unknown" | tr '\n' ' ')"; fi
  secret_outputs="$(grep -ohE '^output "[^"]+"' ./*.tf | grep -iE 'password|connection|secret|key|cert|token' || true)"
  if [[ -z "$secret_outputs" && "$(grep -c '^output ' outputs.tf)" -gt 0 ]]; then ok "the outputs name no secret"; else bad "the outputs name no secret: $secret_outputs"; fi
fi

# The example inputs, planned like the tests: example.tfvars as the variables of one more test file, with the same mock.
{
  sed -n '/^mock_provider/,/^# The required inputs/p' tests/defaults.tftest.hcl | sed '$d'
  echo "variables {"
  cat example.tfvars
  echo "}"
  echo 'run "example_inputs_are_accepted" {'
  echo "  command = plan"
  echo "}"
} > tests/example.tftest.hcl

echo "--- tofu test (a plan per run, against a mock of the aws provider)"
# header <test file>: the file up to its first run (the mock, the overrides and its variables).
header() { sed -n '/^run "/q;p' "$1"; }
if [[ -z "$only" ]]; then
  # Every run of every file, in parts of at most 8 runs, one tofu test each: tofu keeps a provider process for each run until it ends,
  # and the AWS provider's take about 115 MB each, so all the runs at once held 68 of them and some 7.5 GB (measured), more than the CI
  # runner could spare. Each part keeps its file's mock, overrides and variables. A run that fails makes tofu skip the rest of its part,
  # which then count as failed too.
  mkdir "$work/parts"
  for f in tests/*.tftest.hcl; do
    base="$(basename "$f" .tftest.hcl)"
    header "$f" > "$work/header.hcl"
    n=0
    while read -r run; do
      part="$work/parts/$base-$(( n / 8 )).tftest.hcl"
      if [[ ! -f "$part" ]]; then cp "$work/header.hcl" "$part"; fi
      sed -n "/^run \"$run\" {\$/,/^}\$/p" "$f" >> "$part"
      n=$(( n + 1 ))
    done < <(sed -nE 's/^run "([a-z0-9_]+)" \{$/\1/p' "$f")
  done
  runs="$(sed -nE 's/^run "([a-z0-9_]+)" \{$/\1/p' tests/*.tftest.hcl)"
  # Every run must be in exactly one part, and the names read must be every run the files hold.
  split_label="the test files split into parts, every run in one"
  if [[ "$(cat "$work"/parts/*.tftest.hcl | grep -c '^run "')" -eq "$(cat tests/*.tftest.hcl | grep -c '^run "')" \
    && "$(grep -c . <<< "$runs")" -eq "$(cat tests/*.tftest.hcl | grep -c '^run "')" ]]; then ok "$split_label"; else bad "$split_label"; fi
  rm tests/*.tftest.hcl
  mv "$work"/parts/*.tftest.hcl tests/
  : > "$work/test.txt"
  for part in tests/*.tftest.hcl; do tofu test -no-color -filter="$part" >> "$work/test.txt" 2>&1 || true; done
  while read -r run; do
    if grep -Fxq "  run \"$run\"... pass" "$work/test.txt"; then ok "$run"; else bad "$run"; fi
  done <<< "$runs"
  if [[ "$failed" -ne 0 ]]; then echo "tofu test said:"; cat "$work/test.txt"; fi
else
  # --only: first, whether the module still plans with the required inputs at all ("plan: ok"), so that a mutation that merely breaks
  # the module is not counted as caught by the check it names. Then, when <label> is a run, that run alone, in a file of its own (so no
  # earlier run's failure can skip it), and whether it failed on its own assertion or expected failure, or on some other error ("broken").
  { header tests/defaults.tftest.hcl; printf 'run "plan_probe" {\n  command = plan\n}\n'; } > tests/plan_probe.tftest.hcl
  tofu test -no-color -filter=tests/plan_probe.tftest.hcl > "$work/probe.txt" 2>&1 || true
  if grep -Fxq '  run "plan_probe"... pass' "$work/probe.txt"; then echo "plan: ok"; else echo "plan: broken: $(grep '^Error: ' "$work/probe.txt" | head -3 | tr '\n' ' ')"; fi
  test_file="$(grep -lF "run \"$only\" {" tests/*.tftest.hcl || true)"
  if [[ -n "$test_file" ]]; then
    { header "$test_file"; sed -n "/^run \"$only\" {\$/,/^}\$/p" "$test_file"; } > "$work/only.hcl"
    mv "$work/only.hcl" tests/only.tftest.hcl
    tofu test -no-color -filter=tests/only.tftest.hcl > "$work/test.txt" 2>&1 || true
    if grep -Fxq "  run \"$only\"... pass" "$work/test.txt"; then ok "$only"
    else
      # The example's own way to fail is an input it gives being refused.
      own='Error: (Test assertion failed|Missing expected failure)'
      if [[ "$only" == "example_inputs_are_accepted" ]]; then own='Error: (Invalid value for variable|Resource precondition failed)'; fi
      other="$(grep '^Error: ' "$work/test.txt" | grep -vxE "$own" || true)"
      if [[ -n "$other" ]]; then echo "broken: $only: $(echo "$other" | head -3 | tr '\n' ' ')"; fi
      bad "$only"
      echo "tofu test said:"; cat "$work/test.txt"
    fi
  fi
fi

if [[ "$failed" -ne 0 ]]; then echo "template tests: FAILED" >&2; exit 1; fi
echo "template tests: all passed"
