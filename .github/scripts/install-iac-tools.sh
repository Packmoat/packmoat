#!/usr/bin/env bash
# Install the pinned tools the platform templates are checked with into a folder, each checked by sha256 before it is used: the versions
# and checksums are pinned here, never read from the download site at run time. Today that is bicep (deploy/azure-container-apps) and,
# for the Google Cloud Run template, OpenTofu, tflint with its Google ruleset (in tflint-plugins/, for TFLINT_PLUGIN_DIR; never
# `tflint --init`) and the Google provider zip, unextracted (in tofu-providers/, a packed layout for `tofu init -plugin-dir`); the AWS template adds the AWS provider zip
# the same way and tflint's AWS ruleset.
# At each version bump, verify the project's checksum file by hand before pinning a checksum from it: the GPG signature for HashiCorp (the AWS and
# Google providers) and OpenTofu; for the tflint rulesets (their GPG key expired in 2023) the GitHub release attestation, with
# `gh release verify-asset <tag> <file> -R terraform-linters/<repo>`.
# Usage: install-iac-tools.sh <folder>   (Linux x86-64, or macOS on Apple silicon for a local run; then put the folder on PATH)
set -euo pipefail

bicep_version=0.49.2
tofu_version=1.13.9
tflint_version=0.66.0
ruleset_version=0.41.0
google_version=8.11.0
aws_version=6.73.0
aws_ruleset_version=0.50.0

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)
    platform=linux_amd64
    bicep_asset=bicep-linux-x64
    bicep_sha256=0ab9de90c2493f206f8281bae74f9ee2d5ed3605548dab1adf41fef185e1f7c5
    tofu_sha256=b34b327cc1ef07178207b6f2cd1f5276537d3e5b7be316500f6dac50d4c0a857
    tflint_sha256=22be11cadc25cd02401e94c0ccc91aa63467debb8485c2a755d98f4c0a9d1e2e
    ruleset_sha256=efb3910bbc9f0b90bebd76d475ca6cd2cf04a032f6cddc8fa7a334e5011da6d6
    google_sha256=7bdfabf1da462eacceb2d36a8331b35ce70a5f6e4538a14097c47d8958c4067d
    aws_sha256=2ba661a4a782fba053c411ce4d828dc148d7ed3998d073a28370a559400b1578
    aws_ruleset_sha256=cdd433fdbc0929e7b9f9709dd9e72237fdfe1fc235674445c284b1e4b94a5c26
    ;;
  Darwin-arm64)
    platform=darwin_arm64
    bicep_asset=bicep-osx-arm64
    bicep_sha256=8ae74a6cf9cbba0cb220c40cf0e955f98d59df950afab07c21b7bcb78c9c5d68
    tofu_sha256=496f4c3064b5c3bff79174b3dae4faaf65a66774eb8e2bf074f895412c5f124f
    tflint_sha256=859b26af07ead7e65cee7eec9d050b2e2571dc28395b7a39191cae088cd67803
    ruleset_sha256=bcfaa5613f70632c5a04540775c88552d5ab9ba5c2bbb5e46fc03b4be3ae630a
    google_sha256=8e2017664ccf3fd6ee45c0e7e2b6351ca3c5e812387b3a679f1adc54d640ffef
    aws_sha256=9d086b792598b43e87502d47c4a3f388465b3a21a6a6b2783e48c89dee1f0543
    aws_ruleset_sha256=0117f75027cbc1bc452db1defd59d40b4a579ef3e7bb3a8b3079eaf6d372a9a6
    ;;
  *) echo "install-iac-tools.sh: no pinned checksums for $(uname -s)-$(uname -m)" >&2; exit 1 ;;
esac

