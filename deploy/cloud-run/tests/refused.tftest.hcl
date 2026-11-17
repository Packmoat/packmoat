# What the template refuses: each run sets one input wrong and expects exactly that input's check (or the cross-input check) to fail.
# deploy/cloud-run/test-template.sh reports each by its name and has a mutation for each. Planned against a mock of the google
# provider: no Google account, nothing made.

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

# The required inputs, all accepted: each run below changes one.
variables {
  project_id         = "packmoat-test"
  region             = "europe-west1"
  service_image      = "ghcr.io/packmoat/packmoat-core@sha256:1111111111111111111111111111111111111111111111111111111111111111"
  job_image          = "ghcr.io/packmoat/packmoat-core@sha256:2222222222222222222222222222222222222222222222222222222222222222"
  hosts              = "packages.example.test"
  cloud_sql_instance = "packmoat-test:europe-west1:packmoat-pg"
}

run "refused_service_image_by_tag" {
  command = plan

  variables {
    service_image = "ghcr.io/packmoat/packmoat-core:main"
  }

  expect_failures = [var.service_image]
}

run "refused_job_image_by_tag" {
  command = plan

  variables {
    job_image = "ghcr.io/packmoat/packmoat-core:main"
  }

  expect_failures = [var.job_image]
}

run "refused_project_id_not_an_id" {
  command = plan

  variables {
    project_id = "Packmoat Test"
  }

  expect_failures = [var.project_id]
}

run "refused_region_not_a_region" {
  command = plan

  variables {
    region = "europe west1"
  }

  expect_failures = [var.region]
}

run "refused_name_not_a_name" {
  command = plan

  variables {
    name = "packmoat-"
  }

  expect_failures = [var.name]
}

run "refused_hosts_not_host_names" {
  command = plan

  variables {
    hosts = "packages.example.test,other.example.test"
  }

  expect_failures = [var.hosts]
}

run "refused_app_role_name_not_a_role" {
  command = plan

  variables {
    app_role_name = "app; drop"
  }

  expect_failures = [var.app_role_name]
}

run "refused_cloud_sql_instance_not_a_connection_name" {
  command = plan

  variables {
    cloud_sql_instance = "packmoat-test:europe-west1:Packmoat_PG"
  }

  expect_failures = [var.cloud_sql_instance]
}

run "refused_master_key_secret_not_an_id" {
  command = plan

  variables {
    master_key_secret = "projects/x/secrets/y"
  }

  expect_failures = [var.master_key_secret]
}

run "refused_app_database_secret_not_an_id" {
  command = plan

  variables {
    app_database_secret = "a b"
  }

  expect_failures = [var.app_database_secret]
}

run "refused_owner_database_secret_not_an_id" {
  command = plan

  variables {
    owner_database_secret = "a/b"
  }

  expect_failures = [var.owner_database_secret]
}

run "refused_app_cert_secret_not_an_id" {
  command = plan

  variables {
    app_cert_secret = "a:b"
  }

  expect_failures = [var.app_cert_secret]
}

