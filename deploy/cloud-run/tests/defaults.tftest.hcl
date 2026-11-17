# What the template plans with the required inputs only. Each run is one check; deploy/cloud-run/test-template.sh reports each by its
# name and has a mutation for each. Planned against a mock of the google provider: no Google account, nothing made.

mock_provider "google" {}

# The service accounts' addresses, which Google gives when they are made: the mock would make up values the provider then refuses.
override_resource {
  target = google_service_account.server
  values = {
    email  = "packmoat-server@packmoat-test.iam.example"
    member = "serviceAccount:packmoat-server@packmoat-test.iam.example"
  }
}

override_resource {
  target = google_service_account.migrate
  values = {
    email  = "packmoat-migrate@packmoat-test.iam.example"
    member = "serviceAccount:packmoat-migrate@packmoat-test.iam.example"
  }
}

override_resource {
  target = google_service_account.scheduler
  values = {
    email  = "packmoat-scheduler@packmoat-test.iam.example"
    member = "serviceAccount:packmoat-scheduler@packmoat-test.iam.example"
  }
}

# The required inputs; the two images differ, so that each is seen where it belongs.
variables {
  project_id         = "packmoat-test"
  region             = "europe-west1"
  service_image      = "ghcr.io/packmoat/packmoat-core@sha256:1111111111111111111111111111111111111111111111111111111111111111"
  job_image          = "ghcr.io/packmoat/packmoat-core@sha256:2222222222222222222222222222222222222222222222222222222222222222"
  hosts              = "packages.example.test"
  cloud_sql_instance = "packmoat-test:europe-west1:packmoat-pg"
}

run "server_listens_on_8080_and_cloud_run_sets_port" {
  command = plan

  assert {
    condition = (
      [for p in google_cloud_run_v2_service.server.template[0].containers[0].ports : p.container_port] == [8080]
      && !contains([for e in google_cloud_run_v2_service.server.template[0].containers[0].env : e.name], "PORT")
    )
    error_message = "The container port must be 8080, and PORT is Cloud Run's to set."
  }
}

run "startup_probe_readyz_without_host_header" {
  command = plan

  assert {
    condition = alltrue([
      for p in google_cloud_run_v2_service.server.template[0].containers[0].startup_probe : (
        length(p.http_get) == 1 && p.http_get[0].path == "/readyz" && p.http_get[0].port == 8080 && length(p.http_get[0].http_headers) == 0
      )
    ]) && length(google_cloud_run_v2_service.server.template[0].containers[0].startup_probe) == 1
    error_message = "The startup probe must GET /readyz on 8080 and set no header."
  }
}

run "liveness_probe_healthz_without_host_header" {
  command = plan

  assert {
    condition = alltrue([
      for p in google_cloud_run_v2_service.server.template[0].containers[0].liveness_probe : (
        length(p.http_get) == 1 && p.http_get[0].path == "/healthz" && p.http_get[0].port == 8080 && length(p.http_get[0].http_headers) == 0
      )
    ]) && length(google_cloud_run_v2_service.server.template[0].containers[0].liveness_probe) == 1
    error_message = "The liveness probe must GET /healthz on 8080 and set no header."
  }
}

run "server_settings_postgres_database_keys_background_jobs_off" {
  command = plan

  assert {
    condition = { for e in google_cloud_run_v2_service.server.template[0].containers[0].env : e.name => e.value } == {
      PACKMOAT_DB_ENGINE       = "postgres"
      PACKMOAT_COOKIE_KEYS     = "database"
      PACKMOAT_BACKGROUND_JOBS = "off"
      PACKMOAT_HOSTS           = "packages.example.test"
      PACKMOAT_TRUSTED_PROXIES = "169.254.0.0/16"
      PACKMOAT_MASTER_KEY_FILE = "/run/secrets/packmoat/master-key/master-key"
      PACKMOAT_DB_FILE         = "/run/secrets/packmoat/db-app/db-app"
    }
    error_message = "The server's settings are not exactly the template's."
  }
}

run "zero_to_one_copy_by_default" {
  command = plan

  assert {
    condition = (
      google_cloud_run_v2_service.server.template[0].scaling[0].min_instance_count == 0
      && google_cloud_run_v2_service.server.template[0].scaling[0].max_instance_count == 1
    )
    error_message = "By default the server scales from 0 to 1 copy."
  }
}

