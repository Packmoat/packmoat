# What the template plans with the required inputs only. Each run is one check; deploy/aws-ecs/test-template.sh reports each by its name
# and has a mutation for each. Planned against a mock of the aws provider: no AWS account, nothing made.

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

# The required inputs; the two images differ, so that each is seen where it belongs.
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

run "secrets_init_runs_first_and_the_server_after_it_succeeds" {
  command = plan

  assert {
    condition = alltrue([
      for d in [jsondecode(aws_ecs_task_definition.server.container_definitions), jsondecode(aws_ecs_task_definition.migrate.container_definitions)] : (
        length(d) == 2 && d[0].name == "secrets-init" && d[0].essential == false && d[1].essential == true
        && d[1].dependsOn == [{ containerName = "secrets-init", condition = "SUCCESS" }]
        && !can(d[0].dependsOn)
      )
    ]) && jsondecode(aws_ecs_task_definition.server.container_definitions)[1].name == "server"
    error_message = "Each task runs secrets-init first (not essential), and its other container starts only once secrets-init has succeeded."
  }
}

run "secrets_init_runs_the_fixed_script" {
  command = plan

  assert {
    condition = alltrue([
      for d in [jsondecode(aws_ecs_task_definition.server.container_definitions), jsondecode(aws_ecs_task_definition.migrate.container_definitions)] : (
        d[0].entryPoint == ["/bin/bash", "-c"]
        && d[0].command == [join("\n", [
          "set -euo pipefail",
          "umask 077",
          "d=/run/secrets/packmoat",
          "for v in $(compgen -v SECRET_); do",
          "  f=\"$d/$${v#SECRET_}\"",
          "  printf '%s' \"$${!v}\" > \"$f\"",
          "  chmod 0400 \"$f\"",
          "  chown 10001:10001 \"$f\"",
          "done",
          "chmod 0500 \"$d\"",
          "chown 10001:10001 \"$d\"",
          "",
        ])]
        && d[0].user == "0"
        && d[0].linuxParameters == { capabilities = { drop = ["AUDIT_WRITE", "DAC_OVERRIDE", "FOWNER", "FSETID", "KILL", "MKNOD", "NET_BIND_SERVICE", "NET_RAW", "SETFCAP", "SETGID", "SETPCAP", "SETUID", "SYS_CHROOT"] } }
        && !can(d[0].environment) && !can(d[0].portMappings)
      )
    ])
    error_message = "secrets-init must run exactly the template's script, as root with every default capability dropped but CHOWN, with no settings and no port."
  }
}

run "only_secrets_init_has_secrets" {
  command = plan

  assert {
    condition = (
      { for s in jsondecode(aws_ecs_task_definition.server.container_definitions)[0].secrets : s.name => s.valueFrom } == {
        SECRET_master_key = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/master-key-AAAAAA"
        SECRET_db         = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-app-BBBBBB"
        SECRET_db_ca      = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-ca-DDDDDD"
      }
      && !can(jsondecode(aws_ecs_task_definition.server.container_definitions)[1].secrets)
      && !can(jsondecode(aws_ecs_task_definition.migrate.container_definitions)[1].secrets)
    )
    error_message = "The server task's secrets-init gets the master key, the app role and the CA, and no other container gets any secret."
  }
}

run "the_server_reads_the_files_read_only" {
  command = plan

  assert {
    condition = alltrue([
      for t in [aws_ecs_task_definition.server, aws_ecs_task_definition.migrate] : (
        jsondecode(t.container_definitions)[0].mountPoints == [{ sourceVolume = "secrets", containerPath = "/run/secrets/packmoat", readOnly = false }]
        && jsondecode(t.container_definitions)[1].mountPoints == [{ sourceVolume = "secrets", containerPath = "/run/secrets/packmoat", readOnly = true }]
        && length(t.volume) == 1 && one(t.volume).name == "secrets" && one(t.volume).host_path == null
        && length(one(t.volume).efs_volume_configuration) == 0 && length(one(t.volume).docker_volume_configuration) == 0
      )
    ])
    error_message = "Each task's one volume is its own storage: secrets-init writes it, the other container mounts it read-only."
  }
}

