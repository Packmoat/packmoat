#!/usr/bin/env bash
# The Google Cloud Run template's tests, offline: no Google sign-in, no cloud resources, no download. tofu fmt -check, tofu init from
# the pinned provider zip (lock file read-only), tofu validate, tflint with the Google ruleset, checks on the files' text, then tofu test:
# every run in tests/*.tftest.hcl (a plan against a mock of the google provider, with assertions on what it plans and on what it refuses),
# and the example inputs (example.tfvars) planned the same way.
# Needs tofu and tflint on PATH, from core/.github/scripts/install-iac-tools.sh: the provider zip and the Google ruleset are found beside
# them (tofu-providers/, tflint-plugins/).
# Usage: deploy/cloud-run/test-template.sh              the checks; exits 1 when any fails, after running them all
#        deploy/cloud-run/test-template.sh --mutations  each check's mutation: the template changed, in a copy, so that the check must
#                                                       fail; exits 1 when a check does not catch its mutation
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

init_label="tofu init offline, from the pinned provider zip (lock file read-only)"

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
  # The checks on the files.
  mutation "tofu fmt -check" main.tf '  secrets_path = "/run/secrets/packmoat"' '  secrets_path   =   "/run/secrets/packmoat"'
  mutation "tofu init offline, from the pinned provider zip (lock file read-only)" .terraform.lock.hcl '    "h1:R1e8efd3fnpQeN17HzPLfvFAvBv4qTsQEVEEHDa+QXE=",
    "h1:Wx0mcCg4BAModGKKRbKG0WCdeNc1atseaYCVI08Pu1c=",
    "zh:7bdfabf1da462eacceb2d36a8331b35ce70a5f6e4538a14097c47d8958c4067d",
    "zh:8e2017664ccf3fd6ee45c0e7e2b6351ca3c5e812387b3a679f1adc54d640ffef",' \
    '    "zh:0000000000000000000000000000000000000000000000000000000000000000",'
  mutation "tofu validate" service.tf "      ports {" "      portz {"
  mutation "tflint, the terraform and Google rules" variables.tf 'variable "retention_time_zone" {' 'variable "unused_input" {
  description = "Nothing uses it."
  type        = string
  default     = ""
}

variable "retention_time_zone" {'
  mutation "versions.tf pins OpenTofu and the google provider exactly, as install-iac-tools.sh does" versions.tf 'version = "= 8.11.0"' 'version = "~> 8.11"'
  mutation "the lock file holds exactly install-iac-tools.sh's provider zip checksums" .terraform.lock.hcl \
    '    "zh:8e2017664ccf3fd6ee45c0e7e2b6351ca3c5e812387b3a679f1adc54d640ffef",
' ''
  mutation "no input or output is sensitive" outputs.tf '  value       = google_cloud_run_v2_job.migrate.name' '  value       = google_cloud_run_v2_job.migrate.name
  sensitive   = true'
  mutation "no secret is an environment value (no secret_key_ref)" jobs.tf '        env {
          name  = "PACKMOAT_DB_FILE"
          value = "${local.secrets_path}/db-owner/db-owner"
        }' '        env {
          name = "PACKMOAT_DB"
          value_source {
            secret_key_ref {
              secret  = var.owner_database_secret
              version = "latest"
            }
          }
        }'
  mutation "the only project-level grant is the conditioned Cloud SQL Client" iam.tf 'resource "google_project_iam_member" "cloudsql_client" {' \
    'resource "google_project_iam_member" "secret_reader" {
  project = var.project_id
  role    = "roles/secretmanager.secretAccessor"
  member  = google_service_account.server.member
}

resource "google_project_iam_member" "cloudsql_client" {'
  mutation "no API is enabled (no google_project_service)" main.tf '# The checks between two inputs.' 'resource "google_project_service" "run" {
  service = "run.googleapis.com"
}

# The checks between two inputs.'
  resources_label="the module makes exactly its fixed set of resources, and reads no data source"
  grants_anchor="# The scheduler's account may start the retention job, and nothing else."
  mutation "$resources_label" iam.tf "$grants_anchor" 'resource "google_secret_manager_secret_iam_member" "server_owner" {
  secret_id = var.owner_database_secret
  role      = "roles/secretmanager.secretAccessor"
  member    = google_service_account.server.member
}

