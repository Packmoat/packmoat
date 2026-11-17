# tflint, offline: the bundled terraform rules (all of them) and the AWS ruleset, which install-iac-tools.sh puts where TFLINT_PLUGIN_DIR
# finds it (tflint never downloads it: no tflint --init). No deep check, which would call AWS's APIs.
config {
  call_module_type = "none"
}

plugin "terraform" {
  enabled = true
  preset  = "all"
}

plugin "aws" {
  enabled    = true
  version    = "0.50.0"
  source     = "github.com/terraform-linters/tflint-ruleset-aws"
  deep_check = false
}
