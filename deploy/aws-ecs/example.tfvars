# An example: copy it, set your values, then tofu plan -var-file=<your copy> (docs/deploy/aws-ecs.md).
account_id                = "123456789012"
region                    = "eu-west-1"
service_image             = "ghcr.io/packmoat/packmoat-core@sha256:0000000000000000000000000000000000000000000000000000000000000000"
job_image                 = "ghcr.io/packmoat/packmoat-core@sha256:0000000000000000000000000000000000000000000000000000000000000000"
hosts                     = "packages.example.com"
vpc_id                    = "vpc-0123456789abcdef0"
public_subnet_ids         = ["subnet-0123456789abcdef0", "subnet-0123456789abcdef1"]
public_subnet_cidrs       = ["10.0.0.0/24", "10.0.1.0/24"]
database_cidr             = "10.0.20.0/24"
certificate_arn           = "arn:aws:acm:eu-west-1:123456789012:certificate/00000000-0000-0000-0000-000000000000"
master_key_secret_arn     = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/master-key-AbCdEf"
app_database_secret_arn   = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-app-AbCdEf"
owner_database_secret_arn = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-owner-AbCdEf"
database_ca_secret_arn    = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:packmoat/db-ca-AbCdEf"
settings = {
  PACKMOAT_SUPPORT_EMAIL = "support@example.com"
}
