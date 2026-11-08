# Running Packmoat on one server

This guide sets up Packmoat on one Linux server that you run, with Docker and the ready-made deploy kit in `deploy/`. It takes some work: you will set up DNS, Microsoft Entra ID and backups yourself. Read it once from start to end before you begin. [README.md](README.md) lists what every deployment needs. To run it on Kubernetes instead, read [kubernetes.md](kubernetes.md) as well: it replaces the server, Docker and Caddy parts. Every setting, the health checks, `packmoat-admin`, and what a backup holds are described in [operations.md](../operations.md).

The examples use made-up names. Replace them with your own:

- `packages.contoso.example` is the feed, the address developers add to NuGet.
- `admin.contoso.example` is the operator admin site, for you.
- `app.contoso.example` is the customer site, for the people who use your feeds.
- `contoso` is an organisation you serve.

Some words used below:

- **Tenant:** one organisation that Packmoat serves. Each tenant has its own feed settings, tokens and audit trail.
- **Upstream feed:** the feed in Azure Artifacts (the package feeds of Azure DevOps) that Packmoat reads public packages from, on the tenant's behalf. It holds only packages from nuget.org.
- **Cooldown:** how many days a new package version must be public before Packmoat lets developers have it. The default is 14.
- **Directory:** a Microsoft Entra ID tenant. Microsoft calls its id the "Directory (tenant) ID". This guide says "directory" so that it is not confused with a Packmoat tenant.
- **App registration:** an application you define in Entra ID, so that it can sign people in or sign in by itself.
- **Redirect URI:** an address on your site that Microsoft may send a person back to after they sign in. Microsoft accepts only the ones you register.
- **PEM file:** a text file that holds a certificate, and here also its private key.

## 1. What you run

- **One Linux server with Docker** (Docker Engine and its compose plugin).
- **The Packmoat image**, built from `dotnet/Dockerfile`. It runs the server (Packmoat.Server). It also holds the operator command, `packmoat-admin` (Packmoat.Cli), which you run with `docker exec`.
- **Caddy**, a web server that handles HTTPS. It gets and renews certificates by itself.
- **SQLite**, a database in a single file, kept in `/var/lib/packmoat` on the server.

Both containers use the server's own network ("host networking"). The Packmoat server listens only on `127.0.0.1:8080`, so only the server itself can reach it. Caddy listens on ports 80 and 443 and passes requests on. Packmoat trusts the forwarded-address headers only from `127.0.0.1`, so a client cannot fake its address.

Open ports 80 and 443 to the internet. Caddy needs port 80 to get certificates and to redirect to HTTPS. Do not open port 8080.

The deploy kit is in `deploy/`:

| File | What it is | Where it goes on the server |
|---|---|---|
| `deploy/docker-compose.yml` | The two containers | `/opt/packmoat/deploy/` |
| `deploy/Caddyfile` | Caddy's settings | `/opt/packmoat/deploy/` |
| `deploy/packmoat.env.example` | The server's settings | `/etc/packmoat/packmoat.env` |
| `deploy/caddy.env.example` | Caddy's settings: host names only | `/etc/packmoat/caddy.env` |
| `deploy/backup.sh` | The daily backup | `/opt/packmoat/deploy/` |
| `deploy/restore.sh` | Restores a backup | `/opt/packmoat/deploy/` |
| `deploy/systemd/packmoat-backup.service` and `deploy/systemd/packmoat-backup.timer` | Run the backup daily | `/etc/systemd/system/` |

The simplest way to get these paths is to clone this repository to `/opt/packmoat`.

### Prepare the server

Run these as root, or with `sudo`. Clone a **release tag** of this repository, not the main branch: the main branch can change at any time. Section 9 shows how to move to a newer tag.

