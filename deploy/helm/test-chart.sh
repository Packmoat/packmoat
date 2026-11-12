#!/usr/bin/env bash
# The Helm chart's tests: helm lint, then helm template with assertions on what it renders, and on what it refuses.
# Needs helm on PATH (core/.github/scripts/install-helm-kind.sh installs the pinned one). No cluster is needed.
# Usage: deploy/helm/test-chart.sh   (from anywhere; exits 1 when any check fails, after running them all)
set -euo pipefail

chart="$(cd "$(dirname "$0")" && pwd)/packmoat"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
failed=0

# The values every render needs. A made-up master key, shaped like a real one (the base64 of 32 bytes), for the refusal cases only.
base=(--set image.repository=registry.example.test/packmoat-core --set existingSecret=packmoat-secrets --set "hosts[0]=packages.example.test")
# It is the base64 of "chart-test-key, not a real one!!" (32 bytes); below, also without its "=" and in base64url.
fake_key="Y2hhcnQtdGVzdC1rZXksIG5vdCBhIHJlYWwgb25lISE="   # gitleaks:allow (made up, as above)
unpadded_key="${fake_key%=}"
url_key="${fake_key:0:40}-_A"

ok() { echo "ok:   $1"; }
bad() { echo "FAIL: $1" >&2; failed=1; }

# render <label> <helm template args...>: renders into $work/out.yaml; a render that fails is a failed check.
render() {
  local label="$1"; shift
  if helm template t "$chart" "${base[@]}" "$@" > "$work/out.yaml" 2> "$work/err.txt"; then return 0; fi
  bad "$label: the render failed: $(cat "$work/err.txt")"; : > "$work/out.yaml"; return 0
}
# has <label> <extended regex>: some line of the last render matches. lacks: no line does. count <label> <n> <regex>: exactly n lines do.
has() { if grep -Eq -- "$2" "$work/out.yaml"; then ok "$1"; else bad "$1: no line matches /$2/"; fi; }
lacks() { if grep -Eq -- "$2" "$work/out.yaml"; then bad "$1: a line matches /$2/: $(grep -E -- "$2" "$work/out.yaml" | head -1)"; else ok "$1"; fi; }
count() { local n; n="$(grep -Ec -- "$3" "$work/out.yaml" || true)"; if [[ "$n" == "$2" ]]; then ok "$1"; else bad "$1: $n lines match /$3/, not $2"; fi; }
# after <label> <regex A> <regex B>: the line right after the first line matching A matches B.
after() {
  local next; next="$( (grep -E -A1 -- "$2" "$work/out.yaml" || true) | sed -n 2p)"   # no match: empty, and the check fails below
  if [[ "$next" =~ $3 ]]; then ok "$1"; else bad "$1: after /$2/ comes \"$next\", not /$3/"; fi
}
# refused <label> <expected message part> <helm template args...>: the render fails, and says why.
refused() {
  local label="$1" message="$2"; shift 2
  if helm template t "$chart" "${base[@]}" "$@" > /dev/null 2> "$work/err.txt"; then bad "$label: rendered, but must be refused"; return 0; fi
  if grep -Fq -- "$message" "$work/err.txt"; then ok "$label"; else bad "$label: refused, but not with \"$message\": $(cat "$work/err.txt")"; fi
}

echo "--- helm lint"
if helm lint --strict "$chart" "${base[@]}" > "$work/lint.txt" 2>&1; then ok "helm lint --strict"; else bad "helm lint --strict: $(cat "$work/lint.txt")"; fi

echo "--- the deployment, with the defaults"
render "defaults" --show-only templates/deployment.yaml
has    "one replica" '^  replicas: 1$'
after  "the strategy is Recreate" '^  strategy:$' '^    type: Recreate$'
lacks  "no rolling update" 'RollingUpdate|rollingUpdate'
has    "a read-only root filesystem" '^            readOnlyRootFilesystem: true$'
has    "no privilege escalation" '^            allowPrivilegeEscalation: false$'
has    "not privileged" '^            privileged: false$'
has    "every capability dropped" '^              drop: \[ALL\]$'
count  "non-root, in the pod and the container" 2 '^ +runAsNonRoot: true$'
count  "user 10001, in the pod and the container" 2 '^ +runAsUser: 10001$'
count  "seccomp RuntimeDefault, in the pod and the container" 2 '^ +type: RuntimeDefault$'
has    "the volumes belong to group 10001" '^        fsGroup: 10001$'
has    "the pause process is PID 1" '^      shareProcessNamespace: true$'
has    "no API token" '^      automountServiceAccountToken: false$'
has    "no service links in the environment" '^      enableServiceLinks: false$'
after  "PORT is set, so the server listens on every address" '^            - name: PORT$' '^              value: "8080"$'
after  "PACKMOAT_HOSTS from hosts" '^            - name: PACKMOAT_HOSTS$' '^              value: "packages.example.test"$'
after  "the master key is read from its file" '^            - name: PACKMOAT_MASTER_KEY_FILE$' '^              value: "/run/secrets/packmoat/master-key"$'
lacks  "the master key is never in the environment" '^ +- name: PACKMOAT_MASTER_KEY$'
lacks  "no secret is read into the environment" 'secretKeyRef|envFrom'
lacks  "no trusted proxies unless asked" 'PACKMOAT_TRUSTED_PROXIES'
after  "the secret volume is the existing Secret" '^          secret:$' '^            secretName: "packmoat-secrets"$'
has    "the secret files are group-read only" '^            defaultMode: 0440$'
count  "only the master key is mounted by default" 1 '^              - key: '
has    "the secrets are mounted read only" '^              mountPath: /run/secrets/packmoat$'
has    "a writable /tmp" '^              mountPath: /tmp$'
has    "the data volume at /data" '^              mountPath: /data$'
after  "the data volume is the chart's claim" '^          persistentVolumeClaim:$' '^            claimName: "t-packmoat-data"$'
count  "three probes send Host: localhost" 3 '^                  value: localhost$'
count  "startup and liveness probe /healthz" 2 '^              path: /healthz$'
count  "readiness probes /readyz" 1 '^              path: /readyz$'
after  "the readiness probe is /readyz" '^          readinessProbe:$' '^            httpGet:$'
has    "the image is the chart's appVersion" '^          image: "registry.example.test/packmoat-core:main"$'

