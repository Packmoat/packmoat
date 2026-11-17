# The inputs. No secret is ever an input: each secret is a Secret Manager secret, named here by its id only, and read by the service
# account of the resource that mounts it. Each validation refuses one thing and names the input, never its value. The checks between two
# inputs are lifecycle preconditions in main.tf.

variable "project_id" {
  description = "The Google Cloud project to deploy to. Its Secret Manager holds the secrets."
  type        = string

  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{4,28}[a-z0-9]$", var.project_id))
    error_message = "project_id must be a project id: 6 to 30 lower-case letters, digits and hyphens, starting with a letter."
  }
}

variable "region" {
  description = "The region of the service, the jobs and the scheduler job, such as europe-west1."
  type        = string

  validation {
    condition     = can(regex("^[a-z]+-[a-z]+[0-9]+$", var.region))
    error_message = "region must be a region name, such as europe-west1."
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
  description = "The server's image, by digest: ghcr.io/packmoat/packmoat-core@sha256:<digest>, or your registry's copy. The retention job runs it too. Set it after running the migrate job with the same image."
  type        = string

  validation {
    condition     = can(regex("^[^@\\s]+@sha256:[0-9a-f]{64}$", var.service_image))
    error_message = "service_image must name the image by digest (<image>@sha256:<digest>)."
  }
}

variable "job_image" {
  description = "The migrate job's image, by digest. On an upgrade: set this to the new image, run the job, then set service_image to it."
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
  description = "The PostgreSQL app role the server signs in as. The migrate job grants it its rows (db migrate --grant-to)."
  type        = string
  default     = "packmoat_app"

  validation {
    condition     = can(regex("^[a-z_][a-z0-9_]{0,62}$", var.app_role_name))
    error_message = "app_role_name must be a PostgreSQL role name: lower-case letters, digits and underscores, starting with a letter or underscore."
  }
}

variable "cloud_sql_instance" {
  description = "The existing Cloud SQL for PostgreSQL 17 instance's connection name, <project>:<region>:<instance>. The service and the jobs reach it through the socket Cloud Run mounts at /cloudsql/<connection name>."
  type        = string

  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{4,28}[a-z0-9]:[a-z]+-[a-z]+[0-9]+:[a-z][a-z0-9-]{0,96}[a-z0-9]$", var.cloud_sql_instance))
    error_message = "cloud_sql_instance must be an instance connection name: <project>:<region>:<instance>."
  }
}

variable "master_key_secret" {
  description = "The Secret Manager secret that holds the master key (packmoat-admin keygen). Mounted for the server and the retention job."
  type        = string
  default     = "packmoat-master-key"

  validation {
    condition     = can(regex("^[A-Za-z0-9_-]{1,255}$", var.master_key_secret))
    error_message = "master_key_secret must be a secret id: letters, digits, hyphens and underscores."
  }
}

variable "app_database_secret" {
  description = "The Secret Manager secret that holds the app role's connection string. Mounted for the server and the retention job."
  type        = string
  default     = "packmoat-db-app"

  validation {
    condition     = can(regex("^[A-Za-z0-9_-]{1,255}$", var.app_database_secret))
    error_message = "app_database_secret must be a secret id: letters, digits, hyphens and underscores."
  }
}

variable "owner_database_secret" {
  description = "The Secret Manager secret that holds the owner role's connection string. Mounted for the migrate job only."
  type        = string
  default     = "packmoat-db-owner"

  validation {
    condition     = can(regex("^[A-Za-z0-9_-]{1,255}$", var.owner_database_secret))
    error_message = "owner_database_secret must be a secret id: letters, digits, hyphens and underscores."
  }
}

variable "app_cert_secret" {
  description = "Optional: the Secret Manager secret that holds the customer site's sign-in certificate and key (PEM), mounted for the server and named by PACKMOAT_APP_CERT. Empty: none."
  type        = string
  default     = ""

  validation {
    condition     = var.app_cert_secret == "" || can(regex("^[A-Za-z0-9_-]{1,255}$", var.app_cert_secret))
    error_message = "app_cert_secret must be empty or a secret id: letters, digits, hyphens and underscores."
  }
}

