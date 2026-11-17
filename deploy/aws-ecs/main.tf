# Packmoat on AWS ECS Fargate: the server as an ECS service behind an Application Load Balancer, in an existing VPC, on an existing RDS
# for PostgreSQL 17 instance, with its secrets read from Secrets Manager in the same account and region. docs/deploy/aws-ecs.md explains
# each input and the steps around a deploy.
#
# What it makes: a log group; a cluster; the server's task definition and service (service.tf); a migrate task definition, run by hand
# (migrate.tf); the load balancer, its listeners and target group, and the two security groups, each rule a resource of its own
# (network.tf); two execution roles, the server task's and the migrate task's, each with one inline policy on exact ARNs (iam.tf); and,
# only when enable_nat_gateway is true, an Elastic IP, a NAT gateway and private subnets for the tasks (nat.tf). No task role: the
# containers' own code gets no AWS credentials.
#
# No secret is ever an input, an output or an environment value of the server: each task's secrets-init container, the only container
# with secrets, writes each one to a file under /run/secrets/packmoat on a volume the server's container reads, read-only, through the
# setting's _FILE form. Only the migrate task gets the owner role's connection string. deploy/aws-ecs/test-template.sh checks all of this
# offline.

provider "aws" {
  region              = var.region
  allowed_account_ids = [var.account_id]
}

locals {
  # The settings this template sets itself, and the secret ones, which may only ever be files from Secrets Manager. Every PACKMOAT_DB
  # setting (the engine, the connection string, the single-role switch, and any added later) is the template's alone, refused by its
  # prefix (variables.tf), so it is in neither list.
  template_settings = [
    "PACKMOAT_COOKIE_KEYS",
    "PACKMOAT_BACKGROUND_JOBS",
    "PACKMOAT_HOSTS",
    "PACKMOAT_TRUSTED_PROXIES",
    "PACKMOAT_APP_CERT",
  ]
  secret_settings = [
    "PACKMOAT_MASTER_KEY",
    "PACKMOAT_ADMIN_CLIENT_SECRET",
  ]

  # Where the secrets-init container writes each secret, one file per key, and where the other container reads them.
  secrets_path = "/run/secrets/packmoat"

  # The server task's secrets, by file name: the master key, the app role's connection string and the database's certificate authority,
  # always; the customer site's certificate and any file_settings when given. Never the owner's connection string.
  server_secrets = merge(
    {
      master_key = var.master_key_secret_arn
      db         = var.app_database_secret_arn
      db_ca      = var.database_ca_secret_arn
    },
    var.app_cert_secret_arn == "" ? {} : { app_cert = var.app_cert_secret_arn },
    { for k, v in var.file_settings : lower(k) => v },
  )
  # The migrate task's: the owner's connection string and the certificate authority only.
  migrate_secrets = {
    db_owner = var.owner_database_secret_arn
    db_ca    = var.database_ca_secret_arn
  }

  # Every ARN given, for the one-account, one-region check.
  given_arns = concat(
    [var.certificate_arn],
    values(local.server_secrets),
    values(local.migrate_secrets),
    var.secrets_kms_key_arn == "" ? [] : [var.secrets_kms_key_arn],
  )

  log_group_name = "/ecs/${var.name}"
  log_group_arn  = "arn:aws:logs:${var.region}:${var.account_id}:log-group:${local.log_group_name}"

  # Where the tasks run: the public subnets with a public address (the only way out with no NAT gateway), or the private ones without.
  task_subnets        = var.enable_nat_gateway ? aws_subnet.private[*].id : var.public_subnet_ids
  task_public_address = !var.enable_nat_gateway
}

# The checks between two inputs. A precondition stops the plan, naming the inputs, never a value.
resource "terraform_data" "checks" {
  lifecycle {
    # The owner's connection string is the migrate task's alone: no secret the server reads may be the same one.
    precondition {
      condition     = !contains(values(local.server_secrets), var.owner_database_secret_arn)
      error_message = "owner_database_secret_arn may not also be the server's secret (master_key_secret_arn, app_database_secret_arn, database_ca_secret_arn, app_cert_secret_arn or file_settings)."
    }

    # A setting both as a value and as a file would set it and its _FILE form, which stops the server.
    precondition {
      condition     = length(setintersection(keys(var.settings), keys(var.file_settings))) == 0
      error_message = "A setting may be in settings or in file_settings, not both."
    }

    precondition {
      condition     = length(var.public_subnet_cidrs) == length(var.public_subnet_ids)
      error_message = "public_subnet_cidrs must give one network for each of public_subnet_ids, in the same order."
    }

    precondition {
      condition     = !var.enable_nat_gateway || (length(var.private_subnet_cidrs) >= 2 && length(var.private_subnet_azs) == length(var.private_subnet_cidrs))
      error_message = "enable_nat_gateway needs at least two private_subnet_cidrs, and one private_subnet_azs entry for each."
    }

    precondition {
      condition     = var.enable_nat_gateway || length(var.private_subnet_cidrs) + length(var.private_subnet_azs) == 0
      error_message = "private_subnet_cidrs and private_subnet_azs are for enable_nat_gateway only."
    }

    # Every ARN given is in this account and this region: the execution roles may read nothing elsewhere. It compares inputs of the right
    # form only: a malformed account, region or ARN is its own validation's to refuse (variables.tf).
    precondition {
      condition = !can(regex("^[0-9]{12}$", var.account_id)) || !can(regex("^[a-z]{2}(-[a-z]+)+-[0-9]$", var.region)) || alltrue([
        for a in local.given_arns : split(":", a)[3] == var.region && split(":", a)[4] == var.account_id
        if can(regex("^arn:aws:[a-z0-9-]+:[a-z0-9-]+:[0-9]{12}:", a))
      ])
      error_message = "certificate_arn, every secret ARN and secrets_kms_key_arn must be in region and account_id."
    }
  }
}

resource "aws_cloudwatch_log_group" "server" {
  name              = local.log_group_name
  retention_in_days = var.log_retention_days
}

# Container Insights is off: it adds CloudWatch charges (docs/deploy/aws-ecs.md).
resource "aws_ecs_cluster" "main" {
  name = var.name

  setting {
    name  = "containerInsights"
    value = "disabled"
  }
}