run "the_server_runs_as_10001_with_every_capability_dropped" {
  command = plan

  assert {
    condition = alltrue([
      for d in [jsondecode(aws_ecs_task_definition.server.container_definitions), jsondecode(aws_ecs_task_definition.migrate.container_definitions)] : (
        d[1].user == "10001:10001" && d[1].linuxParameters == { capabilities = { drop = ["ALL"] } }
      )
    ])
    error_message = "The server and the migrate containers run as user 10001 with every capability dropped."
  }
}

run "server_settings_postgres_database_keys_background_jobs_on" {
  command = plan

  assert {
    condition = { for e in jsondecode(aws_ecs_task_definition.server.container_definitions)[1].environment : e.name => e.value } == {
      PORT                     = "8080"
      PACKMOAT_DB_ENGINE       = "postgres"
      PACKMOAT_COOKIE_KEYS     = "database"
      PACKMOAT_BACKGROUND_JOBS = "on"
      PACKMOAT_HOSTS           = "packages.example.test"
      PACKMOAT_TRUSTED_PROXIES = "10.0.0.0/24 10.0.1.0/24"
      PACKMOAT_MASTER_KEY_FILE = "/run/secrets/packmoat/master_key"
      PACKMOAT_DB_FILE         = "/run/secrets/packmoat/db"
    }
    error_message = "The server's settings are not exactly the template's."
  }
}

run "the_server_listens_on_8080_without_the_images_database_setting" {
  command = plan

  assert {
    condition = (
      jsondecode(aws_ecs_task_definition.server.container_definitions)[1].entryPoint == ["/usr/bin/env", "-u", "PACKMOAT_DB", "dotnet", "/app/host/Packmoat.Server.dll"]
      && jsondecode(aws_ecs_task_definition.server.container_definitions)[1].portMappings == [{ containerPort = 8080, protocol = "tcp" }]
      && !can(jsondecode(aws_ecs_task_definition.server.container_definitions)[1].command)
    )
    error_message = "The server starts with the image's PACKMOAT_DB unset, and listens on 8080."
  }
}

run "each_file_setting_names_a_written_file" {
  command = plan

  assert {
    condition = alltrue(flatten([
      for d in [jsondecode(aws_ecs_task_definition.server.container_definitions), jsondecode(aws_ecs_task_definition.migrate.container_definitions)] : [
        for e in d[1].environment : contains([for s in d[0].secrets : "/run/secrets/packmoat/${trimprefix(s.name, "SECRET_")}"], e.value)
        if endswith(e.name, "_FILE") || e.name == "PACKMOAT_APP_CERT"
      ]
    ]))
    error_message = "Every _FILE setting (and PACKMOAT_APP_CERT) must name a file its own task's secrets-init writes."
  }
}

run "no_secret_is_an_environment_value" {
  command = plan

  assert {
    condition = alltrue(flatten([
      for d in [jsondecode(aws_ecs_task_definition.server.container_definitions), jsondecode(aws_ecs_task_definition.migrate.container_definitions)] : [
        for e in d[1].environment : (
          !contains(["PACKMOAT_MASTER_KEY", "PACKMOAT_DB", "PACKMOAT_ADMIN_CLIENT_SECRET"], e.name) && !startswith(e.value, "arn:")
        )
      ]
    ]))
    error_message = "No secret setting may be set by value, and no setting may hold an ARN."
  }
}

run "no_single_role" {
  command = plan

  assert {
    condition = (
      !contains([for e in jsondecode(aws_ecs_task_definition.server.container_definitions)[1].environment : e.name], "PACKMOAT_DB_SINGLE_ROLE")
      && !contains(jsondecode(aws_ecs_task_definition.migrate.container_definitions)[1].command, "--single-role")
    )
    error_message = "No PACKMOAT_DB_SINGLE_ROLE for the server, no --single-role in the migrate task."
  }
}

