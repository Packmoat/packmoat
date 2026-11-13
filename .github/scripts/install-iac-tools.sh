#!/usr/bin/env bash
# Install the pinned tools the platform templates are checked with into a folder, each checked by sha256 before it is used: the versions
# and checksums are pinned here, never read from the download site at run time. Today that is bicep (deploy/azure-container-apps).
# Usage: install-iac-tools.sh <folder>   (Linux x86-64, or macOS on Apple silicon for a local run; then put the folder on PATH)
set -euo pipefail

bicep_version=0.49.2

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)
    bicep_asset=bicep-linux-x64
    bicep_sha256=0ab9de90c2493f206f8281bae74f9ee2d5ed3605548dab1adf41fef185e1f7c5
    ;;
  Darwin-arm64)
    bicep_asset=bicep-osx-arm64
    bicep_sha256=8ae74a6cf9cbba0cb220c40cf0e955f98d59df950afab07c21b7bcb78c9c5d68
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

# bicep is one self-contained binary, published on the project's GitHub releases.
curl -fsSL --retry 6 --retry-all-errors --retry-max-time 120 -o "$dir/bicep" "https://github.com/Azure/bicep/releases/download/v${bicep_version}/${bicep_asset}"
echo "${bicep_sha256}  $dir/bicep" | check
install -m 755 "$dir/bicep" "$bin/bicep"

"$bin/bicep" --version