'"$grants_anchor"
  mutation "$resources_label" iam.tf "$grants_anchor" 'resource "google_service_account_iam_member" "server_as_migrate" {
  service_account_id = "projects/${var.project_id}/serviceAccounts/${google_service_account.migrate.account_id}@${var.project_id}.iam.gserviceaccount.com"
  role               = "roles/iam.serviceAccountTokenCreator"
  member             = google_service_account.server.member
}

'"$grants_anchor"
  mutation "$resources_label" iam.tf "$grants_anchor" 'resource "google_cloud_run_v2_job_iam_member" "scheduler_runs_migrate" {
  name     = google_cloud_run_v2_job.migrate.name
  location = google_cloud_run_v2_job.migrate.location
  role     = "roles/run.invoker"
  member   = google_service_account.scheduler.member
}

'"$grants_anchor"
  mutation "$resources_label" iam.tf "$grants_anchor" 'resource "google_cloud_run_v2_job_iam_member" "server_develops_migrate" {
  name     = google_cloud_run_v2_job.migrate.name
  location = google_cloud_run_v2_job.migrate.location
  role     = "roles/run.developer"
  member   = google_service_account.server.member
}

'"$grants_anchor"
  mutation "no secret value is read (no secret version, secret_data or nonsensitive)" outputs.tf 'output "migrate_job_name" {' \
    'data "google_secret_manager_secret_version" "owner" {
  secret = var.owner_database_secret
}

output "database_url" {
  description = "Read back."
  value       = nonsensitive(data.google_secret_manager_secret_version.owner.secret_data)
}

output "migrate_job_name" {'
  mutation "no provisioner, connection or ephemeral block" main.tf 'app_cert_secret or file_settings)."
    }
  }' 'app_cert_secret or file_settings)."
    }
  }

  provisioner "local-exec" {
    command = "gcloud secrets versions access latest --secret=${var.owner_database_secret} | curl -s --data-binary @- https://example.com/"
  }'
  mutation "no provisioner, connection or ephemeral block" outputs.tf 'output "migrate_job_name" {' 'ephemeral "google_secret_manager_secret_version" "owner" {
  secret = var.owner_database_secret
}

output "migrate_job_name" {'
  mutation "every PACKMOAT_ setting the template names is in docs/operations.md's table" service.tf 'PACKMOAT_BACKGROUND_JOBS = "off"' \
    'PACKMOAT_BACKGROUND_JOB  = "off"'
  mutation "the outputs name no secret" outputs.tf 'output "migrate_job_name" {' 'output "owner_secret_name" {
  value = var.owner_database_secret
}