variable "file_settings" {
  description = "Optional secret settings, read through their _FILE form: the setting's name to its Secret Manager secret's id, such as { PACKMOAT_ADMIN_CLIENT_SECRET = \"packmoat-admin-client-secret\" }. Server only."
  type        = map(string)
  default     = {}

  validation {
    condition     = alltrue([for k in keys(var.file_settings) : startswith(k, "PACKMOAT_")])
    error_message = "file_settings may set PACKMOAT_ settings only."
  }

  validation {
    condition     = alltrue([for k in keys(var.file_settings) : !contains(local.template_settings, k)])
    error_message = "file_settings may not set a setting the template sets (PACKMOAT_COOKIE_KEYS, PACKMOAT_BACKGROUND_JOBS, PACKMOAT_HOSTS, PACKMOAT_TRUSTED_PROXIES, PACKMOAT_APP_CERT)."
  }

  validation {
    condition     = alltrue([for k in keys(var.file_settings) : k != "PACKMOAT_MASTER_KEY"])
    error_message = "file_settings may not set PACKMOAT_MASTER_KEY: the template mounts it (master_key_secret)."
  }

  validation {
    condition     = alltrue([for k in keys(var.file_settings) : !startswith(k, "PACKMOAT_DB")])
    error_message = "file_settings may not set a PACKMOAT_DB setting: the database settings are the template's (app_database_secret)."
  }

  validation {
    condition     = alltrue([for k in keys(var.file_settings) : !endswith(k, "_FILE")])
    error_message = "file_settings takes each setting's own name; the template adds _FILE."
  }

  validation {
    condition     = alltrue([for v in values(var.file_settings) : can(regex("^[A-Za-z0-9_-]{1,255}$", v))])
    error_message = "file_settings values must be secret ids: letters, digits, hyphens and underscores."
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
    error_message = "settings may not set a PACKMOAT_DB setting: the database settings are the template's (app_database_secret)."
  }

  validation {
    condition     = alltrue([for k in keys(var.settings) : !endswith(k, "_FILE")])
    error_message = "settings may not set a _FILE setting: use file_settings."
  }
}

variable "min_instances" {
  description = "The fewest copies of the server, from 0 to max_instances. 0 (the default) lets the service stop when idle: the first request then waits for a copy to start. More than 0 is billed while idle."
  type        = number
  default     = 0

  validation {
    condition     = var.min_instances >= 0 && floor(var.min_instances) == var.min_instances
    error_message = "min_instances must be a whole number, 0 or more (and at most max_instances)."
  }
}

variable "max_instances" {
  description = "The most copies of the server. On PostgreSQL more than one may run: budget database connections for it (docs/operations.md)."
  type        = number
  default     = 1

  validation {
    condition     = var.max_instances >= 1 && var.max_instances <= 100 && floor(var.max_instances) == var.max_instances
    error_message = "max_instances must be a whole number from 1 to 100."
  }
}

variable "cpu" {
  description = "CPU for each copy of the server."
  type        = string
  default     = "1"
}

variable "memory" {
  description = "Memory for each copy of the server."
  type        = string
  default     = "1Gi"
}

variable "trusted_proxies" {
  description = "PACKMOAT_TRUSTED_PROXIES: the addresses Cloud Run's front end reaches the container from. The default, 169.254.0.0/16, is not yet verified on a real deploy (docs/deploy/cloud-run.md)."
  type        = string
  default     = "169.254.0.0/16"

  validation {
    condition     = trimspace(var.trusted_proxies) != ""
    error_message = "trusted_proxies may not be empty."
  }

  validation {
    condition     = alltrue([for p in split(" ", replace(var.trusted_proxies, ",", " ")) : !contains(["0.0.0.0/0", "::/0"], p)])
    error_message = "trusted_proxies may not hold a network of everything (0.0.0.0/0 or ::/0), between spaces or commas: any client could choose its address."
  }
}

variable "enable_static_egress" {
  description = "A VPC, a subnet, a Cloud Router, Cloud NAT and a static external address, so that the server's outbound traffic comes from one fixed address: needed only when your customers allowlist the address Packmoat reads their feeds from. It costs money every month."
  type        = bool
  default     = false
}

variable "egress_subnet_cidr" {
  description = "With enable_static_egress: the subnet the server's Direct VPC egress uses (at least /26)."
  type        = string
  default     = "10.40.0.0/26"

  validation {
    condition     = can(cidrhost(var.egress_subnet_cidr, 0)) && tonumber(split("/", var.egress_subnet_cidr)[1]) <= 26
    error_message = "egress_subnet_cidr must be an IPv4 network of /26 or larger."
  }
}

variable "retention_schedule" {
  description = "When Cloud Scheduler starts the daily retention job, in cron form."
  type        = string
  default     = "17 3 * * *"

  validation {
    condition     = length(split(" ", var.retention_schedule)) == 5
    error_message = "retention_schedule must be a cron schedule of five fields."
  }
}

variable "retention_time_zone" {
  description = "The time zone of retention_schedule."
  type        = string
  default     = "Etc/UTC"
}
