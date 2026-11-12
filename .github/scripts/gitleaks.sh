#!/usr/bin/env bash
# Scan for committed secrets with a pinned gitleaks: the release is downloaded by version and its sha256 checked before it runs.
# --redact: a finding is shown by its rule, file and line, never by the secret itself, so a real leak is not copied into a public log.
# A false alarm is marked where it is, with an inline gitleaks:allow comment on its line, so each one is seen and reviewed on its own.
# Usage: gitleaks.sh git <repository>   (every commit; the checkout needs fetch-depth: 0)
#        gitleaks.sh dir <folder>       (the files as they are)
# (Linux x86-64; any finding fails the step)
set -euo pipefail

version=8.30.1
sha256=551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb

dir="$(mktemp -d)"
trap 'rm -rf "$dir"' EXIT
curl -fsSL --retry 3 -o "$dir/gitleaks.tar.gz" \
  "https://github.com/gitleaks/gitleaks/releases/download/v${version}/gitleaks_${version}_linux_x64.tar.gz"
echo "${sha256}  $dir/gitleaks.tar.gz" | sha256sum --check --strict -
tar -xzf "$dir/gitleaks.tar.gz" -C "$dir" gitleaks
"$dir/gitleaks" "$@" --redact --no-banner