echo "--- the other resources"
render "claim" --show-only templates/pvc.yaml
has    "the claim is ReadWriteOncePod, so only one pod at a time may mount it" '^  accessModes: \[ReadWriteOncePod\]$'
count  "the claim has one access mode" 1 '^ *accessModes:'
has    "the claim outlives the release" '^    helm.sh/resource-policy: keep$'
render "service" --show-only templates/service.yaml
has    "the service sends to the named port" '^      targetPort: http$'
render "no ingress by default"
lacks  "no ingress by default" '^kind: Ingress$'
render "an existing claim" --set persistence.existingClaim=mine
lacks  "an existing claim: no claim is made" '^kind: PersistentVolumeClaim$'
has    "an existing claim is mounted" '^            claimName: "mine"$'

echo "--- values that are accepted"
render "replicas 0" --set replicas=0 --show-only templates/deployment.yaml
has    "replicas 0 stops the server" '^  replicas: 0$'
render "by digest" --set image.digest=sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef --show-only templates/deployment.yaml
has    "a digest wins over the tag" '^          image: "registry.example.test/packmoat-core@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"$'
render "the site secrets" --set existingSecretKeys.adminClientSecret=admin-client-secret --set existingSecretKeys.appCert=app-signin.pem --show-only templates/deployment.yaml
after  "the admin client secret is read from its file" '^            - name: PACKMOAT_ADMIN_CLIENT_SECRET_FILE$' '^              value: "/run/secrets/packmoat/admin-client-secret"$'
after  "the customer site's certificate is a mounted file" '^            - name: PACKMOAT_APP_CERT$' '^              value: "/run/secrets/packmoat/app-signin.pem"$'
count  "three keys are mounted" 3 '^              - key: '
cat > "$work/ingress.yaml" <<'EOF'
trustedProxies: 10.244.0.0/16
ingress:
  enabled: true
  className: nginx
env:
  PACKMOAT_ADMIN_HOST: admin.example.test
  PACKMOAT_OPERATOR_NAME: Contoso IT
EOF
render "ingress" -f "$work/ingress.yaml"
has    "the ingress is rendered" '^kind: Ingress$'
has    "the ingress routes the feed" '^    - host: "packages.example.test"$'
has    "the ingress routes the admin site" '^    - host: "admin.example.test"$'
count  "the ingress routes two names" 2 '^    - host: '
after  "trusted proxies from trustedProxies" '^            - name: PACKMOAT_TRUSTED_PROXIES$' '^              value: "10.244.0.0/16"$'
after  "env passes other settings on" '^            - name: "PACKMOAT_OPERATOR_NAME"$' '^              value: "Contoso IT"$'
lacks  "no network policy by default" '^kind: NetworkPolicy$'
cat > "$work/netpol.yaml" <<'EOF2'
networkPolicy:
  enabled: true
  ingressNamespace: ingress-nginx
  ingressPodLabels:
    app.kubernetes.io/name: ingress-nginx
EOF2
render "network policy" -f "$work/netpol.yaml" --show-only templates/networkpolicy.yaml
has    "the network policy is rendered" '^kind: NetworkPolicy$'
after  "it lets in only the ingress namespace" '^            matchLabels:$' '^              kubernetes.io/metadata.name: "ingress-nginx"$'
has    "and only the ingress controller's pods" '^              "app.kubernetes.io/name": "ingress-nginx"$'
has    "on the server's port" '^        - port: http$'
cat > "$work/lookalikes.yaml" <<'EOF2'
podAnnotations:
  checksum/config: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
env:
  PACKMOAT_OPERATOR_NAME: packages-feed-contoso-example-production-ab
EOF2
render "values that only look long: a sha256 checksum, a 43-character lower-case name" -f "$work/lookalikes.yaml"
has    "a sha256 checksum annotation is not taken for a key" 'checksum/config: 0123456789abcdef'

