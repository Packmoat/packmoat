# Operating Packmoat

This page holds what is the same however you run Packmoat: every setting, the health checks, the operator command `packmoat-admin`, backups, and upgrades. The deployment guides in [deploy/README.md](deploy/README.md) show the steps for each way to run it.

## Settings

The server reads its settings from environment variables. This table lists every one. In the [single-server kit](deploy/single-server.md) they are in `/etc/packmoat/packmoat.env`; on [Kubernetes](deploy/kubernetes.md) the chart sets some itself and takes the rest under `env`. `deploy/packmoat.env.example` lists the same settings.

| Setting | Needed? | When it is not set |
|---|---|---|
| `PACKMOAT_MASTER_KEY` | Yes | The server does not start. See [the master key](deploy/single-server.md#3-the-master-key). |
| `PACKMOAT_HOSTS` | Yes | The server does not start. The feed host names, separated by spaces. Never the admin or customer site host. Any other host name gets 400. |
| `PACKMOAT_DB` | Yes | The image sets it to `/data/packmoat.db` (`/var/lib/packmoat/packmoat.db` on the server, in the single-server kit). Without it the server does not start. |
| `PACKMOAT_DB_ENGINE` | No | `sqlite`. The database engine: `sqlite` or `postgres`. **Only `sqlite` works today**: `postgres` is reserved for a later release, and with it both the server and `packmoat-admin` stop with an error. On another engine, `backup`, `verify` and `restore` refuse: use that engine's own tools, such as `pg_dump`. The value must be exactly `sqlite` or `postgres`, in lowercase with no spaces: anything else stops both the server and `packmoat-admin`. |
| `PACKMOAT_PUBLIC_BASE_URL` | No | Packmoat uses each request's own host name in the addresses it writes. Set it only if your HTTPS proxy, or something in front of it, hides that name. |
| `PACKMOAT_TOKEN_RPM` | No | 6000 requests per token per minute, then 429. |
| `PACKMOAT_TRUSTED_PROXIES` | No | Only a proxy on the same machine (loopback), such as the single-server kit's Caddy, may pass on the client's address. Set it only when your reverse proxy runs elsewhere: its IP addresses or networks, such as `10.0.0.0/8`, separated by spaces. Never a network of everything (`0.0.0.0/0` or `::/0`), which would let any client choose its address; Packmoat refuses it. The proxy must overwrite `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` with its own values, never pass on a client's. A forwarded host name is then used only if it is one this server answers to. |
| `PACKMOAT_BACKGROUND_JOBS` | No | `on`: the server runs its daily retention pass, which deletes old records and counts the organisations due for deletion. Set it to `off` only when you run more than one copy of the server on one database (not possible yet with SQLite), or when you run the pass from a scheduler instead. Anything but `on` or `off` stops the server. See [Background jobs](#background-jobs). |
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

### Background jobs

With `PACKMOAT_BACKGROUND_JOBS=on` (the default) the server runs its daily retention pass. Set it to `off` only when you run more than one copy of the server on one database (not possible yet with SQLite), or when you run the pass from a scheduler: leave it `on` in one copy, or run `packmoat-admin jobs run retention` once a day from a scheduler. That command prints what each step did and exits 1 if a step failed.

The pass takes a lease in the database first (one row per job in `job_leases`), so two copies, or a copy and the command, never run it at the same time: the one that finds it taken skips that pass (the command says so and exits 0; the server logs it). A lease lasts one hour unless its pass ends it sooner, and every pass logs how long it took. If a copy is killed mid-pass (a crash, `kill -9`), its lease stays for up to that hour: a copy restarted within it skips its first retention pass and runs the next one about a day later. That is harmless: nothing is lost, the pass only runs later.

### Cookie keys

With `PACKMOAT_COOKIE_KEYS=folder` (the default) the keys that protect sign-in cookies stay in the key folder (`PACKMOAT_ADMIN_KEYS_DIR`). Set it to `database` to keep them in the database instead, encrypted with the master key, so every copy of the server on one database shares them; it needs `PACKMOAT_DB`. In `folder` mode a server with a sign-in site but no key folder (neither `PACKMOAT_ADMIN_KEYS_DIR` nor `PACKMOAT_DB`) refuses to start rather than keep the keys only in memory, where every restart would sign everyone out.

At its first start in `database` mode the server copies the key folder's keys into the database, once, so nobody is signed out. **Start that first copy where the existing key folder is mounted** (the same `/data`, or the same `PACKMOAT_ADMIN_KEYS_DIR`): if it finds no key files it makes a new key, logs a warning, and everyone signs in again; it never creates or changes the folder, and once the database holds a key it never reads the folder again. A key file that is not valid XML stops the start, naming the file.

To go back, set `folder` again: the folder still holds its old keys, but keys made while in `database` mode are not in it, so people signed in since the switch sign in again.

### `PORT`

With `PORT` unset, the server listens on `127.0.0.1:8080`. **Leave it unset with the single-server kit.** That kit uses host networking, so with `PORT` set the server listens in plain HTTP on that port on every one of the server's addresses: clients could reach it directly, without Caddy and without HTTPS.

`PORT` is for when the proxy reaches Packmoat over a network, and for a platform that tells a container which port to use (such as Cloud Run), where the platform's own proxy sits in front: the server then listens on that port on every address, and the image's health check follows it. Such a proxy is usually not on the same machine, so set `PACKMOAT_TRUSTED_PROXIES` to its addresses too.

## Health checks

`/healthz` answers `ok` and the build's version (`ok unknown` for a local build).

`/healthz` only says the server is running. `/readyz` says whether it can serve: it answers `ready` (200) when the database opens, its updates are all in place and the master key matches it, and `not ready` (503) otherwise. It never says why. The server's log says which kind of problem it was (database, schema or key) when the answer changes. The answer is kept for 5 seconds, and checking it changes nothing in the database. Neither needs a token. Point a load balancer's or an orchestrator's readiness check at `/readyz`. Both answer only to a host name the server serves, or to `localhost` and `127.0.0.1`. An orchestrator such as Kubernetes sends the container's own address as the host name, which gets 400, so set the check's `Host` header to `localhost`.

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
