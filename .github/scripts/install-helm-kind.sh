#!/usr/bin/env bash
# Install pinned releases of helm and kind (#249) into a folder, each checked by sha256 before it is used: the versions and checksums are
# pinned here, never read from the download site at run time.
# Usage: install-helm-kind.sh <folder>   (Linux x86-64, or macOS on Apple silicon for a local run; then put the folder on PATH)
set -euo pipefail

helm_version=3.22.1
kind_version=0.33.0

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)
    platform=linux-amd64
    helm_sha256=896cf63684a42a723d61c980369d6e588330927deb13676ca0d85006dd6da0ea
    kind_sha256=aee6151561422756b764a4ae28e7f44cda5af5a9eead3cc9985112b1de8d8e0d
    ;;
  Darwin-arm64)
    platform=darwin-arm64
    helm_sha256=e73da8a0b0d5193774fdff17ce6319c7436bb22ee229b45fd64f3c40368c753a
    kind_sha256=0c8c7dbe5e23594a198b786c4bc13dacc101fa6196b0cb0b23a1ca44e61f4b4f
    ;;
  *) echo "install-helm-kind.sh: no pinned checksums for $(uname -s)-$(uname -m)" >&2; exit 1 ;;
esac

if [[ $# -ne 1 ]]; then echo "Usage: install-helm-kind.sh <folder>" >&2; exit 2; fi
bin="$1"
mkdir -p "$bin"

# sha256sum on Linux, shasum on macOS.
check() { if command -v sha256sum > /dev/null; then sha256sum --check --strict -; else shasum -a 256 --check --strict -; fi; }

dir="$(mktemp -d)"
trap 'rm -rf "$dir"' EXIT

curl -fsSL --retry 3 -o "$dir/helm.tar.gz" "https://get.helm.sh/helm-v${helm_version}-${platform}.tar.gz"
echo "${helm_sha256}  $dir/helm.tar.gz" | check
tar -xzf "$dir/helm.tar.gz" -C "$dir" "${platform}/helm"
install -m 755 "$dir/${platform}/helm" "$bin/helm"

curl -fsSL --retry 3 -o "$dir/kind" "https://github.com/kubernetes-sigs/kind/releases/download/v${kind_version}/kind-${platform}"
echo "${kind_sha256}  $dir/kind" | check
install -m 755 "$dir/kind" "$bin/kind"

"$bin/helm" version --short
"$bin/kind" version
