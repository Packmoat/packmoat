# Operating Packmoat

This page holds what is the same however you run Packmoat: every setting, the health checks, the operator command `packmoat-admin`, backups, and upgrades. The deployment guides in [deploy/README.md](deploy/README.md) show the steps for each way to run it.

## Settings

The server reads its settings from environment variables. This table lists every one. In the [single-server kit](deploy/single-server.md) they are in `/etc/packmoat/packmoat.env`; on [Kubernetes](deploy/kubernetes.md) the chart sets some itself and takes the rest under `env`. `deploy/packmoat.env.example` lists the same settings.

| Setting | Needed? | When it is not set |
|---|---|---|
| `PACKMOAT_MASTER_KEY` | Yes | The server does not start. See [the master key](deploy/single-server.md#3-the-master-key). |
| `PACKMOAT_HOSTS` | Yes | The server does not start. The feed host names, separated by spaces. Never the admin or customer site host. Any other host name gets 400. |
| `PACKMOAT_DB` | Yes | The image sets it to `/data/packmoat.db` (`/var/lib/packmoat/packmoat.db` on the server, in the single-server kit). Without it the server does not start. With SQLite it is the database file. With `PACKMOAT_DB_ENGINE=postgres` it is a PostgreSQL connection string instead, such as `Host=db.example.internal;Database=packmoat;Username=packmoat_app;SSL Mode=VerifyFull` with the role's password added: see [PostgreSQL](#postgresql). Keep it in a secret (`PACKMOAT_DB_FILE`); no error or log line ever shows it. |
| `PACKMOAT_DB_ENGINE` | No | `sqlite`. The database engine: `sqlite` or `postgres` (PostgreSQL 17, which can run more than one copy of the server; SQLite runs one; see [PostgreSQL](#postgresql)). With `postgres`, `PACKMOAT_DB` is a connection string and `PACKMOAT_COOKIE_KEYS` must be `database`, and `backup`, `verify` and `restore` refuse: use PostgreSQL's own tools, such as `pg_dump`. The value must be exactly `sqlite` or `postgres`, in lowercase with no spaces: anything else stops both the server and `packmoat-admin`. |
| `PACKMOAT_DB_SINGLE_ROLE` | No | `off`. PostgreSQL only (with SQLite it is not read). With `on`, the server's database role may own the tables and create in the schema (one role runs both the migrations and the server), and the server logs a warning at every start; with `off` such a role stops the start. A superuser is refused either way. The value must be exactly `on` or `off`, in lowercase with no spaces. |
| `PACKMOAT_PUBLIC_BASE_URL` | No | Packmoat uses each request's own host name in the addresses it writes. Set it only if your HTTPS proxy, or something in front of it, hides that name. |
| `PACKMOAT_TOKEN_RPM` | No | 6000 requests per token per minute, then 429. |
| `PACKMOAT_TRUSTED_PROXIES` | No | Only a proxy on the same machine (loopback), such as the single-server kit's Caddy, may pass on the client's address. Set it only when your reverse proxy runs elsewhere: its IP addresses or networks, such as `10.0.0.0/8`, separated by spaces. Never a network of everything (`0.0.0.0/0` or `::/0`), which would let any client choose its address; Packmoat refuses it. The proxy must overwrite `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` with its own values, never pass on a client's. A forwarded host name is then used only if it is one this server answers to. |
| `PACKMOAT_BACKGROUND_JOBS` | No | `on`: the server runs its daily retention pass, which deletes old records and counts the organisations due for deletion. Set it to `off` only when you run the pass from a scheduler instead. More than one copy on PostgreSQL may all leave it `on`: a lease lets one copy run each pass. Anything but `on` or `off` stops the server. See [Background jobs](#background-jobs). |
| `PACKMOAT_ADMIN_HOST` | No | There is no admin site. |
| `PACKMOAT_ADMIN_TENANT_ID` | For the admin site | The admin site answers 503. Your directory id (see [Microsoft Entra ID](deploy/single-server.md#5-microsoft-entra-id)). |
| `PACKMOAT_ADMIN_CLIENT_ID` | For the admin site | The admin site answers 503. |
| `PACKMOAT_ADMIN_CLIENT_SECRET` | For the admin site | The admin site answers 503. |
| `PACKMOAT_ADMIN_ALLOW` | For the admin site | Nobody can sign in. Who may: directory id and user object id pairs, `tid:oid`, separated by commas (see [Microsoft Entra ID](deploy/single-server.md#5-microsoft-entra-id)). |
| `PACKMOAT_ADMIN_KEYS_DIR` | No | `admin-keys`, next to the database. It holds the keys that protect sign-in cookies. |
| `PACKMOAT_COOKIE_KEYS` | No | `folder`: the keys that protect sign-in cookies stay in the key folder (`PACKMOAT_ADMIN_KEYS_DIR`). `database` keeps them in the database instead. Anything but `folder` or `database`, in lowercase with no spaces, stops the server. See [Cookie keys](#cookie-keys). |
| `PACKMOAT_ADMIN_UPSTREAM_HOSTS` | No | `pkgs.dev.azure.com`. The hosts a tenant made on the admin site may use as its upstream feed. |
| `PACKMOAT_APP_HOST` | No | There is no customer site. |
| `PACKMOAT_APP_CLIENT_ID` | For the customer site | The customer site answers 503. |
| `PACKMOAT_APP_CERT` | For the customer site | The customer site answers 503. The path, inside the container, of a PEM file with the sign-in certificate and its private key. |
| `PACKMOAT_AZURE_SETUP` | No | `off`. See [Azure setup](deploy/single-server.md#7-azure-setup-packmoat_azure_setup). |
| `PACKMOAT_OPERATOR_NAME` | No | Customer pages say "whoever runs Packmoat for you". At most 80 characters. |
| `PACKMOAT_SUPPORT_EMAIL` | No | Customer pages show no support address. |
| `PACKMOAT_SITE_URL` | No | Customer pages link to no website. An `https://` address with no path. Pages link to it, and to its `/privacy` and `/terms` pages. |
| `PORT` | No | The server listens on `127.0.0.1:8080`, which only the server itself can reach. **Leave it unset with the single-server kit.** Anything but a whole number from 1 to 65535 stops the server. See [`PORT`](#port). |

Some settings are checked at start. A bad `PACKMOAT_AZURE_SETUP`, `PACKMOAT_TRUSTED_PROXIES`, operator name, support address, website, or admin or customer site host name stops the server. The error names the setting, never its value. The admin and customer site host names must differ from each other and from every name in `PACKMOAT_HOSTS`.

Any setting can come from a file instead, for example a container secret: add `_FILE` to the setting's name and give the file's path inside the container. One newline at the end of the file is ignored. Set the setting or its file, not both. Both set, or a file that is missing, unreadable or empty, stops the server; the error names the setting, never the path or the value.

### PostgreSQL

With `PACKMOAT_DB_ENGINE=postgres` the server and `packmoat-admin` use a PostgreSQL 17 database, named by the connection string in `PACKMOAT_DB`. On PostgreSQL more than one copy of the server can run at once ([below](#more-than-one-copy-on-postgresql)); on SQLite exactly one runs. [self-hosting.md](self-hosting.md#postgresql) shows how to set it up: the roles, pooling, pool size and backups.

- **Two roles.** An owner role owns the database's schema and runs the migrations: `packmoat-admin db migrate --grant-to <app role>`, signed in as the owner, migrates and then grants the app role what it needs (`SELECT`, `INSERT`, `UPDATE` and `DELETE` on the tables, and use of their sequences: no `TRUNCATE`, no schema changes, no ownership). Run it before each upgrade. The server and the everyday `packmoat-admin` commands sign in as the app role. The server never migrates on PostgreSQL. Every `packmoat-admin` command but `db migrate` and `db check-migrations` (which the owner runs) never migrates either, and runs the server's start checks first: it refuses to run on whatever the server would refuse to start on (below), a schema not at its version among them, before it writes anything, and prints each warning but the pool-size one. Each `packmoat-admin` command opens at most 5 connections of its own, whatever `Maximum Pool Size` says. `packmoat-admin db check-migrations`, as the owner, runs the migrations in a transaction it always rolls back and reports what they would change. One role for both is possible with `db migrate --single-role` and `PACKMOAT_DB_SINGLE_ROLE=on`, but that role can turn off the database rules that keep the audit and deletion records.
- **What the start refuses.** The server checks the database before it serves and refuses to start, listing each problem, on: a server older than 17; a transaction isolation other than read committed; `session_replication_role` other than `origin`; a missing or disabled database rule (trigger); a `search_path` that names no schema that exists; an `idle_session_timeout` under 5 minutes (0, off, is fine); a schema not at its version; a role that is a superuser, may set `session_replication_role`, is a member of a built-in role that reaches past the schema (`pg_execute_server_program`, `pg_read_server_files`, `pg_signal_backend`, `pg_write_all_data`, `pg_write_server_files`), or is granted a superuser's `SECURITY DEFINER` function, directly or through a role (one granted to `PUBLIC` cannot be told from an extension's default, so is not refused: never grant one to `PUBLIC`); a role that (without `PACKMOAT_DB_SINGLE_ROLE=on`) owns a table, may create in the schema, holds more on a table than rows (`TRUNCATE`, `REFERENCES`, `TRIGGER`, `MAINTAIN`, or a write to a version table), or can run a `SECURITY DEFINER` function; a connection pooler in transaction or statement mode (use a direct connection or session pooling; statement mode stops the start with the pooler's own error); a server that refuses the one extra connection the pooler check opens alongside the first (the role's `CONNECTION LIMIT` or `max_connections`); a host that is not this machine reached without TLS (below); a `Maximum Pool Size` under 2; a clock more than 30 seconds apart from the database's. It warns, and starts, on a clock more than 2 seconds apart from the database's and on a `Maximum Pool Size` (Npgsql's default is 100) larger than the database allows an ordinary role (`max_connections` less the reserved connections).
- **Refused options.** `PACKMOAT_DB` may not turn on `Persist Security Info`, `Include Error Detail`, `Log Parameters`, `Include Failed Batched Command`, `No Reset On Close` or `Multiplexing` (it shares one session among many commands, so a session lock would not be its holder's alone), and must name the `Host`, the `Database` and the `Username`. GSS encryption is turned off unless the connection string says `GSS Encryption Mode=Require`: an unset or `Prefer` value becomes `Disable`, since the start counts only TLS, and the images have no Kerberos library to try GSS with.
- **TLS.** A host that is not this machine must be reached over TLS, and the connection string must require it: `SSL Mode=Require`, `VerifyCA` or `VerifyFull` (`VerifyFull` checks the server's certificate and name). `Prefer` and `Allow`, which can fall back to plain text, are refused for such a host.
- **Backups.** Use `pg_dump` and `pg_restore`, or your provider's point-in-time restore. `packmoat-admin backup`, `verify` and `restore` refuse on PostgreSQL.

### Background jobs

With `PACKMOAT_BACKGROUND_JOBS=on` (the default) the server runs its daily retention pass. Several copies on PostgreSQL may all leave it `on`, because a lease lets one copy run each pass. Set it to `off` only when you run the pass from a scheduler: run `packmoat-admin jobs run retention` once a day from a scheduler. That command prints what each step did and exits 1 if a step failed.

The pass takes a lease in the database first (one row per job in `job_leases`), so two copies, or a copy and the command, never run it at the same time: the one that finds it taken skips that pass (the command says so and exits 0; the server logs it). A lease lasts one hour unless its pass ends it sooner, and every pass logs how long it took. If a copy is killed mid-pass (a crash, `kill -9`), its lease stays for up to that hour: a copy restarted within it skips its first retention pass and runs the next one about a day later. That is harmless: nothing is lost, the pass only runs later.

The pass also deletes the rate-limit windows that have ended (`rate_counters`). The limits on what signed-in people and admins do (token issues and revokes, waivers, auto-join changes, feed checks, admin-site writes, and a module's own) are counted in the database, so every copy of the server counts the same attempts; each row holds a scope, the key's SHA-256 (pseudonymous: unsalted, so anyone holding a person's ids or an address can find its row), the window and a count, and is short-lived: deleted within one window plus about a day. If the database cannot be reached, the action is refused as too many requests, never let through, and the server logs one warning per refusal naming the limit and the error's type (`Packmoat.RateLimits`). The `packmoat-admin` commands do not go through these limits, so they still work then. The limits on traffic nobody has signed in to (failed sign-ins, token guesses, requests per token) stay in each copy's memory, so that traffic never writes to the database: with N copies they are N times looser.

The pass also deletes the form nonces that have expired (`form_nonces`). The forms that create a token, add or revoke a waiver, or a module's own (sign-up, choosing a plan) each carry a single-use nonce, kept in the database so a form shown by one copy of the server can be posted to another, and used once only across all of them; a restart keeps them too. A nonce lives 30 minutes, a person (or, on the admin site, a tenant) holds at most 20 at once (opening a form again pushes out the oldest), and each site keeps its own. Each row holds the site, the nonce's SHA-256 (never the nonce), the key's SHA-256 (pseudonymous, as above) and the expiry; it is deleted when the form is posted, or within its life plus about a day. If the database cannot be reached, the form is refused as an old one ("open the form again"), never let through, and the server logs one warning naming the site and the error's type (`Packmoat.FormNonces`).

The pass also deletes what the feed setup wizard kept for a person and has expired (`feed_setup_scratch`). Between two pages the wizard keeps, for each person of each tenant, their last check (with the names of the feed's administrators they may confirm), their last developer proof and the names they typed in step 1, each for 30 minutes, in the database: so the page, or the return from a Microsoft sign-in, finds them whichever copy of the server it reaches, and a restart keeps them. Each row may hold administrators' names, so a tenant purge deletes the tenant's rows, and the pass deletes expired ones (within 30 minutes plus about a day). A tenant's check runs one at a time across every copy, under a job lease named `feed-check:<tenant id>` in `job_leases` (one row per tenant that ran a check, holding the tenant id only); the lease lasts two minutes (the check's 60-second deadline for the Azure DevOps calls, plus a minute to record the result), and a check ends it when it finishes, so only a copy stopped mid-check holds it that long. A tenant purge deletes its lease row. If the database cannot be reached, nothing is ever confirmed or reported from it: "Confirm and check again" and a new check are refused as "Not available yet", the page shows the recorded result, and the server logs one warning naming what failed, the kind and the error's type (`Packmoat.App`; never the tenant, the person or a name).

### Cookie keys

With `PACKMOAT_COOKIE_KEYS=folder` (the default) the keys that protect sign-in cookies stay in the key folder (`PACKMOAT_ADMIN_KEYS_DIR`). Set it to `database` to keep them in the database instead, encrypted with the master key, so every copy of the server on one database shares them; it needs `PACKMOAT_DB`. In `folder` mode a server with a sign-in site but no key folder (neither `PACKMOAT_ADMIN_KEYS_DIR` nor `PACKMOAT_DB`) refuses to start rather than keep the keys only in memory, where every restart would sign everyone out.

At its first start in `database` mode the server copies the key folder's keys into the database, once, so nobody is signed out. **Start that first copy where the existing key folder is mounted** (the same `/data`, or the same `PACKMOAT_ADMIN_KEYS_DIR`): if it finds no key files it makes a new key, logs a warning, and everyone signs in again; it never creates or changes the folder, and once the database holds a key it never reads the folder again. A key file that is not valid XML stops the start, naming the file.

To go back, set `folder` again: the folder still holds its old keys, but keys made while in `database` mode are not in it, so people signed in since the switch sign in again.

### `PORT`

With `PORT` unset, the server listens on `127.0.0.1:8080`. **Leave it unset with the single-server kit.** That kit uses host networking, so with `PORT` set the server listens in plain HTTP on that port on every one of the server's addresses: clients could reach it directly, without Caddy and without HTTPS.

`PORT` is for when the proxy reaches Packmoat over a network, and for a platform that tells a container which port to use (such as Cloud Run), where the platform's own proxy sits in front: the server then listens on that port on every address, and the image's health check follows it. Such a proxy is usually not on the same machine, so set `PACKMOAT_TRUSTED_PROXIES` to its addresses too.

## Health checks

`/healthz` answers `ok` and the build's version (`ok unknown` for a local build).

`/healthz` only says the server is running. `/readyz` says whether it can serve: it answers `ready` (200) when the database opens, its updates are all in place and the master key matches it, and `not ready` (503) otherwise. On PostgreSQL a running server also stays ready when a newer release has updated the database past it, so a rolling upgrade does not take the old copies out of service early ([below](#more-than-one-copy-on-postgresql)). It never says why. The server's log says which kind of problem it was (database, schema or key) when the answer changes. The answer is kept for 5 seconds, and checking it changes nothing in the database. Neither needs a token. Point a load balancer's or an orchestrator's readiness check at `/readyz`. Both answer only to a host name the server serves, or to `localhost` and `127.0.0.1`. An orchestrator such as Kubernetes sends the container's own address as the host name, which gets 400, so set the check's `Host` header to `localhost`.

## The operator command

`packmoat-admin` is the operator's command-line tool. It is in the same image as the server. Run it inside the running container, where it uses the server's own settings:

- one server with Docker: `docker exec packmoat packmoat-admin <command>`;
- Kubernetes: `kubectl -n packmoat exec deployment/packmoat -- packmoat-admin <command>`.

`packmoat-admin --help` lists every command and option. The main ones:

| Command | What it does |
|---|---|
| `keygen` | Prints a new master key. |
| `tenant` | Creates, lists, shows, suspends and activates organisations, sets the cooldown, links a Microsoft directory, and deletes an organisation's data when it is due. |
| `prefix`, `override` | Adds or removes an internal prefix; sets a different cooldown for one package. |
| `waiver` | Lets one version of a package (or `*`, every version) through early, for a number of days; revokes a waiver. |
| `token` | Issues, lists and revokes tokens; lists the developer tokens used lately. |
| `identity` | The certificates and Microsoft identities Packmoat reads feeds with, and when they expire. |
| `onboard check` | Checks an organisation's setup in Azure DevOps and through your feed. |
| `audit tail`, `decision show` | The latest downloads; what was decided for a package's versions, and why. |
| `backup`, `verify`, `restore` | Backups, below. |
| `db check-migrations` | Before an upgrade, below. |
| `jobs run retention` | The daily retention pass, for when `PACKMOAT_BACKGROUND_JOBS` is off. |

## Backups

`packmoat-admin backup <file>` makes a consistent copy of the database while the server runs, and checks it. `packmoat-admin verify <file>` checks a backup without restoring it, and says whether it matches the master key. `packmoat-admin restore <file>` replaces the database with a checked backup: stop the server first. Encrypt each backup and keep it off the machine. The [single-server kit](deploy/single-server.md#8-backups) does all of this for you every day; the [Kubernetes guide](deploy/kubernetes.md#backups-and-upgrades-on-kubernetes) shows the commands there.

A backup holds the database only. It does not hold:

- the master key, which you keep offline;
- the customer site's certificate file, which you can make again (see [Microsoft Entra ID](deploy/single-server.md#5-microsoft-entra-id));
- the cookie keys in `admin-keys`. Without them, people simply sign in again. (With `PACKMOAT_COOKIE_KEYS=database` the cookie keys are in the database instead, so a backup holds them, encrypted with the master key.)

Certificates stored with `identity shared set` or `identity set` are in the database, so a backup holds them, encrypted with the master key.

A restore needs the **same** master key. Packmoat refuses to start on a database made under another key.

### Practise a restore

Restore your newest backup on a spare machine now and then, never on the live server. Time it. Check that `tenant list` shows your tenants and that a token still works.

## Upgrades and rollback

Update by moving to a newer release. Read its release notes first. Each guide gives the exact commands: [one server](deploy/single-server.md#9-upgrades-and-rollback) and [Kubernetes](deploy/kubernetes.md#backups-and-upgrades-on-kubernetes). The order is the same everywhere:

1. Take a backup with the running version.
2. Check that the new version can update your database: `packmoat-admin db check-migrations`, run from the new image, applies the update to a temporary copy of the backup and never touches the live database. It ends with `result: OK` when the update is safe.
3. Switch to the new image. Then check `/healthz`, and run `packmoat-admin tenant list`.

Database updates only go forward. A version refuses to start on a database that a newer version has updated. So if the new version updated the database, a rollback also needs the backup from step 1. Changes made since that backup are lost.

### More than one copy, on PostgreSQL

On PostgreSQL several copies of the server can run at once against one database (on SQLite only one server runs). They can be upgraded one at a time, without stopping the service:

1. Take a backup of the database.
2. Signed in as the owner role, run the new release's `packmoat-admin db migrate --grant-to <app role>` while the old copies keep running.
3. Replace the copies one at a time with the new release, waiting for each new copy's `/readyz` before stopping an old one.

This works because every release's database updates are **expand-only**: they add tables, columns (with a default, or allowing empty), indexes and rules, and never rename, drop or change the meaning of anything the release before reads. A running copy of the previous release keeps serving on the updated database and stays ready. Indexes and rules are added only where the previous release's writes already satisfy them: a new unique index or check that an old copy's insert could break would make that copy fail. Something is removed or renamed only in a later release, once no release still running reads it.

Before each release, the previous release's server is run against the new release's database, to check it keeps serving. (Pull requests to the public repository do not run that check.)

A copy that is ready on a schema a newer release has migrated logs one warning, "a newer release has migrated the database; finish the rollout", the first time (once per process). It means some copies are still the previous release.

Two limits follow:

- Upgrade one release at a time while copies run. To skip releases, stop every copy first, update the database, then start the new release.
- A copy of the previous release that restarts during the rollout refuses to start, as any version does on a database a newer version has updated. Finish the rollout. A rollback to the previous release works on the updated database only after its copies are stopped and the database is restored from the backup in step 1.

### Connections and limits with more than one copy

- **Connection budget.** Each copy opens up to `Maximum Pool Size` connections, so the database must allow N × `Maximum Pool Size`, where N is the most copies you run at once (during a rolling upgrade, old and new together), plus any connections a module holds on its own. The core server holds none: its named locks last one transaction and use pooled connections. A module that holds a lock on a connection of its own says so in its docs; count one for each such lock held at a time. Add room for `packmoat-admin` (up to 5 connections per command) and your own tools. Set `Maximum Pool Size` so the total stays under the database's `max_connections` less its reserved connections. Each copy warns at start when its own pool alone is larger than that.
- **Limits that are per copy.** The limits on what signed-in people and admins do are counted in the database, so they hold across copies. The limits on traffic nobody has signed in to are counted in each copy's memory, so that traffic never writes to the database: failed sign-in attempts and token guesses, and the per-token request limit. With N copies they are N times looser (for example, a limit of 20 a minute becomes up to 20 per copy). Allow for that when you choose how many copies to run.
- **No sticky sessions.** A request can go to any copy. The one-time values in forms, the cookie keys, the held steps of the Azure setup and the job leases are all in the database.
