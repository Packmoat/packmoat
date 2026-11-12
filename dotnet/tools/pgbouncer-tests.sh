#!/usr/bin/env bash
# Runs the opt-in PgBouncer test on this machine: the start's pooler probe against a real PgBouncer in transaction mode.
#
#   dotnet/tools/pgbouncer-tests.sh [more dotnet test arguments]
#
# Starts, on a throwaway Docker network, the PostgreSQL image CI uses (127.0.0.1:55432) and in front of it PgBouncer in transaction mode
# (127.0.0.1:56432), each pinned by digest; points PACKMOAT_TEST_POSTGRES at the first and PACKMOAT_TEST_PGBOUNCER at the second, and
# runs the PgBouncer test. Both containers and the network are removed on exit, whether the test passes or not. DOTNET names the dotnet
# command to use (default: dotnet). Needs Docker. CI runs the same test beside a pgbouncer service on this image (a test keeps them equal).
set -euo pipefail

# The same PostgreSQL image as dotnet/tools/postgres-tests.sh and every workflow's postgres service; PgBouncer 1.26.1.
pg_image="postgres:17.11@sha256:ff87138949ad9a46f32ff23bd6852b52ce024a13dffe9b4087be98292cdd3b4f"
bouncer_image="edoburu/pgbouncer@sha256:8638aa563607256d3c7440437534195f1066a92b68015d89ae9d6b935b444a6d"
pg_port=55432
bouncer_port=56432
password="pm-local-throwaway"   # gitleaks:allow
dotnet_dir="$(cd "$(dirname "$0")/.." && pwd)"
network="pm-pgbouncer-$$"
pg="pm-pgbouncer-pg-$$"
bouncer="pm-pgbouncer-$$"

cleanup() {
  docker rm -f "$bouncer" "$pg" > /dev/null 2>&1 || true
  docker network rm "$network" > /dev/null 2>&1 || true
}
trap cleanup EXIT
trap 'exit 130' INT TERM

docker network create "$network" > /dev/null
docker run --rm -d --name "$pg" --network "$network" -p "127.0.0.1:${pg_port}:5432" -e POSTGRES_PASSWORD="$password" "$pg_image" > /dev/null
docker run --rm -d --name "$bouncer" --network "$network" -p "127.0.0.1:${bouncer_port}:5432" \
  -e DB_HOST="$pg" -e DB_USER=postgres -e DB_PASSWORD="$password" -e AUTH_TYPE=scram-sha-256 -e POOL_MODE=transaction \
  "$bouncer_image" > /dev/null

# PostgreSQL itself, then through PgBouncer (-h 127.0.0.1: the image's first start answers on its socket only, then restarts).
ready=
for _ in $(seq 1 120); do
  if docker exec "$pg" pg_isready -h 127.0.0.1 -U postgres > /dev/null 2>&1 \
    && docker exec "$pg" pg_isready -h "$bouncer" -p 5432 -U postgres > /dev/null 2>&1; then ready=1; break; fi
  sleep 1
done
[ -n "$ready" ] || { echo "pgbouncer-tests.sh: PostgreSQL or PgBouncer did not accept connections within 120 seconds." >&2; exit 1; }

export PACKMOAT_TEST_POSTGRES="Host=127.0.0.1;Port=${pg_port};Database=postgres;Username=postgres;Password=${password}"   # gitleaks:allow
export PACKMOAT_TEST_PGBOUNCER="Host=127.0.0.1;Port=${bouncer_port};Database=postgres;Username=postgres;Password=${password}"   # gitleaks:allow
"${DOTNET:-dotnet}" test "$dotnet_dir/tests/Packmoat.Infrastructure.Tests/Packmoat.Infrastructure.Tests.csproj" --filter "FullyQualifiedName~PgBouncer" "$@"
