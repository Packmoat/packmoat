#!/usr/bin/env bash
# Lint GitHub Actions workflows (and dependabot.yml) for security problems with a pinned zizmor, as a third check behind the workflow
# tests and actionlint: the release is downloaded by version and its sha256 checked before it runs. --offline: no network audits, so
# no token is needed and the result does not depend on the network. --no-config: a zizmor.yml in the repository (it would be read from the
# root) cannot turn an audit off; an inline ignore with a reason is the only way.
# Usage: zizmor.sh <workflow or dependabot files...>   (Linux x86-64; any finding fails the step)
set -euo pipefail

version=1.32.0
sha256=bb110cc765eca9cd7efb2e284a956be5974c60027b0970f16397d86208a9957b

dir="$(mktemp -d)"
trap 'rm -rf "$dir"' EXIT
# An optional cache folder (PACKMOAT_TOOL_CACHE) holds the release under its version and sha256. A cached file is copied into $dir and
# checked like a download; it is added to the cache only after that check, and the tool runs only from $dir.
cache="${PACKMOAT_TOOL_CACHE:-}"
cached="$cache/zizmor_${version}_${sha256}.tar.gz"
if [ -n "$cache" ] && [ -f "$cached" ]; then
  cp "$cached" "$dir/zizmor.tar.gz"
else
  curl -fsSL --retry 6 --retry-all-errors --retry-max-time 120 -o "$dir/zizmor.tar.gz" \
    "https://github.com/zizmorcore/zizmor/releases/download/v${version}/zizmor-x86_64-unknown-linux-gnu.tar.gz"
fi
echo "${sha256}  $dir/zizmor.tar.gz" | sha256sum --check --strict -
if [ -n "$cache" ] && [ ! -e "$cached" ] && [ ! -L "$cached" ]; then
  mkdir -p "$cache"
  cp "$dir/zizmor.tar.gz" "$cached"
fi
tar -xzf "$dir/zizmor.tar.gz" -C "$dir" zizmor
"$dir/zizmor" --offline --no-config "$@"
