# The migrate task: run by hand before each upgrade (the output migrate_run_task_command), with the owner role's connection string, which
# nothing else gets. Its own execution role reads only that and the certificate authority.

locals {
  migrate_containers = [
    {
      name       = "secrets-init"
      image      = var.job_image
      essential  = false
      user       = "0"
      entryPoint = ["/bin/bash", "-c"]
      command    = [local.secrets_init_script]
      secrets    = [for k, arn in local.migrate_secrets : { name = "SECRET_${k}", valueFrom = arn }]
      mountPoints = [
        { sourceVolume = "secrets", containerPath = local.secrets_path, readOnly = false },
      ]
      linuxParameters = { capabilities = { drop = local.secrets_init_drop } }
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          awslogs-group         = local.log_group_name
          awslogs-region        = var.region
          awslogs-stream-prefix = "migrate-secrets-init"
        }
      }
    },
    {
      name       = "migrate"
      image      = var.job_image
      essential  = true
      user       = "10001:10001"
      entryPoint = ["/usr/bin/env", "-u", "PACKMOAT_DB", "packmoat-admin"]
      command    = ["db", "migrate", "--grant-to", var.app_role_name]
      environment = [
        { name = "PACKMOAT_DB_ENGINE", value = "postgres" },
        { name = "PACKMOAT_DB_FILE", value = "${local.secrets_path}/db_owner" },
      ]
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
          awslogs-stream-prefix = "migrate"
        }
      }
    },
  ]
}

resource "aws_ecs_task_definition" "migrate" {
  family                   = "${var.name}-migrate"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 512
  memory                   = 1024
  execution_role_arn       = aws_iam_role.migrate_execution.arn
  container_definitions    = jsonencode(local.migrate_containers)

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }

  volume {
    name = "secrets"
  }

  # What the task needs before it can run, so that a first apply with -target=aws_ecs_task_definition.migrate makes it too.
  depends_on = [
    terraform_data.checks,
    aws_iam_role_policy.migrate_execution,
    aws_cloudwatch_log_group.server,
  ]
}
