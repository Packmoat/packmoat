# What the template refuses: each run sets one input wrong and expects exactly that input's check (or the cross-input checks) to fail.
# deploy/aws-ecs/test-template.sh reports each by its name and has a mutation for each. Planned against a mock of the aws provider: no
# AWS account, nothing made.

mock_provider "aws" {}

# The ids and ARNs AWS gives when each is made: the mock would make up values, so the checks below could not name them.
override_resource {
  target = aws_iam_role.server_execution
  values = {
    id  = "packmoat-server-execution"
    arn = "arn:aws:iam::123456789012:role/packmoat-server-execution"
  }
}

override_resource {
  target = aws_iam_role.migrate_execution
  values = {
    id  = "packmoat-migrate-execution"
    arn = "arn:aws:iam::123456789012:role/packmoat-migrate-execution"
  }
}

override_resource {
  target = aws_security_group.alb
  values = { id = "sg-0aaaaaaaaaaaaaaa1" }
}

override_resource {
  target = aws_security_group.task
  values = { id = "sg-0bbbbbbbbbbbbbbb2" }
}

override_resource {
  target = aws_ecs_cluster.main
  values = { arn = "arn:aws:ecs:eu-west-1:123456789012:cluster/packmoat" }
}

override_resource {
  target = aws_ecs_task_definition.server
  values = { arn = "arn:aws:ecs:eu-west-1:123456789012:task-definition/packmoat-server:1" }
}

override_resource {
  target = aws_ecs_task_definition.migrate
  values = { arn = "arn:aws:ecs:eu-west-1:123456789012:task-definition/packmoat-migrate:1" }
}

override_resource {
  target = aws_lb.main
  values = { arn = "arn:aws:elasticloadbalancing:eu-west-1:123456789012:loadbalancer/app/packmoat/0123456789abcdef" }
}

override_resource {
  target = aws_lb_target_group.server
  values = { arn = "arn:aws:elasticloadbalancing:eu-west-1:123456789012:targetgroup/packmoat-server/0123456789abcdef" }
}

override_resource {
  target = aws_subnet.private
  values = { id = "subnet-0ccccccccccccccc3" }
}

override_resource {
  target = aws_eip.nat
  values = { id = "eipalloc-0123456789abcdef0", public_ip = "203.0.113.10" }
}

override_resource {
  target = aws_nat_gateway.nat
  values = { id = "nat-0123456789abcdef0" }
}

override_resource {
  target = aws_route_table.private
  values = { id = "rtb-0123456789abcdef0" }
}

# The required inputs, all accepted: each run below changes one or two.
variables {
  account_id                = "123456789012"
  region                    = "eu-west-1"
  service_image             = "ghcr.io/packmoat/packmoat-core@sha256:1111111111111111111111111111111111111111111111111111111111111111"
  job_image                 = "ghcr.io/packmoat/packmoat-core@sha256:2222222222222222222222222222222222222222222222222222222222222222"
  hosts                     = "packages.example.test"
  vpc_id                    = "vpc-0123456789abcdef0"
  public_subnet_ids         = ["subnet-0aaaaaaaaaaaaaaa1", "subnet-0aaaaaaaaaaaaaaa2"]
  public_subnet_cidrs       = ["10.0.0.0/24", "10.0.1.0/24"]
  database_cidr             = "10.0.20.0/24"
  certificate_arn           = "arn:aws:acm:eu-west-1:123456789012:certificate/11111111-2222-3333-4444-555555555555"
  master_key_secret_arn     = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/master-key-AAAAAA"
  app_database_secret_arn   = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-app-BBBBBB"
  owner_database_secret_arn = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-owner-CCCCCC"
  database_ca_secret_arn    = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-ca-DDDDDD"
}

run "refused_account_id_not_12_digits" {
  command = plan

  variables {
    account_id = "12345678901"
  }

  expect_failures = [var.account_id]
}

run "refused_region_not_a_region" {
  command = plan

  variables {
    region = "eu west 1"
  }

  expect_failures = [var.region]
}

run "refused_name_not_a_name" {
  command = plan

  variables {
    name = "PackMoat"
  }

  expect_failures = [var.name]
}