if [[ $# -ne 1 ]]; then echo "Usage: install-iac-tools.sh <folder>" >&2; exit 2; fi
bin="$1"
mkdir -p "$bin"

# sha256sum on Linux, shasum on macOS.
check() { if command -v sha256sum > /dev/null; then sha256sum --check --strict -; else shasum -a 256 --check --strict -; fi; }

dir="$(mktemp -d)"
trap 'rm -rf "$dir"' EXIT

# fetch <file> <url> <sha256>: download into $dir, and stop unless the sha256 matches.
fetch() {
  curl -fsSL --retry 6 --retry-all-errors --retry-max-time 120 -o "$dir/$1" "$2"
  echo "$3  $dir/$1" | check
}

# OpenTofu (tofu), tflint and its Google ruleset are archives of one binary each; the ruleset goes where TFLINT_PLUGIN_DIR finds it.
fetch tofu.tar.gz "https://github.com/opentofu/opentofu/releases/download/v${tofu_version}/tofu_${tofu_version}_${platform}.tar.gz" "${tofu_sha256}"
mkdir "$dir/tofu-x"
tar -xzf "$dir/tofu.tar.gz" -C "$dir/tofu-x" tofu
install -m 755 "$dir/tofu-x/tofu" "$bin/tofu"

fetch tflint.zip "https://github.com/terraform-linters/tflint/releases/download/v${tflint_version}/tflint_${platform}.zip" "${tflint_sha256}"
unzip -q "$dir/tflint.zip" tflint -d "$dir/tflint-x"
install -m 755 "$dir/tflint-x/tflint" "$bin/tflint"

fetch ruleset.zip "https://github.com/terraform-linters/tflint-ruleset-google/releases/download/v${ruleset_version}/tflint-ruleset-google_${platform}.zip" "${ruleset_sha256}"
unzip -q "$dir/ruleset.zip" tflint-ruleset-google -d "$dir/ruleset-x"
mkdir -p "$bin/tflint-plugins/github.com/terraform-linters/tflint-ruleset-google/${ruleset_version}"
install -m 755 "$dir/ruleset-x/tflint-ruleset-google" "$bin/tflint-plugins/github.com/terraform-linters/tflint-ruleset-google/${ruleset_version}/tflint-ruleset-google"

# The Google provider stays a zip, under its release file name: the packed layout of a -plugin-dir.
fetch google.zip "https://releases.hashicorp.com/terraform-provider-google/${google_version}/terraform-provider-google_${google_version}_${platform}.zip" "${google_sha256}"
mkdir -p "$bin/tofu-providers/registry.opentofu.org/hashicorp/google"
install -m 644 "$dir/google.zip" "$bin/tofu-providers/registry.opentofu.org/hashicorp/google/terraform-provider-google_${google_version}_${platform}.zip"

# The AWS provider is kept the same way (a zip under its release file name), and the AWS ruleset goes in its own version folder.
fetch aws.zip "https://releases.hashicorp.com/terraform-provider-aws/${aws_version}/terraform-provider-aws_${aws_version}_${platform}.zip" "${aws_sha256}"
mkdir -p "$bin/tofu-providers/registry.opentofu.org/hashicorp/aws"
install -m 644 "$dir/aws.zip" "$bin/tofu-providers/registry.opentofu.org/hashicorp/aws/terraform-provider-aws_${aws_version}_${platform}.zip"

fetch aws-ruleset.zip "https://github.com/terraform-linters/tflint-ruleset-aws/releases/download/v${aws_ruleset_version}/tflint-ruleset-aws_${platform}.zip" "${aws_ruleset_sha256}"
unzip -q "$dir/aws-ruleset.zip" tflint-ruleset-aws -d "$dir/aws-ruleset-x"
mkdir -p "$bin/tflint-plugins/github.com/terraform-linters/tflint-ruleset-aws/${aws_ruleset_version}"
install -m 755 "$dir/aws-ruleset-x/tflint-ruleset-aws" "$bin/tflint-plugins/github.com/terraform-linters/tflint-ruleset-aws/${aws_ruleset_version}/tflint-ruleset-aws"

# bicep is one self-contained binary, published on the project's GitHub releases.
fetch bicep "https://github.com/Azure/bicep/releases/download/v${bicep_version}/${bicep_asset}" "${bicep_sha256}"
install -m 755 "$dir/bicep" "$bin/bicep"

"$bin/bicep" --version