run "migrate_task_runs_db_migrate_grant_to" {
  command = plan

  assert {
    condition = (
      jsondecode(aws_ecs_task_definition.migrate.container_definitions)[1].name == "migrate"
      && jsondecode(aws_ecs_task_definition.migrate.container_definitions)[1].entryPoint == ["/usr/bin/env", "-u", "PACKMOAT_DB", "packmoat-admin"]
      && jsondecode(aws_ecs_task_definition.migrate.container_definitions)[1].command == ["db", "migrate", "--grant-to", "packmoat_app"]
      && !can(jsondecode(aws_ecs_task_definition.migrate.container_definitions)[1].portMappings)
      && aws_ecs_task_definition.migrate.family == "packmoat-migrate"
    )
    error_message = "The migrate task runs packmoat-admin db migrate --grant-to <app role>, with the image's PACKMOAT_DB unset."
  }
}

run "migrate_task_signs_in_as_the_owner_only" {
  command = plan

  assert {
    condition = (
      { for e in jsondecode(aws_ecs_task_definition.migrate.container_definitions)[1].environment : e.name => e.value } == {
        PACKMOAT_DB_ENGINE = "postgres"
        PACKMOAT_DB_FILE   = "/run/secrets/packmoat/db_owner"
      }
      && { for s in jsondecode(aws_ecs_task_definition.migrate.container_definitions)[0].secrets : s.name => s.valueFrom } == {
        SECRET_db_owner = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-owner-CCCCCC"
        SECRET_db_ca    = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-ca-DDDDDD"
      }
    )
    error_message = "The migrate task gets the owner's connection string and the CA, and nothing else."
  }
}

run "the_owner_secret_is_the_migrate_tasks_alone" {
  command = plan

  assert {
    condition = (
      !contains([for s in jsondecode(aws_ecs_task_definition.server.container_definitions)[0].secrets : s.valueFrom], var.owner_database_secret_arn)
      && !contains(flatten([for s in jsondecode(aws_iam_role_policy.server_execution.policy).Statement : s.Resource]), var.owner_database_secret_arn)
    )
    error_message = "Neither the server task nor its execution role may get the owner's connection string."
  }
}

run "each_runs_its_own_image" {
  command = plan

  assert {
    condition = (
      [for c in jsondecode(aws_ecs_task_definition.server.container_definitions) : c.image] == [var.service_image, var.service_image]
      && [for c in jsondecode(aws_ecs_task_definition.migrate.container_definitions) : c.image] == [var.job_image, var.job_image]
    )
    error_message = "The server task runs service_image; the migrate task runs job_image."
  }
}

run "server_execution_role_reads_exactly_its_secrets" {
  command = plan

  assert {
    condition = (
      jsondecode(aws_iam_role_policy.server_execution.policy) == {
        Version = "2012-10-17"
        Statement = [
          {
            Sid    = "Secrets"
            Effect = "Allow"
            Action = ["secretsmanager:GetSecretValue"]
            Resource = [
              "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-app-BBBBBB",
              "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-ca-DDDDDD",
              "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/master-key-AAAAAA",
            ]
          },
          {
            Sid      = "Logs"
            Effect   = "Allow"
            Action   = ["logs:CreateLogStream", "logs:PutLogEvents"]
            Resource = ["arn:aws:logs:eu-west-1:123456789012:log-group:/ecs/packmoat:log-stream:*"]
          },
        ]
      }
      && aws_iam_role_policy.server_execution.role == "packmoat-server-execution"
      && aws_ecs_task_definition.server.execution_role_arn == "arn:aws:iam::123456789012:role/packmoat-server-execution"
    )
    error_message = "The server task's execution role may read exactly its secrets and write its logs, and nothing else."
  }
}

run "migrate_execution_role_reads_exactly_its_secrets" {
  command = plan

  assert {
    condition = (
      jsondecode(aws_iam_role_policy.migrate_execution.policy) == {
        Version = "2012-10-17"
        Statement = [
          {
            Sid    = "Secrets"
            Effect = "Allow"
            Action = ["secretsmanager:GetSecretValue"]
            Resource = [
              "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-ca-DDDDDD",
              "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-owner-CCCCCC",
            ]
          },
          {
            Sid      = "Logs"
            Effect   = "Allow"
            Action   = ["logs:CreateLogStream", "logs:PutLogEvents"]
            Resource = ["arn:aws:logs:eu-west-1:123456789012:log-group:/ecs/packmoat:log-stream:*"]
          },
        ]
      }
      && aws_iam_role_policy.migrate_execution.role == "packmoat-migrate-execution"
      && aws_ecs_task_definition.migrate.execution_role_arn == "arn:aws:iam::123456789012:role/packmoat-migrate-execution"
    )
    error_message = "The migrate task's execution role may read exactly the owner's connection string and the CA, and write its logs."
  }
}

