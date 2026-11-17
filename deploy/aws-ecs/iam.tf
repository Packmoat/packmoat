# The two execution roles, and exactly what each may do. ECS itself uses an execution role to fetch a task's secrets for its secrets-init
# container and to write the task's logs; the containers' own code never gets it. Two roles, so that each task reads only its own secrets:
# the server task's cannot read the owner's connection string; the migrate task's can read nothing else. No managed policy, no wildcard
# resource, and no task role at all.

locals {
  # Only ECS tasks in this account may use either role.
  execution_trust = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "ecs-tasks.amazonaws.com" }
      Action    = "sts:AssumeRole"
      Condition = { StringEquals = { "aws:SourceAccount" = var.account_id } }
    }]
  })

  # The log streams of the one log group, and kms:Decrypt on the secrets' key only through Secrets Manager, when a key is given.
  log_statement = {
    Sid      = "Logs"
    Effect   = "Allow"
    Action   = ["logs:CreateLogStream", "logs:PutLogEvents"]
    Resource = ["${local.log_group_arn}:log-stream:*"]
  }
  kms_statements = var.secrets_kms_key_arn == "" ? [] : [{
    Sid       = "SecretsKey"
    Effect    = "Allow"
    Action    = ["kms:Decrypt"]
    Resource  = [var.secrets_kms_key_arn]
    Condition = { StringEquals = { "kms:ViaService" = "secretsmanager.${var.region}.amazonaws.com" } }
  }]
}

resource "aws_iam_role" "server_execution" {
  name               = "${var.name}-server-execution"
  description        = "Packmoat server task: fetches its secrets for secrets-init and writes its logs."
  assume_role_policy = local.execution_trust
}

resource "aws_iam_role_policy" "server_execution" {
  name = "${var.name}-server-execution"
  role = aws_iam_role.server_execution.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = concat([
      {
        Sid      = "Secrets"
        Effect   = "Allow"
        Action   = ["secretsmanager:GetSecretValue"]
        Resource = sort(distinct(values(local.server_secrets)))
      },
      local.log_statement,
    ], local.kms_statements)
  })
}

resource "aws_iam_role" "migrate_execution" {
  name               = "${var.name}-migrate-execution"
  description        = "Packmoat migrate task: fetches the owner's connection string for secrets-init and writes its logs."
  assume_role_policy = local.execution_trust
}

resource "aws_iam_role_policy" "migrate_execution" {
  name = "${var.name}-migrate-execution"
  role = aws_iam_role.migrate_execution.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = concat([
      {
        Sid      = "Secrets"
        Effect   = "Allow"
        Action   = ["secretsmanager:GetSecretValue"]
        Resource = sort(distinct(values(local.migrate_secrets)))
      },
      local.log_statement,
    ], local.kms_statements)
  })
}