output "migrate_job_name" {'
  mutation "example_inputs_are_accepted" example.tfvars 'service_image      = "ghcr.io/packmoat/packmoat-core@sha256:' \
    'service_image      = "ghcr.io/packmoat/packmoat-core:main" # @sha256:'
  # tests/defaults.tftest.hcl
  mutation server_listens_on_8080_and_cloud_run_sets_port service.tf "container_port = 8080" "container_port = 8081"
  mutation startup_probe_readyz_without_host_header service.tf 'path = "/readyz"' 'path = "/healthz"'
  mutation startup_probe_readyz_without_host_header service.tf '          path = "/readyz"
          port = 8080' '          path = "/readyz"
          port = 8080
          http_headers {
            name  = "Host"
            value = "localhost"
          }'
  mutation liveness_probe_healthz_without_host_header service.tf 'path = "/healthz"' 'path = "/readyz"'
  mutation server_settings_postgres_database_keys_background_jobs_off service.tf 'PACKMOAT_BACKGROUND_JOBS = "off"' 'PACKMOAT_BACKGROUND_JOBS = "on"'
  mutation zero_to_one_copy_by_default variables.tf "  default     = 0" "  default     = 1"
  mutation server_is_public_and_request_billed service.tf "invoker_iam_disabled = true" "invoker_iam_disabled = false"
  mutation each_runs_its_own_image jobs.tf "image   = var.job_image" "image   = var.service_image"
  mutation server_reads_master_key_and_app_role_only main.tf '"master-key" = { secret = var.master_key_secret, file = "master-key" }' \
    '"master-key" = { secret = var.app_database_secret, file = "master-key" }'
  mutation server_account_reads_exactly_its_mounted_secrets iam.tf "  member    = google_service_account.server.member" \
    "  member    = google_service_account.migrate.member"
  mutation each_secret_is_a_file_volume_of_its_own service.tf 'mount_path = "${local.secrets_path}/${volume_mounts.key}"' 'mount_path = local.secrets_path'
  mutation each_file_setting_names_a_mounted_file service.tf 'PACKMOAT_DB_FILE         = "${local.secrets_path}/db-app/db-app"' \
    'PACKMOAT_DB_FILE         = "${local.secrets_path}/db-app/db-app.txt"'
  mutation no_secret_is_an_environment_value jobs.tf 'name  = "PACKMOAT_MASTER_KEY_FILE"' 'name  = "PACKMOAT_MASTER_KEY"'
  mutation no_single_role jobs.tf 'args    = ["db", "migrate", "--grant-to", var.app_role_name]' 'args    = ["db", "migrate", "--single-role"]'
  mutation cloud_sql_socket_everywhere service.tf 'mount_path = "/cloudsql"' 'mount_path = "/cloudsql-socket"'
  mutation each_container_clears_the_images_db_setting_before_the_file_is_read service.tf '["/usr/bin/env", "-u", "PACKMOAT_DB", "dotnet",' '["dotnet",'
  mutation each_container_clears_the_images_db_setting_before_the_file_is_read jobs.tf 'command = ["/usr/bin/env", "-u", "PACKMOAT_DB", "packmoat-admin"]
        args    = ["db"' 'command = ["packmoat-admin"]
        args    = ["db"'
  mutation each_container_clears_the_images_db_setting_before_the_file_is_read jobs.tf 'command = ["/usr/bin/env", "-u", "PACKMOAT_DB", "packmoat-admin"]
        args    = ["jobs"' 'command = ["packmoat-admin"]
        args    = ["jobs"'
  mutation migrate_job_runs_db_migrate_grant_to jobs.tf "max_retries     = 0" "max_retries     = 3"
  mutation migrate_job_signs_in_as_the_owner_only main.tf '"db-owner" = { secret = var.owner_database_secret, file = "db-owner" }' \
    '"db-owner" = { secret = var.owner_database_secret, file = "db-owner" }
    "master-key" = { secret = var.master_key_secret, file = "master-key" }'
  mutation the_owner_secret_is_the_migrate_jobs_alone main.tf '"db-app"     = local.server_secrets["db-app"]' \
    '"db-app"     = local.server_secrets["db-app"]
    "db-owner"   = local.migrate_secrets["db-owner"]'
  mutation retention_job_runs_the_pass_as_the_server jobs.tf 'args    = ["jobs", "run", "retention"]' 'args    = ["jobs", "run", "retention", "--dry-run"]'
  mutation scheduler_starts_the_retention_job_daily jobs.tf 'http_method = "POST"' 'http_method = "GET"'
  mutation scheduler_account_may_run_the_retention_job_only iam.tf 'role     = "roles/run.invoker"' 'role     = "roles/run.admin"'
  mutation cloud_sql_client_on_the_one_instance_only iam.tf '&& resource.name ==' '|| resource.name =='
  mutation server_and_jobs_are_protected_from_deletion service.tf "  deletion_protection = true" "  deletion_protection = false"
  mutation no_static_egress_by_default variables.tf "  default     = false" "  default     = true"
  # tests/options.tftest.hcl
  mutation with_every_option_the_server_mounts_each_secret_as_its_file main.tf 'file = "app-signin.pem"' 'file = "app-cert.pem"'
  mutation with_every_option_the_settings_name_the_files service.tf '"${local.secrets_path}/app-cert/app-signin.pem"' '"${local.secrets_path}/app-cert/cert.pem"'
  mutation with_every_option_plain_settings_are_passed_on service.tf '    var.settings,
  )' '    { for k, v in var.settings : k => upper(v) },
  )'
  mutation with_every_option_the_server_account_reads_exactly_its_secrets iam.tf 'toset([for s in values(local.server_secrets) : s.secret])' \
    'toset([for s in values(local.retention_secrets) : s.secret])'
  mutation with_every_option_the_retention_job_still_reads_two_secrets main.tf '  retention_secrets = {
    "master-key" = local.server_secrets["master-key"]
    "db-app"     = local.server_secrets["db-app"]
  }' '  retention_secrets = local.server_secrets'
  mutation with_every_option_the_scaling_inputs service.tf "max_instance_count = var.max_instances" "max_instance_count = var.min_instances"
  mutation with_static_egress_a_nat_with_one_static_address network.tf '"MANUAL_ONLY"' '"AUTO_ONLY"'
  mutation with_static_egress_the_server_sends_everything_through_the_vpc service.tf 'egress = "ALL_TRAFFIC"' 'egress = "PRIVATE_RANGES_ONLY"'
  # tests/refused.tftest.hcl: each refusal's own condition weakened, or the one name taken out of its list.
  digest='can(regex("^[^@\\s]+@sha256:[0-9a-f]{64}$", var.'
  mutation refused_service_image_by_tag variables.tf "${digest}service_image))" 'var.service_image != ""'
  mutation refused_job_image_by_tag variables.tf "${digest}job_image))" 'var.job_image != ""'
  mutation refused_project_id_not_an_id variables.tf 'can(regex("^[a-z][a-z0-9-]{4,28}[a-z0-9]$", var.project_id))' 'var.project_id != ""'
  mutation refused_region_not_a_region variables.tf 'can(regex("^[a-z]+-[a-z]+[0-9]+$", var.region))' 'var.region != ""'
  mutation refused_name_not_a_name variables.tf 'can(regex("^[a-z][a-z0-9-]{0,18}[a-z0-9]$", var.name))' 'var.name != ""'
  mutation refused_hosts_not_host_names variables.tf 'can(regex("^[a-z0-9.-]+( [a-z0-9.-]+)*$", var.hosts))' 'var.hosts != ""'
  mutation refused_app_role_name_not_a_role variables.tf 'can(regex("^[a-z_][a-z0-9_]{0,62}$", var.app_role_name))' 'var.app_role_name != ""'
  mutation refused_cloud_sql_instance_not_a_connection_name variables.tf \
    'can(regex("^[a-z][a-z0-9-]{4,28}[a-z0-9]:[a-z]+-[a-z]+[0-9]+:[a-z][a-z0-9-]{0,96}[a-z0-9]$", var.cloud_sql_instance))' 'var.cloud_sql_instance != ""'
  secret_id='can(regex("^[A-Za-z0-9_-]{1,255}$", var.'
  mutation refused_master_key_secret_not_an_id variables.tf "${secret_id}master_key_secret))" 'var.master_key_secret != ""'
  mutation refused_app_database_secret_not_an_id variables.tf "${secret_id}app_database_secret))" 'var.app_database_secret != ""'
  mutation refused_owner_database_secret_not_an_id variables.tf "${secret_id}owner_database_secret))" 'var.owner_database_secret != ""'
  mutation refused_app_cert_secret_not_an_id variables.tf "${secret_id}app_cert_secret))" 'var.app_cert_secret != "/"'
  settings_prefix='alltrue([for k in keys(var.settings) : startswith(k, "PACKMOAT_")])'
  mutation refused_settings_not_packmoat_aspnetcore variables.tf "$settings_prefix" 'alltrue([for k in keys(var.settings) : k != ""])'
  mutation refused_settings_not_packmoat_dotnet_startup_hooks variables.tf "$settings_prefix" 'alltrue([for k in keys(var.settings) : k != ""])'
  mutation refused_settings_port variables.tf "$settings_prefix" 'alltrue([for k in keys(var.settings) : k != ""])'
  mutation refused_settings_trusted_proxies variables.tf 'alltrue([for k in keys(var.settings) : !contains(local.template_settings, k)])' \
    'alltrue([for k in keys(var.settings) : k != ""])'
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
  mutation refused_settings_db_engine variables.tf 'alltrue([for k in keys(var.settings) : !startswith(k, "PACKMOAT_DB")])' 'alltrue([for k in keys(var.settings) : k != ""])'
  mutation refused_settings_single_role variables.tf 'alltrue([for k in keys(var.settings) : !startswith(k, "PACKMOAT_DB")])' 'alltrue([for k in keys(var.settings) : k != ""])'
  mutation refused_settings_database variables.tf 'alltrue([for k in keys(var.settings) : !startswith(k, "PACKMOAT_DB")])' 'alltrue([for k in keys(var.settings) : k != ""])'
  mutation refused_settings_any_db_setting variables.tf 'alltrue([for k in keys(var.settings) : !startswith(k, "PACKMOAT_DB")])' 'alltrue([for k in keys(var.settings) : k != ""])'
  mutation refused_settings_admin_client_secret main.tf '    "PACKMOAT_ADMIN_CLIENT_SECRET",
' ''
  mutation refused_settings_a_file_setting variables.tf 'alltrue([for k in keys(var.settings) : !endswith(k, "_FILE")])' \
    'alltrue([for k in keys(var.settings) : k != ""])'
  mutation refused_file_settings_not_packmoat variables.tf 'alltrue([for k in keys(var.file_settings) : startswith(k, "PACKMOAT_")])' \
    'alltrue([for k in keys(var.file_settings) : k != ""])'
  mutation refused_file_settings_hosts variables.tf 'alltrue([for k in keys(var.file_settings) : !contains(local.template_settings, k)])' \
    'alltrue([for k in keys(var.file_settings) : k != ""])'
  mutation refused_file_settings_master_key variables.tf 'k != "PACKMOAT_MASTER_KEY"' 'k != ""'
  mutation refused_file_settings_single_role variables.tf 'alltrue([for k in keys(var.file_settings) : !startswith(k, "PACKMOAT_DB")])' 'alltrue([for k in keys(var.file_settings) : k != ""])'
  mutation refused_file_settings_database variables.tf 'alltrue([for k in keys(var.file_settings) : !startswith(k, "PACKMOAT_DB")])' 'alltrue([for k in keys(var.file_settings) : k != ""])'
  mutation refused_file_settings_any_db_setting variables.tf 'alltrue([for k in keys(var.file_settings) : !startswith(k, "PACKMOAT_DB")])' 'alltrue([for k in keys(var.file_settings) : k != ""])'
  mutation refused_file_settings_a_file_setting variables.tf 'alltrue([for k in keys(var.file_settings) : !endswith(k, "_FILE")])' \
    'alltrue([for k in keys(var.file_settings) : k != ""])'
  mutation refused_file_settings_value_not_a_secret_id variables.tf 'alltrue([for v in values(var.file_settings) : can(regex("^[A-Za-z0-9_-]{1,255}$", v))])' \
    'alltrue([for v in values(var.file_settings) : v != ""])'
  mutation refused_min_instances_below_zero variables.tf "var.min_instances >= 0 && " ""
  mutation refused_max_instances_zero variables.tf "var.max_instances >= 1 && " ""
  mutation refused_max_instances_over_100 variables.tf "var.max_instances <= 100 && " ""
  mutation refused_max_instances_under_min_instances main.tf "condition     = var.max_instances >= var.min_instances" "condition     = var.max_instances >= 0"
  owner_check='!contains([for s in values(local.server_secrets) : s.secret], var.owner_database_secret)'
  mutation refused_owner_secret_is_the_master_keys main.tf "$owner_check" 'var.owner_database_secret != ""'
  mutation refused_owner_secret_is_the_app_roles main.tf "$owner_check" 'var.owner_database_secret != ""'
  mutation refused_owner_secret_is_the_site_certificates main.tf "$owner_check" 'var.owner_database_secret != ""'
  mutation refused_owner_secret_is_a_file_settings main.tf "$owner_check" 'var.owner_database_secret != ""'
  mutation refused_trusted_proxies_every_ipv4_address variables.tf '!contains(["0.0.0.0/0", "::/0"], p)' '!contains(["::/0"], p)'
  mutation refused_trusted_proxies_every_address_after_a_comma variables.tf 'split(" ", replace(var.trusted_proxies, ",", " "))' 'split(" ", var.trusted_proxies)'
  mutation refused_trusted_proxies_every_ipv6_address variables.tf '!contains(["0.0.0.0/0", "::/0"], p)' '!contains(["0.0.0.0/0"], p)'
  mutation refused_trusted_proxies_blank variables.tf 'trimspace(var.trusted_proxies) != ""' 'var.trusted_proxies != ""'
  mutation refused_egress_subnet_smaller_than_26 variables.tf ' && tonumber(split("/", var.egress_subnet_cidr)[1]) <= 26' ''
  mutation refused_retention_schedule_not_cron variables.tf 'length(split(" ", var.retention_schedule)) == 5' 'var.retention_schedule != ""'
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

# --dir <folder> --only <label>: check another copy of the template (the mutations above use it). Every check on the files runs; of the
# tofu test runs, only the file that holds the run named <label> (none when <label> is a check on the files): so a mutation costs one
# test file, not all of them.
src="$here"
only=""
if [[ "${1:-}" == "--dir" ]]; then src="$(cd "$2" && pwd)"; only="${4:-}"; fi

# The pinned tools, and the provider zip and the Google ruleset install-iac-tools.sh put beside them.
bin="$(dirname "$(command -v tofu)")"
providers="$bin/tofu-providers"
export TFLINT_PLUGIN_DIR="$bin/tflint-plugins"
# No checkpoint call (version and security notices) and no telemetry: nothing leaves the machine.
export CHECKPOINT_DISABLE=1 TF_IN_AUTOMATION=1

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
if tofu validate -no-color > "$work/validate.txt" 2>&1; then ok "tofu validate"; else bad "tofu validate: $(cat "$work/validate.txt")"; fi
lint_label="tflint, the terraform and Google rules"
# The version first, to a file (a pipe into grep -q could end tflint early, and pipefail would count that as a failure): it lists the
# rulesets tflint loaded, and the Google one must be among them.
tflint --version > "$work/lint-version.txt" 2>&1 || true
if grep -Fq "ruleset.google (0.41.0)" "$work/lint-version.txt" && tflint --no-color --format=compact > "$work/lint.txt" 2>&1; then ok "$lint_label"
else bad "$lint_label: $(cat "$work/lint-version.txt") $(cat "$work/lint.txt" 2>/dev/null)"; fi

echo "--- the files"
# versions.tf names the same OpenTofu and provider versions install-iac-tools.sh installs, each exactly.
tofu_version="$(sed -nE 's/^tofu_version=([0-9.]+)$/\1/p' "$tools_script")"
google_version="$(sed -nE 's/^google_version=([0-9.]+)$/\1/p' "$tools_script")"
pins_label="versions.tf pins OpenTofu and the google provider exactly, as install-iac-tools.sh does"
if grep -Fxq "  required_version = \"= $tofu_version\"" versions.tf && grep -Fxq "      version = \"= $google_version\"" versions.tf \
  && [[ "$(grep -cE '^[[:space:]]*(required_)?version[[:space:]]*=' versions.tf)" -eq 2 ]]; then ok "$pins_label"; else bad "$pins_label"; fi
# The lock file's zip checksums (zh:) are exactly install-iac-tools.sh's pinned provider zips, one per platform, and its version is the pin.
lock_label="the lock file holds exactly install-iac-tools.sh's provider zip checksums"
pinned_zips="$(sed -nE 's/^    google_sha256=([0-9a-f]{64})$/\1/p' "$tools_script" | sort)"
locked_zips="$(sed -nE 's/^    "zh:([0-9a-f]{64})",$/\1/p' .terraform.lock.hcl | sort)"
if [[ -n "$pinned_zips" && "$pinned_zips" == "$locked_zips" && "$(grep -c '^provider ' .terraform.lock.hcl)" -eq 1 ]] \
  && grep -Fxq "  version     = \"$google_version\"" .terraform.lock.hcl; then ok "$lock_label"; else bad "$lock_label"; fi
if grep -nE '^[[:space:]]*sensitive[[:space:]]*=' ./*.tf > "$work/sensitive.txt"; then bad "no input or output is sensitive: $(cat "$work/sensitive.txt")"
else ok "no input or output is sensitive"; fi
if grep -nE 'secret_key_ref|value_source' ./*.tf > "$work/env.txt"; then bad "no secret is an environment value (no secret_key_ref): $(cat "$work/env.txt")"
else ok "no secret is an environment value (no secret_key_ref)"; fi
# No authoritative IAM (a binding or a policy replaces everyone else's), no grant on a folder or an organisation, and on a project only the
# one Cloud SQL Client grant, with its condition (tests/defaults.tftest.hcl checks the condition itself).
grant_label="the only project-level grant is the conditioned Cloud SQL Client"
project_grants="$(grep -hE '^resource "google_(project|folder|organization)_iam_' ./*.tf || true)"
if [[ "$project_grants" == 'resource "google_project_iam_member" "cloudsql_client" {' ]] && ! grep -qE '_iam_(binding|policy)"' ./*.tf \
  && grep -Fq '  role    = "roles/cloudsql.client"' iam.tf && [[ "$(grep -c '^  condition {$' iam.tf)" -eq 1 ]]; then ok "$grant_label"
else bad "$grant_label: $project_grants"; fi
if grep -nE 'google_project_service' ./*.tf > "$work/service.txt"; then bad "no API is enabled (no google_project_service): $(cat "$work/service.txt")"
else ok "no API is enabled (no google_project_service)"; fi
# The module makes exactly these resources and reads no data source: another grant, on a secret, a service account or a job, is a change
# to this list, and so to this test.
resources_label="the module makes exactly its fixed set of resources, and reads no data source"
expected_resources='resource "google_cloud_run_v2_job" "migrate"
resource "google_cloud_run_v2_job" "retention"
resource "google_cloud_run_v2_job_iam_member" "scheduler_runs_retention"
resource "google_cloud_run_v2_service" "server"
resource "google_cloud_scheduler_job" "retention"
resource "google_compute_address" "egress"
resource "google_compute_network" "egress"
resource "google_compute_router" "egress"
resource "google_compute_router_nat" "egress"
resource "google_compute_subnetwork" "egress"
resource "google_project_iam_member" "cloudsql_client"
resource "google_secret_manager_secret_iam_member" "migrate"
resource "google_secret_manager_secret_iam_member" "server"
resource "google_service_account" "migrate"
resource "google_service_account" "scheduler"
resource "google_service_account" "server"
resource "terraform_data" "checks"'
declared="$(grep -hE '^[[:space:]]*(resource|data|module|import)[[:space:]]' ./*.tf | sed -E 's/[[:space:]]*\{[[:space:]]*$//' | LC_ALL=C sort)"
if [[ "$declared" == "$expected_resources" ]]; then ok "$resources_label"
else bad "$resources_label: $(diff <(echo "$expected_resources") <(echo "$declared") | grep '^[<>]' | tr '\n' ' ')"; fi
# Nothing runs on the deployer's machine or reads a value outside the plan: no provisioner (local-exec, remote-exec, file), no connection
# block, no ephemeral resource.
blocks_label="no provisioner, connection or ephemeral block"
if grep -nE '^[[:space:]]*(provisioner|connection|ephemeral)([[:space:]]|\{|$)' ./*.tf > "$work/blocks.txt"; then bad "$blocks_label: $(cat "$work/blocks.txt")"
else ok "$blocks_label"; fi
# No secret's value is ever read into the configuration (and so into the plan and the state): no secret version, no secret_data, and
# nothing made non-sensitive.
if grep -nE 'google_secret_manager_secret_version|secret_data|nonsensitive\(' ./*.tf > "$work/values.txt"; then
  bad "no secret value is read (no secret version, secret_data or nonsensitive): $(cat "$work/values.txt")"
else ok "no secret value is read (no secret version, secret_data or nonsensitive)"; fi

# Every PACKMOAT_ name the template uses, its _FILE form as the setting itself, must be a row of the settings table.
documented="$(grep -oE '^\| .PACKMOAT_[A-Z0-9_]+. \|' "$settings_doc" | grep -oE 'PACKMOAT_[A-Z0-9_]+' | sort -u)"
named="$(grep -ohE 'PACKMOAT_[A-Z0-9_]*[A-Z0-9]' ./*.tf example.tfvars | sed -E 's/_FILE$//' | sort -u)"
unknown="$(comm -23 <(echo "$named") <(echo "$documented"))"
if [[ -n "$named" && -z "$unknown" ]]; then ok "every PACKMOAT_ setting the template names is in docs/operations.md's table"
else bad "every PACKMOAT_ setting the template names is in docs/operations.md's table: not there: $(echo "$unknown" | tr '\n' ' ')"; fi
secret_outputs="$(grep -ohE '^output "[^"]+"' ./*.tf | grep -iE 'password|connection|secret|key|cert|token' || true)"
if [[ -z "$secret_outputs" && "$(grep -c '^output ' outputs.tf)" -gt 0 ]]; then ok "the outputs name no secret"; else bad "the outputs name no secret: $secret_outputs"; fi

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

echo "--- tofu test (a plan per run, against a mock of the google provider)"
# header <test file>: the file up to its first run (the mock, the overrides and its variables).
header() { sed -n '/^run "/q;p' "$1"; }
if [[ -z "$only" ]]; then
  # Every run of every file. A run that fails makes tofu skip the rest of its file, which then count as failed too.
  tofu test -no-color > "$work/test.txt" 2>&1 || true
  while read -r run; do
    if grep -Fxq "  run \"$run\"... pass" "$work/test.txt"; then ok "$run"; else bad "$run"; fi
  done < <(sed -nE 's/^run "([a-z0-9_]+)" \{$/\1/p' tests/*.tftest.hcl)
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
