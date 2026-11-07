# Packmoat: build-ready architecture (NuGet MVP, npm later)

Status: describes what is built. "PROVED" means a spike test showed it. "ASSUMED" means nobody has tested it.

## 1. Components and what each owns

```
 dev / CI --HTTPS+token--> [Edge: TLS] --> [Host: ASP.NET Core]
                                             |-- Auth (token -> tenant)
                                             |-- NuGet adapter (parse, filter, rewrite)
                                             |-- Policy engine (pure rules)
                                             |-- Upstream client --(SP cert -> Entra token)--> customer's PUBLIC-ONLY Azure feed
                                             |-- Date source + cache --anonymous--> nuget.org API
                                             |-- Tenant store / Audit log --> database
                                             '-- Admin CLI (hand-run for first customers)
```

- **Edge**: terminates TLS. Owns the certificate and nothing else.
- **Host**: owns the request pipeline and the wiring. It contains no business rules.
- **NuGet adapter**: owns knowing what NuGet JSON looks like and how to rewrite it. It does not decide what is hidden.
- **Policy engine**: owns "is this version visible to this tenant right now?" Pure code, no network.
- **Upstream client**: owns talking to the customer's Azure feed, including redirects.
- **Date source**: owns "when was this version published?" using nuget.org, with a cache.
- **Tenant store and audit log**: own state. **Admin CLI**: owns hand-run setup, waivers and token operations.

## 2. Solution layout and the dependency rule

```
Packmoat.Host  ->  Packmoat.Protocols.NuGet  ->  Packmoat.Application  ->  Packmoat.Policy  ->  Packmoat.Domain
      |                                                  ^
      '-> Packmoat.Infrastructure.* (Azure, NuGetOrg, Postgres/Sqlite, KeyVault) implement Application ports
```

- **Domain**: Tenant, Waiver, PackageVersionFacts, Verdict, AuditEvent. Plain types.
- **Policy**: IPolicy, CooldownPolicy, InternalNamespacePolicy, WaiverPolicy, PolicyEngine.
- **Application**: use cases and the ports (interfaces) that the outside world implements.
- **Protocols.NuGet**: JSON filtering and rewriting. YARP is NOT here.
- **Infrastructure.\***: Azure credential source, nuget.org dates, database, vault. Each is replaceable.
- **Host**: ASP.NET Core and YARP, used only to stream binary downloads. Rewriting JSON bodies is our own code, because YARP transforms don't do that well.
- **Rule**: Domain, Policy and Application reference no YARP, HTTP, Redis, Cosmos or Azure package. Enforced by an architecture test that fails the build.
- **Tests**: Policy and Application are tested with fakes and a fake clock. Protocol tests run the real `dotnet restore` against the Host.
- **Modules** (`IPackmoatModule`, `ICustomerSiteModule`, `IAdminSiteModule`, `IAdminCliModule`): what is not the core proxy is added by a module from outside this repository, through these seams only. The core reads only its own `PACKMOAT_*` settings. A setting that only a module acts on is not read at all by a server that runs no such module: it is ignored, not refused. A module checks its own settings when it starts. These seams are the module API. Unstable: may change in any release before 1.0.

## 3. Key interfaces

| Interface | Hides |
|---|---|
| `IPolicy.Decide(facts, tenantPolicy, now)` | One rule. Returns Allow, Hide or Abstain, plus a reason. |
| `IPolicyEngine.Decide(...)` | Rule order: internal namespace, then waiver, then cooldown. Unknown date means Hide (fail closed). |
| `IUpstreamCredentialSource.GetAsync(tenant)` | Service-principal certificate, Entra tokens, Basic encoding. A pass-through variant is for demos only. |
| `IPublishDateSource.GetAsync(ecosystem, id)` | nuget.org registration paging and version-string normalisation. |
| `IDecisionCache` | Memoised verdicts per tenant, package, version and policy revision. A config change bumps the revision. |
| `IAuditLog.Append(event)` | Where download events go (table, file, SIEM later). In the server a bounded queue in front of the store: it never waits and never fails a request. |
| `ITenantStore` | Database choice. Holds tenants, token hashes, waivers, policy config. |
| `ITokenService` | Token creation, hashing, verification, revocation. |
| `IClock` | Time, so rules test with a fake clock. |

