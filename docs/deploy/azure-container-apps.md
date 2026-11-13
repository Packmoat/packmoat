# Azure Container Apps

**Status:** Supported (pre-production), more than one copy on PostgreSQL (SQLite runs one); tested in CI against PostgreSQL 17. It has not yet been tried on Container Apps with Azure Database for PostgreSQL.

## Why it needs PostgreSQL

By default Packmoat keeps its data in SQLite: one database file, on a local disk that survives restarts, written by exactly one running copy of the server. Container Apps gives a container no such disk: its persistent storage is a network file share (Azure Files), and a SQLite database is not safe on a network file share. So on Container Apps, Packmoat uses a PostgreSQL database instead, with `PACKMOAT_DB_ENGINE=postgres`. [PostgreSQL](../self-hosting.md#postgresql) explains how to set one up, and what the server checks before it starts.

## What it looks like

The same image as everywhere else, with:

- **A PostgreSQL 17 database**, such as Azure Database for PostgreSQL flexible server, with two roles: an owner that runs the migrations, and an app role the server signs in as. Connect directly, not through a pooler in transaction mode (the server's start refuses one). Use `SSL Mode=VerifyFull` with the server's certificate authority given by `Root Certificate`, as [PostgreSQL](../self-hosting.md#postgresql) shows.
- **A migration job.** A Container Apps job, run by hand before each upgrade, that runs `packmoat-admin db migrate --grant-to <app role>` from the new image, signed in as the owner role.
- **Replicas.** More than one replica is supported on PostgreSQL, and so are multiple revision mode and rolling updates: every copy keeps its forms, its limits for signed-in actions, the cookie keys and the job leases in the database, so a request may go to any copy and no sticky sessions are needed. Run the migration job first, then roll out the new revision: see [More than one copy, on PostgreSQL](../operations.md#more-than-one-copy-on-postgresql). Two things to plan for:
  - **Connections.** The database must allow N × `Maximum Pool Size` connections, plus any connections a module holds on its own (the core server holds none; a module that does says so in its docs), where N is the most replicas you will run, counting the old and the new revision during an update. Set `Maximum Pool Size` in the connection string to fit. [Connections and pooling](../self-hosting.md#connections-and-pooling) has the rest.
  - **Limits on traffic nobody has signed in to** (failed sign-ins, token guesses) are counted by each replica on its own, so with N replicas they are N times looser.
- **Container Apps' own HTTPS ingress** in front, so `PORT` and `PACKMOAT_TRUSTED_PROXIES` are set as for [your own proxy](own-proxy.md). Point the ingress's target port at `PORT`.
- **Health probes** on `/healthz` (liveness) and `/readyz` (readiness), with the `Host` header set to `localhost` (see [Health checks](../operations.md#health-checks)).
- **Settings:** `PACKMOAT_DB_ENGINE=postgres`, `PACKMOAT_DB` set to the app role's connection string with `SSL Mode=VerifyFull`, and `PACKMOAT_COOKIE_KEYS=database`, so the keys that protect sign-in cookies live in the database rather than on a disk.
- **Secrets as files:** the master key, the connection string and the other secrets, given through each setting's `_FILE` form.

Never sign the server in as the administrator role that Azure Database for PostgreSQL gives you: it is a member of `azure_pg_admin`, which can do far more than Packmoat needs.

## Deploy with the template

**Status:** Template checked offline in CI; not yet deployed by the project.

`deploy/azure-container-apps/main.bicep` is a Bicep template that sets up all of the above but the database. In the resource group you deploy it to, it makes:

- a virtual network with one subnet, for a Container Apps environment with workload profiles, and a Log Analytics workspace for its logs;
- two user-assigned managed identities, one for the server and one for the migrate job, each given **Key Vault Secrets User** on its own secrets in your existing Key Vault, one secret at a time: the server's identity cannot read the owner role's connection string, and the job's cannot read the master key or the app role's. Container Apps uses them only to read those secrets: the code in the containers gets no token for them;
- the Container App: the server, with ingress on port 8080, one revision at a time, the probes above with `Host: localhost`, and 1 copy unless you set `minReplicas` and `maxReplicas`;
- a Container Apps job, `<name>-migrate`, run by hand, that runs `packmoat-admin db migrate --grant-to <app role>`;
- only when `enableNatGateway` is true, a NAT gateway and a static public IP (below).

It takes an **existing** PostgreSQL 17 database and an **existing** Key Vault. No secret is ever a template parameter, an output or a plain environment value: each one is a Key Vault secret, which Container Apps mounts as a file under `/run/secrets/packmoat`, and the server reads it through the setting's `_FILE` form. Only the migrate job gets the owner role's connection string; the server gets only the app role's. `deploy/azure-container-apps/test-template.sh` checks these rules on the compiled template in CI, with no Azure account.

### Before you deploy

1. **The database.** Make the database and its two roles as in [Setting up the database](../self-hosting.md#setting-up-the-database), and let the environment's subnet reach it (private access in the same network, or a firewall rule). Write each role's connection string in `Key=Value` form with `SSL Mode=VerifyFull`. If your database's certificate authority is not one the image already trusts, store its certificate in the vault too (`databaseCaSecretName`) and add `Root Certificate=/run/secrets/packmoat/db-ca.crt`. Set `Maximum Pool Size` in the app role's string so that `maxReplicas` times it fits the database ([Connections and pooling](../self-hosting.md#connections-and-pooling)).
2. **The Key Vault.** It must use Azure role-based access control, not access policies. Store each secret from a file, so it never sits in your shell's history:

   ```
   az keyvault secret set --vault-name <vault> --name packmoat-master-key --file master-key
   az keyvault secret set --vault-name <vault> --name packmoat-db-app --file db-app.txt
   az keyvault secret set --vault-name <vault> --name packmoat-db-owner --file db-owner.txt
   ```

   Make the master key with `packmoat-admin keygen`, as in [Kubernetes](kubernetes.md), and keep a copy offline. Delete the local files afterwards. Every secret the parameters name must exist before you deploy: each role is given on the secret itself. The owner's secret must be its own, never one the server also reads (the template refuses that).
3. **Your own rights.** The template gives each identity a role on its secrets in the vault, so whoever deploys it needs the right to assign roles there (Owner, User Access Administrator, or Role Based Access Control Administrator on the vault).
4. **An environment of its own.** Keep the Container Apps environment for Packmoat alone: any app in it is inside the trusted subnet (`PACKMOAT_TRUSTED_PROXIES`), so it could tell Packmoat any client address.

### Deploy

Copy `deploy/azure-container-apps/main.example.bicepparam`, set your values, then:

```
az deployment group create --resource-group <group> --parameters packmoat.bicepparam
az containerapp job start --resource-group <group> --name packmoat-migrate
az containerapp job execution list --resource-group <group> --name packmoat-migrate --output table
```

Wait until the job's run says `Succeeded`. Until then the server's copies do not become ready (`/readyz` answers 503 on a database that has not been migrated) and are restarted; once the job has run, they become ready. On a new database, the first deploy may report the server's revision as unhealthy, because the job has not run yet: run the job, then deploy again. If the first deploy fails to read a Key Vault secret, a new role on the vault has not reached Azure everywhere yet: wait a few minutes and run the deploy again.

The main parameters:

| Parameter | What it is |
|---|---|
| `serviceImage`, `jobImage` | The server's image and the migrate job's, each by digest (`ghcr.io/packmoat/packmoat-core@sha256:<digest>`). A tag is refused. |
| `hosts` | `PACKMOAT_HOSTS`: the feed host names, separated by spaces. |
| `keyVaultName`, `keyVaultResourceGroup` | The existing vault, and its resource group if it is not the one you deploy to. |
| `masterKeySecretName`, `appDatabaseSecretName`, `ownerDatabaseSecretName` | The secrets' names in the vault. Defaults: `packmoat-master-key`, `packmoat-db-app`, `packmoat-db-owner`. The owner's secret may not be one the server also reads. |
| `appRoleName` | The app role the job grants to. Default: `packmoat_app`. |
| `databaseCaSecretName`, `appCertSecretName` | Optional: the database's certificate authority (both the server and the job get it), and the customer site's certificate and key (`PACKMOAT_APP_CERT`). |
| `fileSettings` | Optional secret settings, read from the vault through their `_FILE` form, such as `{ PACKMOAT_ADMIN_CLIENT_SECRET: 'packmoat-admin-client-secret' }`. |
| `settings` | Other settings that are not secret, such as `{ PACKMOAT_ADMIN_HOST: 'admin.example.com' }`. It takes `PACKMOAT_` settings only; a secret setting, a `_FILE` setting, or one the template sets itself (`PACKMOAT_DB_SINGLE_ROLE` among them) is refused. |
| `minReplicas`, `maxReplicas` | Default 1 and 1. More than one copy is supported on PostgreSQL. |
| `enableNatGateway` | Default false. See below. |

The template sets `PACKMOAT_DB_ENGINE=postgres`, `PACKMOAT_COOKIE_KEYS=database`, `PACKMOAT_BACKGROUND_JOBS=on`, `PORT=8080`, and `PACKMOAT_TRUSTED_PROXIES` to the environment's subnet, where Container Apps' own proxy runs. The [settings table](../operations.md#settings) has the rest.

**A fixed outbound address.** By default, traffic from Packmoat to Azure DevOps and nuget.org leaves from an address Azure picks, which can change. If your customers allowlist the address Packmoat reads their feeds from, set `enableNatGateway` to true: the template adds a NAT gateway and a static public IP, and its output `outboundIpAddress` is the address to give them. A NAT gateway and its IP cost money every month, so it is off unless you need it.

**Your host names.** Point each name in `hosts` at the output `appFqdn` with a CNAME record, add a TXT record `asuid.<name>` holding the output `customDomainVerificationId`, then add the name with a managed certificate:

```
az containerapp hostname add --resource-group <group> --name packmoat --hostname packages.example.com
az containerapp hostname bind --resource-group <group> --name packmoat --hostname packages.example.com --environment packmoat-env --validation-method CNAME
```

### Upgrade

The migrate job and the server each have their own image parameter, so the database moves first:

1. Take a backup, or note the time for point-in-time restore.
2. Set `jobImage` to the new release's digest and deploy. Only the job changes.
3. Run the job (`az containerapp job start`) and wait until it says `Succeeded`.
4. Set `serviceImage` to the same digest and deploy. A new revision starts, takes the traffic once its copies are ready, and the old one stops.

If step 3 is skipped or fails, the new revision never becomes ready and the old one keeps serving. Upgrade one release at a time, as [More than one copy, on PostgreSQL](../operations.md#more-than-one-copy-on-postgresql) explains.

## Backups

`packmoat-admin backup` works on SQLite only. Use the database service's own backups and point-in-time restore, or `pg_dump`, and keep the master key apart from them. A restore needs the same master key.
