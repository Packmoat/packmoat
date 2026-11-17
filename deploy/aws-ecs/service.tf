# The server: an ECS service on Fargate, one task definition with two containers. secrets-init runs first, writes each secret to a file on
# the task's own volume and exits; the server's container starts only once it has succeeded, and reads the files, read-only.

locals {
  # The one script every secrets-init container runs, fixed: each SECRET_<key> its execution role fetched becomes the file
  # /run/secrets/packmoat/<key>, 0400 and owned by the server's user (10001), and then the folder itself is the server's, 0500. umask
  # 077 first, so no file is ever readable by anyone else, even for a moment. It prints nothing: no name, no value. $${ is HCL's way of
  # writing bash's ${.
  secrets_init_script = <<-EOT
    set -euo pipefail
    umask 077
    d=/run/secrets/packmoat
    for v in $(compgen -v SECRET_); do
      f="$d/$${v#SECRET_}"
      printf '%s' "$${!v}" > "$f"
      chmod 0400 "$f"
      chown 10001:10001 "$f"
    done
    chmod 0500 "$d"
    chown 10001:10001 "$d"
  EOT

  # secrets-init runs as root, to hand the files to user 10001, with every one of Fargate's default capabilities dropped but CHOWN.
  secrets_init_drop = ["AUDIT_WRITE", "DAC_OVERRIDE", "FOWNER", "FSETID", "KILL", "MKNOD", "NET_BIND_SERVICE", "NET_RAW", "SETFCAP", "SETGID", "SETPCAP", "SETUID", "SYS_CHROOT"]

  # The server's own settings. Every secret is a _FILE path; none is a value. Background jobs are on: a Fargate task keeps its CPU between
  # requests, and on PostgreSQL a lease in the database lets one copy at a time run the daily pass. PORT makes the server listen on every
  # address of the task, where the load balancer reaches it (the image's default is 127.0.0.1 only).
  server_env = merge(
    {
      PORT                     = "8080"
      PACKMOAT_DB_ENGINE       = "postgres"
      PACKMOAT_COOKIE_KEYS     = "database"
      PACKMOAT_BACKGROUND_JOBS = "on"
      PACKMOAT_HOSTS           = var.hosts
      PACKMOAT_TRUSTED_PROXIES = join(" ", var.public_subnet_cidrs)
      PACKMOAT_MASTER_KEY_FILE = "${local.secrets_path}/master_key"
      PACKMOAT_DB_FILE         = "${local.secrets_path}/db"
    },
    var.app_cert_secret_arn == "" ? {} : { PACKMOAT_APP_CERT = "${local.secrets_path}/app_cert" },
    { for k in keys(var.file_settings) : "${k}_FILE" => "${local.secrets_path}/${lower(k)}" },
    var.settings,
  )

  server_containers = [
    {
      name       = "secrets-init"
      image      = var.service_image
      essential  = false
      user       = "0"
      entryPoint = ["/bin/bash", "-c"]
      command    = [local.secrets_init_script]
      secrets    = [for k, arn in local.server_secrets : { name = "SECRET_${k}", valueFrom = arn }]
      mountPoints = [
        { sourceVolume = "secrets", containerPath = local.secrets_path, readOnly = false },
      ]
      linuxParameters = { capabilities = { drop = local.secrets_init_drop } }
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          awslogs-group         = local.log_group_name
          awslogs-region        = var.region
          awslogs-stream-prefix = "secrets-init"
        }
      }
    },
    {
      name      = "server"
      image     = var.service_image
      essential = true
      user      = "10001:10001"
      # The image sets PACKMOAT_DB to its SQLite file; the server refuses a setting and its _FILE both set, so the image's is unset here.
      entryPoint   = ["/usr/bin/env", "-u", "PACKMOAT_DB", "dotnet", "/app/host/Packmoat.Server.dll"]
      portMappings = [{ containerPort = 8080, protocol = "tcp" }]
      environment  = [for k, v in local.server_env : { name = k, value = v }]
      mountPoints = [
        { sourceVolume = "secrets", containerPath = local.secrets_path, readOnly = true },
      ]
      dependsOn       = [{ containerName = "secrets-init", condition = "SUCCESS" }]
      linuxParameters = { capabilities = { drop = ["ALL"] } }
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          awslogs-group         = local.log_group_name
          awslogs-region        = var.region
          awslogs-stream-prefix = "server"
        }
      }
    },
  ]
}

resource "aws_ecs_task_definition" "server" {
  family                   = "${var.name}-server"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = var.cpu
  memory                   = var.memory
  execution_role_arn       = aws_iam_role.server_execution.arn
  container_definitions    = jsonencode(local.server_containers)

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }

  # The task's own storage, empty at each start: secrets-init writes the files, the server reads them.
  volume {
    name = "secrets"
  }

  depends_on = [terraform_data.checks]
}

resource "aws_ecs_service" "server" {
  name            = var.name
  cluster         = aws_ecs_cluster.main.arn
  task_definition = aws_ecs_task_definition.server.arn
  desired_count   = var.desired_count
  launch_type     = "FARGATE"

  # No shell into the containers: ECS Exec would need a task role and its own channel.
  enable_execute_command = false

  # A new task takes traffic only once /readyz answers 200; the old ones keep serving until then. A deployment that never gets there is
  # rolled back to the last task definition that did.
  health_check_grace_period_seconds  = 60
  deployment_minimum_healthy_percent = 100
  deployment_maximum_percent         = 200

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  network_configuration {
    subnets          = local.task_subnets
    security_groups  = [aws_security_group.task.id]
    assign_public_ip = local.task_public_address
  }

  load_balancer {
    target_group_arn = aws_lb_target_group.server.arn
    container_name   = "server"
    container_port   = 8080
  }

  depends_on = [
    aws_lb_listener.https,
    aws_iam_role_policy.server_execution,
    aws_cloudwatch_log_group.server,
    aws_route.private_nat,
    aws_route_table_association.private,
  ]
}