run "execution_roles_trust_ecs_tasks_in_this_account_only" {
  command = plan

  assert {
    condition = alltrue([
      for r in [aws_iam_role.server_execution, aws_iam_role.migrate_execution] : jsondecode(r.assume_role_policy) == {
        Version = "2012-10-17"
        Statement = [{
          Effect    = "Allow"
          Principal = { Service = "ecs-tasks.amazonaws.com" }
          Action    = "sts:AssumeRole"
          Condition = { StringEquals = { "aws:SourceAccount" = "123456789012" } }
        }]
      }
    ])
    error_message = "Each execution role may be used by ECS tasks in account_id only."
  }
}

run "no_task_role_and_no_shell" {
  command = plan

  assert {
    condition = (
      aws_ecs_task_definition.server.task_role_arn == null && aws_ecs_task_definition.migrate.task_role_arn == null
      && aws_ecs_service.server.enable_execute_command == false
    )
    error_message = "No task role (the containers get no AWS credentials) and no ECS Exec."
  }
}

run "tasks_log_to_their_log_group_kept_30_days" {
  command = plan

  assert {
    condition = (
      aws_cloudwatch_log_group.server.name == "/ecs/packmoat" && aws_cloudwatch_log_group.server.retention_in_days == 30
      && alltrue(flatten([
        for t in [aws_ecs_task_definition.server, aws_ecs_task_definition.migrate] : [
          for c in jsondecode(t.container_definitions) : (
            c.logConfiguration.logDriver == "awslogs"
            && c.logConfiguration.options["awslogs-group"] == "/ecs/packmoat" && c.logConfiguration.options["awslogs-region"] == "eu-west-1"
          )
        ]
      ]))
    )
    error_message = "Every container logs to the template's log group, which keeps logs 30 days by default."
  }
}

run "cluster_without_container_insights" {
  command = plan

  assert {
    condition = (
      aws_ecs_cluster.main.name == "packmoat"
      && [for s in aws_ecs_cluster.main.setting : "${s.name}=${s.value}"] == ["containerInsights=disabled"]
      && aws_ecs_service.server.cluster == "arn:aws:ecs:eu-west-1:123456789012:cluster/packmoat"
    )
    error_message = "The cluster has Container Insights off."
  }
}

run "server_task_definition_is_fargate_with_its_sizes" {
  command = plan

  assert {
    condition = (
      alltrue([
        for t in [aws_ecs_task_definition.server, aws_ecs_task_definition.migrate] : (
          t.requires_compatibilities == toset(["FARGATE"]) && t.network_mode == "awsvpc"
          && one(t.runtime_platform).operating_system_family == "LINUX" && one(t.runtime_platform).cpu_architecture == "X86_64"
        )
      ])
      && aws_ecs_task_definition.server.cpu == "512" && aws_ecs_task_definition.server.memory == "1024"
      && aws_ecs_task_definition.server.family == "packmoat-server"
    )
    error_message = "Both tasks are Fargate, awsvpc, Linux x86-64; the server's is 512 CPU units and 1024 MiB by default."
  }
}

run "one_task_behind_the_load_balancer_rolled_back_on_failure" {
  command = plan

  assert {
    condition = (
      aws_ecs_service.server.desired_count == 1 && aws_ecs_service.server.launch_type == "FARGATE"
      && aws_ecs_service.server.task_definition == "arn:aws:ecs:eu-west-1:123456789012:task-definition/packmoat-server:1"
      && aws_ecs_service.server.deployment_minimum_healthy_percent == 100 && aws_ecs_service.server.deployment_maximum_percent == 200
      && one(aws_ecs_service.server.deployment_circuit_breaker).enable == true && one(aws_ecs_service.server.deployment_circuit_breaker).rollback == true
      && aws_ecs_service.server.health_check_grace_period_seconds == 60
      && [for l in aws_ecs_service.server.load_balancer : "${l.target_group_arn} ${l.container_name}:${l.container_port}"] == [
        "arn:aws:elasticloadbalancing:eu-west-1:123456789012:targetgroup/packmoat-server/0123456789abcdef server:8080"
      ]
    )
    error_message = "One task, behind the target group on 8080; a new deployment keeps the old task until the new one is healthy, and is rolled back if it never is."
  }
}