run "server_is_public_and_request_billed" {
  command = plan

  assert {
    condition = (
      google_cloud_run_v2_service.server.ingress == "INGRESS_TRAFFIC_ALL"
      && google_cloud_run_v2_service.server.invoker_iam_disabled == true
      && google_cloud_run_v2_service.server.template[0].containers[0].resources[0].cpu_idle == true
    )
    error_message = "The server takes traffic from anywhere, with no invoker check, and is billed by request."
  }
}

run "each_runs_its_own_image" {
  command = plan

  assert {
    condition = (
      google_cloud_run_v2_service.server.template[0].containers[0].image == var.service_image
      && google_cloud_run_v2_job.retention.template[0].template[0].containers[0].image == var.service_image
      && google_cloud_run_v2_job.migrate.template[0].template[0].containers[0].image == var.job_image
    )
    error_message = "The server and the retention job run service_image; the migrate job runs job_image."
  }
}

run "server_reads_master_key_and_app_role_only" {
  command = plan

  assert {
    condition = (
      toset([for v in google_cloud_run_v2_service.server.template[0].volumes : v.secret[0].secret if length(v.secret) > 0])
      == toset(["packmoat-master-key", "packmoat-db-app"])
    )
    error_message = "The server mounts the master key and the app role's connection string, and no other secret."
  }
}

run "server_account_reads_exactly_its_mounted_secrets" {
  command = plan

  assert {
    condition = (
      toset([for m in google_secret_manager_secret_iam_member.server : m.secret_id])
      == toset([for v in google_cloud_run_v2_service.server.template[0].volumes : v.secret[0].secret if length(v.secret) > 0])
      && alltrue([for m in google_secret_manager_secret_iam_member.server :
      m.role == "roles/secretmanager.secretAccessor" && m.member == "serviceAccount:packmoat-server@packmoat-test.iam.example"])
      && google_cloud_run_v2_service.server.template[0].service_account == "packmoat-server@packmoat-test.iam.example"
    )
    error_message = "The server's account must be Secret Accessor on exactly the secrets the server mounts."
  }
}

run "each_secret_is_a_file_volume_of_its_own" {
  command = plan

  assert {
    condition = alltrue(flatten([
      for c in [
        google_cloud_run_v2_service.server.template[0],
        google_cloud_run_v2_job.migrate.template[0].template[0],
        google_cloud_run_v2_job.retention.template[0].template[0],
        ] : [
        for v in c.volumes : (
          length(v.secret[0].items) == 1 && v.secret[0].items[0].version == "latest"
          && contains([for m in c.containers[0].volume_mounts : "${m.name}=${m.mount_path}"], "${v.name}=/run/secrets/packmoat/${v.name}")
        ) if length(v.secret) > 0
      ]
    ]))
    error_message = "Each secret must be its own volume of one file (its latest version), mounted at /run/secrets/packmoat/<volume>."
  }
}

run "each_file_setting_names_a_mounted_file" {
  command = plan

  assert {
    condition = alltrue(flatten([
      for c in [
        google_cloud_run_v2_service.server.template[0],
        google_cloud_run_v2_job.migrate.template[0].template[0],
        google_cloud_run_v2_job.retention.template[0].template[0],
        ] : [
        for e in c.containers[0].env : contains(flatten([
          for v in c.volumes : [for i in v.secret[0].items : "/run/secrets/packmoat/${v.name}/${i.path}"] if length(v.secret) > 0
        ]), e.value) if endswith(e.name, "_FILE") || e.name == "PACKMOAT_APP_CERT"
      ]
    ]))
    error_message = "Every _FILE setting (and PACKMOAT_APP_CERT) must name a file in one of its own resource's secret volumes."
  }
}

run "no_secret_is_an_environment_value" {
  command = plan

  assert {
    condition = alltrue(flatten([
      for c in [
        google_cloud_run_v2_service.server.template[0],
        google_cloud_run_v2_job.migrate.template[0].template[0],
        google_cloud_run_v2_job.retention.template[0].template[0],
        ] : [
        for e in c.containers[0].env : length(e.value_source) == 0 && !contains(["PACKMOAT_MASTER_KEY", "PACKMOAT_DB", "PACKMOAT_ADMIN_CLIENT_SECRET"], e.name)
      ]
    ]))
    error_message = "No environment value may come from a secret, and no secret setting may be set by value."
  }
}

