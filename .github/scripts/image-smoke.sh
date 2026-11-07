#!/usr/bin/env bash
# Image smoke (#197): proves a built image runs the entry point it should. CI runs it on every image it builds.
#   .github/scripts/image-smoke.sh <image> <entry dll>      e.g.  packmoat-core:ci /app/host/Packmoat.Server.dll
# 1. docker inspect shows ENTRYPOINT ["dotnet", "<entry dll>"].
# 2. The image's own packmoat-admin wrapper runs: --help exits 0, and keygen makes the throwaway master key for step 3.
# 3. The image's ENTRYPOINT starts with minimal dummy settings (a fresh database in the container's /data) and /healthz answers 200.
# A wrong DLL name in the ENTRYPOINT or the wrapper fails here, before CD ever builds the image.
set -euo pipefail
image="${1:?usage: image-smoke.sh <image> <entry dll>}"
dll="${2:?usage: image-smoke.sh <image> <entry dll>}"

entrypoint="$(docker inspect --format '{{json .Config.Entrypoint}}' "${image}")"
expected="[\"dotnet\",\"${dll}\"]"
if [[ "${entrypoint}" != "${expected}" ]]; then
  echo "Entrypoint is ${entrypoint}, expected ${expected}" >&2
  exit 1
fi
echo "Entrypoint: ${entrypoint}"

docker run --rm --entrypoint packmoat-admin "${image}" --help > /dev/null
echo "packmoat-admin --help: exit 0"
key="$(docker run --rm --entrypoint packmoat-admin "${image}" keygen)"

name="image-smoke-$$"
# On any exit: the container's log when the smoke failed, then remove the container. The script's exit status is kept.
trap 'status=$?; if (( status != 0 )); then docker logs "${name}" >&2 2>&1 || true; fi; docker rm -f "${name}" > /dev/null 2>&1 || true' EXIT
# The image listens on 127.0.0.1 inside the container; for the probe from outside it listens on all of the container's interfaces.
docker run -d --name "${name}" -p 127.0.0.1::8080 -e ASPNETCORE_URLS=http://0.0.0.0:8080 -e PACKMOAT_MASTER_KEY="${key}" \
  -e PACKMOAT_HOSTS=feeds.example.com -e PACKMOAT_PUBLIC_BASE_URL=https://feeds.example.com "${image}" > /dev/null
port="$(docker port "${name}" 8080/tcp | head -n 1)"
port="${port##*:}"
for _ in $(seq 1 60); do
  code="$(curl -s -o /tmp/image-smoke-body -w '%{http_code}' "http://127.0.0.1:${port}/healthz" || true)"
  if [[ "${code}" == "200" ]]; then
    echo "/healthz: 200 $(cat /tmp/image-smoke-body)"
    exit 0
  fi
  if [[ "$(docker inspect --format '{{.State.Running}}' "${name}")" != "true" ]]; then
    echo "the container exited before /healthz answered" >&2
    exit 1
  fi
  sleep 1
done
echo "/healthz did not answer 200 within 60 seconds (last: ${code})" >&2
exit 1
