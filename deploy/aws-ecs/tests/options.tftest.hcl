# What the template plans with every option set, the NAT gateway among them. Each run is one check; deploy/aws-ecs/test-template.sh
# reports each by its name and has a mutation for each. Planned against a mock of the aws provider: no AWS account, nothing made.

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

variables {
  account_id                = "123456789012"
  region                    = "eu-west-1"
  service_image             = "ghcr.io/packmoat/packmoat-core@sha256:1111111111111111111111111111111111111111111111111111111111111111"
  job_image                 = "ghcr.io/packmoat/packmoat-core@sha256:2222222222222222222222222222222222222222222222222222222222222222"
  hosts                     = "packages.example.test"
  vpc_id                    = "vpc-0123456789abcdef0"
  public_subnet_ids         = ["subnet-0aaaaaaaaaaaaaaa1", "subnet-0aaaaaaaaaaaaaaa2", "subnet-0aaaaaaaaaaaaaaa3"]
  public_subnet_cidrs       = ["10.0.0.0/24", "10.0.1.0/24", "10.0.2.0/24"]
  database_cidr             = "10.0.20.5/32"
  certificate_arn           = "arn:aws:acm:eu-west-1:123456789012:certificate/11111111-2222-3333-4444-555555555555"
  master_key_secret_arn     = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/master-key-AAAAAA"
  app_database_secret_arn   = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-app-BBBBBB"
  owner_database_secret_arn = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-owner-CCCCCC"
  database_ca_secret_arn    = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-ca-DDDDDD"
  app_cert_secret_arn       = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/app-cert-EEEEEE"
  secrets_kms_key_arn       = "arn:aws:kms:eu-west-1:123456789012:key/66666666-7777-8888-9999-000000000000"
  file_settings             = { PACKMOAT_ADMIN_CLIENT_SECRET = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/admin-client-FFFFFF" }
  settings                  = { PACKMOAT_ADMIN_HOST = "admin.example.test", PACKMOAT_TOKEN_RPM = "600" }
  app_role_name             = "packmoat_web"
  name                      = "pm-prod"
  desired_count             = 3
  cpu                       = 1024
  memory                    = 2048
  log_retention_days        = 90
  enable_nat_gateway        = true
  private_subnet_cidrs      = ["10.0.10.0/24", "10.0.11.0/24"]
  private_subnet_azs        = ["eu-west-1a", "eu-west-1b"]
}

run "with_every_option_secrets_init_writes_each_secret" {
  command = plan

  assert {
    condition = { for s in jsondecode(aws_ecs_task_definition.server.container_definitions)[0].secrets : s.name => s.valueFrom } == {
      SECRET_master_key                   = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/master-key-AAAAAA"
      SECRET_db                           = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-app-BBBBBB"
      SECRET_db_ca                        = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-ca-DDDDDD"
      SECRET_app_cert                     = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/app-cert-EEEEEE"
      SECRET_packmoat_admin_client_secret = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/admin-client-FFFFFF"
    }
    error_message = "With every option the server task's secrets-init writes the master key, the app role, the CA, the site certificate and each file setting."
  }
}

run "with_every_option_the_settings_name_the_files" {
  command = plan

  assert {
    condition = (
      { for e in jsondecode(aws_ecs_task_definition.server.container_definitions)[1].environment : e.name => e.value if e.name == "PACKMOAT_APP_CERT" || e.name == "PACKMOAT_ADMIN_CLIENT_SECRET_FILE" } == {
        PACKMOAT_APP_CERT                 = "/run/secrets/packmoat/app_cert"
        PACKMOAT_ADMIN_CLIENT_SECRET_FILE = "/run/secrets/packmoat/packmoat_admin_client_secret"
      }
    )
    error_message = "PACKMOAT_APP_CERT and each file setting's _FILE must name its written file."
  }
}

run "with_every_option_plain_settings_are_passed_on" {
  command = plan

  assert {
    condition = (
      { for e in jsondecode(aws_ecs_task_definition.server.container_definitions)[1].environment : e.name => e.value if contains(["PACKMOAT_ADMIN_HOST", "PACKMOAT_TOKEN_RPM"], e.name) } == {
        PACKMOAT_ADMIN_HOST = "admin.example.test"
        PACKMOAT_TOKEN_RPM  = "600"
      }
      && { for e in jsondecode(aws_ecs_task_definition.server.container_definitions)[1].environment : e.name => e.value }["PACKMOAT_TRUSTED_PROXIES"] == "10.0.0.0/24 10.0.1.0/24 10.0.2.0/24"
    )
    error_message = "settings are passed on as given, and the trusted proxies are every public subnet."
  }
}

run "with_every_option_the_server_role_reads_exactly_its_secrets_and_the_key" {
  command = plan

  assert {
    condition = (
      jsondecode(aws_iam_role_policy.server_execution.policy).Statement[0].Resource == [
        "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/admin-client-FFFFFF",
        "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/app-cert-EEEEEE",
        "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-app-BBBBBB",
        "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-ca-DDDDDD",
        "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/master-key-AAAAAA",
      ]
      && try(jsondecode(aws_iam_role_policy.server_execution.policy).Statement[2], {}) == {
        Sid       = "SecretsKey"
        Effect    = "Allow"
        Action    = ["kms:Decrypt"]
        Resource  = ["arn:aws:kms:eu-west-1:123456789012:key/66666666-7777-8888-9999-000000000000"]
        Condition = { StringEquals = { "kms:ViaService" = "secretsmanager.eu-west-1.amazonaws.com" } }
      }
      && length(jsondecode(aws_iam_role_policy.server_execution.policy).Statement) == 3
    )
    error_message = "With a key, the server's execution role may decrypt with it only through Secrets Manager, and still reads exactly its secrets."
  }
}

run "with_every_option_the_migrate_role_still_reads_two_secrets" {
  command = plan

  assert {
    condition = (
      jsondecode(aws_iam_role_policy.migrate_execution.policy).Statement[0].Resource == [
        "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-ca-DDDDDD",
        "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-owner-CCCCCC",
      ]
      && length(jsondecode(aws_iam_role_policy.migrate_execution.policy).Statement) == 3
      && try(jsondecode(aws_iam_role_policy.migrate_execution.policy).Statement[2], {}) == {
        Sid       = "SecretsKey"
        Effect    = "Allow"
        Action    = ["kms:Decrypt"]
        Resource  = ["arn:aws:kms:eu-west-1:123456789012:key/66666666-7777-8888-9999-000000000000"]
        Condition = { StringEquals = { "kms:ViaService" = "secretsmanager.eu-west-1.amazonaws.com" } }
      }
      && length(jsondecode(aws_ecs_task_definition.migrate.container_definitions)[0].secrets) == 2
      && jsondecode(aws_ecs_task_definition.migrate.container_definitions)[1].command == ["db", "migrate", "--grant-to", "packmoat_web"]
    )
    error_message = "With every option the migrate task still reads the owner's connection string and the CA only."
  }
}

run "with_every_option_the_sizes_and_names" {
  command = plan

  assert {
    condition = (
      aws_ecs_service.server.desired_count == 3
      && aws_ecs_task_definition.server.cpu == "1024" && aws_ecs_task_definition.server.memory == "2048"
      && aws_cloudwatch_log_group.server.retention_in_days == 90 && aws_cloudwatch_log_group.server.name == "/ecs/pm-prod"
      && aws_ecs_cluster.main.name == "pm-prod" && aws_lb.main.name == "pm-prod" && aws_lb_target_group.server.name == "pm-prod-server"
      && jsondecode(aws_iam_role_policy.server_execution.policy).Statement[1].Resource == ["arn:aws:logs:eu-west-1:123456789012:log-group:/ecs/pm-prod:log-stream:*"]
    )
    error_message = "desired_count, cpu, memory, log_retention_days and name are used as given."
  }
}

run "with_nat_the_tasks_run_in_private_subnets_without_a_public_address" {
  command = plan

  assert {
    condition = (
      [for s in aws_subnet.private : "${s.cidr_block} ${s.availability_zone} ${s.vpc_id} ${s.map_public_ip_on_launch}"] == [
        "10.0.10.0/24 eu-west-1a vpc-0123456789abcdef0 false", "10.0.11.0/24 eu-west-1b vpc-0123456789abcdef0 false"
      ]
      && one(aws_ecs_service.server.network_configuration).subnets == toset(["subnet-0ccccccccccccccc3"])
      && one(aws_ecs_service.server.network_configuration).assign_public_ip == false
    )
    error_message = "With the NAT gateway the tasks run in the template's private subnets, with no public address."
  }
}

run "with_nat_everything_leaves_through_one_elastic_ip" {
  command = plan

  assert {
    condition = (
      length(aws_eip.nat) == 1 && aws_eip.nat[0].domain == "vpc"
      && aws_nat_gateway.nat[0].allocation_id == "eipalloc-0123456789abcdef0" && aws_nat_gateway.nat[0].subnet_id == "subnet-0aaaaaaaaaaaaaaa1"
      && aws_route.private_nat[0].destination_cidr_block == "0.0.0.0/0" && aws_route.private_nat[0].nat_gateway_id == "nat-0123456789abcdef0"
      && aws_route.private_nat[0].route_table_id == "rtb-0123456789abcdef0"
      && [for a in aws_route_table_association.private : "${a.subnet_id} ${a.route_table_id}"] == [
        "subnet-0ccccccccccccccc3 rtb-0123456789abcdef0", "subnet-0ccccccccccccccc3 rtb-0123456789abcdef0"
      ]
      && output.outbound_ip_address == "203.0.113.10"
    )
    error_message = "With the NAT gateway, each private subnet's one route out is through the NAT gateway and its Elastic IP."
  }
}

run "with_nat_the_migrate_command_has_no_public_address" {
  command = plan

  assert {
    condition     = endswith(output.migrate_run_task_command, "'awsvpcConfiguration={subnets=[subnet-0ccccccccccccccc3,subnet-0ccccccccccccccc3],securityGroups=[sg-0bbbbbbbbbbbbbbb2],assignPublicIp=DISABLED}'")
    error_message = "With the NAT gateway the migrate task runs in the private subnets with no public address."
  }
}
