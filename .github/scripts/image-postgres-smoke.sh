#!/usr/bin/env bash
# Image PostgreSQL smoke: proves a built image runs on PostgreSQL 17 as production would. CI runs it after image-smoke.sh.
#   .github/scripts/image-postgres-smoke.sh <image>      e.g.  packmoat-core:ci
# 1. A throwaway PostgreSQL container (the image every workflow's postgres service pins) with an owner role, an app role and a database the
#    owner owns. The image's containers share its network, so the database is on 127.0.0.1 (a local host: no TLS needed).
#    The connection strings are files (mode 0444, in a folder mounted read-only) and each container is given them through PACKMOAT_DB_FILE,
#    as the container templates do. The image bakes PACKMOAT_DB (a SQLite path), which the server refuses beside PACKMOAT_DB_FILE, so every
#    container starts through /usr/bin/env -u PACKMOAT_DB, then the image's own entrypoint (read from the image) or packmoat-admin.
# 2. The image's packmoat-admin, as the owner role: db migrate --grant-to the app role.
# 3. Its packmoat-admin, as the app role: a tenant and a token (the schema checked, never migrated).
# 4. Its server, as the app role (PACKMOAT_DB_ENGINE=postgres, PACKMOAT_COOKIE_KEYS=database): the start's checks pass, /healthz and
#    /readyz answer 200, a request with a wrong token is refused (401) and the token from step 3 is not (the token read from PostgreSQL).
# 5. Its server as the app role without the env -u: it exits non-zero saying PACKMOAT_DB and PACKMOAT_DB_FILE are both set (the case the
#    override exists for).
# 6. Its server, as the database's superuser: the start's checks refuse it, and it exits non-zero, saying so, without serving.
# Every container is removed on exit, whatever happened, and the server's log is shown when the smoke failed.
set -euo pipefail
image="${1:?usage: image-postgres-smoke.sh <image>}"

# The official image's multi-platform index digest; the same as every workflow's postgres service (CoreWorkflowTests keeps them equal).
postgres="postgres:17.11@sha256:ff87138949ad9a46f32ff23bd6852b52ce024a13dffe9b4087be98292cdd3b4f"
# A throwaway password for this run's own container, which nothing outside this machine can reach.
password="pm-smoke-$(od -An -N8 -tx8 /dev/urandom | tr -d ' \n')"   # gitleaks:allow
db="image-pg-smoke-db-$$"
server="image-pg-smoke-$$"
secrets="$(mktemp -d)"
trap 'status=$?; if (( status != 0 )); then docker logs "${server}" >&2 2>&1 || true; fi; docker rm -f "${server}" "${db}" > /dev/null 2>&1 || true; rm -rf "${secrets}"' EXIT

# The server's port is published on the database's container: the server joins its network, where a container cannot publish its own.
docker run -d --rm --name "${db}" -p 127.0.0.1::8080 -e POSTGRES_PASSWORD="${password}" "${postgres}" > /dev/null   # gitleaks:allow
ready=""
for _ in $(seq 1 120); do
  if docker exec "${db}" pg_isready -h 127.0.0.1 -U postgres > /dev/null 2>&1; then ready=1; break; fi
  sleep 1
done
[[ -n "${ready}" ]] || { echo "the PostgreSQL container did not accept connections within 120 seconds" >&2; exit 1; }
owner_role="CREATE ROLE pm_owner LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '${password}'"   # gitleaks:allow
app_role="CREATE ROLE pm_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '${password}'"   # gitleaks:allow
superuser=(-e "PGPASSWORD=${password}")   # gitleaks:allow
docker exec "${superuser[@]}" "${db}" psql -q -v ON_ERROR_STOP=1 -h 127.0.0.1 -U postgres \
  -c "${owner_role}" -c "${app_role}" -c "CREATE DATABASE packmoat OWNER pm_owner"
echo "PostgreSQL: owner and app roles, and the database, made"

key="$(docker run --rm --entrypoint packmoat-admin "${image}" keygen)"
# Each role's connection string, as a file the container's user can read (the folder is world-readable, the files 0444, the mount read-only).
for role in pm_owner pm_app postgres; do
  printf 'Host=127.0.0.1;Database=packmoat;Username=%s;Password=%s' "${role}" "${password}" > "${secrets}/${role}"   # gitleaks:allow
  chmod 0444 "${secrets}/${role}"
done
chmod 0755 "${secrets}"
# The image's entrypoint, as the image declares it (the core image runs Packmoat.Server.dll, the hosted image its own host).
entrypoint=()
while IFS= read -r word; do [[ -n "${word}" ]] && entrypoint+=("${word}"); done \
  < <(docker image inspect --format '{{range .Config.Entrypoint}}{{println .}}{{end}}' "${image}")