run "tasks_in_the_public_subnets_with_a_public_address" {
  command = plan

  assert {
    condition = (
      one(aws_ecs_service.server.network_configuration).subnets == toset(["subnet-0aaaaaaaaaaaaaaa1", "subnet-0aaaaaaaaaaaaaaa2"])
      && one(aws_ecs_service.server.network_configuration).assign_public_ip == true
      && one(aws_ecs_service.server.network_configuration).security_groups == toset(["sg-0bbbbbbbbbbbbbbb2"])
    )
    error_message = "With no NAT gateway the tasks run in the public subnets with a public address, in the tasks' security group only."
  }
}

run "no_nat_gateway_by_default" {
  command = plan

  assert {
    condition = (
      length(aws_eip.nat) + length(aws_nat_gateway.nat) + length(aws_subnet.private) + length(aws_route_table.private)
      + length(aws_route.private_nat) + length(aws_route_table_association.private) == 0
      && output.outbound_ip_address == ""
    )
    error_message = "By default: no Elastic IP, no NAT gateway, no private subnet or route."
  }
}

run "load_balancer_public_and_strict" {
  command = plan

  assert {
    condition = (
      aws_lb.main.load_balancer_type == "application" && aws_lb.main.internal == false
      && aws_lb.main.drop_invalid_header_fields == true && aws_lb.main.desync_mitigation_mode == "strictest"
      && aws_lb.main.xff_header_processing_mode == "append" && aws_lb.main.enable_deletion_protection == true
      && aws_lb.main.subnets == toset(["subnet-0aaaaaaaaaaaaaaa1", "subnet-0aaaaaaaaaaaaaaa2"])
      && aws_lb.main.security_groups == toset(["sg-0aaaaaaaaaaaaaaa1"])
    )
    error_message = "The load balancer is public, in the public subnets, drops invalid headers, uses the strictest desync mode, appends X-Forwarded-For and is protected from deletion."
  }
}

run "https_listener_tls13_with_the_certificate" {
  command = plan

  assert {
    condition = (
      aws_lb_listener.https.port == 443 && aws_lb_listener.https.protocol == "HTTPS"
      && aws_lb_listener.https.ssl_policy == "ELBSecurityPolicy-TLS13-1-2-2021-06"
      && aws_lb_listener.https.certificate_arn == var.certificate_arn
      && [for a in aws_lb_listener.https.default_action : "${a.type} ${a.target_group_arn}"] == [
        "forward arn:aws:elasticloadbalancing:eu-west-1:123456789012:targetgroup/packmoat-server/0123456789abcdef"
      ]
    )
    error_message = "HTTPS on 443 with the certificate and a TLS 1.3 policy, forwarding to the target group."
  }
}

run "http_listener_only_redirects_to_https" {
  command = plan

  assert {
    condition = (
      aws_lb_listener.http.port == 80 && aws_lb_listener.http.protocol == "HTTP"
      && length(aws_lb_listener.http.default_action) == 1 && aws_lb_listener.http.default_action[0].type == "redirect"
      && aws_lb_listener.http.default_action[0].target_group_arn == null
      && [for r in aws_lb_listener.http.default_action[0].redirect : "${r.protocol}:${r.port} ${r.status_code}"] == ["HTTPS:443 HTTP_301"]
    )
    error_message = "HTTP on 80 only answers with a permanent redirect to HTTPS."
  }
}

