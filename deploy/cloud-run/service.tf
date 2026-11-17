# The server: a Cloud Run service on port 8080, public, with each secret a file volume of its own and the Cloud SQL socket at /cloudsql.

locals {
  # The server's own settings. Every secret is a _FILE path; none is a value. Background jobs are off: with CPU allocated only during requests
  # (cpu_idle), Cloud Run throttles the CPU between them, so the daily retention pass runs as a job instead (jobs.tf).
  server_env = merge(
    {
      PACKMOAT_DB_ENGINE       = "postgres"
      PACKMOAT_COOKIE_KEYS     = "database"
      PACKMOAT_BACKGROUND_JOBS = "off"
      PACKMOAT_HOSTS           = var.hosts
      PACKMOAT_TRUSTED_PROXIES = var.trusted_proxies
      PACKMOAT_MASTER_KEY_FILE = "${local.secrets_path}/master-key/master-key"
      PACKMOAT_DB_FILE         = "${local.secrets_path}/db-app/db-app"
    },
    var.app_cert_secret == "" ? {} : { PACKMOAT_APP_CERT = "${local.secrets_path}/app-cert/app-signin.pem" },
    { for k, v in local.file_setting_volumes : "${v.setting}_FILE" => "${local.secrets_path}/${k}/${v.file}" },
    var.settings,
  )
}

resource "google_cloud_run_v2_service" "server" {
  name     = var.name
  location = var.region
  ingress  = "INGRESS_TRAFFIC_ALL"

  # Packmoat is public: anyone may reach it, and it checks every request itself (tokens, sign-in). So Cloud Run's own invoker check is
  # off, rather than an allUsers grant.
  invoker_iam_disabled = true

  # The provider's own default, written out so that a change to it shows: tofu destroy refuses the service until it is set to false.
  deletion_protection = true

  template {
    service_account = google_service_account.server.email

    scaling {
      min_instance_count = var.min_instances
      max_instance_count = var.max_instances
    }

    # Only with enable_static_egress: every outbound packet through the subnet, so that Cloud NAT gives it the static address.
    dynamic "vpc_access" {
      for_each = var.enable_static_egress ? [1] : []
      content {
        network_interfaces {
          network    = google_compute_network.egress[0].id
          subnetwork = google_compute_subnetwork.egress[0].id
        }
        egress = "ALL_TRAFFIC"
      }
    }

    containers {
      name  = "packmoat"
      image = var.service_image
      # The image bakes PACKMOAT_DB (a SQLite path), which the server refuses beside PACKMOAT_DB_FILE: env -u clears it, then the image's own entrypoint runs.
      command = ["/usr/bin/env", "-u", "PACKMOAT_DB", "dotnet", "/app/host/Packmoat.Server.dll"]

      ports {
        container_port = 8080
      }

      resources {
        limits = {
          cpu    = var.cpu
          memory = var.memory
        }
        cpu_idle          = true
        startup_cpu_boost = true
      }

      dynamic "env" {
        for_each = local.server_env
        content {
          name  = env.key
          value = env.value
        }
      }

      # The probes set no Host header: the server answers /healthz and /readyz for an IP-address Host as well as its own names and
      # localhost. What Host Cloud Run's probes send is not yet verified on a real deploy.
      #
      # A new copy starts serving only once the database is at its schema (the migrate job ran first): until then /readyz answers 503,
      # the new revision never becomes ready and the old one keeps serving.
      startup_probe {
        http_get {
          path = "/readyz"
          port = 8080
        }
        period_seconds    = 10
        timeout_seconds   = 5
        failure_threshold = 24
      }

      liveness_probe {
        http_get {
          path = "/healthz"
          port = 8080
        }
        period_seconds    = 30
        timeout_seconds   = 5
        failure_threshold = 3
      }

      dynamic "volume_mounts" {
        for_each = local.server_secrets
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

    # Each secret is a volume of its own: its latest version, as one file.
    dynamic "volumes" {
      for_each = local.server_secrets
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

  depends_on = [
    terraform_data.checks,
    google_secret_manager_secret_iam_member.server,
    google_project_iam_member.cloudsql_client,
  ]
}