run "refused_name_too_long" {
  command = plan

  variables {
    name = "packmoat-production-eu1"
  }

  expect_failures = [var.name]
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

run "refused_hosts_not_host_names" {
  command = plan

  variables {
    hosts = "Packages.Example.Test"
  }

  expect_failures = [var.hosts]
}

run "refused_app_role_name_not_a_role" {
  command = plan

  variables {
    app_role_name = "packmoat-app"
  }

  expect_failures = [var.app_role_name]
}

run "refused_vpc_id_not_an_id" {
  command = plan

  variables {
    vpc_id = "vpc-xyz"
  }

  expect_failures = [var.vpc_id]
}

run "refused_public_subnet_ids_only_one" {
  command = plan

  variables {
    public_subnet_ids   = ["subnet-0aaaaaaaaaaaaaaa1"]
    public_subnet_cidrs = ["10.0.0.0/24"]
  }

  expect_failures = [var.public_subnet_ids]
}

run "refused_public_subnet_ids_the_same_twice" {
  command = plan

  variables {
    public_subnet_ids = ["subnet-0aaaaaaaaaaaaaaa1", "subnet-0aaaaaaaaaaaaaaa1"]
  }

  expect_failures = [var.public_subnet_ids]
}

run "refused_public_subnet_ids_not_ids" {
  command = plan

  variables {
    public_subnet_ids = ["subnet-0aaaaaaaaaaaaaaa1", "sn-2"]
  }

  expect_failures = [var.public_subnet_ids]
}

run "refused_public_subnet_cidrs_not_networks" {
  command = plan

  variables {
    public_subnet_cidrs = ["10.0.0.0/24", "10.0.1.0"]
  }

  expect_failures = [var.public_subnet_cidrs]
}

run "refused_public_subnet_cidrs_host_bits_set" {
  command = plan

  variables {
    public_subnet_cidrs = ["10.0.0.0/24", "10.0.1.5/24"]
  }

  expect_failures = [var.public_subnet_cidrs]
}

run "refused_public_subnet_cidrs_ipv6" {
  command = plan

  variables {
    public_subnet_cidrs = ["10.0.0.0/24", "2001:db8::/64"]
  }

  expect_failures = [var.public_subnet_cidrs]
}

run "refused_public_subnet_cidrs_everything" {
  command = plan

  variables {
    public_subnet_cidrs = ["10.0.0.0/24", "0.0.0.0/0"]
  }

  expect_failures = [var.public_subnet_cidrs]
}

run "refused_database_cidr_everything" {
  command = plan

  variables {
    database_cidr = "0.0.0.0/0"
  }

  expect_failures = [var.database_cidr]
}

run "refused_certificate_arn_not_an_acm_arn" {
  command = plan

  variables {
    certificate_arn = "arn:aws:iam::123456789012:server-certificate/packmoat"
  }

  expect_failures = [var.certificate_arn]
}

run "refused_master_key_secret_by_name" {
  command = plan

  variables {
    master_key_secret_arn = "packmoat/master-key"
  }

  expect_failures = [var.master_key_secret_arn]
}

run "refused_app_database_secret_without_suffix" {
  command = plan

  variables {
    app_database_secret_arn = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-app"
  }

  expect_failures = [var.app_database_secret_arn]
}

run "refused_owner_database_secret_with_a_json_key" {
  command = plan

  variables {
    owner_database_secret_arn = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-owner-CCCCCC:password::"
  }

  expect_failures = [var.owner_database_secret_arn]
}

run "refused_database_ca_secret_an_ssm_parameter" {
  command = plan

  variables {
    database_ca_secret_arn = "arn:aws:ssm:eu-west-1:123456789012:parameter/packmoat/db-ca"
  }

  expect_failures = [var.database_ca_secret_arn]
}

run "refused_app_cert_secret_not_an_arn" {
  command = plan

  variables {
    app_cert_secret_arn = "packmoat/app-cert"
  }

  expect_failures = [var.app_cert_secret_arn]
}

run "refused_secrets_kms_key_an_alias" {
  command = plan

  variables {
    secrets_kms_key_arn = "arn:aws:kms:eu-west-1:123456789012:alias/packmoat"
  }

  expect_failures = [var.secrets_kms_key_arn]
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
    settings = { PORT = "8081" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_trusted_proxies" {
  command = plan

  variables {
    settings = { PACKMOAT_TRUSTED_PROXIES = "0.0.0.0/1" }
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
    settings = { PACKMOAT_BACKGROUND_JOBS = "off" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_hosts" {
  command = plan

  variables {
    settings = { PACKMOAT_HOSTS = "other.example.test" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_app_cert" {
  command = plan

  variables {
    settings = { PACKMOAT_APP_CERT = "/tmp/cert.pem" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_master_key" {
  command = plan

  variables {
    settings = { PACKMOAT_MASTER_KEY = "not-a-key" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_admin_client_secret" {
  command = plan

  variables {
    settings = { PACKMOAT_ADMIN_CLIENT_SECRET = "not-a-secret" }
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

run "refused_settings_single_role" {
  command = plan

  variables {
    settings = { PACKMOAT_DB_SINGLE_ROLE = "on" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_database" {
  command = plan

  variables {
    settings = { PACKMOAT_DB = "Host=db.example.test" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_any_db_setting" {
  command = plan

  variables {
    settings = { PACKMOAT_DB_ANYTHING = "x" }
  }

  expect_failures = [var.settings]
}

run "refused_settings_a_file_setting" {
  command = plan

  variables {
    settings = { PACKMOAT_SUPPORT_EMAIL_FILE = "/etc/passwd" }
  }

  expect_failures = [var.settings]
}

run "refused_file_settings_not_packmoat" {
  command = plan

  variables {
    file_settings = { DOTNET_STARTUP_HOOKS = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/hook-GGGGGG" }
  }

  expect_failures = [var.file_settings]
}

run "refused_file_settings_not_upper_case" {
  command = plan

  variables {
    file_settings = { PACKMOAT_Admin_Client_Secret = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/admin-client-FFFFFF" }
  }

  expect_failures = [var.file_settings]
}

run "refused_file_settings_hosts" {
  command = plan

  variables {
    file_settings = { PACKMOAT_HOSTS = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/hosts-GGGGGG" }
  }

  expect_failures = [var.file_settings]
}

run "refused_file_settings_master_key" {
  command = plan

  variables {
    file_settings = { PACKMOAT_MASTER_KEY = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/master-key-AAAAAA" }
  }

  expect_failures = [var.file_settings]
}

run "refused_file_settings_database" {
  command = plan

  variables {
    file_settings = { PACKMOAT_DB = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-other-GGGGGG" }
  }

  expect_failures = [var.file_settings]
}

run "refused_file_settings_single_role" {
  command = plan

  variables {
    file_settings = { PACKMOAT_DB_SINGLE_ROLE = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/on-GGGGGG" }
  }

  expect_failures = [var.file_settings]
}

run "refused_file_settings_a_file_setting" {
  command = plan

  variables {
    file_settings = { PACKMOAT_ADMIN_CLIENT_SECRET_FILE = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/admin-client-FFFFFF" }
  }

  expect_failures = [var.file_settings]
}

run "refused_file_settings_value_not_an_arn" {
  command = plan

  variables {
    file_settings = { PACKMOAT_ADMIN_CLIENT_SECRET = "packmoat/admin-client" }
  }

  expect_failures = [var.file_settings]
}

run "refused_desired_count_zero" {
  command = plan

  variables {
    desired_count = 0
  }

  expect_failures = [var.desired_count]
}

run "refused_desired_count_over_10" {
  command = plan

  variables {
    desired_count = 11
  }

  expect_failures = [var.desired_count]
}

run "refused_cpu_not_a_fargate_size" {
  command = plan

  variables {
    cpu = 1000
  }

  expect_failures = [var.cpu]
}

run "refused_memory_too_small" {
  command = plan

  variables {
    memory = 256
  }

  expect_failures = [var.memory]
}

run "refused_log_retention_never_expiring" {
  command = plan

  variables {
    log_retention_days = 0
  }

  expect_failures = [var.log_retention_days]
}

run "refused_private_subnet_cidrs_everything" {
  command = plan

  variables {
    enable_nat_gateway   = true
    private_subnet_cidrs = ["10.0.10.0/24", "0.0.0.0/0"]
    private_subnet_azs   = ["eu-west-1a", "eu-west-1b"]
  }

  expect_failures = [var.private_subnet_cidrs]
}

run "refused_private_subnet_azs_not_zones" {
  command = plan

  variables {
    enable_nat_gateway   = true
    private_subnet_cidrs = ["10.0.10.0/24", "10.0.11.0/24"]
    private_subnet_azs   = ["eu-west-1a", "eu-west-1"]
  }

  expect_failures = [var.private_subnet_azs]
}

run "refused_owner_secret_is_the_master_keys" {
  command = plan

  variables {
    owner_database_secret_arn = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/master-key-AAAAAA"
  }

  expect_failures = [terraform_data.checks]
}

run "refused_owner_secret_is_the_app_roles" {
  command = plan

  variables {
    owner_database_secret_arn = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-app-BBBBBB"
  }

  expect_failures = [terraform_data.checks]
}

run "refused_owner_secret_is_the_cas" {
  command = plan

  variables {
    owner_database_secret_arn = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-ca-DDDDDD"
  }

  expect_failures = [terraform_data.checks]
}

run "refused_owner_secret_is_the_site_certificates" {
  command = plan

  variables {
    app_cert_secret_arn = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-owner-CCCCCC"
  }

  expect_failures = [terraform_data.checks]
}

run "refused_owner_secret_is_a_file_settings" {
  command = plan

  variables {
    file_settings = { PACKMOAT_ADMIN_CLIENT_SECRET = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-owner-CCCCCC" }
  }

  expect_failures = [terraform_data.checks]
}

run "refused_setting_both_a_value_and_a_file" {
  command = plan

  variables {
    settings      = { PACKMOAT_ADMIN_CLIENT_ID = "client" }
    file_settings = { PACKMOAT_ADMIN_CLIENT_ID = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/admin-client-id-GGGGGG" }
  }

  expect_failures = [terraform_data.checks]
}

run "refused_public_subnet_cidrs_fewer_than_subnets" {
  command = plan

  variables {
    public_subnet_cidrs = ["10.0.0.0/24"]
  }

  expect_failures = [terraform_data.checks]
}

run "refused_nat_without_private_subnets" {
  command = plan

  variables {
    enable_nat_gateway = true
  }

  expect_failures = [terraform_data.checks]
}

run "refused_nat_with_one_private_subnet" {
  command = plan

  variables {
    enable_nat_gateway   = true
    private_subnet_cidrs = ["10.0.10.0/24"]
    private_subnet_azs   = ["eu-west-1a"]
  }

  expect_failures = [terraform_data.checks]
}

run "refused_nat_with_a_zone_missing" {
  command = plan

  variables {
    enable_nat_gateway   = true
    private_subnet_cidrs = ["10.0.10.0/24", "10.0.11.0/24"]
    private_subnet_azs   = ["eu-west-1a"]
  }

  expect_failures = [terraform_data.checks]
}

run "refused_private_subnets_without_nat" {
  command = plan

  variables {
    private_subnet_cidrs = ["10.0.10.0/24", "10.0.11.0/24"]
    private_subnet_azs   = ["eu-west-1a", "eu-west-1b"]
  }

  expect_failures = [terraform_data.checks]
}

run "refused_secret_in_another_region" {
  command = plan

  variables {
    master_key_secret_arn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:packmoat/master-key-AAAAAA"
  }

  expect_failures = [terraform_data.checks]
}

run "refused_secret_in_another_account" {
  command = plan

  variables {
    database_ca_secret_arn = "arn:aws:secretsmanager:eu-west-1:210987654321:secret:packmoat/db-ca-DDDDDD"
  }

  expect_failures = [terraform_data.checks]
}

run "refused_owner_secret_in_another_account" {
  command = plan

  variables {
    owner_database_secret_arn = "arn:aws:secretsmanager:eu-west-1:210987654321:secret:packmoat/db-owner-CCCCCC"
  }

  expect_failures = [terraform_data.checks]
}

run "refused_file_setting_in_another_region" {
  command = plan

  variables {
    file_settings = { PACKMOAT_ADMIN_CLIENT_SECRET = "arn:aws:secretsmanager:us-east-1:123456789012:secret:packmoat/admin-client-FFFFFF" }
  }

  expect_failures = [terraform_data.checks]
}

run "refused_certificate_in_another_region" {
  command = plan

  variables {
    certificate_arn = "arn:aws:acm:us-east-1:123456789012:certificate/11111111-2222-3333-4444-555555555555"
  }

  expect_failures = [terraform_data.checks]
}

run "refused_kms_key_in_another_account" {
  command = plan

  variables {
    secrets_kms_key_arn = "arn:aws:kms:eu-west-1:210987654321:key/66666666-7777-8888-9999-000000000000"
  }

  expect_failures = [terraform_data.checks]
}
