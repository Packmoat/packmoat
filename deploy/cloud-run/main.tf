# Packmoat on Google Cloud Run: the server as a Cloud Run service, on an existing Cloud SQL for PostgreSQL 17 instance, with its secrets
# read from Secret Manager in the same project. docs/deploy/cloud-run.md explains each input and the steps around a deploy.
#
# What it makes: three service accounts (the server's, the migrate job's, the scheduler's), each given Secret Manager Secret Accessor on its
# own secrets only (one secret at a time, never the project); Cloud SQL Client for the server's and the migrate job's, on the project with
# a condition naming the one instance; the service (service.tf); a migrate job, run by hand, and a retention job, started daily by Cloud
# Scheduler (jobs.tf); and, only when enable_static_egress is true, a VPC with Cloud NAT and a static external address (network.tf).
#
# No secret is ever an input, an output or an environment value: each one is a secret volume of its own, mounted as a file under
# /run/secrets/packmoat, and the server reads it through the setting's _FILE form. Only the migrate job gets the owner role's connection
# string. It enables no API (docs/deploy/cloud-run.md lists them). deploy/cloud-run/test-template.sh checks all of this offline.

provider "google" {
  project = var.project_id
  region  = var.region
}

locals {
  # The settings this template sets itself, and the secret ones, which may only ever be files from Secret Manager. Every PACKMOAT_DB
  # setting (the engine, the connection string, the single-role switch, and any added later) is the template's alone, refused by its
  # prefix (variables.tf), so it is in neither list. PORT is Cloud Run's own: it sets it to the container port, 8080.
  template_settings = [
    "PACKMOAT_COOKIE_KEYS",
    "PACKMOAT_BACKGROUND_JOBS",
    "PACKMOAT_HOSTS",
    "PACKMOAT_TRUSTED_PROXIES",
    "PACKMOAT_APP_CERT",
  ]
  secret_settings = [
    "PACKMOAT_MASTER_KEY",
    "PACKMOAT_ADMIN_CLIENT_SECRET",
  ]

  # Where each secret is mounted: its own volume at /run/secrets/packmoat/<volume>, holding one file.
  secrets_path = "/run/secrets/packmoat"

  # The server's secrets, by volume: the master key and the app role's connection string, always; the customer site's certificate and
  # any file_settings when given. Never the owner's connection string.
  file_setting_volumes = { for k, v in var.file_settings : lower(replace(k, "_", "-")) => { secret = v, file = lower(replace(k, "_", "-")), setting = k } }
  server_secrets = merge(
    {
      "master-key" = { secret = var.master_key_secret, file = "master-key" }
      "db-app"     = { secret = var.app_database_secret, file = "db-app" }
    },
    var.app_cert_secret == "" ? {} : { "app-cert" = { secret = var.app_cert_secret, file = "app-signin.pem" } },
    { for k, v in local.file_setting_volumes : k => { secret = v.secret, file = v.file } },
  )
  # The retention job runs the server's pass from the command line: the master key and the app role's connection string only.
  retention_secrets = {
    "master-key" = local.server_secrets["master-key"]
    "db-app"     = local.server_secrets["db-app"]
  }
  migrate_secrets = {
    "db-owner" = { secret = var.owner_database_secret, file = "db-owner" }
  }

  # The Cloud SQL instance, from its connection name.
  sql_project  = split(":", var.cloud_sql_instance)[0]
  sql_instance = split(":", var.cloud_sql_instance)[2]
}

# The checks between two inputs. A precondition stops the plan, naming the inputs, never a value.
resource "terraform_data" "checks" {
  lifecycle {
    precondition {
      condition     = var.max_instances >= var.min_instances
      error_message = "max_instances must be at least min_instances."
    }

    # The owner's connection string is the migrate job's alone: no secret the server reads may be the same one.
    precondition {
      condition     = !contains([for s in values(local.server_secrets) : s.secret], var.owner_database_secret)
      error_message = "owner_database_secret may not also be the server's secret (master_key_secret, app_database_secret, app_cert_secret or file_settings)."
    }
  }
}