```
git clone --branch <release tag> <this repository> /opt/packmoat
docker build -t packmoat:local /opt/packmoat/dotnet

# The data folder. The container runs as user 10001, which must own it.
install -d -m 700 -o 10001 -g 10001 /var/lib/packmoat /var/lib/packmoat/backups

# The settings folder. Only root may read it.
install -d -m 700 /etc/packmoat
install -m 600 /opt/packmoat/deploy/packmoat.env.example /etc/packmoat/packmoat.env
install -m 600 /opt/packmoat/deploy/caddy.env.example /etc/packmoat/caddy.env
```

### Prebuilt image

Instead of building the image, you can pull the one this repository (`Packmoat/packmoat` on GitHub) publishes: `ghcr.io/packmoat/packmoat-core` (a registry name is always lower case). It is for linux/amd64 and linux/arm64 (64-bit Intel or AMD, and 64-bit ARM). It is built from the main branch on every change to it. The `main` tag is the newest build, and `sha-<commit>` is the build of one commit; there are no release images yet, so use the kit from the same commit as the image.

Each image is signed with **cosign** (a tool that signs and checks container images) in "keyless" mode: the signature is tied to the workflow that built the image, not to a key someone keeps. Check it before you run the image. The check passes only for an image that this repository's `core-image.yml` workflow built and signed on the main branch. Type the identity exactly as below, with the capital P: cosign compares it letter for letter.

```
cosign verify \
  --certificate-identity https://github.com/Packmoat/packmoat/.github/workflows/core-image.yml@refs/heads/main \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  ghcr.io/packmoat/packmoat-core:sha-<commit>
```

It prints the image's digest (`"docker-manifest-digest":"sha256:..."`). Run the image by that digest, so the image you checked is the one that runs. Pull it and give it the compose file's default name, as below, or set `PACKMOAT_IMAGE` to the `@sha256:` name when you call `docker compose`:

```
docker pull ghcr.io/packmoat/packmoat-core@sha256:<digest>
docker tag ghcr.io/packmoat/packmoat-core@sha256:<digest> packmoat:local
```

The same workflow also publishes, for each image, a **build provenance** (a signed record of which workflow, commit and run built it) and an **SBOM** (software bill of materials: a list of every package in the image), as GitHub artifact attestations. Checking them is optional; the cosign check above is the one that matters. They need the GitHub CLI (`gh`), signed in to any GitHub account. The provenance is for the digest cosign printed:

```
gh attestation verify oci://ghcr.io/packmoat/packmoat-core@sha256:<digest> \
  --repo Packmoat/packmoat \
  --signer-workflow Packmoat/packmoat/.github/workflows/core-image.yml \
  --source-ref refs/heads/main
```

Each platform has its own SBOM, for its own digest. List the platforms' digests, then check and save the SBOM of yours (here `<platform-digest>`):

```
docker buildx imagetools inspect ghcr.io/packmoat/packmoat-core@sha256:<digest>
gh attestation verify oci://ghcr.io/packmoat/packmoat-core@<platform-digest> \
  --repo Packmoat/packmoat \
  --signer-workflow Packmoat/packmoat/.github/workflows/core-image.yml \
  --source-ref refs/heads/main \
  --predicate-type https://spdx.dev/Document/v2.3 \
  --format json --jq '.[0].verificationResult.statement.predicate' > packmoat-sbom.spdx.json
```

Do not start anything yet. First set up DNS (section 2), make the master key (section 3) and fill in both settings files (section 4). Section 4 ends with how to start.

## 2. DNS

Create one **A record** (a DNS entry that points a name at an IPv4 address) for each host name, pointing at your server:

- the feed, `packages.contoso.example`;
- the admin site, `admin.contoso.example`, only if you use it;
- the customer site, `app.contoso.example`, only if you use it.

Caddy gets a certificate for each name by the **HTTP-01 challenge**: the certificate authority checks that the name reaches your server on port 80. So the A record must work before Caddy starts. No DNS password or API key is needed. If you use a DNS service that can also proxy traffic, turn the proxy off for these names.

## 3. The master key

Packmoat encrypts the credentials it keeps (certificates and keys) with one **master key**. Make it once:

