#!/usr/bin/env bash
# Install the Helm chart into a throwaway kind cluster with a local image, then check that it serves (#249):
# - the pod runs as user 10001 and cannot write its root filesystem;
# - /healthz and /readyz answer 200 through the Service, with Host: localhost and with a feed host name, and another host name gets 400;
# - packmoat-admin works in the pod (it reads the master key from its file, as the server does), and can take a backup;
# - the data claim is ReadWriteOncePod and bound (kind's local-path provisioner supports it), and an existing claim that is only
#   ReadWriteOnce is refused by helm install --dry-run=server;
# - after a restart (Recreate), the new pod opens the same database with the same key and is ready again;
# - the restore in docs/deploy/kubernetes.md, works: kubectl cp in, verify, replicas 0, deploy/helm/restore-pod.yaml, replicas 1.
# The image is loaded into the cluster from the local Docker, never pulled. The cluster is deleted on any exit.
# Needs docker, kind, helm, kubectl and curl on PATH. Usage: helm-kind-smoke.sh <image:tag> <chart folder>
set -euo pipefail

if [[ $# -ne 2 ]]; then echo "Usage: helm-kind-smoke.sh <image:tag> <chart folder>" >&2; exit 2; fi
image="$1"
chart="$2"
cluster=packmoat-helm-smoke
# The node image kind v0.33.0 names for Kubernetes v1.37.0, pinned by digest.
node_image="kindest/node:v1.37.0@sha256:a1ed56cfb0e7b93589bdf97c8cd566405a265939e3620fc4f5de89adff580ae5"
ns=packmoat
release=packmoat
port=18080
feed_host=packages.example.test

work="$(mktemp -d)"
forward_pid=""

stop_forward() { if [[ -n "$forward_pid" ]]; then kill "$forward_pid" 2> /dev/null || true; wait "$forward_pid" 2> /dev/null || true; forward_pid=""; fi; }
cleanup() {
  local status=$?
  stop_forward
  if [[ "$status" -ne 0 ]] && kind get clusters 2> /dev/null | grep -qx "$cluster"; then
    echo "--- diagnostics (the smoke test failed)" >&2
    kubectl -n "$ns" get all,pvc -o wide >&2 || true
    kubectl -n "$ns" describe pods >&2 || true
    kubectl -n "$ns" logs "deployment/$release" --all-containers --tail=200 >&2 || true
  fi
  kind delete cluster --name "$cluster" > /dev/null 2>&1 || true
  rm -rf "$work"
  exit "$status"
}
trap cleanup EXIT

fail() { echo "FAIL: $1" >&2; exit 1; }

# Forward a local port to the Service (a port-forward follows one pod, so it is started again after a restart), and wait for it.
start_forward() {
  stop_forward
  kubectl -n "$ns" port-forward "service/$release" "$port:80" > "$work/forward.log" 2>&1 &
  forward_pid=$!
  for _ in $(seq 1 30); do
    if curl -s -o /dev/null -H 'Host: localhost' "http://127.0.0.1:$port/healthz"; then return 0; fi
    sleep 1
  done
  cat "$work/forward.log" >&2
  fail "the port-forward to the Service did not answer"
}

# expect <host> <path> <status> [body prefix]
expect() {
  local code body
  code="$(curl -s -o "$work/body" -w '%{http_code}' -H "Host: $1" "http://127.0.0.1:$port$2")"
  body="$(cat "$work/body")"
  [[ "$code" == "$3" ]] || fail "GET $2 with Host: $1 answered $code, not $3 (body: $body)"
  if [[ $# -ge 4 && "$body" != "$4"* ]]; then fail "GET $2 with Host: $1 answered \"$body\", not \"$4...\""; fi
  echo "ok:   GET $2 with Host: $1 answers $code${4:+ ($body)}"
}

echo "--- a kind cluster, with the local image loaded"
kind create cluster --name "$cluster" --image "$node_image" --wait 180s
kind load docker-image "$image" --name "$cluster"
kubectl create namespace "$ns"

echo "--- the Secret: a new master key, from the image itself, written to a private file and never printed"
(umask 077 && docker run --rm --network none --entrypoint packmoat-admin "$image" keygen > "$work/master-key")
kubectl -n "$ns" create secret generic packmoat-secrets --from-file=master-key="$work/master-key" > /dev/null
rm -f "$work/master-key"

echo "--- helm install"
helm install "$release" "$chart" --namespace "$ns" --wait --timeout 5m \
  --set image.repository="${image%:*}" --set image.tag="${image##*:}" --set image.pullPolicy=Never \
  --set existingSecret=packmoat-secrets --set "hosts[0]=$feed_host" --set persistence.size=1Gi
kubectl -n "$ns" rollout status "deployment/$release" --timeout 5m

echo "--- the data claim is ReadWriteOncePod, and an existing claim that is not is refused"
modes="$(kubectl -n "$ns" get pvc "$release-data" -o jsonpath='{.spec.accessModes}')"
phase="$(kubectl -n "$ns" get pvc "$release-data" -o jsonpath='{.status.phase}')"
[[ "$modes" == '["ReadWriteOncePod"]' && "$phase" == "Bound" ]] || fail "the data claim is $phase with access modes $modes"
echo "ok:   the data claim is bound, ReadWriteOncePod"
kubectl -n "$ns" apply -f - > /dev/null <<'EOF'
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: rwo-claim
spec:
  accessModes: [ReadWriteOnce]
  resources:
    requests:
      storage: 1Gi
EOF
if helm install other "$chart" --namespace "$ns" --dry-run=server \
     --set image.repository="${image%:*}" --set image.tag="${image##*:}" --set existingSecret=packmoat-secrets \
     --set "hosts[0]=$feed_host" --set persistence.existingClaim=rwo-claim > /dev/null 2> "$work/err.txt"; then
  fail "an existing ReadWriteOnce claim was accepted"
fi
grep -q "not \[ReadWriteOncePod\] alone" "$work/err.txt" || fail "an existing ReadWriteOnce claim was refused for another reason: $(cat "$work/err.txt")"
kubectl -n "$ns" delete pvc rwo-claim > /dev/null
echo "ok:   an existing ReadWriteOnce claim is refused"

echo "--- the pod is unprivileged"
uid="$(kubectl -n "$ns" exec "deployment/$release" -- id -u)"
[[ "$uid" == "10001" ]] || fail "the server runs as user $uid, not 10001"
echo "ok:   the server runs as user 10001"
if kubectl -n "$ns" exec "deployment/$release" -- touch /app/probe 2> /dev/null; then fail "the root filesystem is writable"; fi
echo "ok:   the root filesystem is read only"
kubectl -n "$ns" exec "deployment/$release" -- touch /data/probe /tmp/probe || fail "/data or /tmp is not writable"
echo "ok:   /data and /tmp are writable"

echo "--- the server answers through the Service"
start_forward
expect localhost /healthz 200 "ok "
expect localhost /readyz 200 "ready"
expect "$feed_host" /healthz 200 "ok "
expect other.example.test /healthz 400

echo "--- packmoat-admin in the pod"
kubectl -n "$ns" exec "deployment/$release" -- packmoat-admin tenant list > /dev/null || fail "packmoat-admin tenant list failed"
echo "ok:   packmoat-admin tenant list"
kubectl -n "$ns" exec "deployment/$release" -- packmoat-admin backup /data/backups/smoke.db > /dev/null || fail "packmoat-admin backup failed"
echo "ok:   packmoat-admin backup to /data/backups"

echo "--- a restart (Recreate): the new pod opens the same database with the same key"
kubectl -n "$ns" exec "deployment/$release" -- test -f /data/packmoat.db || fail "no database in /data"
kubectl -n "$ns" rollout restart "deployment/$release"
kubectl -n "$ns" rollout status "deployment/$release" --timeout 5m
pods="$(kubectl -n "$ns" get pods -l "app.kubernetes.io/instance=$release" --field-selector=status.phase=Running -o name | wc -l | tr -d ' ')"
[[ "$pods" == "1" ]] || fail "$pods pods are running after the restart, not 1"
kubectl -n "$ns" exec "deployment/$release" -- test -f /data/probe || fail "the data volume did not survive the restart"
echo "ok:   one pod, on the same data volume"
start_forward
expect localhost /readyz 200 "ready"

echo "--- a restore, as docs/deploy/kubernetes.md, gives it"
pod="$(kubectl -n "$ns" get pods -l "app.kubernetes.io/instance=$release" -o jsonpath='{.items[0].metadata.name}')"
kubectl -n "$ns" cp "$pod:/data/backups/smoke.db" "$work/backup.db" > /dev/null
kubectl -n "$ns" cp "$work/backup.db" "$pod:/data/backups/restore-candidate.db" > /dev/null
kubectl -n "$ns" exec "deployment/$release" -- packmoat-admin verify /data/backups/restore-candidate.db > /dev/null || fail "verify refused the backup"
echo "ok:   the backup was copied out and in with kubectl cp, and verify accepts it"
stop_forward
helm upgrade "$release" "$chart" --namespace "$ns" --reuse-values --set replicas=0 --wait --timeout 5m > /dev/null
kubectl -n "$ns" wait --for=delete pod -l "app.kubernetes.io/instance=$release" --timeout 2m
sed -e "s|__IMAGE__|$image|" -e "s|__CLAIM__|$release-data|" -e "s|__SECRET__|packmoat-secrets|" -e "s|__KEY__|master-key|" \
  "$(dirname "$chart")/restore-pod.yaml" | kubectl -n "$ns" apply -f - > /dev/null
kubectl -n "$ns" wait --for=jsonpath='{.status.phase}'=Succeeded pod/packmoat-restore --timeout 3m \
  || { kubectl -n "$ns" describe pod packmoat-restore >&2; kubectl -n "$ns" logs packmoat-restore >&2 || true; fail "the restore pod did not succeed"; }
kubectl -n "$ns" logs packmoat-restore
kubectl -n "$ns" delete pod packmoat-restore > /dev/null
echo "ok:   the restore pod ran to completion"
helm upgrade "$release" "$chart" --namespace "$ns" --reuse-values --set replicas=1 --wait --timeout 5m > /dev/null
kubectl -n "$ns" rollout status "deployment/$release" --timeout 5m
kubectl -n "$ns" exec "deployment/$release" -- packmoat-admin tenant list > /dev/null || fail "packmoat-admin tenant list failed after the restore"
start_forward
expect localhost /readyz 200 "ready"

echo "helm kind smoke: all passed"