run "refused_settings_not_packmoat_aspnetcore" {
  command = plan

  variables {
    settings = { ASPNETCORE_FORWARDEDHEADERS_ENABLED = "true" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_not_packmoat_dotnet_startup_hooks" {
  command = plan

  variables {
    settings = { DOTNET_STARTUP_HOOKS = "/tmp/hook.dll" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_port" {
  command = plan

  variables {
    settings = { PORT = "9090" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_trusted_proxies" {
  command = plan

  variables {
    settings = { PACKMOAT_TRUSTED_PROXIES = "0.0.0.0/0" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_db_engine" {
  command = plan

  variables {
    settings = { PACKMOAT_DB_ENGINE = "sqlite" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_cookie_keys" {
  command = plan

  variables {
    settings = { PACKMOAT_COOKIE_KEYS = "folder" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_background_jobs" {
  command = plan

  variables {
    settings = { PACKMOAT_BACKGROUND_JOBS = "on" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_hosts" {
  command = plan

  variables {
    settings = { PACKMOAT_HOSTS = "x" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_app_cert" {
  command = plan

  variables {
    settings = { PACKMOAT_APP_CERT = "/x" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_single_role" {
  command = plan

  variables {
    settings = { PACKMOAT_DB_SINGLE_ROLE = "on" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_master_key" {
  command = plan

  variables {
    settings = { PACKMOAT_MASTER_KEY = "x" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_database" {
  command = plan

  variables {
    settings = { PACKMOAT_DB = "x" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_admin_client_secret" {
  command = plan

  variables {
    settings = { PACKMOAT_ADMIN_CLIENT_SECRET = "x" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_a_file_setting" {
  command = plan

  variables {
    settings = { PACKMOAT_MASTER_KEY_FILE = "/x" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_any_db_setting" {
  command = plan

  variables {
    settings = { PACKMOAT_DB_POOL = "x" }
  }

  expect_failures = [var.settings]
}

run "refused_file_settings_not_packmoat" {
  command = plan

  variables {
    file_settings = { DOTNET_STARTUP_HOOKS = "hook" }
  }

  expect_failures = [var.file_settings]
}

run "refused_file_settings_hosts" {
  command = plan

  variables {
    file_settings = { PACKMOAT_HOSTS = "x" }
  }

  expect_failures = [var.file_settings]
}

run "refused_file_settings_single_role" {
  command = plan

  variables {
    file_settings = { PACKMOAT_DB_SINGLE_ROLE = "x" }
  }

  expect_failures = [var.file_settings]
}

run "refused_file_settings_master_key" {
  command = plan

  variables {
    file_settings = { PACKMOAT_MASTER_KEY = "other-key" }
  }

  expect_failures = [var.file_settings]
}

run "refused_file_settings_database" {
  command = plan

  variables {
    file_settings = { PACKMOAT_DB = "other-db" }
  }

  expect_failures = [var.file_settings]
}

run "refused_file_settings_any_db_setting" {
  command = plan

  variables {
    file_settings = { PACKMOAT_DB_OTHER = "other-db" }
  }

  expect_failures = [var.file_settings]
}

run "refused_file_settings_a_file_setting" {
  command = plan

  variables {
    file_settings = { PACKMOAT_ADMIN_CLIENT_SECRET_FILE = "x" }
  }

  expect_failures = [var.file_settings]
}

run "refused_file_settings_value_not_a_secret_id" {
  command = plan

  variables {
    file_settings = { PACKMOAT_ADMIN_CLIENT_SECRET = "projects/x/secrets/y" }
  }

  expect_failures = [var.file_settings]
}

run "refused_min_instances_below_zero" {
  command = plan

  variables {
    min_instances = -1
  }

  expect_failures = [var.min_instances]
}

run "refused_max_instances_zero" {
  command = plan

  variables {
    max_instances = 0
  }

  expect_failures = [var.max_instances]
}

run "refused_max_instances_over_100" {
  command = plan

  variables {
    max_instances = 101
  }

  expect_failures = [var.max_instances]
}

run "refused_max_instances_under_min_instances" {
  command = plan

  variables {
    min_instances = 3
    max_instances = 2
  }

  expect_failures = [terraform_data.checks]
}

run "refused_owner_secret_is_the_master_keys" {
  command = plan

  variables {
    owner_database_secret = "packmoat-master-key"
  }

  expect_failures = [terraform_data.checks]
}

run "refused_owner_secret_is_the_app_roles" {
  command = plan

  variables {
    owner_database_secret = "packmoat-db-app"
  }

  expect_failures = [terraform_data.checks]
}

run "refused_owner_secret_is_the_site_certificates" {
  command = plan

  variables {
    app_cert_secret = "packmoat-db-owner"
  }

  expect_failures = [terraform_data.checks]
}

run "refused_owner_secret_is_a_file_settings" {
  command = plan

  variables {
    file_settings = { PACKMOAT_ADMIN_CLIENT_SECRET = "packmoat-db-owner" }
  }

  expect_failures = [terraform_data.checks]
}

run "refused_trusted_proxies_every_ipv4_address" {
  command = plan

  variables {
    trusted_proxies = "10.0.0.0/8 0.0.0.0/0"
  }

  expect_failures = [var.trusted_proxies]
}

run "refused_trusted_proxies_every_address_after_a_comma" {
  command = plan

  variables {
    trusted_proxies = "10.0.0.0/8,0.0.0.0/0"
  }

  expect_failures = [var.trusted_proxies]
}

run "refused_trusted_proxies_every_ipv6_address" {
  command = plan

  variables {
    trusted_proxies = "::/0"
  }

  expect_failures = [var.trusted_proxies]
}

run "refused_trusted_proxies_blank" {
  command = plan

  variables {
    trusted_proxies = " "
  }

  expect_failures = [var.trusted_proxies]
}

run "refused_egress_subnet_smaller_than_26" {
  command = plan

  variables {
    egress_subnet_cidr = "10.40.0.0/28"
  }

  expect_failures = [var.egress_subnet_cidr]
}

run "refused_retention_schedule_not_cron" {
  command = plan

  variables {
    retention_schedule = "daily"
  }

  expect_failures = [var.retention_schedule]
}
