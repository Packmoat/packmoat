# Running Packmoat on Kubernetes (Helm)

`deploy/helm/packmoat` is a **Helm chart** (a package of Kubernetes resources, installed with the `helm` command) that runs one copy of the Packmoat image on a Kubernetes cluster. It is not published to a chart repository yet: install it from a clone of a release tag of this repository. In the [single-server guide](single-server.md), sections 2 (DNS), 3 (the master key), 5 (Microsoft Entra ID), 6 (adding an organisation) and 7 (Azure setup) still apply. Its sections 1, 4, 8 and 9 (the server, its settings file, backups and upgrades) are replaced by what follows. [README.md](README.md) lists what every deployment needs.

What the chart makes:

- a **Deployment** with one pod. The database is one SQLite file, and only one copy of the server may write it, so the chart refuses `replicas` other than 0 or 1. Upgrades use the `Recreate` strategy: the old pod stops before the new one starts. Set `replicas: 0` to stop the server, for a restore. The chart runs SQLite only. More than one copy needs PostgreSQL, which this chart does not set up (see [PostgreSQL](../self-hosting.md#postgresql)).
- a **PersistentVolumeClaim** (a request for a disk) for `/data`, with the access mode **ReadWriteOncePod**: the cluster lets only one pod at a time mount it. This, not `Recreate`, is what keeps a second pod off the database when a pod is deleted, a node is drained or a pod is evicted: the replacement waits until the old pod is gone. (ReadWriteOnce would not: it allows any number of pods on the same node.) It needs Kubernetes 1.29 or later and a storage class whose driver supports ReadWriteOncePod, as CSI drivers do. `helm uninstall` leaves the claim, so the database is not lost with the release.
- a **Service** on port 80, and, if you set `ingress.enabled`, an **Ingress** for the feed host names and the admin and customer site host names.
- if you set `networkPolicy.enabled`, a **NetworkPolicy** that lets only the ingress controller reach the server (see `trustedProxies` below).

To use a claim you made yourself, set `persistence.existingClaim`. It must be ReadWriteOncePod, and no other mode: `helm install` and `helm upgrade` refuse one that is not (`helm template` cannot read the cluster, so it cannot check). Do not change `nameOverride` or `fullnameOverride` after the install: the chart's claim is named after them, so the server would start on a new, empty database.

The pod runs as user 10001, with a read-only root filesystem (only `/data` and `/tmp` are writable), every Linux capability dropped, no privilege escalation and the runtime's default seccomp profile (a filter on the system calls it may make). It gets no Kubernetes API token. Its probes ask `/healthz` (is it running) and `/readyz` (can it serve; see [Health checks](../operations.md#health-checks)) with the `Host` header set to `localhost`, the only name besides your own that the server answers.

## Secrets

The chart never takes a secret as a value: a secret in a values file ends up in Helm's release history, in plain text. You create a Kubernetes **Secret** yourself, and the chart mounts each of its keys as a file under `/run/secrets/packmoat`, read only. The server reads each through its `_FILE` setting (see [Settings](../operations.md#settings)), so no secret is in the pod's environment either. The chart refuses a master key given as a value (`masterKey`, or an `env` entry), and refuses to install without `existingSecret`. It also refuses any value with the usual shape of a master key (43 characters of base64 or base64url, with or without the closing `=`). That check is a safety net, not a proof: a key written some other way gets past it, so never put one in a values file.

Make the master key ([the master key](single-server.md#3-the-master-key)) and store it in a Secret, without it passing through your shell's history:

```
umask 077
docker run --rm --entrypoint packmoat-admin ghcr.io/packmoat/packmoat-core@sha256:<digest> keygen > master-key
kubectl create namespace packmoat
kubectl -n packmoat create secret generic packmoat-secrets --from-file=master-key=master-key
```

Keep the offline copy ([the master key](single-server.md#3-the-master-key)), then delete the `master-key` file. For the admin site's client secret and the customer site's certificate, add keys to the same Secret (for example `--from-file=admin-client-secret=...` and `--from-file=app-signin.pem=...`), and name them in `existingSecretKeys.adminClientSecret` and `existingSecretKeys.appCert`.

## Values

Write your own values file. `deploy/helm/packmoat/values.yaml` lists every value and what it does. The ones you must set:

| Value | What it is |
|---|---|
| `image.repository` | `ghcr.io/packmoat/packmoat-core` ([Prebuilt image](single-server.md#prebuilt-image)), or your own registry's copy of it. |
| `hosts` | The feed host names (`PACKMOAT_HOSTS`). |
| `existingSecret` | The Secret above. |
| `image.digest` | The image's digest, from `cosign verify` ([Prebuilt image](single-server.md#prebuilt-image)). Without it the chart runs `image.tag` (`main` when unset), which can change under you. |
| `trustedProxies` | `PACKMOAT_TRUSTED_PROXIES`: the addresses or networks of your ingress controller's pods. Only these may pass on the client's address and scheme, and any pod in these networks can claim to be any client. Required with `ingress.enabled`. Never `0.0.0.0/0` or `::/0`. The ingress controller must overwrite `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host`, never pass on a client's. |

Choose `trustedProxies` with care. The narrowest is the ingress controller's own pod addresses (`kubectl -n <ingress namespace> get pods -o wide`), but those change when its pods restart. A wider range, such as the cluster's whole pod network (for example `10.244.0.0/16`), is safe only if no other pod can reach the server: turn on `networkPolicy.enabled` and set `networkPolicy.ingressNamespace` to the ingress controller's namespace (and `networkPolicy.ingressPodLabels` to its pods' labels), so that the server takes connections only from the ingress controller. A NetworkPolicy does nothing unless the cluster's network plugin enforces it; check that yours does.

Put any other setting from the [settings table](../operations.md#settings) under `env`, for example `PACKMOAT_ADMIN_HOST` or `PACKMOAT_OPERATOR_NAME`. The chart sets `PORT`, `PACKMOAT_DB`, `PACKMOAT_HOSTS` and `PACKMOAT_TRUSTED_PROXIES` itself, and refuses them, and the secret settings, under `env`. The ingress controller handles HTTPS: give it the certificates under `ingress.tls`, or let a tool such as cert-manager make them, through `ingress.annotations`. It must pass on the original `Host` header, as most do.

```
helm install packmoat /opt/packmoat/deploy/helm/packmoat --namespace packmoat -f my-values.yaml
kubectl -n packmoat rollout status deployment/packmoat
kubectl -n packmoat exec deployment/packmoat -- packmoat-admin tenant list
```

Use the same commands as sections 6 and 7 of the [single-server guide](single-server.md), with `kubectl -n packmoat exec deployment/packmoat --` in place of `docker exec packmoat`.

## Backups and upgrades on Kubernetes

`deploy/backup.sh` and `deploy/restore.sh` are for the Docker server and do not work here. Take a backup with `packmoat-admin backup` and copy it out of the pod, then encrypt it and store it off-site as [Backups](single-server.md#8-backups) in the single-server guide describes. `kubectl cp` needs the pod's name, which the first command finds:

```
POD=$(kubectl -n packmoat get pods -l app.kubernetes.io/instance=packmoat -o jsonpath='{.items[0].metadata.name}')
kubectl -n packmoat exec deployment/packmoat -- packmoat-admin backup /data/backups/packmoat-<date>.db
kubectl -n packmoat cp "$POD:/data/backups/packmoat-<date>.db" packmoat-<date>.db
```

Your storage's own volume snapshots are a second line of defence, not a substitute: a snapshot of a database being written may not be consistent. Before an upgrade, take a backup, check it with `packmoat-admin db check-migrations` (see [Upgrades and rollback](../operations.md#upgrades-and-rollback)) using the new image, then `helm upgrade` with the new digest. To roll back, `helm rollback`; if the new version updated the database, restore the backup too.

To restore a backup (decrypt it first, if you encrypted it):

1. Copy it into the running server's data volume and check it against the master key. Do not go on unless `verify` says `master key: matches`:

   ```
   POD=$(kubectl -n packmoat get pods -l app.kubernetes.io/instance=packmoat -o jsonpath='{.items[0].metadata.name}')
   kubectl -n packmoat cp packmoat-<date>.db "$POD:/data/backups/restore-candidate.db"
   kubectl -n packmoat exec deployment/packmoat -- packmoat-admin verify /data/backups/restore-candidate.db
   ```

2. Stop the server, and wait until its pod is gone:

   ```
   helm upgrade packmoat /opt/packmoat/deploy/helm/packmoat --namespace packmoat --reuse-values --set replicas=0 --wait
   kubectl -n packmoat wait --for=delete pod -l app.kubernetes.io/instance=packmoat --timeout=2m
   ```

3. Run the one-off restore pod, `deploy/helm/restore-pod.yaml`. Its comments say what to put in place of `__IMAGE__`, `__CLAIM__`, `__SECRET__` and `__KEY__`. It checks the backup again, against the master key too, puts it in place of `/data/packmoat.db`, keeps the old database beside it, and exits. Its log line describes the restored file, read without the key, so it says `master key: NOT CHECKED`; the key was checked before the restore:

   ```
   sed -e 's|__IMAGE__|ghcr.io/packmoat/packmoat-core@sha256:<digest>|' -e 's|__CLAIM__|packmoat-data|' \
       -e 's|__SECRET__|packmoat-secrets|' -e 's|__KEY__|master-key|' /opt/packmoat/deploy/helm/restore-pod.yaml \
     | kubectl -n packmoat apply -f -
   kubectl -n packmoat wait --for=jsonpath='{.status.phase}'=Succeeded pod/packmoat-restore --timeout=3m
   kubectl -n packmoat logs packmoat-restore
   kubectl -n packmoat delete pod packmoat-restore
   ```

4. Start the server again, and check it:

   ```
   helm upgrade packmoat /opt/packmoat/deploy/helm/packmoat --namespace packmoat --reuse-values --set replicas=1 --wait
   kubectl -n packmoat exec deployment/packmoat -- packmoat-admin tenant list
   ```

The chart runs one copy only and is SQLite-only. More than one copy needs PostgreSQL, which the chart does not set up.