render "pod labels" --set "podLabels.team=feeds" --set "podLabels.app\.kubernetes\.io/instance=other" --show-only templates/deployment.yaml
has    "pod labels are added" '^        team: feeds$'
lacks  "pod labels cannot replace the selector's labels" 'instance: other'

echo "--- values are quoted, so a newline in one cannot add YAML"
render "a size with a newline" --set-string "persistence.size=1Gi
  accessModes: [ReadWriteMany]" --show-only templates/pvc.yaml
count  "a size with a newline adds no access mode" 1 '^ *accessModes:'
lacks  "a size with a newline adds no ReadWriteMany line" '^ *accessModes: \[ReadWriteMany\]'
render "a Secret name with a newline" --set-string "existingSecret=s
            defaultMode: 0777" --show-only templates/deployment.yaml
count  "a Secret name with a newline adds no file mode" 1 '^ *defaultMode:'
render "a pull policy and a service type with a newline" --set-string "image.pullPolicy=Never
          privileged: true" --set-string "service.type=ClusterIP
  externalIPs: [192.0.2.1]"
lacks  "a pull policy with a newline adds no privileged line" '^ *privileged: true'
lacks  "a service type with a newline adds no external address" '^  externalIPs:'

echo "--- values that are refused"
refused "replicas 2" "replicas must be 0 or 1" --set replicas=2
refused "replicas \"2\"" "replicas must be 0 or 1" --set-string replicas=2
refused "replicas 3" "replicas must be 0 or 1" --set replicas=3
refused "replicas 1.5" "replicas must be 0 or 1" --set replicas=1.5
printf 'masterKey: %s\n' "$fake_key" > "$work/key.yaml"
refused "a master key in a values file" "the master key may not be a value" -f "$work/key.yaml"
refused "a master key with --set, whatever its shape" "the master key may not be a value" --set masterKey=not-a-key-shape
printf 'env:\n  PACKMOAT_MASTER_KEY: "%s"\n' "$fake_key" > "$work/env-key.yaml"
refused "a master key in env" "may not be a value" -f "$work/env-key.yaml"
refused "a master key in env, whatever its shape" "env.PACKMOAT_MASTER_KEY: a secret may not be a value" --set env.PACKMOAT_MASTER_KEY=x
printf 'env:\n  PACKMOAT_OPERATOR_NAME: "%s"\n' "$fake_key" > "$work/hidden-key.yaml"
refused "a master key without its =, in an annotation" "looks like a master key" --set "podAnnotations.k=$unpadded_key"
refused "a master key in base64url, in an annotation" "looks like a master key" --set "podAnnotations.k=$url_key"
printf 'env:\n  "FOO\\n            - name: PACKMOAT_ADMIN_CLIENT_SECRET\\n              value: x": "y"\n' > "$work/env-name.yaml"
refused "an env name with a newline (it would add a variable)" "is not a variable name" -f "$work/env-name.yaml"
refused "an env name with a space" "is not a variable name" --set "env.A B=x"
refused "an env name starting with a digit" "is not a variable name" --set env.1A=x
refused "a value shaped like a master key, anywhere" "looks like a master key" -f "$work/hidden-key.yaml"
printf 'existingSecretKeys:\n  masterKey: "%s"\n' "$fake_key" > "$work/key-as-name.yaml"
refused "the master key where its key name goes" "looks like a master key" -f "$work/key-as-name.yaml"
refused "a key name that is not one" "must be the name of a key in the Secret" --set "existingSecretKeys.masterKey=a b"
refused "a key name of .." "must be the name of a key in the Secret" --set "existingSecretKeys.masterKey=.."
refused "the admin client secret in env" "env.PACKMOAT_ADMIN_CLIENT_SECRET: a secret may not be a value" --set env.PACKMOAT_ADMIN_CLIENT_SECRET=x
refused "the customer certificate in env" "env.PACKMOAT_APP_CERT: a secret may not be a value" --set env.PACKMOAT_APP_CERT=/x.pem
refused "a _FILE variable in env" "env.PACKMOAT_MASTER_KEY_FILE: the chart mounts the secrets" --set env.PACKMOAT_MASTER_KEY_FILE=/x
refused "PORT in env" "env.PORT: the chart sets it" --set env.PORT=9090
refused "PACKMOAT_TRUSTED_PROXIES in env" "env.PACKMOAT_TRUSTED_PROXIES: the chart sets it" --set env.PACKMOAT_TRUSTED_PROXIES=10.0.0.0/8
refused "no existingSecret" "existingSecret is required" --set existingSecret=
refused "no master key name" "existingSecretKeys.masterKey is required" --set existingSecretKeys.masterKey=
refused "no hosts" "hosts is required" --set hosts=null
refused "a network policy without the ingress namespace" "networkPolicy.enabled needs networkPolicy.ingressNamespace" --set networkPolicy.enabled=true
refused "no image repository" "image.repository is required" --set image.repository=
refused "an ingress without trusted proxies" "ingress.enabled needs trustedProxies" --set ingress.enabled=true

if [[ "$failed" -ne 0 ]]; then echo "chart tests: FAILED" >&2; exit 1; fi
echo "chart tests: all passed"
