{{/* The chart's name, and the release's full name (the release name, with the chart's added unless it already holds it). */}}
{{- define "packmoat.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "packmoat.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- define "packmoat.selectorLabels" -}}
app.kubernetes.io/name: {{ include "packmoat.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "packmoat.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{ include "packmoat.selectorLabels" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{/* The image: by digest when one is given, else by tag (the chart's appVersion when image.tag is empty). */}}
{{- define "packmoat.image" -}}
{{- if .Values.image.digest -}}
{{- printf "%s@%s" .Values.image.repository .Values.image.digest -}}
{{- else -}}
{{- printf "%s:%s" .Values.image.repository (default .Chart.AppVersion .Values.image.tag) -}}
{{- end -}}
{{- end -}}

{{/* Where the Secret's keys are mounted, one file each. */}}
{{- define "packmoat.secretsDir" -}}/run/secrets/packmoat{{- end -}}

{{/* The admin and customer site host names env sets, if any: the ingress routes them too. */}}
{{- define "packmoat.siteHosts" -}}
{{- $hosts := list -}}
{{- range $name := list "PACKMOAT_ADMIN_HOST" "PACKMOAT_APP_HOST" -}}
{{- with index $.Values.env $name -}}{{- $hosts = append $hosts (toString .) -}}{{- end -}}
{{- end -}}
{{- join " " $hosts -}}
{{- end -}}


{{/* The data claim's name: the one you made (persistence.existingClaim), or the chart's. */}}
{{- define "packmoat.claimName" -}}
{{- default (printf "%s-data" (include "packmoat.fullname" .)) .Values.persistence.existingClaim -}}
{{- end -}}

{{/*
The chart's refusals, rendered (as nothing) at the top of deployment.yaml, so a bad value stops helm install, upgrade and template alike.
- replicas: 0 or 1. One SQLite writer (values.yaml). The claim is ReadWriteOncePod, so the cluster itself never lets a second pod
  mount it, even while an old pod is still terminating.
- an existing claim must be ReadWriteOncePod too. Checked when helm can read the cluster (install, upgrade, --dry-run=server); helm
  template cannot, so there it is the operator's to check (docs/self-hosting.md, section 10).
- secrets: only from existingSecret, as files. A master key in the values is refused: masterKey, an env entry, or a value with the usual
  shapes of one (43 characters of base64 or base64url, with or without its "=", mixing capitals, small letters and digits, as the
  base64 of 32 random bytes nearly always does). Other shapes cannot be told from ordinary values, so this is a guard, not a proof.
- env: names must be plain variable names; the settings the chart sets itself, and any _FILE variable, are refused.
- ingress without trustedProxies: the server would take the ingress controller for the client.
*/}}
{{- define "packmoat.checks" -}}
{{- if not (has (toString .Values.replicas) (list "0" "1")) -}}
{{- fail (printf "replicas must be 0 or 1, not %v: the database is one SQLite file, which only one copy of the server may write. Running more copies needs PostgreSQL, which this chart does not support yet." .Values.replicas) -}}
{{- end -}}
{{- if and .Values.persistence.enabled .Values.persistence.existingClaim -}}
{{- with lookup "v1" "PersistentVolumeClaim" .Release.Namespace .Values.persistence.existingClaim -}}
{{- if ne (toJson .spec.accessModes) (toJson (list "ReadWriteOncePod")) -}}
{{- fail (printf "persistence.existingClaim: the claim's access modes are %s, not [ReadWriteOncePod] alone. Only ReadWriteOncePod stops a second pod mounting the database while the first is still stopping (docs/self-hosting.md, section 10)." (toJson .spec.accessModes)) -}}
{{- end -}}
{{- end -}}
{{- end -}}
{{- if .Values.masterKey -}}
{{- fail "masterKey: the master key may not be a value. Put it in the Secret named by existingSecret (docs/self-hosting.md, section 10)." -}}
{{- end -}}
{{- range $candidate := regexFindAll "(^|[^A-Za-z0-9+/_=-])[A-Za-z0-9+/_-]{43}=?([^A-Za-z0-9+/_=-]|$)" (toYaml .Values) -1 -}}
{{- if and (regexMatch "[A-Z]" $candidate) (regexMatch "[a-z]" $candidate) (regexMatch "[0-9]" $candidate) -}}
{{- fail "A value looks like a master key (43 characters of base64, as packmoat-admin keygen prints). The master key may not be a value: put it in the Secret named by existingSecret." -}}
{{- end -}}
{{- end -}}
{{- if not .Values.existingSecret -}}
{{- fail "existingSecret is required: the name of a Secret you created that holds the master key (docs/self-hosting.md, section 10). The chart never makes it." -}}
{{- end -}}
{{- if not .Values.existingSecretKeys.masterKey -}}
{{- fail "existingSecretKeys.masterKey is required: the key in the Secret that holds the master key." -}}
{{- end -}}
{{- range $what, $key := .Values.existingSecretKeys -}}
{{- if and $key (or (not (regexMatch "^[-._a-zA-Z0-9]+$" (toString $key))) (has (toString $key) (list "." ".."))) -}}
{{- fail (printf "existingSecretKeys.%s must be the name of a key in the Secret (letters, digits, '-', '_' and '.'), not the secret itself." $what) -}}
{{- end -}}
{{- end -}}
{{- $secretSettings := list "PACKMOAT_MASTER_KEY" "PACKMOAT_ADMIN_CLIENT_SECRET" "PACKMOAT_APP_CERT" -}}
{{- $chartSettings := list "PORT" "PACKMOAT_DB" "PACKMOAT_HOSTS" "PACKMOAT_TRUSTED_PROXIES" -}}
{{- range $name, $value := .Values.env -}}
{{- if not (regexMatch "^[A-Za-z_][A-Za-z0-9_]*$" $name) -}}
{{- fail (printf "env: %q is not a variable name (letters, digits and '_', not starting with a digit)." $name) -}}
{{- else if has $name $secretSettings -}}
{{- fail (printf "env.%s: a secret may not be a value. Put it in the Secret named by existingSecret and name its key in existingSecretKeys." $name) -}}
{{- else if has $name $chartSettings -}}
{{- fail (printf "env.%s: the chart sets it. Use the chart's own value instead (values.yaml)." $name) -}}
{{- else if hasSuffix "_FILE" $name -}}
{{- fail (printf "env.%s: the chart mounts the secrets and sets their _FILE variables itself." $name) -}}
{{- end -}}
{{- end -}}
{{- if not .Values.image.repository -}}
{{- fail "image.repository is required: ghcr.io/packmoat/packmoat-core, or your own registry's copy (docs/self-hosting.md, section 10)." -}}
{{- end -}}
{{- if not .Values.hosts -}}
{{- fail "hosts is required: the feed host names (PACKMOAT_HOSTS)." -}}
{{- end -}}
{{- if and .Values.ingress.enabled (not .Values.trustedProxies) -}}
{{- fail "ingress.enabled needs trustedProxies: the ingress controller's addresses or network, so the server believes its X-Forwarded-* headers and no one else's (docs/self-hosting.md, section 10)." -}}
{{- end -}}
{{- if and .Values.networkPolicy.enabled (not .Values.networkPolicy.ingressNamespace) -}}
{{- fail "networkPolicy.enabled needs networkPolicy.ingressNamespace: the namespace of the ingress controller, the only one let in." -}}
{{- end -}}
{{- end -}}
