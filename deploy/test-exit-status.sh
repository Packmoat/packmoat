#!/usr/bin/env bash
# The exit status of the three template test scripts (azure-container-apps, cloud-run, aws-ecs), checked without running any of them.
# Each has one EXIT trap that removes its work folder and keeps the script's exit status: a stop that is not an exit of the script's own
# (an unbound variable under set -u) must not end as success, which macOS's bash 3.2 would do with a trap that only removes the folder
# (there $? is already 0 inside the trap). The trap line is taken from each script and run in a small shell of its own, under
# set -euo pipefail, with the bash on PATH and with /bin/bash when there is one.
# Usage: deploy/test-exit-status.sh      exits 1 when any check fails, after running them all
# The texts run in the small shells are single-quoted on purpose (no SC2016 notes).
# shellcheck disable=SC2016
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
failed=0
ok() { echo "ok:   $1"; }
bad() { echo "FAIL: $1" >&2; failed=1; }

standard='trap '\''rc=$?; rm -rf "$work"; if [[ $rc -eq 0 && $finished -ne 1 ]]; then rc=1; fi; exit $rc'\'' EXIT'

shells=(bash)
if [[ -x /bin/bash ]]; then shells+=(/bin/bash); fi

# run_case <shell> <trap line> <script text>: the work folder, a trap and the text, in one shell under set -euo pipefail. Prints the exit
# status, then whether the work folder is gone.
run_case() {
  local sh="$1" trap_line="$2" text="$3" marker rc work
  marker="$(mktemp)"
  rc=0
  "$sh" -c 'set -euo pipefail; marker="$1"; work="$(mktemp -d)"; printf "%s" "$work" > "$marker"; finished=0; '"$trap_line"'; '"$text" _ "$marker" > /dev/null 2>&1 || rc=$?
  work="$(cat "$marker")"
  rm -f "$marker"
  if [[ -d "$work" ]]; then rm -rf "$work"; echo "$rc kept"; else echo "$rc removed"; fi
}

# expect <label> <shell> <trap line> <script text> <exit status, or "nonzero">
expect() {
  local label="$1" sh="$2" trap_line="$3" text="$4" want="$5" got
  got="$(run_case "$sh" "$trap_line" "$text")"
  if [[ "${got#* }" != "removed" ]]; then bad "$label ($sh): the work folder was left behind"; return 0; fi
  if [[ "$want" == "nonzero" && "${got%% *}" -ne 0 ]] || [[ "$want" != "nonzero" && "${got%% *}" == "$want" ]]; then ok "$label ($sh)"
  else bad "$label ($sh): exit status ${got%% *}, wanted $want"; fi
}

for script in azure-container-apps cloud-run aws-ecs; do
  file="$here/$script/test-template.sh"
  if [[ "$(grep -c '^trap ' "$file")" -eq 1 && "$(grep -cFx -- "$standard" "$file")" -eq 1 ]]; then ok "$script/test-template.sh has the one standard EXIT trap"
  else bad "$script/test-template.sh has the one standard EXIT trap"; fi
  # The flag: set to 0 on the line just before the trap, and to 1 only at the two success exits (a bash 3.2 stop is not run in CI, so
  # a flag that starts at 1 would pass everywhere else).
  if [[ "$(grep -B1 -Fx -- "$standard" "$file" | head -1)" == "finished=0" && "$(grep -c '^ *finished=' "$file")" -eq 3 \
    && "$(grep -c '^ *finished=1$' "$file")" -eq 2 ]]; then ok "$script/test-template.sh starts with finished=0 and sets finished=1 at its two success exits"
  else bad "$script/test-template.sh starts with finished=0 and sets finished=1 at its two success exits"; fi
done

# The trap, as the scripts have it, run under each shell.
for sh in "${shells[@]}"; do
  expect "an unbound variable at the top level exits non-zero" "$sh" "$standard" 'echo "$unset_variable"; exit 0' nonzero
  expect "an unbound variable in a function exits non-zero" "$sh" "$standard" 'f() { echo "$unset_variable"; }; f; exit 0' nonzero
  expect "finished=1 then exit 0 exits 0" "$sh" "$standard" 'finished=1; exit 0' 0
  expect "exit 1 exits 1" "$sh" "$standard" 'exit 1' 1
done

# The Azure script reads its --dir folder as $2: with none, it must stop with a non-zero status.
if "$here/azure-container-apps/test-template.sh" --dir > /dev/null 2>&1; then bad "azure-container-apps/test-template.sh --dir with no folder exits non-zero"
else ok "azure-container-apps/test-template.sh --dir with no folder exits non-zero"; fi

if [[ "$failed" -ne 0 ]]; then echo "exit status checks: FAILED" >&2; exit 1; fi
echo "exit status checks: all passed"
