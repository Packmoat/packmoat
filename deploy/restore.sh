#!/usr/bin/env bash
# Restore the Packmoat database from a backup. This REPLACES the live database (the old file is kept beside it, WAL included).
#   usage: restore.sh <backup.db.age | backup.db>
# For an encrypted backup set AGE_IDENTITY to the private key file (fetched from offline storage for this one task).
# The master key in /etc/packmoat/packmoat.env must be the SAME key the backup was made under; the check below refuses a mismatch.
set -euo pipefail
umask 077

file="${1:?usage: restore.sh <backup.db.age|backup.db>}"
DATA_DIR="${DATA_DIR:-/var/lib/packmoat}"
COMPOSE_FILE="${COMPOSE_FILE:-/opt/packmoat/deploy/docker-compose.yml}"
ENV_FILE="${ENV_FILE:-/etc/packmoat/packmoat.env}"
IMAGE="${IMAGE:-packmoat:local}"
candidate="${DATA_DIR}/backups/restore-candidate.db"

# The container gets exactly one setting: the master key, passed by NAME (-e NAME) so the value is never on a command line.
# The rest of the environment file (hosts, anything added later) is deliberately not forwarded.
PACKMOAT_MASTER_KEY="$(grep -m1 '^PACKMOAT_MASTER_KEY=' "${ENV_FILE}" | cut -d= -f2-)"
[ -n "${PACKMOAT_MASTER_KEY}" ] || { echo "PACKMOAT_MASTER_KEY is not set in ${ENV_FILE}" >&2; exit 1; }
export PACKMOAT_MASTER_KEY

cleanup() { rm -f "${candidate}" "${candidate}-wal" "${candidate}-shm" "${candidate}-journal"; }
trap cleanup EXIT

if [[ "${file}" == *.age ]]; then
  age -d -i "${AGE_IDENTITY:?set AGE_IDENTITY to the private key file}" -o "${candidate}" "${file}"
else
  cp "${file}" "${candidate}"
fi
chown 10001:10001 "${candidate}"

run_admin() {
  docker run --rm --network none -v "${DATA_DIR}:/data" -e PACKMOAT_MASTER_KEY \
    --user 10001:10001 --read-only --cap-drop ALL --security-opt no-new-privileges:true \
    --entrypoint packmoat-admin "${IMAGE}" "$@"
}

echo "Checking the backup (integrity, schema and master key) before touching anything..."
run_admin verify /data/backups/restore-candidate.db   # exits non-zero unless integrity is ok and the key matches

echo "Stopping the server..."
docker compose -f "${COMPOSE_FILE}" stop packmoat

echo "Restoring (the current database is kept as packmoat.db.before-restore-*)..."
run_admin restore /data/backups/restore-candidate.db --force

echo "Starting the server..."
docker compose -f "${COMPOSE_FILE}" start packmoat
echo "Restore complete. Check:  docker exec packmoat packmoat-admin tenant list"