## 4. Data model and where it lives

| Entity | Fields (core) | Stored in |
|---|---|---|
| Tenant | id, name, cooldownDays, feedUrl, spClientId, certRef, internalPrefixes, status | database |
| Token | id, tenantId, sha256(hash), displayPrefix, createdAt, revokedAt, lastUsedAt | database (the token itself is shown once and never stored) |
| Waiver / override | id, tenantId, ecosystem, packageId, versionOrRange, kind, expiresAt, reason, createdBy, source (operator or customer), revokedAt, revokedBy | database |
| AuditEvent | id, time, requestId, tenantId, tokenId, package, version, verdict, reason, waiverId | database, append-only |
| CachedDate | ecosystem, packageId, version, publishedAt, fetchedAt | in-memory with TTL, mirrored to the database |
| SP certificate private key | per own-app tenant, plus one for the shared app (active, and next during a rotation) | the database, encrypted and wrapped by an off-box master key (a Key Vault key is an option on Azure) |
| Upstream identity | tenantId, kind (own or shared), directory id, client id and encrypted key (own only) | database |

## 5. Request lifecycle (NuGet)

1. **Auth**: read the Basic password or Bearer token, hash, look up. No match: 401, nothing goes upstream. (PROVED: upstream calls stayed at 0.)
2. **Service index**: fetch upstream with the SP credential, rewrite every Azure host URL to the public host. (PROVED.)
3. **Registration**: for each version, ask the engine. Remove hidden entries and fix counts. Registration pages inline PROVED; paged pages ASSUMED, so the plan is to fetch each page through the same pipeline.
4. **Registration leaf**: hidden returns 404.
5. **Flat container version list**: filter the list (PROVED).
6. **nupkg/nuspec download**: decide BEFORE contacting upstream. Hidden returns 404. Otherwise send our credential to Azure, follow the 303 redirect to blob storage without any credential, and stream the bytes back (PROVED, including the redirect-leak test).
7. Every download, allowed or hidden, writes an audit event; listings and metadata do not (the decision record covers them). Fail closed on missing dates (503 for source outages, 404 for hidden).

**Silent fallback (PROVED):** floating or range requests quietly resolve to an older allowed version. Exact pins and lock files fail with NU1102. Document this.

**npm later (PROVED in spike):** packument filtering, `time` pruning, `dist-tags.latest` repointed to the highest allowed stable, tarball blocking, scoped names. Azure returns the full document even for abbreviated requests.

Downloads through the proxy make Azure save only allowed versions, because hidden ones are never downloaded.

## 6. Onboarding, tokens, certificates

