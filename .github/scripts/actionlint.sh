#!/usr/bin/env bash
# Lint GitHub Actions workflows with a pinned actionlint, as a second check behind the workflow tests: the release is downloaded by
# version and its sha256 checked before it runs. actionlint also runs shellcheck on every run: script (shellcheck is on the runner image).
# Usage: actionlint.sh <workflow files...>   (Linux x86-64; any finding fails the step)
set -euo pipefail

version=1.7.12
sha256=8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8

dir="$(mktemp -d)"
trap 'rm -rf "$dir"' EXIT
curl -fsSL --retry 3 -o "$dir/actionlint.tar.gz" \
  "https://github.com/rhysd/actionlint/releases/download/v${version}/actionlint_${version}_linux_amd64.tar.gz"
echo "${sha256}  $dir/actionlint.tar.gz" | sha256sum --check --strict -
tar -xzf "$dir/actionlint.tar.gz" -C "$dir" actionlint
"$dir/actionlint" "$@"
