# The two jobs. migrate: run by hand before each upgrade, with the owner role's connection string, which nothing else gets. retention: the
# server's daily retention pass (packmoat-admin jobs run retention), started by Cloud Scheduler, as the server's account.

resource "google_cloud_run_v2_job" "migrate" {
  name     = "${var.name}-migrate"
  location = var.region

  deletion_protection = true

  template {
    task_count  = 1
    parallelism = 1

    template {
      service_account = google_service_account.migrate.email
      max_retries     = 0
      timeout         = "1800s"

      containers {
        name    = "migrate"
        image   = var.job_image
        command = ["/usr/bin/env", "-u", "PACKMOAT_DB", "packmoat-admin"]
        args    = ["db", "migrate", "--grant-to", var.app_role_name]

        env {
          name  = "PACKMOAT_DB_ENGINE"
          value = "postgres"
        }
        env {
          name  = "PACKMOAT_DB_FILE"
          value = "${local.secrets_path}/db-owner/db-owner"
        }

        dynamic "volume_mounts" {
          for_each = local.migrate_secrets
          content {
            name       = volume_mounts.key
            mount_path = "${local.secrets_path}/${volume_mounts.key}"
          }
        }

        volume_mounts {
          name       = "cloudsql"
          mount_path = "/cloudsql"
        }
      }

      dynamic "volumes" {
        for_each = local.migrate_secrets
        content {
          name = volumes.key
          secret {
            secret = volumes.value.secret
            items {
              version = "latest"
              path    = volumes.value.file
            }
          }
        }
      }

      volumes {
        name = "cloudsql"
        cloud_sql_instance {
          instances = [var.cloud_sql_instance]
        }
      }
    }
  }

  depends_on = [
    terraform_data.checks,
    google_secret_manager_secret_iam_member.migrate,
    google_project_iam_member.cloudsql_client,
  ]
}

# The retention pass opens the stores as the server does, so it needs the master key and the app role's connection string; it runs the
# server's image, whose schema the database is at.
resource "google_cloud_run_v2_job" "retention" {
  name     = "${var.name}-retention"
  location = var.region

  deletion_protection = true

  template {
    task_count  = 1
    parallelism = 1

    template {
      service_account = google_service_account.server.email
      max_retries     = 0
      timeout         = "1800s"

      containers {
        name    = "retention"
        image   = var.service_image
        command = ["/usr/bin/env", "-u", "PACKMOAT_DB", "packmoat-admin"]
        args    = ["jobs", "run", "retention"]

        env {
          name  = "PACKMOAT_DB_ENGINE"
          value = "postgres"
        }
        env {
          name  = "PACKMOAT_DB_FILE"
          value = "${local.secrets_path}/db-app/db-app"
        }
        env {
          name  = "PACKMOAT_MASTER_KEY_FILE"
          value = "${local.secrets_path}/master-key/master-key"
        }

        dynamic "volume_mounts" {
          for_each = local.retention_secrets
          content {
            name       = volume_mounts.key
            mount_path = "${local.secrets_path}/${volume_mounts.key}"
          }
        }

        volume_mounts {
          name       = "cloudsql"
          mount_path = "/cloudsql"
        }
      }

      dynamic "volumes" {
        for_each = local.retention_secrets
        content {
          name = volumes.key
          secret {
            secret = volumes.value.secret
            items {
              version = "latest"
              path    = volumes.value.file
            }
          }
        }
      }

      volumes {
        name = "cloudsql"
        cloud_sql_instance {
          instances = [var.cloud_sql_instance]
        }
      }
    }
  }

  depends_on = [
    terraform_data.checks,
    google_secret_manager_secret_iam_member.server,
    google_project_iam_member.cloudsql_client,
  ]
}

# Cloud Scheduler starts the retention job once a day through the Cloud Run Admin API, signed in as the scheduler's account.
resource "google_cloud_scheduler_job" "retention" {
  name      = "${var.name}-retention"
  region    = var.region
  schedule  = var.retention_schedule
  time_zone = var.retention_time_zone

  http_target {
    http_method = "POST"
    uri         = "https://run.googleapis.com/v2/projects/${var.project_id}/locations/${var.region}/jobs/${google_cloud_run_v2_job.retention.name}:run"

    oauth_token {
      service_account_email = google_service_account.scheduler.email
      scope                 = "https://www.googleapis.com/auth/cloud-platform"
    }
  }

  depends_on = [google_cloud_run_v2_job_iam_member.scheduler_runs_retention]
}
