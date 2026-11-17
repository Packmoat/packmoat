# The inputs. No secret is ever an input: each secret is a Secrets Manager secret, named here by its full ARN only, and read by the
# execution role of the task that needs it. Each validation refuses one thing and names the input, never its value. The checks between
# two inputs are lifecycle preconditions in main.tf.

variable "account_id" {
  description = "The AWS account to deploy to: 12 digits. The provider refuses to run against any other, and every ARN given must be in it."
  type        = string

  validation {
    condition     = can(regex("^[0-9]{12}$", var.account_id))
    error_message = "account_id must be an AWS account id: 12 digits."
  }
}

variable "region" {
  description = "The region of everything the template makes, such as eu-west-1. Every ARN given must be in it."
  type        = string

  validation {
    condition     = can(regex("^[a-z]{2}(-[a-z]+)+-[0-9]$", var.region))
    error_message = "region must be a region name, such as eu-west-1."
  }
}

variable "name" {
  description = "The prefix of every resource's name: lower-case letters, digits and hyphens, starting with a letter, 2 to 20 characters."
  type        = string
  default     = "packmoat"

  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{0,18}[a-z0-9]$", var.name))
    error_message = "name must be 2 to 20 lower-case letters, digits and hyphens, starting with a letter and not ending with a hyphen."
  }
}

variable "service_image" {
  description = "The server's image, by digest: ghcr.io/packmoat/packmoat-core@sha256:<digest>. Its secrets-init container runs it too. Set it after running the migrate task with the same image."
  type        = string

  validation {
    condition     = can(regex("^[^@\\s]+@sha256:[0-9a-f]{64}$", var.service_image))
    error_message = "service_image must name the image by digest (<image>@sha256:<digest>)."
  }
}

variable "job_image" {
  description = "The migrate task's image, by digest. On an upgrade: set this to the new image, run the migrate task, then set service_image to it."
  type        = string

  validation {
    condition     = can(regex("^[^@\\s]+@sha256:[0-9a-f]{64}$", var.job_image))
    error_message = "job_image must name the image by digest (<image>@sha256:<digest>)."
  }
}

variable "hosts" {
  description = "PACKMOAT_HOSTS: the feed host names, separated by spaces. Never the admin or customer site host (set those in settings)."
  type        = string

  validation {
    condition     = can(regex("^[a-z0-9.-]+( [a-z0-9.-]+)*$", var.hosts))
    error_message = "hosts must be one or more host names in lower case, separated by single spaces."
  }
}

variable "app_role_name" {
  description = "The PostgreSQL app role the server signs in as. The migrate task grants it its rows (db migrate --grant-to)."
  type        = string
  default     = "packmoat_app"

  validation {
    condition     = can(regex("^[a-z_][a-z0-9_]{0,62}$", var.app_role_name))
    error_message = "app_role_name must be a PostgreSQL role name: lower-case letters, digits and underscores, starting with a letter or underscore."
  }
}

variable "vpc_id" {
  description = "The existing VPC: the load balancer, the tasks and their security groups go in it."
  type        = string

  validation {
    condition     = can(regex("^vpc-([0-9a-f]{8}|[0-9a-f]{17})$", var.vpc_id))
    error_message = "vpc_id must be a VPC id (vpc- and 8 or 17 hexadecimal digits)."
  }
}

variable "public_subnet_ids" {
  description = "The VPC's existing public subnets (with a route to an internet gateway), in at least two Availability Zones: the load balancer goes in them, and so do the tasks unless enable_nat_gateway is true."
  type        = list(string)

  validation {
    condition     = length(var.public_subnet_ids) >= 2 && length(distinct(var.public_subnet_ids)) == length(var.public_subnet_ids)
    error_message = "public_subnet_ids must name at least two different subnets (an Application Load Balancer needs two Availability Zones)."
  }

  validation {
    condition     = alltrue([for s in var.public_subnet_ids : can(regex("^subnet-([0-9a-f]{8}|[0-9a-f]{17})$", s))])
    error_message = "public_subnet_ids must be subnet ids (subnet- and 8 or 17 hexadecimal digits)."
  }
}

