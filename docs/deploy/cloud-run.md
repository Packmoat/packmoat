# Google Cloud Run

**Status:** Template checked offline in CI; not yet deployed by the project. The server itself is supported (pre-production) on PostgreSQL, more than one copy, and tested in CI against PostgreSQL 17; it has not yet been run on Cloud Run or Cloud SQL. The items not yet verified on a real deploy are listed [at the end](#not-yet-verified).

## Why it needs PostgreSQL

Cloud Run gives a container no disk that survives a restart and is written by one copy only, so Packmoat's default SQLite database cannot live there. On Cloud Run, Packmoat uses a PostgreSQL database instead, with `PACKMOAT_DB_ENGINE=postgres`: an existing Cloud SQL for PostgreSQL 17 instance. [PostgreSQL](../self-hosting.md#postgresql) explains how to set one up, and what the server checks before it starts.

## What the template makes

`deploy/cloud-run/` is an OpenTofu module, pinned to OpenTofu 1.13.9 and the Google provider 8.11.0. With the inputs you give it, it makes:

- **Three service accounts:** the server's, the migrate job's and the scheduler's. Each server or job account gets **Secret Manager Secret Accessor** on its own secrets only, one secret at a time, never on the project: the server's account cannot read the owner role's connection string, and the migrate job's can read nothing else.
- **Cloud SQL Client** for the server's and the migrate job's accounts, on the instance's project, with an IAM condition that names the one instance. It is the only project-level grant the template makes.
- **The service:** the server, on port 8080, public (Cloud Run's own invoker check is off; Packmoat checks every request itself), from 0 to 1 copy unless you set `min_instances` and `max_instances`. A startup probe on `/readyz` and a liveness probe on `/healthz`, with no `Host` header (see [Health checks](../operations.md#health-checks): the server answers them when the `Host` is an IP address, `localhost` or one of its own names).
- **A migrate job**, run by hand, that runs `packmoat-admin db migrate --grant-to <app role>` from `job_image`, signed in as the owner role. Only this job gets the owner role's connection string.
- **A retention job** that runs `packmoat-admin jobs run retention` from `service_image`, as the server's account, with the master key and the app role's connection string only (the command opens the database as the server does, and checks the master key). **Cloud Scheduler** starts it once a day, signed in as the scheduler's account, which may start that job and nothing else (**Cloud Run Invoker** on the job).
- Only when `enable_static_egress` is true: a VPC with one subnet, a Cloud Router, **Cloud NAT** and a **static external address**, and the server's outbound traffic goes through them (below).

The server and both jobs reach the database through the Unix socket Cloud Run mounts at `/cloudsql/<connection name>`. Each secret is a Secret Manager secret of its own, mounted as a file under `/run/secrets/packmoat/` (its latest version), and the server reads it through the setting's `_FILE` form. No secret is ever an input, an output or an environment value.

The template sets `PACKMOAT_DB_ENGINE=postgres`, `PACKMOAT_COOKIE_KEYS=database`, `PACKMOAT_BACKGROUND_JOBS=off`, `PACKMOAT_HOSTS` and `PACKMOAT_TRUSTED_PROXIES`. Cloud Run sets `PORT` to the container port, 8080. Background jobs are off because the service gets CPU only while it handles a request: the daily retention pass would not run reliably inside it, so the retention job runs it instead ([Background jobs](../operations.md#background-jobs)). The [settings table](../operations.md#settings) has the rest.

The image sets `PACKMOAT_DB` to a SQLite path, and the server refuses to start when both `PACKMOAT_DB` and `PACKMOAT_DB_FILE` are set. So the template starts the server and each job through `/usr/bin/env -u PACKMOAT_DB`, which clears the image's value before the image's own entrypoint (or `packmoat-admin`) runs. If you replace a container's `command`, keep that prefix.

It enables no Google Cloud API, makes no database and stores no secret: those are steps for you, below.

`deploy/cloud-run/test-template.sh` checks the module in CI with no Google account: `tofu fmt`, `tofu init` from the pinned provider (no download), `tofu validate`, `tflint` with the Google rules, and `tofu test`, which plans the module against a stand-in for the Google provider and checks what it would make and what it refuses. Each check has a change to the module that it must catch.

## Before you deploy

1. **The APIs.** In the project:

   ```
   gcloud services enable run.googleapis.com sqladmin.googleapis.com secretmanager.googleapis.com \
     cloudscheduler.googleapis.com iam.googleapis.com cloudresourcemanager.googleapis.com
   ```

   Add `compute.googleapis.com` if you set `enable_static_egress`.
2. **The database.** An existing Cloud SQL for PostgreSQL 17 instance. Make the database and its two roles as in [Setting up the database](../self-hosting.md#setting-up-the-database), signed in as the `postgres` user Cloud SQL gives you, and never give that user to Packmoat (it is a member of `cloudsqlsuperuser`). Write each role's connection string in `Key=Value` form with the socket as the host:

   ```
   Host=/cloudsql/<project>:<region>:<instance>;Database=packmoat;Username=packmoat_app;Password=<password>;Maximum Pool Size=20
   ```

   Over the socket no TLS settings are needed: the server's start treats a Unix socket as this machine's own. Set `Maximum Pool Size` so that `max_instances` times it fits the instance's connection limit ([Connections and pooling](../self-hosting.md#connections-and-pooling)). The instance may be in another project: the template grants Cloud SQL Client in the instance's project.
3. **The secrets,** in Secret Manager in the project you deploy to. Store each one from a file, so it never sits in your shell's history:

   ```
   gcloud secrets create packmoat-master-key --data-file=master-key
   gcloud secrets create packmoat-db-app --data-file=db-app.txt
   gcloud secrets create packmoat-db-owner --data-file=db-owner.txt
   ```

   Make the master key with `packmoat-admin keygen`, as in [Kubernetes](kubernetes.md), and keep a copy offline. Delete the local files afterwards. Every secret the inputs name must exist before you deploy: each account's access is granted on the secret itself. The owner's secret must be its own, never one the server also reads (the template refuses that).
4. **Your own rights.** Whoever runs the template needs, in the project: Cloud Run Admin (`roles/run.admin`), Service Account Admin (`roles/iam.serviceAccountAdmin`) and Service Account User (`roles/iam.serviceAccountUser`, to run the service and the jobs as the new accounts), Secret Manager Admin (`roles/secretmanager.admin`, to grant access on each secret), Cloud Scheduler Admin (`roles/cloudscheduler.admin`); Project IAM Admin (`roles/resourcemanager.projectIamAdmin`) in the database's project, for the conditioned grant; and, with `enable_static_egress`, Compute Network Admin (`roles/compute.networkAdmin`). This list has not yet been tried on a real deploy: a narrower set may do.
5. **Unauthenticated access.** The service turns off Cloud Run's invoker check so that anyone can reach Packmoat. An organization policy may forbid that (not yet verified); if yours does, the apply fails on the service.

## Deploy

Copy `deploy/cloud-run/example.tfvars`, set your values, and keep the state somewhere safe (it holds no secret values; a Cloud Storage backend is usual). On a new database, make and run the migrate job first, so that the service's first revision finds the database at its schema:

```
cd deploy/cloud-run
tofu init
tofu apply -var-file=packmoat.tfvars -target=google_cloud_run_v2_job.migrate
gcloud run jobs execute packmoat-migrate --region <region> --wait
tofu apply -var-file=packmoat.tfvars
```

Until the migrate job has run, `/readyz` answers 503, the startup probe fails and the service's revision does not become ready. If a job or the service fails to read a secret right after the apply, the new grant may not have reached Google Cloud everywhere yet: wait a few minutes and try again.

The main inputs:

| Input | What it is |
|---|---|
| `project_id`, `region` | Where the service, the jobs and the scheduler job go. Secret Manager in `project_id` holds the secrets. |
| `service_image`, `job_image` | The server's image (the retention job runs it too) and the migrate job's, each by digest (`ghcr.io/packmoat/packmoat-core@sha256:<digest>`). A tag is refused. |
| `hosts` | `PACKMOAT_HOSTS`: the feed host names, separated by spaces. |
| `cloud_sql_instance` | The instance's connection name, `<project>:<region>:<instance>`. |
| `master_key_secret`, `app_database_secret`, `owner_database_secret` | The secrets' ids. Defaults: `packmoat-master-key`, `packmoat-db-app`, `packmoat-db-owner`. The owner's secret may not be one the server also reads. |
| `app_role_name` | The app role the migrate job grants to. Default: `packmoat_app`. |
| `app_cert_secret` | Optional: the customer site's certificate and key (`PACKMOAT_APP_CERT`). |
| `file_settings` | Optional secret settings, read from Secret Manager through their `_FILE` form, such as `{ PACKMOAT_ADMIN_CLIENT_SECRET = "packmoat-admin-client-secret" }`. |
| `settings` | Other settings that are not secret, such as `{ PACKMOAT_ADMIN_HOST = "admin.example.com" }`. It takes `PACKMOAT_` settings only; a secret setting, a `_FILE` setting, one the template sets itself, or any `PACKMOAT_DB` setting (`PACKMOAT_DB_SINGLE_ROLE` among them) is refused. `file_settings` refuses the same, and the master key. |
| `min_instances`, `max_instances` | Default 0 and 1. More than one copy is supported on PostgreSQL. |
| `trusted_proxies` | `PACKMOAT_TRUSTED_PROXIES`. Default `169.254.0.0/16`, not yet verified (below). `0.0.0.0/0` and `::/0` are refused anywhere in the list, between spaces or commas. |
| `enable_static_egress` | Default false. See below. |
| `retention_schedule`, `retention_time_zone` | When the retention job runs. Default `17 3 * * *`, `Etc/UTC`. |

**More than one copy.** As on any platform, every copy keeps its forms, its limits for signed-in actions, the cookie keys and the job leases in the database, so no sticky sessions are needed; budget N × `Maximum Pool Size` connections, and remember that limits on traffic nobody has signed in to are counted by each copy on its own ([More than one copy, on PostgreSQL](../operations.md#more-than-one-copy-on-postgresql)).

**A fixed outbound address.** By default, traffic from Packmoat to Azure DevOps and nuget.org leaves from addresses Google picks, which can change. If your customers allowlist the address Packmoat reads their feeds from, set `enable_static_egress` to true: the server's outbound traffic then goes through a subnet (`egress_subnet_cidr`, default `10.40.0.0/26`) and Cloud NAT, from one static external address, the output `outbound_ip_address`. Cloud NAT and a static address cost money every month, so it is off unless you need it.

**Your host names.** The output `service_uri` is the service's own `*.run.app` address. Packmoat answers only the names in `hosts`, so map your own names to the service: with a Cloud Run domain mapping (`gcloud beta run domain-mappings create --service packmoat --domain packages.example.com --region <region>`, available in some regions only), or with a global external Application Load Balancer in front, through a serverless network endpoint group. Neither is part of the template. A load balancer in front changes the addresses the requests come from, so `trusted_proxies` would need to change too; that has not been measured.

**Removing it.** The template keeps the Google provider's deletion protection on for the service and the jobs (`deletion_protection = true`), so `tofu destroy` refuses them until you set it to false in your copy and apply.

## Upgrade

The migrate job and the server each have their own image input, so the database moves first:

1. Take a backup, or note the time for point-in-time restore.
2. Set `job_image` to the new release's digest and apply. Only the migrate job changes.
3. Run it: `gcloud run jobs execute packmoat-migrate --region <region> --wait`, and check that it succeeded.
4. Set `service_image` to the same digest and apply. A new revision starts and takes the traffic once its startup probe passes; the retention job moves to the same image.

If step 3 is skipped or fails, the new revision never becomes ready and the old one keeps serving. Upgrade one release at a time, as [More than one copy, on PostgreSQL](../operations.md#more-than-one-copy-on-postgresql) explains.

## What it costs

What the template makes, and what each part costs; Google's pricing pages have the rates.

- **`min_instances`.** At 0 (the default) the service stops when nobody uses it, and the first request after that waits while a copy starts. Above 0, those copies are kept warm and paid for while idle.
- **The service's CPU** is allocated only while it handles requests.
- **The jobs** are paid for while they run: the retention job a few minutes a day, the migrate job once per upgrade.
- **Cloud Scheduler:** the template makes one scheduler job; Google's pricing page says how many a project gets free.
- **`enable_static_egress`:** Cloud NAT and the static external address are charged every month, and per gigabyte through NAT.
- **Cloud SQL** is your own instance, and its cost is separate.

## Backups

`packmoat-admin backup` works on SQLite only. Use Cloud SQL's own backups and point-in-time restore, or `pg_dump`, and keep the master key apart from them. A restore needs the same master key.

## Not yet verified

The project has not deployed this template. Until a real deploy settles them, these are assumptions:

- **The probes' `Host` header.** The template's probes set none. The server answers `/healthz` and `/readyz` when the `Host` is an IP address, `localhost` or one of its names; what Cloud Run's probes send is not measured.
- **Cloud Run's front end.** That it reaches the container from `169.254.0.0/16` (the `trusted_proxies` default). The settings table requires a trusted proxy to overwrite `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` ([Settings](../operations.md#settings)); whether Cloud Run's front end does is not measured, and two outcomes are likely enough to plan for:
  - **`X-Forwarded-Host`.** Cloud Run's front end probably does not overwrite it. Then, with the front end trusted, a client's own `X-Forwarded-Host` is believed. Packmoat uses a forwarded host name only if it is one it serves (a name in `PACKMOAT_HOSTS` or a site's host), so a client can pick among your own names, and no other.
  - **`X-Forwarded-For`.** If the front end appends its own address to the list rather than the client's, or the client's address never reaches the container, every client looks like the same address. The limits on traffic nobody has signed in to (failed sign-ins, token guesses), which each copy counts by address, would then be shared by all clients: one client's failures could hold back everyone's, on that copy.
- **The Cloud SQL socket.** That the connection string's `Host=/cloudsql/<connection name>` connects through the socket Cloud Run mounts. A unit test shows the server's database library keeps the whole path as the host and the start's checks treat it as local; no real connection has been made.
- **The IAM condition on Cloud SQL Client** (`resource.type == "sqladmin.googleapis.com/Instance"` and the instance's `resource.name`): that it lets the socket connect, and only to that instance.
- **Cloud Run Invoker on the retention job:** that it lets Cloud Scheduler start the job through the Cloud Run Admin API's `:run`.
- **The `env -u PACKMOAT_DB` command:** that Cloud Run runs the `command` override as written (the image's entrypoint is replaced, not extended). The command is checked in the plan, and in CI against the built image, but not on Cloud Run.
- **The mount path:** that Cloud Run accepts secret volumes under `/run/secrets/packmoat/`.
- **Cloud Scheduler in your region,** the deployer roles listed above, and whether an organization policy refuses a service with the invoker check off.
- **The checks themselves:** they run with OpenTofu 1.13.9, the Google provider 8.11.0, tflint 0.66.0 and its Google ruleset 0.41.0, against a stand-in for the provider. They check what the module plans, not what Google's APIs accept.