run "no_single_role" {
  command = plan

  assert {
    condition = (
      !contains([for e in google_cloud_run_v2_service.server.template[0].containers[0].env : e.name], "PACKMOAT_DB_SINGLE_ROLE")
      && !contains([for e in google_cloud_run_v2_job.retention.template[0].template[0].containers[0].env : e.name], "PACKMOAT_DB_SINGLE_ROLE")
      && !contains(google_cloud_run_v2_job.migrate.template[0].template[0].containers[0].args, "--single-role")
    )
    error_message = "No PACKMOAT_DB_SINGLE_ROLE for the server or the retention job, no --single-role in the migrate job."
  }
}

run "cloud_sql_socket_everywhere" {
  command = plan

  assert {
    condition = alltrue([
      for c in [
        google_cloud_run_v2_service.server.template[0],
        google_cloud_run_v2_job.migrate.template[0].template[0],
        google_cloud_run_v2_job.retention.template[0].template[0],
        ] : (
        jsonencode([for v in c.volumes : v.cloud_sql_instance[0].instances if length(v.cloud_sql_instance) > 0]) == jsonencode([["packmoat-test:europe-west1:packmoat-pg"]])
        && contains([for m in c.containers[0].volume_mounts : "${m.name}=${m.mount_path}"], "cloudsql=/cloudsql")
      )
    ])
    error_message = "The server and both jobs mount the Cloud SQL socket of cloud_sql_instance at /cloudsql."
  }
}

# The image bakes PACKMOAT_DB (a SQLite path) and the server refuses it beside PACKMOAT_DB_FILE, so each container clears it first with
# env -u, then runs what the image would: its entrypoint (the server) or packmoat-admin.
run "each_container_clears_the_images_db_setting_before_the_file_is_read" {
  command = plan

  assert {
    condition = (
      google_cloud_run_v2_service.server.template[0].containers[0].command
      == tolist(["/usr/bin/env", "-u", "PACKMOAT_DB", "dotnet", "/app/host/Packmoat.Server.dll"])
      && google_cloud_run_v2_job.migrate.template[0].template[0].containers[0].command == tolist(["/usr/bin/env", "-u", "PACKMOAT_DB", "packmoat-admin"])
      && google_cloud_run_v2_job.retention.template[0].template[0].containers[0].command == tolist(["/usr/bin/env", "-u", "PACKMOAT_DB", "packmoat-admin"])
    )
    error_message = "The server, the migrate job and the retention job must each run env -u PACKMOAT_DB first (the image's PACKMOAT_DB beside PACKMOAT_DB_FILE is refused)."
  }
}

run "migrate_job_runs_db_migrate_grant_to" {
  command = plan

  assert {
    condition = (
      google_cloud_run_v2_job.migrate.template[0].template[0].containers[0].args == tolist(["db", "migrate", "--grant-to", "packmoat_app"])
      && google_cloud_run_v2_job.migrate.template[0].template[0].max_retries == 0
      && google_cloud_run_v2_job.migrate.template[0].task_count == 1
      && google_cloud_run_v2_job.migrate.template[0].template[0].service_account == "packmoat-migrate@packmoat-test.iam.example"
    )
    error_message = "The migrate job runs packmoat-admin db migrate --grant-to <app role> once, as its own account."
  }
}

run "migrate_job_signs_in_as_the_owner_only" {
  command = plan

  assert {
    condition = (
      { for e in google_cloud_run_v2_job.migrate.template[0].template[0].containers[0].env : e.name => e.value } == {
        PACKMOAT_DB_ENGINE = "postgres"
        PACKMOAT_DB_FILE   = "/run/secrets/packmoat/db-owner/db-owner"
      }
      && [for v in google_cloud_run_v2_job.migrate.template[0].template[0].volumes : v.secret[0].secret if length(v.secret) > 0] == ["packmoat-db-owner"]
      && toset([for m in google_secret_manager_secret_iam_member.migrate : m.secret_id]) == toset(["packmoat-db-owner"])
      && alltrue([for m in google_secret_manager_secret_iam_member.migrate :
      m.role == "roles/secretmanager.secretAccessor" && m.member == "serviceAccount:packmoat-migrate@packmoat-test.iam.example"])
    )
    error_message = "The migrate job mounts and may read the owner's connection string, and nothing else."
  }
}

run "the_owner_secret_is_the_migrate_jobs_alone" {
  command = plan

  assert {
    condition = (
      !contains([for v in google_cloud_run_v2_service.server.template[0].volumes : v.secret[0].secret if length(v.secret) > 0], "packmoat-db-owner")
      && !contains([for v in google_cloud_run_v2_job.retention.template[0].template[0].volumes : v.secret[0].secret if length(v.secret) > 0], "packmoat-db-owner")
      && !contains([for m in google_secret_manager_secret_iam_member.server : m.secret_id], "packmoat-db-owner")
    )
    error_message = "Neither the server, the retention job nor the server's account may get the owner's connection string."
  }
}