variable "public_subnet_cidrs" {
  description = "The IPv4 networks of public_subnet_ids, in the same order. They are PACKMOAT_TRUSTED_PROXIES: the load balancer reaches the tasks from addresses in them."
  type        = list(string)

  validation {
    condition     = alltrue([for c in var.public_subnet_cidrs : can(cidrhost(c, 0)) && can(regex("^[0-9.]+/[0-9]+$", c))])
    error_message = "public_subnet_cidrs must be IPv4 networks, such as 10.0.0.0/24."
  }

  # A network's own address, with no host bits set (10.0.1.0/24, never 10.0.1.5/24): the server refuses the other at start.
  validation {
    condition     = alltrue([for c in var.public_subnet_cidrs : !can(cidrhost(c, 0)) || try(cidrhost(c, 0) == split("/", c)[0], false)])
    error_message = "public_subnet_cidrs must be networks with no host bits set, such as 10.0.1.0/24 (not 10.0.1.5/24)."
  }

  validation {
    condition     = alltrue([for c in var.public_subnet_cidrs : !can(regex("/0$", c))])
    error_message = "public_subnet_cidrs may not hold a network of everything (/0): any client could choose its address."
  }
}

variable "database_cidr" {
  description = "The IPv4 network of the existing RDS for PostgreSQL 17 instance (its subnet, or its address with /32). The tasks may reach it on 5432 only; admit the output task_security_group_id in the database's own security group."
  type        = string

  # That it is an IPv4 network is the provider's own check (on the security group rule); this one refuses a network of everything.
  validation {
    condition     = !can(regex("/0$", var.database_cidr))
    error_message = "database_cidr may not be a network of everything (/0): the tasks may reach the database's network only."
  }
}

variable "certificate_arn" {
  description = "The existing ACM certificate for the HTTPS listener (every name in hosts and your site hosts), in region."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:acm:[a-z0-9-]+:[0-9]{12}:certificate/[0-9a-f-]{36}$", var.certificate_arn))
    error_message = "certificate_arn must be an ACM certificate ARN (arn:aws:acm:<region>:<account>:certificate/<id>)."
  }
}

variable "master_key_secret_arn" {
  description = "The full ARN (with its six-character suffix) of the Secrets Manager secret that holds the master key (packmoat-admin keygen). Read by the server task's secrets-init container."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:secretsmanager:[a-z0-9-]+:[0-9]{12}:secret:[A-Za-z0-9/_+=.@-]+-[A-Za-z0-9]{6}$", var.master_key_secret_arn))
    error_message = "master_key_secret_arn must be a secret's full ARN (arn:aws:secretsmanager:<region>:<account>:secret:<name>-<6 characters>)."
  }
}

variable "app_database_secret_arn" {
  description = "The full ARN of the secret that holds the app role's connection string, with Root Certificate=/run/secrets/packmoat/db_ca. Server task only."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:secretsmanager:[a-z0-9-]+:[0-9]{12}:secret:[A-Za-z0-9/_+=.@-]+-[A-Za-z0-9]{6}$", var.app_database_secret_arn))
    error_message = "app_database_secret_arn must be a secret's full ARN (arn:aws:secretsmanager:<region>:<account>:secret:<name>-<6 characters>)."
  }
}

variable "owner_database_secret_arn" {
  description = "The full ARN of the secret that holds the owner role's connection string, with Root Certificate=/run/secrets/packmoat/db_ca. Migrate task only."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:secretsmanager:[a-z0-9-]+:[0-9]{12}:secret:[A-Za-z0-9/_+=.@-]+-[A-Za-z0-9]{6}$", var.owner_database_secret_arn))
    error_message = "owner_database_secret_arn must be a secret's full ARN (arn:aws:secretsmanager:<region>:<account>:secret:<name>-<6 characters>)."
  }
}

variable "database_ca_secret_arn" {
  description = "The full ARN of the secret that holds the RDS certificate authority bundle for your region (PEM), written to /run/secrets/packmoat/db_ca for both tasks."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:secretsmanager:[a-z0-9-]+:[0-9]{12}:secret:[A-Za-z0-9/_+=.@-]+-[A-Za-z0-9]{6}$", var.database_ca_secret_arn))
    error_message = "database_ca_secret_arn must be a secret's full ARN (arn:aws:secretsmanager:<region>:<account>:secret:<name>-<6 characters>)."
  }
}

