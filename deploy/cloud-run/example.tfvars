# An example: copy it, set your values, then tofu plan -var-file=<your copy> (docs/deploy/cloud-run.md).
project_id         = "packmoat-example"
region             = "europe-west1"
service_image      = "ghcr.io/packmoat/packmoat-core@sha256:0000000000000000000000000000000000000000000000000000000000000000"
job_image          = "ghcr.io/packmoat/packmoat-core@sha256:0000000000000000000000000000000000000000000000000000000000000000"
hosts              = "packages.example.com"
cloud_sql_instance = "packmoat-example:europe-west1:packmoat"
settings = {
  PACKMOAT_SUPPORT_EMAIL = "support@example.com"
}
