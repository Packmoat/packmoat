# What the template plans with every option set. Each run is one check; deploy/cloud-run/test-template.sh reports each by its name and
# has a mutation for each. Planned against a mock of the google provider: no Google account, nothing made.

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

variables {
  project_id         = "packmoat-test"
  region             = "europe-west1"
  service_image      = "ghcr.io/packmoat/packmoat-core@sha256:1111111111111111111111111111111111111111111111111111111111111111"
  job_image          = "ghcr.io/packmoat/packmoat-core@sha256:2222222222222222222222222222222222222222222222222222222222222222"
  hosts              = "packages.example.test"
  cloud_sql_instance = "packmoat-test:europe-west1:packmoat-pg"
  app_cert_secret    = "packmoat-app-cert"
  file_settings      = { PACKMOAT_ADMIN_CLIENT_SECRET = "packmoat-admin-client-secret" }
  settings           = { PACKMOAT_ADMIN_HOST = "admin.example.test", PACKMOAT_TOKEN_RPM = "600" }
  min_instances      = 2
  max_instances      = 3
}

run "with_every_option_the_server_mounts_each_secret_as_its_file" {
  command = plan

  assert {
    condition = (
      { for v in google_cloud_run_v2_service.server.template[0].volumes : v.name => "${v.secret[0].secret}/${v.secret[0].items[0].path}" if length(v.secret) > 0 } == {
        "master-key"                   = "packmoat-master-key/master-key"
        "db-app"                       = "packmoat-db-app/db-app"
        "app-cert"                     = "packmoat-app-cert/app-signin.pem"
        "packmoat-admin-client-secret" = "packmoat-admin-client-secret/packmoat-admin-client-secret"
      }
    )
    error_message = "With every option the server mounts the master key, the app role, the site certificate and each file setting, each as its own file."
  }
}

run "with_every_option_the_settings_name_the_files" {
  command = plan

  assert {
    condition = (
      { for e in google_cloud_run_v2_service.server.template[0].containers[0].env : e.name => e.value if e.name == "PACKMOAT_APP_CERT" || e.name == "PACKMOAT_ADMIN_CLIENT_SECRET_FILE" } == {
        PACKMOAT_APP_CERT                 = "/run/secrets/packmoat/app-cert/app-signin.pem"
        PACKMOAT_ADMIN_CLIENT_SECRET_FILE = "/run/secrets/packmoat/packmoat-admin-client-secret/packmoat-admin-client-secret"
      }
    )
    error_message = "PACKMOAT_APP_CERT and each file setting's _FILE must name its mounted file."
  }
}

run "with_every_option_plain_settings_are_passed_on" {
  command = plan

  assert {
    condition = (
      { for e in google_cloud_run_v2_service.server.template[0].containers[0].env : e.name => e.value if contains(["PACKMOAT_ADMIN_HOST", "PACKMOAT_TOKEN_RPM"], e.name) } == {
        PACKMOAT_ADMIN_HOST = "admin.example.test"
        PACKMOAT_TOKEN_RPM  = "600"
      }
    )
    error_message = "settings must be passed on as they are."
  }
}

run "with_every_option_the_server_account_reads_exactly_its_secrets" {
  command = plan

  assert {
    condition = (
      toset([for m in google_secret_manager_secret_iam_member.server : m.secret_id])
      == toset(["packmoat-master-key", "packmoat-db-app", "packmoat-app-cert", "packmoat-admin-client-secret"])
      && toset([for m in google_secret_manager_secret_iam_member.migrate : m.secret_id]) == toset(["packmoat-db-owner"])
    )
    error_message = "With every option the server's account reads exactly the server's four secrets, the migrate job's the owner's only."
  }
}

run "with_every_option_the_retention_job_still_reads_two_secrets" {
  command = plan

  assert {
    condition = (
      toset([for v in google_cloud_run_v2_job.retention.template[0].template[0].volumes : v.secret[0].secret if length(v.secret) > 0])
      == toset(["packmoat-master-key", "packmoat-db-app"])
    )
    error_message = "The retention job mounts the master key and the app role only, whatever the server has."
  }
}

run "with_every_option_the_scaling_inputs" {
  command = plan

  assert {
    condition = (
      google_cloud_run_v2_service.server.template[0].scaling[0].min_instance_count == 2
      && google_cloud_run_v2_service.server.template[0].scaling[0].max_instance_count == 3
    )
    error_message = "The server scales between min_instances and max_instances."
  }
}

run "with_static_egress_a_nat_with_one_static_address" {
  command = plan

  variables {
    enable_static_egress = true
  }

  assert {
    condition = (
      length(google_compute_network.egress) == 1 && google_compute_network.egress[0].auto_create_subnetworks == false
      && length(google_compute_subnetwork.egress) == 1 && google_compute_subnetwork.egress[0].ip_cidr_range == "10.40.0.0/26"
      && length(google_compute_router.egress) == 1
      && length(google_compute_address.egress) == 1 && google_compute_address.egress[0].address_type == "EXTERNAL"
      && length(google_compute_router_nat.egress) == 1
      && google_compute_router_nat.egress[0].nat_ip_allocate_option == "MANUAL_ONLY"
      && length(google_compute_router_nat.egress[0].nat_ips) == 1
      && google_compute_router_nat.egress[0].source_subnetwork_ip_ranges_to_nat == "LIST_OF_SUBNETWORKS"
    )
    error_message = "With enable_static_egress: a VPC, a subnet, a router, one static external address and Cloud NAT that uses only it."
  }
}

run "with_static_egress_the_server_sends_everything_through_the_vpc" {
  command = plan

  variables {
    enable_static_egress = true
  }

  assert {
    condition = (
      length(google_cloud_run_v2_service.server.template[0].vpc_access) == 1
      && google_cloud_run_v2_service.server.template[0].vpc_access[0].egress == "ALL_TRAFFIC"
      && length(google_cloud_run_v2_service.server.template[0].vpc_access[0].network_interfaces) == 1
    )
    error_message = "With enable_static_egress the server's every outbound packet goes through the subnet."
  }
}