```
docker run --rm --entrypoint packmoat-admin packmoat:local keygen
```

Put the printed value in `/etc/packmoat/packmoat.env` as `PACKMOAT_MASTER_KEY`. Keep a second copy somewhere safe and offline, for example in a password manager.

- Keep the master key away from your backups. Someone who has both could read the stored credentials.
- A restore needs the **same** master key. Packmoat refuses to start, and `restore.sh` refuses to restore, when the key does not match the database.
- A lost key cannot be recovered. The server refuses to start on a database made under another key. So keep the offline copy.

## 4. Settings

The server reads its settings from environment variables in `/etc/packmoat/packmoat.env`. The [settings table](../operations.md#settings) lists every one, what it does, and what happens when it is not set. Caddy reads `/etc/packmoat/caddy.env` instead, which holds only host names and `ACME_EMAIL` (the address the certificate authority may write to). Caddy must never read `packmoat.env`.

With this kit, leave `PORT` and `PACKMOAT_TRUSTED_PROXIES` unset: Caddy runs on the same machine.

`deploy/packmoat.env.example` lists the same settings. For Caddy, copy each host name into `/etc/packmoat/caddy.env` too, and set `ACME_EMAIL`.

### Start Packmoat

Start only when the A records from section 2 work, since Caddy asks for its certificates as soon as it starts. Both settings files must be filled in.

```
docker compose -f /opt/packmoat/deploy/docker-compose.yml up -d
curl https://packages.contoso.example/healthz
```

`/healthz` answers `ok` and the build's version (`ok unknown` for a local build). Then try the operator command:

```
docker exec packmoat packmoat-admin tenant list
docker exec packmoat packmoat-admin --help
```

The compose file runs the image `packmoat:local`. To run another tag, set `PACKMOAT_IMAGE` when you call `docker compose`.

`/healthz` only says the server is running. `/readyz` says whether it can serve. [Health checks](../operations.md#health-checks) says more about both.

After any change to either settings file, recreate the containers so that they read it again:

```
docker compose -f /opt/packmoat/deploy/docker-compose.yml up -d --force-recreate
```

Caddy's admin interface is turned off (`admin off` in the Caddyfile), so no local program can change its configuration while it runs. The other side of this: after a change to the Caddyfile, restart Caddy with `docker compose -f /opt/packmoat/deploy/docker-compose.yml restart caddy`.

## 5. Microsoft Entra ID

Packmoat signs people in with Microsoft, and reads your customers' feeds with a Microsoft identity. You register your own apps in your own directory. Portal wording changes over time, so the names below may differ a little.

You may need up to three app registrations. Keep them separate.

### The admin site's app (only if you use the admin site)

1. In the Entra admin center, go to **App registrations** and make a **New registration**.
2. Supported account types: **this organizational directory only** (single tenant).
3. Redirect URI: platform **Web**, `https://admin.contoso.example/signin-oidc`. Use https, this exact path and no trailing slash.
4. Copy the **Application (client) ID** to `PACKMOAT_ADMIN_CLIENT_ID`, and the **Directory (tenant) ID** to `PACKMOAT_ADMIN_TENANT_ID`.
5. Under **Certificates & secrets**, make a client secret. Copy its value to `PACKMOAT_ADMIN_CLIENT_SECRET`. Note when it expires.
6. Find your own user's **Object ID** in Entra (Users, then your user). Set `PACKMOAT_ADMIN_ALLOW` to `<Directory ID>:<Object ID>`.

Only people on that list can use the admin site. Sign-ins from any other directory are refused.

### The customer sign-in app (only if you use the customer site)

This app signs in the people from the organisations you serve.

1. Make a **New registration**. Supported account types: **any organizational directory** (multitenant). Do not include personal Microsoft accounts: Packmoat refuses them.
2. Redirect URIs, platform **Web**:
   - `https://app.contoso.example/signin-microsoft`, always;
   - `https://app.contoso.example/setup/azure/consented`, the page Microsoft returns to after an administrator approves the app;
   - `https://app.contoso.example/signin-azure-devops`, if you turn on Azure setup (section 7).
3. Copy the **Application (client) ID** to `PACKMOAT_APP_CLIENT_ID`.
4. **App roles:** create two, for users and groups, with exactly these values: `Packmoat.Admin` and `Packmoat.Developer`. People with the Admin role manage their organisation's tokens and settings. People with the Developer role use the feed.
5. **Token configuration:** add two optional claims to the ID token:
   - `auth_time`, the time the person last signed in. Without it, the actions that need a recent sign-in are blocked for everyone.
   - `acct`, which tells a member of a directory from a guest. Without it, people with no role are never let in automatically.
6. **Certificate:** this app signs in with a certificate, not a client secret. Make one and upload the public part under **Certificates & secrets**:

   ```
   openssl req -x509 -newkey rsa:3072 -sha256 -days 365 -nodes -subj "/CN=packmoat-app-signin" \
     -keyout app-signin.key -out app-signin.crt
   cat app-signin.crt app-signin.key > app-signin.pem
   openssl x509 -in app-signin.crt -outform der -out app-signin.cer   # upload this one
   ```

   Copy `app-signin.pem` to the server and set `PACKMOAT_APP_CERT=/data/app-signin.pem`:

   ```
   install -m 600 -o 10001 -g 10001 app-signin.pem /var/lib/packmoat/app-signin.pem
   ```

   Then delete `app-signin.key` and `app-signin.pem` from your own machine. Note when the certificate expires. You need not keep a copy: if the file is ever lost, make a new certificate the same way and upload it. Nobody else has to change anything.
7. **API permissions**, only if you turn on Azure setup: add the **Azure DevOps** delegated permissions `vso.packaging`, `vso.graph`, `vso.identity` and `vso.project`. Azure setup asks for these, read-only, while a customer checks their feed. Packmoat keeps no copy of that access.

Each organisation you serve must approve this app once. A directory administrator does that. If a person's sign-in is refused because the organisation has not approved the app yet, the page shows a link to send to their administrator. Then their administrator gives people the Packmoat Admin or Packmoat Developer role, under **Enterprise applications**, then the app, then **Users and groups**.

Members of a linked directory who have no role are let in as Developers. This is called auto-join, and it is on by default. Guests are never let in this way. An Admin can turn auto-join off on the customer site.

### Your shared feed-access app

Packmoat reads each organisation's upstream feed with a Microsoft identity. In **shared-app mode**, you own one multitenant app, and each organisation adds it to their own directory. Packmoat then signs in to their directory as that app, and can reach only what they give it in Azure DevOps.

1. Make a **New registration**, multitenant, with **no API permissions**. A clear name helps your customers, for example "Packmoat feed access".
2. If you turn on Azure setup, add the redirect URI `https://app.contoso.example/setup/azure/feed-app-added` (platform **Web**).
3. Make its certificate on the server:

   ```
   docker exec packmoat packmoat-admin identity shared new-cert --out-dir /data/shared
   ```

   It writes `packmoat-shared.cer` (public) and `packmoat-shared.pem` (private) to `/var/lib/packmoat/shared`. Upload the `.cer` file to the app, under **Certificates & secrets**.
4. Store it, then delete the private file:

   ```
   docker exec packmoat packmoat-admin identity shared set --client-id <application id> --pem-file /data/shared/packmoat-shared.pem
   shred -u /var/lib/packmoat/shared/packmoat-shared.pem
   docker exec packmoat packmoat-admin identity shared show
   ```

   `identity shared show` prints the client id, the certificate's thumbprint and its expiry. Check the thumbprint against the portal.

Each organisation then adds your app to their directory, either with Azure setup or with this command:

```
az ad sp create --id <your application id>
```

In Azure DevOps they add it to their organisation with Stakeholder access, in the project's Readers group only. Then they give it the **Collaborator** role on the upstream feed, and no role on any other feed.

`packmoat-admin identity expiry` lists when every certificate expires. To renew the shared certificate without a break:

1. Make the new certificate in a **new** folder. `new-cert` refuses to overwrite the old files.

   ```
   docker exec packmoat packmoat-admin identity shared new-cert --out-dir /data/shared-2
   ```

2. Store it as the next certificate, then delete the private file. The old certificate stays the active one.

   ```
   docker exec packmoat packmoat-admin identity shared set --client-id <application id> --pem-file /data/shared-2/packmoat-shared.pem --next
   shred -u /var/lib/packmoat/shared-2/packmoat-shared.pem
   ```

3. Upload the new `.cer` to the app, beside the old one.
4. Make it active: `docker exec packmoat packmoat-admin identity shared promote`. Running servers switch within 5 seconds.
5. Check that a feed still works, then remove the old certificate from the app.

The other way to read customers' feeds, own-app mode, is in section 6.

## 6. Add an organisation

### They connect their own feed

Make the tenant and link it to the organisation's directory, with no upstream feed:

```
docker exec packmoat packmoat-admin tenant create contoso "Contoso" --azure-tenant <their directory id>
```

The tenant serves nothing yet. A person from that directory with the Packmoat Admin role signs in at `https://app.contoso.example` and connects their feed with Azure setup (section 7). For this you need the customer site, the shared feed-access app and `PACKMOAT_AZURE_SETUP`.

### You set up the feed

Give the upstream options instead. The feed address and the path prefix must match:

```
docker exec packmoat packmoat-admin tenant create contoso "Contoso" --upstream-base https://pkgs.dev.azure.com/ \
  --path-prefix contoso-org/ --feed https://pkgs.dev.azure.com/contoso-org/<project>/_packaging/<feed>/nuget/v3/index.json \
  --azure-tenant <their directory id>
```

- `--azure-tenant` is optional here. It lets that directory's people sign in to the customer site. You can add it later with `tenant directory bind`.
- `--cooldown` sets the cooldown in days. The default is 14.

Then give Packmoat its identity in their directory. In shared-app mode (section 5), the organisation adds your feed-access app, and you link it:

```
docker exec packmoat packmoat-admin identity bind contoso --azure-tenant <their directory id>
```

In **own-app mode**, the organisation makes its own single-tenant app instead, with a certificate you make for it. Use this when they want Microsoft's Conditional Access rules on Packmoat's identity, which apply only to single-tenant apps:

```
docker exec packmoat packmoat-admin identity new-cert contoso --out-dir /data/onboarding
# send them /var/lib/packmoat/onboarding/contoso.cer; they upload it to their app
docker exec packmoat packmoat-admin identity set contoso --azure-tenant <their directory id> --client-id <their application id> --pem-file /data/onboarding/contoso.pem
shred -u /var/lib/packmoat/onboarding/contoso.pem
```

The stored copy is encrypted with the master key.

Then issue a token and check the setup. `token issue` prints the new token once: copy it straight into a secret store.

```
docker exec packmoat packmoat-admin token issue contoso --label "build agents"
```

`onboard check` checks the organisation's setup in Azure DevOps and through your feed. It needs three secrets: a short-lived Azure DevOps personal access token from one of their administrators, one from a developer, and the Packmoat token. It reads them from environment variables, so they never appear on a command line.

1. Open a root shell, so that the variables reach `docker`:

   ```
   sudo -i
   ```

2. Type the three secrets with `read -rs`, so that they are not shown or kept in the shell's history. Run these lines **one at a time**: each waits for you to paste a secret and press Enter.

   ```
   read -rsp "admin token: " ADMIN_TOK; echo
   ```

   ```
   read -rsp "developer token: " DEV_TOK; echo
   ```

   ```
   read -rsp "Packmoat token: " PROXY_TOK; echo
   ```

3. Run the check, then leave the root shell, which forgets the secrets:

   ```
   export ADMIN_TOK DEV_TOK PROXY_TOK
   docker exec -e ADMIN_TOK -e DEV_TOK -e PROXY_TOK packmoat packmoat-admin onboard check contoso --org contoso-org --feed <feed> \
     --admin-token-env ADMIN_TOK --developer-token-env DEV_TOK --proxy-url https://packages.contoso.example --proxy-token-env PROXY_TOK
   exit
   ```

- In shared-app mode the tenant serves nothing until `onboard check` passes in full.
- `packmoat-admin --help` lists every option, including `--project` and `--confirm-admin`.

Developers then add the feed to NuGet with their token:

```
dotnet nuget add source https://packages.contoso.example/v3/index.json -n packmoat -u packmoat -p <token> --store-password-in-clear-text
```

## 7. Azure setup (`PACKMOAT_AZURE_SETUP`)

Azure setup is a wizard on the customer site. With it, an organisation's Admin connects their own upstream feed, with no help from you. It has three settings:

- `off`, the default: the wizard does not exist. Its pages, its sign-in return address (`/signin-azure-devops`) and `/setup/azure/feed-app-added` answer 404. Only `/setup/azure/consented` still answers, because the customer site's own approval link returns there.
- `on`: the wizard is open to people from any directory.
- A list of directory ids, separated by commas: the wizard is open only to people from those directories. Use this to try it with one organisation first.

The wizard connects a feed only for a tenant you made with `--azure-tenant` and no upstream. For a tenant whose feed you set up, it shows the feed's status instead.

It needs:

- the customer site, with the redirect URIs and the Azure DevOps permissions from section 5;
- the shared feed-access app, stored with `identity shared set`.

In the wizard, the Admin names their Azure DevOps organisation, project and feed. A directory administrator approves your feed-access app. The Admin gives it access in Azure DevOps, then signs in again so Packmoat can check the setup. Next, a Developer from the same organisation proves that they cannot read the feed directly. Only then can the Admin connect the feed.

To turn it off again, set `PACKMOAT_AZURE_SETUP=off` and recreate the container.

## 8. Backups

`deploy/backup.sh` makes a consistent copy of the database while the server runs, checks it, and encrypts it. Then it uploads it to off-site storage and checks the upload. It keeps the 3 newest encrypted copies on the server. Off-site, it deletes copies older than 30 days, but always keeps the 7 newest.

It uses two tools. Install both on the server (on Debian and Ubuntu: `apt install age rclone`).

- **age** encrypts files. Its key comes in two parts: a public key, which can only encrypt, and a private key, which decrypts.
- **rclone** copies files to almost any storage service.

### Set it up

1. On your own machine, not the server, make an age key: `age-keygen -o packmoat-backup.key`. It prints the public key, which starts with `age1`. Keep the key file offline. Never put it on the server.
2. Choose where backups go, and set up an rclone remote for it, here named `offsite`. The timer runs the backup as root, so set the remote up as root too: `sudo rclone config` saves it in root's own rclone settings, where the backup finds it. Then check that root can reach it: `sudo rclone lsd offsite:`. (Instead, you can put rclone's settings in the file below, as `RCLONE_CONFIG_OFFSITE_...` variables.)
3. Write `/etc/packmoat/backup.env`, mode 600:

   ```
   BACKUP_AGE_RECIPIENT=age1...
   BACKUP_REMOTE=offsite:packmoat-backups
   # Optional: KEEP_REMOTE=7  RETAIN_DAYS=30  KEEP_LOCAL=3
   ```

4. Install the timer, then run the first backup by hand and read its output:

   ```
   cp /opt/packmoat/deploy/systemd/packmoat-backup.service /opt/packmoat/deploy/systemd/packmoat-backup.timer /etc/systemd/system/
   systemctl daemon-reload
   systemctl enable --now packmoat-backup.timer
   systemctl start packmoat-backup.service
   journalctl -u packmoat-backup.service
   ```

The timer runs every day at 03:17 UTC. A run that backed up but could not delete old copies ends with exit code 3, so `systemctl --failed` shows it.

A backup holds the database only: not the master key (section 3) nor the customer site's certificate file (section 5). [Backups](../operations.md#backups) lists what a backup holds and what it does not.

### Restore

`deploy/restore.sh` replaces the live database with a backup. It checks the backup first: its integrity, its schema, and that it matches the master key in `/etc/packmoat/packmoat.env`. Only then does it stop the server, restore, and start it again. The old database stays beside the new one.

Copy the backup you want to the server first, for example with `rclone copyto`. Then, as root:

```
AGE_IDENTITY=/root/packmoat-backup.key /opt/packmoat/deploy/restore.sh packmoat-<date>.db.age
docker exec packmoat packmoat-admin tenant list
```

Bring the private key to the server for the restore only. Delete it, and the backup file, when you are done. `restore.sh` also takes an unencrypted backup made with `packmoat-admin backup`.

Practise a restore now and then, as [Practise a restore](../operations.md#practise-a-restore) describes.

### Before an upgrade

Before each upgrade, check that the new version can update your database: `packmoat-admin db check-migrations` runs the update on a copy of a backup. Section 9 shows how.

## 9. Upgrades and rollback

Update by moving to a newer **release tag** of this repository. Read its release notes first. Run these as root.

1. Get the new tag, note the one you run now (for a rollback), and build the new code under a new name:

   ```
   git -C /opt/packmoat describe --tags
   git -C /opt/packmoat fetch --tags
   git -C /opt/packmoat checkout <new release tag>
   docker build -t packmoat:new /opt/packmoat/dotnet
   ```

   Compare `deploy/packmoat.env.example` and `deploy/caddy.env.example` with your files in `/etc/packmoat`, in case the new release adds a setting. Compare `deploy/systemd/` with `/etc/systemd/system/packmoat-backup.*` too; if they changed, copy them over and run `systemctl daemon-reload`.
2. Take a backup with the running version:

   ```
   docker exec packmoat packmoat-admin backup /data/backups/before-upgrade.db
   ```

3. Check that the new version can update your database. This dry run works on a temporary copy and never touches the live database. It ends with `result: OK` when the update is safe:

   ```
   docker run --rm --network none --read-only --user 10001:10001 --cap-drop ALL --tmpfs /tmp \
     -v /var/lib/packmoat/backups:/in:ro --entrypoint packmoat-admin packmoat:new db check-migrations /in/before-upgrade.db
   ```

4. Keep the running image under a second name, then switch. The release's compose file names the Caddy version it was tested with, so get that image too, then recreate both containers. NuGet restores fail for a few seconds while they restart.

   ```
   docker tag packmoat:local packmoat:previous
   docker tag packmoat:new packmoat:local
   docker compose -f /opt/packmoat/deploy/docker-compose.yml pull caddy
   docker compose -f /opt/packmoat/deploy/docker-compose.yml up -d --force-recreate
   ```

5. Check `/healthz`, and run `docker exec packmoat packmoat-admin tenant list`.

To roll back, go back to the tag you noted in step 1 and run the old image again:

```
git -C /opt/packmoat checkout <previous release tag>
docker tag packmoat:previous packmoat:local
docker compose -f /opt/packmoat/deploy/docker-compose.yml pull caddy
docker compose -f /opt/packmoat/deploy/docker-compose.yml up -d --force-recreate
```

Database updates only go forward. A version refuses to start on a database that a newer version has updated. So if the new version updated the database, a rollback also needs the backup from step 2. After the commands above, run:

```
IMAGE=packmoat:previous /opt/packmoat/deploy/restore.sh /var/lib/packmoat/backups/before-upgrade.db
```

Changes made since that backup are lost. The backup from step 2 is not encrypted, so delete it once you no longer need it.

For how Packmoat works inside, see [architecture.md](../architecture.md).
