#!/usr/bin/env bash
# Runs the PostgreSQL tests on this machine, against the same image CI runs beside its test jobs:
#
#   dotnet/tools/postgres-tests.sh [more dotnet test arguments]
#
# Starts a throwaway container on 127.0.0.1:55432, waits up to 120 seconds for it to accept connections, points PACKMOAT_TEST_POSTGRES
# at it and runs `dotnet test --filter Category=Postgres` over dotnet/Packmoat.sln. The container is stopped (and so removed) on exit,
# whether the tests pass or not. DOTNET names the dotnet command to use (default: dotnet). Needs Docker.
#
# Without PACKMOAT_TEST_POSTGRES a plain dotnet test skips these tests (and fails them in CI, where CI=true).
set -euo pipefail

# The official image's multi-platform index digest; the same as every workflow's postgres service (CoreWorkflowTests keeps them equal).
image="postgres:17.11@sha256:ff87138949ad9a46f32ff23bd6852b52ce024a13dffe9b4087be98292cdd3b4f"
port=55432
password="pm-local-throwaway"   # gitleaks:allow
dotnet_dir="$(cd "$(dirname "$0")/.." && pwd)"

container="$(docker run --rm -d -p "127.0.0.1:${port}:5432" -e POSTGRES_PASSWORD="$password" "$image")"
trap 'docker stop "$container" > /dev/null' EXIT
trap 'exit 130' INT TERM

# -h 127.0.0.1: the image's first start runs a server on its socket only, then restarts it; TCP answers only once it is really up.
ready=
for _ in $(seq 1 120); do
  if docker exec "$container" pg_isready -h 127.0.0.1 -U postgres > /dev/null 2>&1; then ready=1; break; fi
  sleep 1
done
[ -n "$ready" ] || { echo "postgres-tests.sh: the PostgreSQL container did not accept connections within 120 seconds." >&2; exit 1; }

export PACKMOAT_TEST_POSTGRES="Host=127.0.0.1;Port=${port};Database=postgres;Username=postgres;Password=${password}"   # gitleaks:allow
"${DOTNET:-dotnet}" test "$dotnet_dir/Packmoat.sln" --filter Category=Postgres "$@"