Hand-run for the first customers, with a written checklist:
1. Customer creates a project-scoped feed with only nuget.org and npmjs upstreams. Trim the nine portal-default upstreams to two.
2. The customer's identity for Packmoat, one of two modes per tenant (`upstream_identities.kind`, migration 6):
   - **Shared app** (default for new customers; BUILT, NOT YET PROVEN LIVE): Packmoat owns one multitenant Entra app with no API permissions. The customer's directory admin adds it (admin consent link or `az ad sp create --id`) and sends their directory id; `identity bind` stores only that id. To call Azure DevOps, Packmoat requests a token from the **customer's** directory using **our** client id and certificate (scope `499b84ac-1321-427f-aa17-267ca6975798/.default`). One certificate for all such customers: easier to renew, and removing it from the app is a global kill switch, but a leak of that one key affects every shared-mode customer (each still only to what they granted in Azure DevOps). `identity bind` requires a non-empty path prefix and refuses a directory already bound to another tenant (`--allow-shared-directory` overrides).
   - **Own app** (as before; PROVED in the spikes): the customer's single-tenant app registration with a per-tenant certificate we supply. For customers who need Conditional Access for workload identities, which covers single-tenant apps only.
   The shared credential lives in `shared_app_credentials` (slots `active` and `next` for rotation), encrypted with the master key under the context `shared-app:<slot>`; tenant keys use the tenant id, which may not contain ':', so the two can never be swapped. `AppSignInChooser` (pure) picks authority, client id and PEM per mode and fails closed when anything is missing; `EntraCertificateCredentialSource` re-reads the binding, the active shared credential and the verification every 5 seconds (`RecheckInterval`, the same as the token cache, #170), so a promoted certificate is used, and a withdrawn or deleted verification stops serving (502), within 5 s. The Entra credential is rebuilt only when the sign-in (directory, client id, SHA-256 of the PEM) changes, so its token cache survives the re-checks; the cache keeps the hash, not the key, and comparing it needs no certificate parse. Each re-check is three SQLite reads and one AES-GCM decrypt per tenant, at most once per 5 s while it has traffic.
   **Gate:** a shared tenant gets no credential (fail closed) until `onboard check` has passed for its current binding. The check runs in two phases: every check but `proxy`; if all required ones pass, a record is written for exactly the directory, feed URL and client id read at the start (re-read and compared in the same transaction); then the `proxy` check against the live server, retried up to 150 seconds; if it does not pass, that record is removed. A refusal is never cached, so the server honours a new record on the next request. Feed and base URLs may not contain `.` or `..` segments or escapes, and must already be in resolved form. The pass is stored in `shared_app_verifications` (tenant, directory, feed URL, shared client id, time, who) and must match the current values; binding again deletes it. Two shared tenants can never share a directory (`ux_shared_directory`, a partial unique index). Every tenant's path prefix is matched as whole path segments, and bind requires https, a prefix ending in `/`, and the feed under base + prefix.
   **Schema guard:** a build refuses to run on a database migrated by a newer build (`SchemaTooNewException`); `restore` does not migrate, so rolling back across a migration means restoring the pre-upgrade backup with the older build.
   **Two migration tracks (#196):** `SqliteDatabase.MigrateAsync(modules' tracks)` runs the core's `MigrationTrack` (`schema_migrations`) and then each module's (`{name}_schema_migrations`; `IPackmoatModule.Migrations`, `IAdminCliModule.Migrations`). A module's track may carry a `TrackBaseline` that adopts, by structure, the databases built before the module had its own track, without running its first migration. A track table no module declares, a duplicate track, a newer track, or a database the baseline cannot adopt refuses the start before anything is written. `packmoat-admin db check-migrations FILE` (`MigrationDryRun`) runs the same on a temporary copy and compares it with a fresh build (`SqliteSchemaSnapshot`).
   UNTESTED: consent for an app with zero permissions, a real cross-tenant token accepted by Azure DevOps, and the Graph service-principal list used by the onboarding `identity` check.
3. Add the principal to the org (Stakeholder worked, no license cost), the project **Readers** group only, and Collaborator on the public feed only.
4. We verify with a script, issue a token, store its hash.
- **Never**: Contributors, Project Administrators, the build service, or any broad group or Valid Users role. (PROVED exposure with Valid Users; Contributors INFERRED.)
- **Step 3b, remove developer access from the public-only feed (portal default grants it):** a portal-created feed gives Collaborator to the project's Contributors group and to the Build Service, and its visibility is "Members of your Microsoft Entra tenant". Developers are normally Contributors, so they could read AND save from the public-only feed directly and bypass Packmoat. In Feed settings, Permissions: delete the Contributors and Build Service entries. Leave only the Packmoat principal (Collaborator) and admins the customer chooses. Set view visibility to Specific people.
- **Verification script (proves the bypass is closed):** using an ordinary developer account's token (or a test account in Contributors), request the public-only feed's service index and a package. Pass means 403 or 404 for both. Also confirm that the same account cannot restore the dummy package. A 200 means step 3b was not done.
- **Second, independent bypass control:** this is separate from blocking nuget.org and npmjs at the network level. Either one alone leaves a hole: the network block stops direct public access but not the feed, and the feed lockdown stops the feed but not nuget.org.
- **Revoke**: set `revokedAt`. The token cache lives 5 seconds, so revocation takes effect within that.
- **Rotate**: issue a second token, overlap, then revoke the first.
- MVP: one shared token per tenant. Per-user tokens are roadmap.

## 7. Deployment

How to run it, step by step: [self-hosting.md](self-hosting.md).

- One container image, configured by environment variables. It runs anywhere Docker runs.
- **Host**: one Linux server. Caddy terminates TLS (automatic certificates, HTTP-01 per hostname) and reverse-proxies to the app. Both use host networking: the app listens only on 127.0.0.1:8080 and trusts the forwarded headers only from loopback. Each hostname (the feed, and the admin and customer sites if used) is its own A record pointing at the server.
- **Secrets**: per-tenant SP keys are encrypted in the database, wrapped by one master key kept off-box (a sealed file or key service). Never logged.
- **State: SQLite on the server's LOCAL disk, with off-box backups.** Tradeoff: SQLite has one writer, one VM and a short restart on deploy; Postgres supports several replicas. Tenant config, tokens and waivers are tiny, and audit writes are small. **SQLite until a second replica is needed**; `ITenantStore` and `IAuditLog` hide the choice. A nightly online copy, encrypted and copied off-box, with a tested restore (`deploy/backup.sh`, `deploy/restore.sh`).
- Date lookups: in-memory cache, 5–15 minute TTL, stale-while-error up to a limit, then fail closed.
- Streaming, never buffering nupkgs. Request timeouts and per-token limits.

**Minimum protections**
- A firewall that allows only 80 and 443 (plus SSH from admin addresses).
- Caddy or Kestrel limits: GET-only, header and body limits, slow-request timeouts.
- Per-IP and per-token rate limits, per-tenant concurrency caps.
- Failed-auth rate limit with SHORT blocks (a customer's NAT address is shared by their developers). Only a token that was presented and is wrong counts. NuGet sends every request once without credentials and retries after the 401 (VERIFIED: NuGet/Home#12517, still open), so a missing or malformed header is answered 401 at a fixed cost (no token lookup) and is never counted; counting it throttled real restores (#124). An address is blocked for the rest of its one-minute window after 20 DIFFERENT wrong tokens (only their hashes are kept; the same stale CI secret repeated is not guessing), or after 300 wrong attempts of any kind, a backstop that protects the token store because wrong tokens are not cached. The block is checked before any lookup, so a blocked address gets 429 with `Retry-After` even with a valid token. Keyed per address, never globally (one guesser must not lock everyone out) and not per token prefix (each guess would be a fresh key).
- Alerts on 401 rate, 5xx rate, upstream failures and certificate expiry.
- **Consented break-glass bypass**: a documented customer-side switch to restore directly, used only with the customer's consent and recorded in the audit trail. Packmoat cannot enforce the cooldown while it is in use.

## 8. Security model

- **Boundaries**: client to proxy (token), proxy to Azure (SP cert), proxy to nuget.org (anonymous).
- **PROVED**: the client's Authorization header is never forwarded; the SP credential never reaches the client or the redirect target; response bodies are leak-checked.
- **Fail closed**: unknown date, date source down past the stale limit, or missing tenant config means Hide or refuse.
- **Dependency confusion**: names matching the tenant's internal prefixes are never served from the public feed. The spike let unknown packages through; the product must not.
- **SSRF guard**: only rewrite and forward known Azure hosts.
- **Never logged**: tokens, Authorization headers, SP tokens, certificates, query strings. Logs carry token IDs only.

## 9. Observability and audit

- Structured logs with request ID, tenant ID, token ID, verdict.
- Metrics: verdict counts, upstream latency, date-cache hit rate, 401 rate, 5xx.
- Alerts: date-source failures, 5xx spike, certificate expiring in 30 days, use of a revoked token.
- Audit trail (#136): every package download (time, package, version, decision, reason, token id), kept 90 days (`AuditPolicy.RetentionDays`; the database refuses to delete a younger row except in a tenant purge). It answers "which token received version X". Written off the request path through a bounded queue (10,000 events) and fails open: a full queue, a storage failure or a crash loses audit rows, never a download; drops and failures are counted and logged without token, package or version. Listings and metadata are not audited: the per-version decision record (#123) holds what was hidden or shown and why. Token use by day (`token_activity`) is kept 365 days.

## 10. Proved vs assumed

**PROVED** (spikes): token auth and per-tenant cooldown; Entra token as NuGet Basic password; Reader cannot trigger a save; SP gets 403 on a separate private feed; at portal defaults the private feed stays hidden; HTTPS restore through a public tunnel with a trusted certificate; lock-file and central-package-management behaviour; the credential provider adds no delay and declines non-Azure hosts; npm filtering.

**ASSUMED / NOT TESTED**: clean CI agent with the provider (blocked on secret writes); Container Apps; paged registration pages; multi-tenant (shared) app model, built but not proven live; old NuGet clients and Visual Studio; nuget.org date edge cases (unlisted, normalised versions); nuget.org rate limits; large-file streaming under load; Contributors exposure.

**Checklist (customer)**: Readers group only; never Contributors or the build service; no broad group role; trim to two upstreams.

## 11. MVP scope vs roadmap

- **MVP**: NuGet only, tenant tokens, cooldown, explicit internal prefixes, waivers and overrides hand-run through tenant config, audit log, hand-run onboarding.
- **Roadmap**: npm (about 4–5 days), admin UI customer self-service and certificate/identity handling (the operator site with write actions exists, see section 11a), per-user tokens, vulnerability resource pass-through, automated onboarding checks, SSO.

## 11a. Operator admin site (read and safe write actions; built, not yet run against real Entra ID)

- **Where**: the same Host process, on its own hostname (`PACKMOAT_ADMIN_HOST`, default empty = the site does not exist). `PackmoatApp` branches on the Host header before the proxy's GET-only gate and token middleware, which are untouched for every other host. Code: `core/dotnet/src/Packmoat.Host/Admin/`. The OpenID Connect and cookie packages are referenced by the Host project only; Domain, Policy and Application stay package-free (architecture test).
- **Sign-in**: Microsoft Entra ID, authorization-code flow with PKCE, one directory only (issuer pinned to it). Access is an allowlist of `(tid, oid)` pairs from `PACKMOAT_ADMIN_ALLOW`; email and user name are never read; an empty list admits nobody. The check sits in the cookie's sign-in event, so whatever the identity provider is, a person off the list gets 403 and no cookie.
- **Two doors**: the proxy never sees the admin cookie (the authentication middleware runs only inside the admin branch) and the admin pages never read a Packmoat token. Tests pin both directions.
- **Shared signed-in-site plumbing** (`core/dotnet/src/Packmoat.Host/Web/SignedInSite.cs`): security headers, error mapping, request limits, the antiforgery gate (one neutral host-only cookie, `__Host-packmoat-af`), cookie settings, the failed sign-in limiter and the common OpenID Connect settings. There is no app-wide default authentication scheme: each site branch runs only its own sign-in callback handler and authenticates only with its own named cookie scheme, so a second signed-in site can sit beside the admin site without either one acting on the other's host.
- **Session**: `__Host-` cookie, Secure, HttpOnly, SameSite=Lax, 30 minutes sliding, 8 hours absolute, encryption keys persisted under `/data/admin-keys` (`PACKMOAT_COOKIE_KEYS`, unset or `folder`), or, with `PACKMOAT_COOKIE_KEYS=database` (#232 B1), in the database's `cookie_keys` through `DatabaseXmlRepository` and the port `ICookieKeyStore` (`SqliteCookieKeyStore`: each key's XML encrypted with the master key under `cookie-key:<row id>`; a row it cannot read stops the start). In database mode the first start imports the folder's keys once (`CookieKeyImport`, one `BEGIN IMMEDIATE` transaction, only into an empty table) and never creates or changes the folder.
- **Pages**: tenants, one tenant (shows only the identity mode: "own app" or "Packmoat app, directory <id>"; the key is never read), audit (last 100), sign-out. All reads go through `AdminQueries` as the operator; every POST needs an antiforgery token; strict CSP and no-store on every response.
- **Writes** (`AdminWrites`, `AdminInput`): thin POST handlers over `AdminCommands` as `ActorScope.OperatorAs(<signed-in display name>)`; the site never builds a tenant scope (a test scans for it). Each needs antiforgery, an exact `Origin`, the re-checked allowlist, a per-address rate limit and server-side validation; destructive ones add a confirm page. Results go back as a redirect carrying a fixed message code, never typed text. The plaintext of an issued token exists only in that one response. Each write is recorded in the append-only `admin_actions` table (migration 5) **before** the change (fail closed), shown on `/actions`. Certificates and upstream identities stay CLI-only.
- Setup: [self-hosting.md](self-hosting.md), section 5. Not yet run against real Entra ID.

## 11b. Customer site (sign-in, tokens and waivers; built, not yet run against real Entra ID)

- **Where**: the same Host process on a third hostname, `PACKMOAT_APP_HOST` (for example app.contoso.example). Default empty means the site does not exist. If it is set but `PACKMOAT_APP_CLIENT_ID` or `PACKMOAT_APP_CERT` is missing or unreadable, every page answers 503. Code: `core/dotnet/src/Packmoat.Host/Customer/` on the shared `Web/SignedInSite` plumbing; own cookie (`__Host-packmoat-app`), own scheme names, own callback path (`/signin-microsoft`).
- **Sign-in**: a separate multitenant app registration. It is neither the admin app nor the shared upstream app. Authorization code + PKCE against `organizations`, scopes `openid profile`, a certificate client assertion and no secret. The issuer is accepted only when it equals `https://login.microsoftonline.com/{tid}/v2.0` for the token's own `tid`, and never for the consumer directory.
- **Mapping (confused-deputy rule)**: the pure `CustomerSignIn.Decide` (Application) takes the verified claims and the operator-made binding found by the token's `tid` (`sign_in_directories`, migration 7, one tenant per directory). It returns Admin, Developer (Entra app roles `Packmoat.Admin` / `Packmoat.Developer`; both mean Admin) or Denied(reason). The decision runs in the cookie's sign-in event, so a refused person never gets a cookie. The session holds the directory id, object id, display name and role. The binding is re-read on every request, so unbinding ends sessions.
- **Auto-join (#80)**: with no Packmoat role, `Decide(..., autoJoin)` lets a person in as Developer (never Admin) when the tenant's setting is on and the token shows a member: exactly one `acct` optional claim equal to `0`, and an `idp` that is absent or names the directory itself (`acct` `1` or another `idp` is `GuestNoRole`; a missing, repeated or other `acct` is `MemberStatusUnknown`: both fail closed). The setting is read only when the person has no role (Decide runs first with auto-join off). It is `auto_join_settings` (migration 11, one row per tenant, **no row means on**) behind `IAutoJoinSettingStore`, with no cache; a read error means no cookie for that person. With no binding it is never read. An auto-joined session carries `packmoat_join=auto`, and each of its requests reads the setting again, so turning it off ends those sessions on the next click (tokens they made stay until revoked). `AutoJoinCommands.SetAutoJoinAsync` (fresh Admin, the token guard, a rate limit, no-op when unchanged, the `admin_actions` intent row before the change) backs `POST /team/auto-join`.
- **Reads**: `CustomerQueries` takes a `CustomerActor` built only from the session and the binding, and reads through `AdminQueries` with `ActorScope.ForTenant`. The site never builds an operator scope (an architecture test scans for it).
- **Bindings**: operator CLI only, `tenant directory bind|unbind|show`, recorded in `admin_actions` with `site = 'cli'`.
- **Operator links (#186)**: the operator's name, support address and website come only from `PACKMOAT_OPERATOR_NAME`, `PACKMOAT_SUPPORT_EMAIL` and `PACKMOAT_SITE_URL` (privacy and terms derived as `<site>/privacy` and `<site>/terms`), parsed strictly by `OperatorLinks` in the composition root and handed down (`AppSiteSettings.Operator`; `SupportContact` to the Application and Infrastructure wording); unset, a link is left out or the sentence is neutral, and an architecture test keeps packmoat.com out of every string in `core/dotnet/src/`.
- Setup and Entra steps: [self-hosting.md](self-hosting.md), section 5; the Caddy block is in `deploy/Caddyfile`. Not yet run against real Entra ID.

## 11c. Operations and self-service (built)

- **Job lease (#232 M1, M2)**: migration 23 adds `job_leases(job TEXT NOT NULL PK, holder, expires_at)` and `cookie_keys(id TEXT NOT NULL PK, friendly_name, created, xml_encrypted BLOB)` (the second for #232 B1, read and written only with `PACKMOAT_COOKIE_KEYS=database`: the id is a GUID the store makes, the key ring's friendly name is kept apart because it repeats, and the XML is AesGcmSecretProtector bytes); neither holds tenant data (`SqliteTenantPurgeStore.CoreTables`). The port `IJobLease` (Application) has `TryAcquireAsync(job, holder, ttl)`, one statement on SQLite (`SqliteJobLease`: `INSERT ... ON CONFLICT(job) DO UPDATE ... WHERE job_leases.expires_at < @now OR job_leases.holder = @me`, rows affected 1 = acquired; the same statement runs on Postgres), and `ReleaseAsync` (sets `expires_at` to now, only for its own holder). The holder is a random id per process (`JobLeases.ProcessHolder`). `LeasedJob.RunAsync` takes the lease, runs and times the pass, and ends the lease in a `finally` (also at shutdown); `LeasedJobs.RunAsync` (Host) logs the duration, or the skip, at Information level. Every scheduled job runs through it: `RetentionService` and `packmoat-admin jobs run retention` share `retention`; a module names its own job (one name per job), and gets the lease from `PackmoatModuleContext.JobLease`. The lease is made by the composition root, not registered, so the composition is unchanged. TTL (`JobLeases.Ttl`) is one hour: far above any plausible pass, and no longer than the shortest interval; it matters only when a copy stops mid-pass without ending its lease. The holder id is one per process, so two passes of the same job at once inside one process would share the lease (none does today: each job has one service). The lease stops passes overlapping; it does not stop each copy running its own pass on its own schedule (every pass is safe to repeat: the purge nudge is claimed once per UTC day).

- **admin_actions retention (#117)**: kept 12 months (`TenantPurgePolicy.AdminActionsRetention`). Migration 18 lets the no-delete trigger pass only rows more than 365 days old by SQLite's own clock (an unreadable time never passes); UPDATE stays refused. `RetentionService` calls `IAdminActionLog.PruneAsync` daily: one transaction deletes the older rows and, when there were any, appends one `admin_actions.prune` row.
- **Upstream readiness (#63 H1)**: `UpstreamReadiness.Of(tenant, binding, verification, activeClientId)` (Application, pure): no binding is `NotSetUp`; an own-app binding is `Ready` when its URLs pass the create-time rule (`TenantUrlRules.Check` with https and a prefix not required, as the CLI and admin site always allowed; empty or invalid URLs are `NotSetUp`); a shared binding needs the strict rule (https and a prefix) and is `Ready` only with a matching `shared_app_verifications` record, else `NotVerified`. `CustomerTokenGuard` uses the three-argument `ServingRule.Decide(active, gate, readiness)` (operator switch, then the serving gate, then upstream), so a tenant with no upstream issues no token on the customer site (`TenantNotSetUp`, "This organisation isn't connected to an Azure Artifacts feed yet."). The proxy's `AuthenticateAsync` keeps the two-argument rule on purpose: the deploy canary has no upstream and must keep answering `/v3/ping`, and a tenant without a working upstream already fails closed per request. Migration 13 adds `feed_checks` (each run of the feed setup check: who, what was checked, outcome, results and confirmed admins as JSON; behind `IFeedCheckStore`) and `shared_app_verifications.verified_via` (`operator`, the default and every existing row, or `wizard`) and `feed_check_id`. `CheckStatus`, `CheckResult` and `OnboardingSummary` live in Application behind the `IFeedSetupCheck` port; `OnboardingChecks` (the HTTP) stays in Infrastructure.
- **Connect and withdraw (#64 I1)**: migration 14 adds `feed_checks.kind` (`settings`, the default and every existing row, or `developer`) and `shared_app_verifications.developer_check_id` (a foreign key). `ConnectRules` (Application, pure) decides whether a settings check and a developer check qualify (every required result PASS, the 24-hour and 7-day windows, two different people, the GUID-built URL shape) and holds the withdraw rule. `ConnectFeedCommands` checks the person (switch, Admin, fresh sign-in); `ISelfServiceFeedStore.ConnectSelfServiceFeedAsync` (`SqliteSelfServiceFeedStore`) re-checks the tenant and both rows in one `BEGIN IMMEDIATE` transaction and writes the URLs, the shared binding, the `wizard` verification and `feed.connect`, or nothing. A failed re-check of a connected self-made tenant withdraws the exact verification with `feed.withdrawn` (`AzureSetupCommands`). `RetentionService` blanks feed checks' personal data daily (confirmed admins, the checker's display name and result details, #107).
- **Self-service waivers, core (#157 PR 1)**: migration 20 adds `waivers.source` (`operator`, the default and every existing row, or `customer`; a CHECK) and `waivers.revoked_by` (NULL for older rows). `AdminCommands.AddWaiverAsync` keeps the operator's limits (1 to 3650 days, `*` allowed, no cap) and, for a tenant actor only, applies `WaiverRules` (Application, pure) before anything is written: `CleanPackageId`, `CleanExactVersion` (two to four numeric parts, optional pre-release and build, never `*`, normalised with `NuGetVersionKey` as the engine compares), 1 to 30 days, a reason of 10 to 200 plain characters, and no package matching `InternalPrefixes.Matches` (Domain; the same rule `InternalNamespacePolicy` uses, which runs before waivers in the engine). Refusals are `WaiverRefusedException` with a `WaiverRefusal` reason and a fixed message. `ITenantStore.AddCustomerWaiverAsync` (`SqliteTenantStore`) does, in one `BEGIN IMMEDIATE` transaction, the tenant read (its origin, for the alert), the duplicate check (an active waiver for the same package and normalised version; `*` only matches `*`; applies to operator inserts too), the count of active customer waivers against `WaiverRules.MaxActiveCustomerWaivers` (25; operator, expired and revoked rows do not count), the insert and the revision bump; a refusal writes nothing. The insert that makes the count exactly 25 raises `OpsAlert.WaiverCapReached` (a `TenantRef` and the cap only). `RevokeWaiverAsync` records `revoked_by` (the actor's name) and, for a tenant actor, still filters on `AND tenant_id`. `AdminQueries.ListWaiversAsync` returns a tenant's waivers with history (active ones first, then newest first, so the 200-row limit never hides an active waiver; scoped like every query). The admin site shows a Source column. The cap alert is raised at most once per tenant per hour (`AdminCommands.CapAlertInterval`, in memory).
- **Self-service waivers, customer site (#157 PR 2)**: `CustomerWaiverCommands` and `CustomerWaiverQueries` (Application) follow the token pages' pattern: `CustomerTokenRules.RequireFreshAdmin` (extracted, shared with `CustomerTokenCommands`) first, because the core's `ActorScope` carries no role and no sign-in time; then `CustomerTokenGuard` (binding and active tenant; the tenant only from the actor); a `PersonRateLimiter` (10 adds and 10 revokes per person per hour); input cleaned with `WaiverRules` before anything is recorded; the `admin_actions` intent row (site `app`, the reason last and quoted) before the write; then the core `AdminCommands` (its own instance, so the customer site never names it) as `ActorScope.ForTenant(tenant, "Name (oid)")`. A revoke looks the id up in the guard's tenant first: another tenant's, an unknown and a malformed id (not `^wvr_[0-9a-f]{12}$`) all return false with no record. `AppWaivers` (Host) is thin: `/waivers` for every role (Developers: the active list only, no forms), `POST /waivers/create`, `GET`/`POST /waivers/revoke` and `POST /waivers/reauth` for Admins, with Origin, antiforgery, a single-use `AppFormNonces` nonce, the role and fresh sign-in checked before a form is read, and every stored text HTML-encoded. An architecture test pins the fields it reads (`days`, `m`, `nonce`, `package`, `reason`, `version`, `waiver`).

## 12. Biggest remaining risks, in order

1. **Customer trust**: a third party holds a credential into their Azure org; vendor review may stall it. The hosting provider's own certifications help a sub-processor review but are not Packmoat's.
2. **Customer misconfiguration** exposing private feeds, or leaving developer groups on the public-only feed. The checklist and verification script are the only control.
3. **Single IP, single host**: DDoS or a blackhole of the server's one address takes every customer down. A provider's network protection usually covers layers 3 and 4 only; HTTP floods are ours to handle. Mitigations: the minimum protections in section 7.
4. **Availability**: the proxy is in every restore path and fails closed. We need the break-glass procedure and monitoring.
5. **Date-source correctness and limits**: nuget.org edge cases, rate limits, outages.
6. **Client compatibility**: older NuGet, Visual Studio, paged registrations.
7. **Azure behaviour changes** and per-principal licensing.
8. **State and backups**: SQLite on one disk means a lost server without a tested restore loses tenants, tokens and the audit trail.
9. **Unverified clean-CI behaviour** of the credential provider.