(( ${#entrypoint[@]} > 0 )) || { echo "the image declares no entrypoint" >&2; exit 1; }
admin() {
  local role="$1"; shift
  docker run --rm --network "container:${db}" -v "${secrets}:/run/secrets/pm:ro" -e PACKMOAT_DB_ENGINE=postgres \
    -e PACKMOAT_DB_FILE="/run/secrets/pm/${role}" -e PACKMOAT_MASTER_KEY="${key}" \
    --entrypoint /usr/bin/env "${image}" -u PACKMOAT_DB packmoat-admin "$@"
}

migrated="$(admin pm_owner db migrate --grant-to pm_app)"
grep -q '^granted to pm_app on schema public' <<< "${migrated}" || { echo "db migrate did not grant: ${migrated}" >&2; exit 1; }
echo "packmoat-admin db migrate --grant-to pm_app (as the owner): migrated and granted"
admin pm_app tenant create acme Acme --upstream-base https://pkgs.dev.azure.com/ --path-prefix org/ \
  --feed https://pkgs.dev.azure.com/org/p/_packaging/f/nuget/v3/index.json > /dev/null
issued="$(admin pm_app token issue acme --label smoke)"
token="$(sed -n 's/^token: //p' <<< "${issued}")"
[[ -n "${token}" ]] || { echo "packmoat-admin token issue printed no token" >&2; exit 1; }
echo "packmoat-admin tenant create and token issue (as the app role): done"

# run_server <role> [keep]: the server as <role>, started through env -u PACKMOAT_DB; with "keep" it is started without that, as the image's own
# entrypoint would, so that the image's PACKMOAT_DB stands beside PACKMOAT_DB_FILE.
run_server() {
  local start=(--entrypoint /usr/bin/env "${image}" -u PACKMOAT_DB "${entrypoint[@]}")
  if [[ "${2:-}" == "keep" ]]; then start=("${image}"); fi
  docker run -d --name "${server}" --network "container:${db}" -v "${secrets}:/run/secrets/pm:ro" -e ASPNETCORE_URLS=http://0.0.0.0:8080 \
    -e PACKMOAT_DB_ENGINE=postgres -e PACKMOAT_DB_FILE="/run/secrets/pm/$1" -e PACKMOAT_COOKIE_KEYS=database -e PACKMOAT_MASTER_KEY="${key}" \
    -e PACKMOAT_HOSTS=feeds.example.com -e PACKMOAT_PUBLIC_BASE_URL=https://feeds.example.com "${start[@]}" > /dev/null
}
run_server pm_app
port="$(docker port "${db}" 8080/tcp | head -n 1)"
port="${port##*:}"
code=""
for _ in $(seq 1 60); do
  code="$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:${port}/readyz" || true)"
  [[ "${code}" == "200" ]] && break
  if [[ "$(docker inspect --format '{{.State.Running}}' "${server}")" != "true" ]]; then echo "the server exited before /readyz answered" >&2; exit 1; fi
  sleep 1
done
[[ "${code}" == "200" ]] || { echo "/readyz did not answer 200 within 60 seconds (last: ${code})" >&2; exit 1; }
[[ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:${port}/healthz")" == "200" ]] || { echo "/healthz did not answer 200" >&2; exit 1; }
echo "server (as the app role): the start's checks passed; /healthz and /readyz 200"
index="/v3/u/org/p/_packaging/f/nuget/v3/index.json"
wrong="$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer pm_not-a-real-token" -H "Host: feeds.example.com" "http://127.0.0.1:${port}${index}")"   # gitleaks:allow
[[ "${wrong}" == "401" ]] || { echo "a wrong token got ${wrong}, not 401" >&2; exit 1; }
right="$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer ${token}" -H "Host: feeds.example.com" "http://127.0.0.1:${port}${index}")"   # gitleaks:allow
# 502 exactly: the token was read from PostgreSQL and let through, and only the upstream (unreachable here) failed. 401 would be the
# token refused; 403 the tenant not served; 500 a server error.
[[ "${right}" == "502" ]] || { echo "the issued token got ${right}, not 502 (accepted, then the upstream unreachable)" >&2; exit 1; }
echo "a wrong token: 401; the issued token: ${right} (accepted; the upstream itself is not reachable here)"
# No native library the image lacks is reached for (Npgsql's GSS encryption, off unless required by name, would write one per connection).
served="$(docker logs "${server}" 2>&1)"
if grep -q "Cannot load library" <<< "${served}"; then echo "the server's log has a library the image lacks: $(grep "Cannot load library" <<< "${served}" | head -n 1)" >&2; exit 1; fi
docker rm -f "${server}" > /dev/null

# Without the override, the image's own PACKMOAT_DB stands beside PACKMOAT_DB_FILE, and the server refuses to start: what env -u prevents.
run_server pm_app keep
for _ in $(seq 1 60); do
  [[ "$(docker inspect --format '{{.State.Running}}' "${server}")" != "true" ]] && break
  sleep 1
done
[[ "$(docker inspect --format '{{.State.Running}} {{.State.ExitCode}}' "${server}")" == "false 1" ]] || { echo "the server without env -u did not stop with exit 1" >&2; exit 1; }
both="$(docker logs "${server}" 2>&1)"
grep -q "PACKMOAT_DB and PACKMOAT_DB_FILE are both set" <<< "${both}" || { echo "the server without env -u did not say PACKMOAT_DB and PACKMOAT_DB_FILE are both set" >&2; exit 1; }
echo "server without env -u PACKMOAT_DB: refused, exit 1, saying PACKMOAT_DB and PACKMOAT_DB_FILE are both set"
docker rm -f "${server}" > /dev/null

# As the superuser the start's checks refuse: the server exits non-zero within seconds, never serving.
run_server postgres
for _ in $(seq 1 60); do
  [[ "$(docker inspect --format '{{.State.Running}}' "${server}")" != "true" ]] && break
  sleep 1
done
[[ "$(docker inspect --format '{{.State.Running}} {{.State.ExitCode}}' "${server}")" == "false 1" ]] || { echo "the server did not stop with exit 1 as the superuser" >&2; exit 1; }
refusal="$(docker logs "${server}" 2>&1)"
grep -q "is not safe to serve from. Refusing to start" <<< "${refusal}" || { echo "the server's refusal did not say why" >&2; exit 1; }
grep -q "superuser" <<< "${refusal}" || { echo "the refusal did not name the superuser" >&2; exit 1; }
if grep -qF "${password}" <<< "${refusal}"; then echo "the refusal printed the password" >&2; exit 1; fi
echo "server (as the superuser): refused at start, exit 1, without the password"
echo "image PostgreSQL smoke: OK"
