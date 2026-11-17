# The OpenTofu version and the one provider, each pinned exactly. .terraform.lock.hcl holds the provider's checksums for Linux x86-64 and
# macOS on Apple silicon; tofu init -lockfile=readonly refuses a provider file that matches none of them.
terraform {
  required_version = "= 1.13.9"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "= 6.73.0"
    }
  }
}