run "retention_job_runs_the_pass_as_the_server" {
  command = plan

  assert {
    condition = (
      google_cloud_run_v2_job.retention.template[0].template[0].containers[0].args == tolist(["jobs", "run", "retention"])
      && google_cloud_run_v2_job.retention.template[0].template[0].service_account == "packmoat-server@packmoat-test.iam.example"
      && { for e in google_cloud_run_v2_job.retention.template[0].template[0].containers[0].env : e.name => e.value } == {
        PACKMOAT_DB_ENGINE       = "postgres"
        PACKMOAT_DB_FILE         = "/run/secrets/packmoat/db-app/db-app"
        PACKMOAT_MASTER_KEY_FILE = "/run/secrets/packmoat/master-key/master-key"
      }
      && toset([for v in google_cloud_run_v2_job.retention.template[0].template[0].volumes : v.secret[0].secret if length(v.secret) > 0])
      == toset(["packmoat-master-key", "packmoat-db-app"])
    )
    error_message = "The retention job runs packmoat-admin jobs run retention as the server's account, with the master key and the app role only."
  }
}

run "scheduler_starts_the_retention_job_daily" {
  command = plan

  assert {
    condition = (
      google_cloud_scheduler_job.retention.schedule == "17 3 * * *"
      && google_cloud_scheduler_job.retention.http_target[0].http_method == "POST"
      && google_cloud_scheduler_job.retention.http_target[0].uri == "https://run.googleapis.com/v2/projects/packmoat-test/locations/europe-west1/jobs/packmoat-retention:run"
      && google_cloud_scheduler_job.retention.http_target[0].oauth_token[0].service_account_email == "packmoat-scheduler@packmoat-test.iam.example"
    )
    error_message = "Cloud Scheduler must POST the retention job's :run daily, signed in as the scheduler's account."
  }
}

run "scheduler_account_may_run_the_retention_job_only" {
  command = plan

  assert {
    condition = (
      google_cloud_run_v2_job_iam_member.scheduler_runs_retention.name == "packmoat-retention"
      && google_cloud_run_v2_job_iam_member.scheduler_runs_retention.role == "roles/run.invoker"
      && google_cloud_run_v2_job_iam_member.scheduler_runs_retention.member == "serviceAccount:packmoat-scheduler@packmoat-test.iam.example"
    )
    error_message = "The scheduler's account is Cloud Run Invoker on the retention job, and on nothing else."
  }
}

run "cloud_sql_client_on_the_one_instance_only" {
  command = plan

  assert {
    condition = (
      toset(keys(google_project_iam_member.cloudsql_client)) == toset(["server", "migrate"])
      && google_project_iam_member.cloudsql_client["server"].member == "serviceAccount:packmoat-server@packmoat-test.iam.example"
      && google_project_iam_member.cloudsql_client["migrate"].member == "serviceAccount:packmoat-migrate@packmoat-test.iam.example"
      && alltrue([for m in google_project_iam_member.cloudsql_client : (
        m.project == "packmoat-test" && m.role == "roles/cloudsql.client"
        && m.condition[0].expression == "resource.type == \"sqladmin.googleapis.com/Instance\" && resource.name == \"projects/packmoat-test/instances/packmoat-pg\""
      )])
    )
    error_message = "Cloud SQL Client for the server's and the migrate job's accounts, conditioned on the one instance."
  }
}

run "no_static_egress_by_default" {
  command = plan

  assert {
    condition = (
      length(google_compute_network.egress) + length(google_compute_subnetwork.egress) + length(google_compute_router.egress)
      + length(google_compute_address.egress) + length(google_compute_router_nat.egress) == 0
      && length(google_cloud_run_v2_service.server.template[0].vpc_access) == 0
      && output.outbound_ip_address == ""
    )
    error_message = "By default: no VPC, no NAT, no static address, and the service has no VPC egress."
  }
}

run "server_and_jobs_are_protected_from_deletion" {
  command = plan

  assert {
    condition = (
      google_cloud_run_v2_service.server.deletion_protection == true
      && google_cloud_run_v2_job.migrate.deletion_protection == true
      && google_cloud_run_v2_job.retention.deletion_protection == true
    )
    error_message = "The service and both jobs keep the provider's deletion protection on."
  }
}
