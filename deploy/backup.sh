#!/usr/bin/env bash
# Daily encrypted, off-box backup of the Packmoat database.
# Runs on the server (the systemd timer). Needs: docker, age, rclone with a configured remote.
# Settings come from /etc/packmoat/backup.env (root-only):
#   BACKUP_AGE_RECIPIENT=age1...   public key; the matching private key is kept OFFLINE, never on the server
#   BACKUP_REMOTE=remote:bucket/packmoat
#   KEEP_REMOTE=7 and RETAIN_DAYS=30 (optional): off-box backups older than RETAIN_DAYS are deleted, but the KEEP_REMOTE newest never are
# Whatever happens, no plaintext copy of the database (nor a -wal, -shm or -journal companion) is left behind.
set -euo pipefail
umask 077

: "${BACKUP_AGE_RECIPIENT:?set BACKUP_AGE_RECIPIENT in /etc/packmoat/backup.env}"
: "${BACKUP_REMOTE:?set BACKUP_REMOTE in /etc/packmoat/backup.env}"
CONTAINER="${CONTAINER:-packmoat}"
DATA_DIR="${DATA_DIR:-/var/lib/packmoat}"
KEEP_LOCAL="${KEEP_LOCAL:-3}"
KEEP_REMOTE="${KEEP_REMOTE:-7}"
RETAIN_DAYS="${RETAIN_DAYS:-30}"

[[ "${KEEP_LOCAL}" =~ ^[0-9]+$ ]] || { echo "KEEP_LOCAL must be a number" >&2; exit 1; }
# Both from 1 to 9999, with no leading zero (bash would read 08 as a bad octal number).
[[ "${KEEP_REMOTE}" =~ ^[1-9][0-9]{0,3}$ ]] || { echo "KEEP_REMOTE must be a whole number from 1 to 9999; nothing was backed up" >&2; exit 1; }
[[ "${RETAIN_DAYS}" =~ ^[1-9][0-9]{0,3}$ ]] || { echo "RETAIN_DAYS must be a whole number from 1 to 9999; nothing was backed up" >&2; exit 1; }
# An age X25519 public key: "age1" then 58 characters of the bech32 alphabet. Check the shape, then make age itself accept it.
[[ "${BACKUP_AGE_RECIPIENT}" =~ ^age1[qpzry9x8gf2tvdw0s3jn54khce6mua7l]{58}$ ]] \
  || { echo "BACKUP_AGE_RECIPIENT is not an age public key (it must start with age1 and be 62 characters); nothing was backed up" >&2; exit 1; }
printf 'probe' | age -r "${BACKUP_AGE_RECIPIENT}" > /dev/null \
  || { echo "age did not accept BACKUP_AGE_RECIPIENT; nothing was backed up" >&2; exit 1; }

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
name="packmoat-${stamp}.db"
plain="${DATA_DIR}/backups/${name}"
partial="${plain}.age.partial"

cleanup() {
  rm -f "${plain}" "${plain}-wal" "${plain}-shm" "${plain}-journal" "${partial}"
}
trap cleanup EXIT

# 1. Consistent online copy, verified inside the container (safe while the server is running).
docker exec "${CONTAINER}" packmoat-admin backup "/data/backups/${name}"
[ -s "${plain}" ] || { echo "the backup file was not created" >&2; exit 1; }

# 2. Encrypt to a partial file, and only a complete one gets its real name.
age -r "${BACKUP_AGE_RECIPIENT}" -o "${partial}" "${plain}"
mv "${partial}" "${plain}.age"
rm -f "${plain}" "${plain}-wal" "${plain}-shm" "${plain}-journal"

# 3. Upload, then prove the uploaded bytes match what we encrypted. The encrypted copy stays on the server if this fails.
rclone copyto "${plain}.age" "${BACKUP_REMOTE}/${name}.age"
local_sum="$(sha256sum "${plain}.age" | cut -d' ' -f1)"
remote_sum="$(rclone cat "${BACKUP_REMOTE}/${name}.age" | sha256sum | cut -d' ' -f1)"
if [[ "${local_sum}" != "${remote_sum}" ]]; then
  echo "backup upload does not match the local copy; keeping the encrypted local file" >&2
  exit 1
fi

# 4. Keep only the newest few encrypted copies on the server.
# shellcheck disable=SC2012
ls -1t "${DATA_DIR}"/backups/packmoat-*.db.age 2>/dev/null | tail -n +"$((KEEP_LOCAL + 1))" | xargs -r rm -f

echo "backup ok: ${name}.age (${local_sum})"

# 5. Delete off-box backups older than RETAIN_DAYS, but never the KEEP_REMOTE newest. Only reached after a verified upload.
# The age comes from the date in the file name, never the bucket's modification time. Only names this script makes are touched
# (manual copies and anything else are never deleted), and each is deleted by its exact name. Any failure makes the run exit 3:
# the backup itself is fine, but systemd shows the unit as failed.
# Called as `prune_remote || ...`, so `set -e` is off inside: every step checks its own result.
prune_remote() {
  local listing epoch cutoff f digits seen=0 pruned=0 failed=0
  if ! listing="$(rclone lsf --files-only "${BACKUP_REMOTE}")"; then
    echo "could not list the off-box backups" >&2
    return 1
  fi
  if ! grep -qxF "${name}.age" <<<"${listing}"; then
    echo "the off-box listing does not show ${name}.age, which was just uploaded; pruning nothing" >&2
    return 1
  fi
  epoch=$(( $(date -u +%s) - RETAIN_DAYS * 86400 )) || return 1
  # GNU date (the server) takes -d @epoch; BSD date (macOS) takes -r epoch.
  cutoff="$(date -u -d "@${epoch}" +%Y%m%d%H%M%S 2>/dev/null || date -u -r "${epoch}" +%Y%m%d%H%M%S)" || return 1
  [[ "${cutoff}" =~ ^[0-9]{14}$ ]] || { echo "could not work out the pruning cutoff date" >&2; return 1; }
  # The fixed-width stamp sorts by time, so newest first is a reverse sort.
  while IFS= read -r f; do
    [[ "${f}" =~ ^packmoat-[0-9]{8}T[0-9]{6}Z\.db\.age$ ]] || continue
    seen=$((seen + 1))
    if (( seen <= KEEP_REMOTE )); then continue; fi
    digits="${f:9:8}${f:18:6}"                      # packmoat-YYYYMMDDTHHMMSSZ.db.age -> YYYYMMDDHHMMSS
    # Delete only when the comparison itself succeeds and says older: an arithmetic error means no delete.
    if (( 10#${digits} < 10#${cutoff} )); then
      if rclone deletefile "${BACKUP_REMOTE}/${f}" < /dev/null; then
        echo "pruned off-box backup: ${f}"
        pruned=$((pruned + 1))
      else
        echo "could not delete off-box backup: ${f}" >&2
        failed=1
      fi
    fi
  done <<<"$(LC_ALL=C sort -r <<<"${listing}")"
  echo "off-box backups: pruned ${pruned} older than ${RETAIN_DAYS} days; the ${KEEP_REMOTE} newest are always kept"
  return "${failed}"
}
if ! prune_remote; then
  echo "backup ok, but pruning off-box backups failed" >&2
  exit 3
fi
