# The service accounts, and exactly what each may do. Three accounts, so that each reads only its own secrets: the server's (also the
# retention job's, which reads a subset of the server's) cannot read the owner's connection string; the migrate job's can read nothing
# else; the scheduler's can only start the retention job.

resource "google_service_account" "server" {
  account_id   = "${var.name}-server"
  display_name = "Packmoat server and retention job"
}

resource "google_service_account" "migrate" {
  account_id   = "${var.name}-migrate"
  display_name = "Packmoat migrate job"
}

resource "google_service_account" "scheduler" {
  account_id   = "${var.name}-scheduler"
  display_name = "Packmoat scheduler (starts the retention job)"
}

# Secret Manager Secret Accessor on each secret the account's own resources mount, one secret at a time: never on the project.
resource "google_secret_manager_secret_iam_member" "server" {
  for_each = toset([for s in values(local.server_secrets) : s.secret])

  secret_id = each.value
  role      = "roles/secretmanager.secretAccessor"
  member    = google_service_account.server.member
}

resource "google_secret_manager_secret_iam_member" "migrate" {
  for_each = toset([for s in values(local.migrate_secrets) : s.secret])

  secret_id = each.value
  role      = "roles/secretmanager.secretAccessor"
  member    = google_service_account.migrate.member
}

# Cloud SQL Client, needed to open the instance's socket. It is granted on the instance's project, so a condition narrows it to the one
# instance. The only project-level grant this template makes.
resource "google_project_iam_member" "cloudsql_client" {
  for_each = {
    server  = google_service_account.server.member
    migrate = google_service_account.migrate.member
  }

  project = local.sql_project
  role    = "roles/cloudsql.client"
  member  = each.value

  condition {
    title       = "${var.name}-cloudsql-${each.key}"
    description = "Packmoat's Cloud SQL instance only."
    expression  = "resource.type == \"sqladmin.googleapis.com/Instance\" && resource.name == \"projects/${local.sql_project}/instances/${local.sql_instance}\""
  }
}

# The scheduler's account may start the retention job, and nothing else.
resource "google_cloud_run_v2_job_iam_member" "scheduler_runs_retention" {
  name     = google_cloud_run_v2_job.retention.name
  location = google_cloud_run_v2_job.retention.location
  role     = "roles/run.invoker"
  member   = google_service_account.scheduler.member
}