run "target_group_checks_readyz_on_the_tasks_8080" {
  command = plan

  assert {
    condition = (
      aws_lb_target_group.server.target_type == "ip" && aws_lb_target_group.server.port == 8080 && aws_lb_target_group.server.protocol == "HTTP"
      && aws_lb_target_group.server.vpc_id == "vpc-0123456789abcdef0"
      && one(aws_lb_target_group.server.health_check).path == "/readyz" && one(aws_lb_target_group.server.health_check).matcher == "200"
      && one(aws_lb_target_group.server.health_check).port == "traffic-port" && one(aws_lb_target_group.server.health_check).protocol == "HTTP"
    )
    error_message = "The target group sends to each task's address on 8080 and checks /readyz for 200."
  }
}

run "tasks_take_8080_from_the_load_balancer_only" {
  command = plan

  assert {
    condition = (
      aws_vpc_security_group_ingress_rule.task_from_alb.security_group_id == "sg-0bbbbbbbbbbbbbbb2"
      && aws_vpc_security_group_ingress_rule.task_from_alb.referenced_security_group_id == "sg-0aaaaaaaaaaaaaaa1"
      && aws_vpc_security_group_ingress_rule.task_from_alb.ip_protocol == "tcp"
      && aws_vpc_security_group_ingress_rule.task_from_alb.from_port == 8080 && aws_vpc_security_group_ingress_rule.task_from_alb.to_port == 8080
      && aws_vpc_security_group_ingress_rule.task_from_alb.cidr_ipv4 == null && aws_vpc_security_group_ingress_rule.task_from_alb.cidr_ipv6 == null
    )
    error_message = "The tasks' security group admits 8080 from the load balancer's security group, and nothing else."
  }
}

run "load_balancer_takes_80_and_443_and_reaches_only_the_tasks" {
  command = plan

  assert {
    condition = (
      [for r in [aws_vpc_security_group_ingress_rule.alb_https, aws_vpc_security_group_ingress_rule.alb_http] : "${r.security_group_id} ${r.ip_protocol} ${r.from_port}-${r.to_port} ${r.cidr_ipv4}"] == [
        "sg-0aaaaaaaaaaaaaaa1 tcp 443-443 0.0.0.0/0", "sg-0aaaaaaaaaaaaaaa1 tcp 80-80 0.0.0.0/0"
      ]
      && aws_vpc_security_group_egress_rule.alb_to_tasks.security_group_id == "sg-0aaaaaaaaaaaaaaa1"
      && aws_vpc_security_group_egress_rule.alb_to_tasks.referenced_security_group_id == "sg-0bbbbbbbbbbbbbbb2"
      && aws_vpc_security_group_egress_rule.alb_to_tasks.from_port == 8080 && aws_vpc_security_group_egress_rule.alb_to_tasks.to_port == 8080
      && aws_vpc_security_group_egress_rule.alb_to_tasks.cidr_ipv4 == null
    )
    error_message = "The load balancer's security group takes 80 and 443 from anywhere and reaches the tasks on 8080 only."
  }
}

run "tasks_reach_https_and_the_database_only" {
  command = plan

  assert {
    condition = (
      [for r in [aws_vpc_security_group_egress_rule.task_https, aws_vpc_security_group_egress_rule.task_database] : "${r.security_group_id} ${r.ip_protocol} ${r.from_port}-${r.to_port} ${r.cidr_ipv4}"] == [
        "sg-0bbbbbbbbbbbbbbb2 tcp 443-443 0.0.0.0/0", "sg-0bbbbbbbbbbbbbbb2 tcp 5432-5432 10.0.20.0/24"
      ]
    )
    error_message = "The tasks may reach HTTPS anywhere and PostgreSQL in database_cidr, and nothing else."
  }
}

run "migrate_run_task_command" {
  command = plan

  assert {
    condition     = output.migrate_run_task_command == "aws ecs run-task --region eu-west-1 --cluster packmoat --launch-type FARGATE --task-definition arn:aws:ecs:eu-west-1:123456789012:task-definition/packmoat-migrate:1 --network-configuration 'awsvpcConfiguration={subnets=[subnet-0aaaaaaaaaaaaaaa1,subnet-0aaaaaaaaaaaaaaa2],securityGroups=[sg-0bbbbbbbbbbbbbbb2],assignPublicIp=ENABLED}'"
    error_message = "The migrate command runs the migrate task definition once, where the server's tasks run."
  }
}
