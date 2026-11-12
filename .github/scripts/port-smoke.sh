#!/usr/bin/env bash
# PORT smoke: proves a built image follows PORT. CI runs it after image-smoke.sh.
#   .github/scripts/port-smoke.sh <image> <port>      e.g.  packmoat-core:ci 9090
# 1. With PORT=<port> and nothing else about addresses, /healthz answers 200 on <port> from outside the container.
# 2. Nothing listens on 8080, the default, inside the container.
# 3. The image's own HEALTHCHECK (run every 2 seconds here) reports healthy, so it probes <port> too.
# 4. /readyz answers "ready" on <port> (a fresh database, migrated, read with the master key that made it).
# 5. PORT=0 stops the server at start, with an error that names PORT. 6. With no master key the server stops at start too, saying so.
# Steps 5 and 6 run with no init (no --init), as a bare docker run does: the server must exit on its own within seconds, not hang as the
# container's first process.
set -euo pipefail
image="${1:?usage: port-smoke.sh <image> <port>}"
port="${2:?usage: port-smoke.sh <image> <port>}"

key="$(docker run --rm --entrypoint packmoat-admin "${image}" keygen)"
name="port-smoke-$$"
body="$(mktemp)"
# On any exit: the container's log when the smoke failed, then remove the container and the body file. The exit status is kept.
trap 'status=$?; if (( status != 0 )); then docker logs "${name}" >&2 2>&1 || true; fi; docker rm -f "${name}" > /dev/null 2>&1 || true; rm -f "${body}"' EXIT

docker run -d --name "${name}" -p "127.0.0.1::${port}" --health-interval=2s -e PORT="${port}" -e PACKMOAT_MASTER_KEY="${key}" \
  -e PACKMOAT_HOSTS=feeds.example.com -e PACKMOAT_PUBLIC_BASE_URL=https://feeds.example.com "${image}" > /dev/null
published="$(docker port "${name}" "${port}/tcp" | head -n 1)"
published="${published##*:}"

ok=""
for _ in $(seq 1 60); do
  code="$(curl -s -o "${body}" -w '%{http_code}' "http://127.0.0.1:${published}/healthz" || true)"
  if [[ "${code}" == "200" ]]; then ok=1; break; fi
  if [[ "$(docker inspect --format '{{.State.Running}}' "${name}")" != "true" ]]; then
    echo "the container exited before /healthz answered on ${port}" >&2
    exit 1
  fi
  sleep 1
done
if [[ -z "${ok}" ]]; then echo "/healthz did not answer 200 on ${port} within 60 seconds (last: ${code})" >&2; exit 1; fi
echo "PORT=${port}: /healthz 200 $(cat "${body}")"

if docker exec "${name}" bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080' 2> /dev/null; then
  echo "something listens on 8080 inside the container, with PORT=${port}" >&2
  exit 1
fi
echo "PORT=${port}: nothing listens on 8080"

ready="$(curl -s "http://127.0.0.1:${published}/readyz" || true)"
if [[ "${ready}" != "ready" ]]; then echo "/readyz said '${ready}' on ${port}, expected 'ready'" >&2; exit 1; fi
echo "PORT=${port}: /readyz ready"

health=""
for _ in $(seq 1 30); do
  health="$(docker inspect --format '{{.State.Health.Status}}' "${name}")"
  if [[ "${health}" == "healthy" ]]; then break; fi
  sleep 1
done
if [[ "${health}" != "healthy" ]]; then echo "the image's HEALTHCHECK says ${health} with PORT=${port}" >&2; exit 1; fi
echo "PORT=${port}: HEALTHCHECK healthy"

docker rm -f "${name}" > /dev/null

# A start that must fail: runs the image with no init and the given settings, and checks that the container exits 1 within 15 seconds
# with the given error line in its log. Left unhandled, a start error ends .NET with an abort, which the kernel ignores for a container's
# first process: the container would keep running and fail this check.
stops_at_start() {
  local what="$1" line="$2"
  shift 2
  docker run -d --name "${name}" "$@" "${image}" > /dev/null
  for _ in $(seq 1 15); do
    if [[ "$(docker inspect --format '{{.State.Running}}' "${name}")" != "true" ]]; then break; fi
    sleep 1
  done
  local running status logs
  running="$(docker inspect --format '{{.State.Running}}' "${name}")"
  status="$(docker inspect --format '{{.State.ExitCode}}' "${name}")"
  logs="$(docker logs "${name}" 2>&1)"
  if [[ "${running}" == "true" || "${status}" != "1" || "${logs}" != *"${line}"* ]]; then
    echo "${what} did not stop the server at start within 15 seconds, with exit 1 and '${line}' (running ${running}, exit ${status})" >&2
    exit 1
  fi
  echo "${what}: the server stops at start (exit ${status}): ${line}"
  docker rm -f "${name}" > /dev/null
}

stops_at_start "PORT=0" "error: PORT must be a whole number from 1 to 65535." \
  -e PORT=0 -e PACKMOAT_MASTER_KEY="${key}" -e PACKMOAT_HOSTS=feeds.example.com
stops_at_start "No master key" "error: PACKMOAT_MASTER_KEY is not set." \
  -e PACKMOAT_HOSTS=feeds.example.com -e PACKMOAT_PUBLIC_BASE_URL=https://feeds.example.com