variable "app_cert_secret_arn" {
  description = "Optional: the full ARN of the secret that holds the customer site's sign-in certificate and key (PEM), written to /run/secrets/packmoat/app_cert and named by PACKMOAT_APP_CERT. Empty: none."
  type        = string
  default     = ""

  validation {
    condition     = var.app_cert_secret_arn == "" || can(regex("^arn:aws:secretsmanager:[a-z0-9-]+:[0-9]{12}:secret:[A-Za-z0-9/_+=.@-]+-[A-Za-z0-9]{6}$", var.app_cert_secret_arn))
    error_message = "app_cert_secret_arn must be empty or a secret's full ARN (arn:aws:secretsmanager:<region>:<account>:secret:<name>-<6 characters>)."
  }
}

variable "secrets_kms_key_arn" {
  description = "Optional: the customer managed KMS key the secrets are encrypted with. Empty (the default): the secrets use the AWS managed key, and the roles get no KMS permission."
  type        = string
  default     = ""

  validation {
    condition     = var.secrets_kms_key_arn == "" || can(regex("^arn:aws:kms:[a-z0-9-]+:[0-9]{12}:key/[0-9a-f-]{36}$", var.secrets_kms_key_arn))
    error_message = "secrets_kms_key_arn must be empty or a KMS key ARN (arn:aws:kms:<region>:<account>:key/<id>)."
  }
}

variable "file_settings" {
  description = "Optional secret settings, read through their _FILE form: the setting's name to its secret's full ARN, such as { PACKMOAT_ADMIN_CLIENT_SECRET = \"arn:aws:secretsmanager:...\" }. Server only; each is written to /run/secrets/packmoat/<the name in lower case>."
  type        = map(string)
  default     = {}

  validation {
    condition     = alltrue([for k in keys(var.file_settings) : can(regex("^PACKMOAT_[A-Z0-9_]*[A-Z0-9]$", k))])
    error_message = "file_settings may set PACKMOAT_ settings only (upper-case letters, digits and underscores)."
  }

  validation {
    condition     = alltrue([for k in keys(var.file_settings) : !contains(local.template_settings, k)])
    error_message = "file_settings may not set a setting the template sets (PACKMOAT_COOKIE_KEYS, PACKMOAT_BACKGROUND_JOBS, PACKMOAT_HOSTS, PACKMOAT_TRUSTED_PROXIES, PACKMOAT_APP_CERT)."
  }

  validation {
    condition     = alltrue([for k in keys(var.file_settings) : k != "PACKMOAT_MASTER_KEY"])
    error_message = "file_settings may not set PACKMOAT_MASTER_KEY: the template writes it (master_key_secret_arn)."
  }

  validation {
    condition     = alltrue([for k in keys(var.file_settings) : !startswith(k, "PACKMOAT_DB")])
    error_message = "file_settings may not set a PACKMOAT_DB setting: the database settings are the template's (app_database_secret_arn)."
  }

  validation {
    condition     = alltrue([for k in keys(var.file_settings) : !endswith(k, "_FILE")])
    error_message = "file_settings takes each setting's own name; the template adds _FILE."
  }

  validation {
    condition     = alltrue([for v in values(var.file_settings) : can(regex("^arn:aws:secretsmanager:[a-z0-9-]+:[0-9]{12}:secret:[A-Za-z0-9/_+=.@-]+-[A-Za-z0-9]{6}$", v))])
    error_message = "file_settings values must be secrets' full ARNs (arn:aws:secretsmanager:<region>:<account>:secret:<name>-<6 characters>)."
  }
}

