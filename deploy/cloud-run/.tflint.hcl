# tflint, offline: the bundled terraform rules (all of them) and the Google ruleset, which install-iac-tools.sh puts where
# TFLINT_PLUGIN_DIR finds it (tflint never downloads it: no tflint --init). No deep check, which would call Google's APIs.
config {
  call_module_type = "none"
}

plugin "terraform" {
  enabled = true
  preset  = "all"
}

plugin "google" {
  enabled    = true
  version    = "0.41.0"
  source     = "github.com/terraform-linters/tflint-ruleset-google"
  deep_check = false
}