variable "settings" {
  description = "Other settings that are not secret, such as { PACKMOAT_ADMIN_HOST = \"admin.example.com\" }. PACKMOAT_ settings only: a setting this template sets (every PACKMOAT_DB setting among them), a secret setting and a _FILE setting are refused."
  type        = map(string)
  default     = {}

  validation {
    condition     = alltrue([for k in keys(var.settings) : startswith(k, "PACKMOAT_")])
    error_message = "settings may set PACKMOAT_ settings only."
  }

  validation {
    condition     = alltrue([for k in keys(var.settings) : !contains(local.template_settings, k)])
    error_message = "settings may not set a setting the template sets (PACKMOAT_COOKIE_KEYS, PACKMOAT_BACKGROUND_JOBS, PACKMOAT_HOSTS, PACKMOAT_TRUSTED_PROXIES, PACKMOAT_APP_CERT)."
  }

  validation {
    condition     = alltrue([for k in keys(var.settings) : !contains(local.secret_settings, k)])
    error_message = "settings may not set a secret setting (PACKMOAT_MASTER_KEY, PACKMOAT_ADMIN_CLIENT_SECRET): use file_settings."
  }

  validation {
    condition     = alltrue([for k in keys(var.settings) : !startswith(k, "PACKMOAT_DB")])
    error_message = "settings may not set a PACKMOAT_DB setting: the database settings are the template's (app_database_secret_arn)."
  }

  validation {
    condition     = alltrue([for k in keys(var.settings) : !endswith(k, "_FILE")])
    error_message = "settings may not set a _FILE setting: use file_settings."
  }
}

variable "desired_count" {
  description = "How many copies of the server run. On PostgreSQL more than one may: budget database connections for it (docs/operations.md)."
  type        = number
  default     = 1

  validation {
    condition     = var.desired_count >= 1 && var.desired_count <= 10
    error_message = "desired_count must be from 1 to 10 (the provider refuses a number that is not whole)."
  }
}

variable "cpu" {
  description = "CPU units for each server task (1024 is one vCPU): 256, 512, 1024, 2048 or 4096. memory must be a size Fargate allows with it."
  type        = number
  default     = 512

  validation {
    condition     = contains([256, 512, 1024, 2048, 4096], var.cpu)
    error_message = "cpu must be 256, 512, 1024, 2048 or 4096."
  }
}

variable "memory" {
  description = "Memory for each server task, in MiB, such as 1024."
  type        = number
  default     = 1024

  validation {
    condition     = var.memory >= 512 && var.memory <= 30720 && floor(var.memory) == var.memory
    error_message = "memory must be a whole number of MiB from 512 to 30720."
  }
}

variable "log_retention_days" {
  description = "How long CloudWatch Logs keeps the tasks' logs, in days: one of the values CloudWatch accepts, never 0 (kept forever)."
  type        = number
  default     = 30

  validation {
    condition     = contains([1, 3, 5, 7, 14, 30, 60, 90, 120, 150, 180, 365, 400, 545, 731, 1096, 1827, 2192, 2557, 2922, 3288, 3653], var.log_retention_days)
    error_message = "log_retention_days must be one of 1, 3, 5, 7, 14, 30, 60, 90, 120, 150, 180, 365, 400, 545, 731, 1096, 1827, 2192, 2557, 2922, 3288 or 3653."
  }
}

variable "enable_nat_gateway" {
  description = "A NAT gateway with an Elastic IP and two private subnets for the tasks, so that their outbound traffic comes from one fixed address and they have no public address: needed only when your customers allowlist the address Packmoat reads their feeds from. It costs money every hour."
  type        = bool
  default     = false
}

variable "private_subnet_cidrs" {
  description = "With enable_nat_gateway: the IPv4 networks of the two (or more) private subnets the template makes for the tasks, inside the VPC and free."
  type        = list(string)
  default     = []

  validation {
    condition     = alltrue([for c in var.private_subnet_cidrs : can(cidrhost(c, 0)) && can(regex("^[0-9.]+/[0-9]+$", c)) && !can(regex("/0$", c))])
    error_message = "private_subnet_cidrs must be IPv4 networks, not /0, such as 10.0.10.0/24."
  }
}

variable "private_subnet_azs" {
  description = "With enable_nat_gateway: the Availability Zone of each private subnet, in the same order as private_subnet_cidrs, such as eu-west-1a."
  type        = list(string)
  default     = []

  validation {
    condition     = alltrue([for z in var.private_subnet_azs : can(regex("^[a-z]{2}(-[a-z]+)+-[0-9][a-z]$", z))])
    error_message = "private_subnet_azs must be Availability Zone names, such as eu-west-1a."
  }
}
