# Packmoat: the behaviour every implementation follows

This is the language-neutral description of what Packmoat does to a NuGet feed. The .NET implementation is in `dotnet/`. A second
implementation (for example TypeScript, beside it) must behave the same way. The shared test cases in `vectors/` are the proof: an
implementation conforms when it gives the expected output for every case.

Words used here:

- **Upstream feed**: the private NuGet v3 feed Packmoat sits in front of (for example an Azure Artifacts feed).
- **Tenant**: one organisation using Packmoat. It has one upstream feed, one policy and its own tokens.
- **Version**: one version of one package, such as `Fabrikam.Json 4.21.0`.
- **Verdict**: the decision about one version: **allow** or **hide**, with a reason code (below).
- **Fail closed**: when something is unknown or broken, hide or refuse. Never serve by default.

## 1. What the proxy hides, and why

A new package version is the riskiest one: a hijacked maintainer account or a poisoned release is usually found and pulled within days.
Packmoat makes NuGet clients behave as if young versions did not exist yet, so a restore picks the newest version that has been public for
long enough. It also hides public packages whose names look like the tenant's own internal packages (dependency confusion).

Concretely, for each version the upstream feed lists, Packmoat hides it when:

- its id starts with one of the tenant's **internal prefixes** (always, whatever else is set); or
- it is younger than the tenant's **cooldown** (in days), and no **waiver** allows it; or
- its publish date is unknown, and the cooldown is not 0 (fail closed).

Hidden versions are removed from version lists and registrations, and a request for a hidden version's file is answered 404. Clients only
see what was allowed. Packmoat is read-only: it offers restore, never search or publish.

## 2. A feed request, in order

1. **No token needed:** `/healthz` answers 200. Everything else goes through the token check (section 5).
2. **The token check.** No token or a wrong one is 401; a store or gate that cannot answer is 503. Rate limits (429) also apply here;
   their numbers are deployment settings, not part of this spec. `/v3/ping` is the authenticated liveness check: once the token check
   passes it answers 204, and nothing is fetched upstream.
3. **The path.** The client asks `/v3/index.json` (the tenant's feed index) or `/v3/u/{path}`, where `{path}` is a path on the upstream.
   - The path must sit under the tenant's own organisation prefix, compared in any case and in whole segments (`contoso/` never admits
     `contoso-other/`). An empty prefix admits every path; the other checks here still apply.
   - It must have no `..` or `.` segment, no backslash, no NUL, and no encoded `/` or `\` (`%2f`, `%5c`, `%2e%2e`).
   - Otherwise: 404, and nothing is fetched. A tenant with no upstream set up yet also gets 404.
4. **What is asked for.** The upstream path is split into segments, each unescaped and lower-cased, and checked in this order. The
   first match wins (vectors: `nuget/routes`):
   1. a `flat2` segment anywhere, followed by:
      - exactly `{id}/index.json`: a **version list**;
      - exactly `{id}/{version}/{file}`: a **package file**. The file must be `{id}.{version}.nupkg`, where the version matches after
        normalising (section 3), or `{id}.nuspec`. Any other file is unknown;
      - anything else: unknown. So `…/flat2/v3/index.json` is the version list of a package called `v3`, not a service index;
   2. a segment starting `registrations`, followed by an id and at least one more segment:
      - exactly `index.json`: a **registration index**;
      - `page/…`: a **registration page**;
      - exactly one `{version}.json`: a **registration leaf**;
      - anything else: unknown;
   3. last two segments `v3/index.json`: the **service index**;
   4. exactly `/{org}[/{project}]/_apis/packaging/{feed}/nuget/commitLog/{hex id}`: a **catalog entry** (it lists no versions);
   5. anything else is **unknown** and answered 404 (fail closed).
5. **The upstream is fetched.** Its 404 stays 404. Any other failure is 502, and nothing from a failed fetch is passed on. Failures
   include:
   - another status;
   - an unreachable feed;
   - a body that is not the expected JSON shape (for example a service index with no `resources`, a non-string `@type`, or a
     non-string version in a version list). In a registration a non-string `catalogEntry.version` is not a failure: the leaf's
     version comes from its `@id` instead (step 6);
   - a JSON body over the size limit.
6. **The answer is filtered**, by kind:
   - **Service index.** Only resources whose `@type` starts with `PackageBaseAddress` or `RegistrationsBaseUrl` stay. The match is
     case-sensitive (`packagebaseaddress/3.0.0` goes), and a resource with no `@type` goes. Search, publish and every other resource go,
     so clients cannot browse around the filter. Other properties of the document stay.
   - **Version list.** Only allowed versions stay, in their order.
   - **Registration.**
     - It is an **index** when its `items` holds at least one object with a non-null `lower` or `upper`, or an `items` array; otherwise
       it is a **page**, whose `items` are the leaves.
     - An entry of `items` that is not an object (in a page, or in an index's inline page) is kept as it is, and counted in `count`.
     - Only allowed leaves stay. A leaf's version is its `catalogEntry.version` when that is a string, and that version always wins
       over the `@id`. Otherwise it is the last segment of its `@id`, URL-unescaped, without its `.json` ending (matched in any case).
     - A leaf with no readable version is removed, whatever the verdicts say.
     - In an index, each inline page's `count` is set to the leaves left, added if it was missing. A page emptied this way stays,
       with `count` 0, and the index's own `count` (the number of pages) is not touched.
     - In a page, its own `count` is set to the leaves left.
     - `lower` and `upper` are left as they were. A page given only as a link (no inline `items`) stays, and is filtered when the
       client fetches it through Packmoat.
   - **Package file and registration leaf.** The one version is decided.
     - Hidden: 404, with the header `X-Packmoat-Reason` (table below) and the text body `Hidden by Packmoat policy ({header value}).`
     - `date_source_unavailable`: 503, as below.
     - Allowed: a package file is streamed as is, never buffered; a registration leaf is served with its URLs pointed back (step 7).
   - **Catalog entry.** Served with its URLs pointed back (step 7).
7. **URLs point back at Packmoat.** In every JSON answer, each occurrence of the tenant's upstream base URL (matched in any case) is
   replaced by Packmoat's own base plus `/v3/u/`, so the client's next request comes back through the filter.

A version list or registration asks for the verdict of every version it lists, at once. A version with no verdict is hidden, and
verdicts for versions the body doesn't list are ignored. If any listed version's verdict is `date_source_unavailable`, the whole answer
is 503 with `Retry-After: 30`. A shorter list would look like "no such version", which is worse than "try again".

**`X-Packmoat-Reason`.** Its values are the .NET names of the reasons, not the stable codes. A second implementation must send exactly
these strings:

| Header value | Code |
| --- | --- |
| `Cooldown` | `cooldown` |
| `InternalNamespace` | `internal_namespace` |
| `DateUnknown` | `date_unknown` |
| `NoPolicyDecided` | `no_policy_decided` |
| `PolicyError` | `policy_error` |

The header is never sent for `cooldown_elapsed` or `waiver` (served), or for `date_source_unavailable` (503 instead).

## 3. The decision for one version

The rules run in this order. The first rule that decides wins:

1. **Internal prefix.**
   - If the id (trimmed, lower-cased) starts with one of the tenant's internal prefixes: **hide**, `internal_namespace`.
   - Each prefix is trimmed, then every trailing `*` is stripped (with no second trim), then it is lower-cased.
   - A prefix that is then empty (including a bare `*`) is ignored, never "match everything".
   - Nothing overrides this rule, not even a waiver.
2. **Waiver.** The tenant's waivers are checked in their order, and the first that matches wins: **allow**, `waiver`, with that
   waiver's id. A waiver matches when:
   - it has not expired (it is expired from the exact instant of its expiry onwards);
   - its package id, trimmed, equals the id in any case;
   - its version, trimmed, is `*` (every version) or equals the version after normalising both.
3. **Cooldown.**
   - The cooldown is the tenant's, unless a per-package override names this package (trimmed, in any case). Then the first such
     override replaces it, shorter or longer.
   - Cooldown 0: **allow**, `cooldown_elapsed`, with no date needed.
   - No publish date known: **hide**, `date_unknown` (fail closed).
   - Otherwise age = now − publish date, as elapsed time; a cooldown of N days is N × 86,400 seconds, with no calendar or time-zone
     rules. Age < cooldown: **hide**, `cooldown`. Age ≥ cooldown: **allow**, `cooldown_elapsed`.
   - A version exactly as old as the cooldown is allowed, a publish date in the future is young, and prereleases follow the same rule.
4. **No rule decided**: **hide**, `no_policy_decided`. **A rule failed** (threw): **hide**, `policy_error`.

**Publish dates are fetched only when a rule needs one.**
- Rules 1 and 2, and a cooldown of 0, decide without a date, so they still decide while the date source is down.
- When a date is needed and the date source fails (an outage, not a missing date), the verdict is **hide**, `date_source_unavailable`,
  and the request is answered 503 (section 2).
- A date source that answers but has no date for the version gives `date_unknown`.

**Version normalising** is used to match waivers, file names and publish dates. It does this, in order:
1. Trim, lower-case, and drop everything from the first `+` (build metadata).
2. Split off the `-prerelease` part at the first `-`, and keep it as it is.
3. Split the rest at `.`. Every part must be plain digits that fit an unsigned 64-bit integer. Otherwise the whole version is used as
   it is after step 1 (trimmed, lower-cased, without metadata).
4. Write each number without leading zeros (`01.02.03` becomes `1.2.3`), pad to three parts (`1.0` becomes `1.0.0`), and drop a fourth
   part that is `0` (`1.0.0.0` becomes `1.0.0`).

**What a verdict reports**, so it can be explained later:
- The cooldown rule reports the cooldown it applied (`cooldownDaysApplied`), and the publish date it used (`publishedAt`), only when it
  had one.
- A waiver verdict reports the waiver's id (`waiverId`).
- Internal-prefix verdicts, and `date_source_unavailable`, `no_policy_decided` and `policy_error`, report none of these.

### Reason codes

| Code | Decision | Meaning |
| --- | --- | --- |
| `cooldown` | hide | younger than the cooldown |
| `cooldown_elapsed` | allow | old enough (or a cooldown of 0) |
| `waiver` | allow | an unexpired waiver allows it |
| `internal_namespace` | hide | matches an internal prefix |
| `date_unknown` | hide | no publish date (fail closed) |
| `date_source_unavailable` | hide | the date source failed: answer 503, try again |
| `no_policy_decided` | hide | no rule decided (fail closed) |
| `policy_error` | hide | a rule failed (fail closed) |

The codes are stable. An implementation may name its reasons anything inside, but these are the names in the vectors.

## 4. Fail-closed cases

| Case | Answer |
| --- | --- |
| No token, a wrong, revoked or expired token, or a suspended tenant | 401 with a challenge |
| The token store or the serving gate cannot answer | 503 |
| A path outside the tenant's organisation, or with traversal or encoded separators | 404, nothing fetched |
| A path that is not one of the known kinds, or a package file not matching its id and version | 404, nothing fetched |
| A version with no verdict, or a leaf whose version cannot be read | hidden |
| No publish date for a version that needs one | hidden (`date_unknown`) |
| The date source fails, for a version that needs a date | 503 with `Retry-After: 30`, nothing listed |
| No rule decides, or a rule fails | hidden |
| The upstream fails, or sends something other than the expected JSON, or too much of it | 502 (404 stays 404) |

## 5. The token check

1. **Read the token** from the `Authorization` header. The scheme name matches in any case:
   - `Bearer {token}`: the token, trimmed. The scheme name must be followed by a space (not a tab or nothing), and the same holds
     for `Basic`;
   - `Basic {base64}`: the base64 is trimmed and decoded. It must be standard base64 with its `=` padding: unpadded base64 gives
     no token. Whitespace inside the base64 (such as spaces and tabs) is ignored, as .NET's decoder does. The bytes are read as
     UTF-8, and bytes that are not UTF-8 become U+FFFD replacement characters, so such a token never matches. The token is
     everything after the first `:`, colons included, not trimmed; the user name is ignored. With no `:`, the whole decoded text is
     the token;
   - an empty token, base64 that does not decode, another scheme, or no header: **no token**.
2. **No token**: 401 with `WWW-Authenticate: Basic realm="Packmoat"`. NuGet asks every URL first with no credentials and retries after
   this challenge, so it is not counted as a guess.
3. **Look the token up**, in this order. Each failure is the same answer as a wrong token: 401 with the same challenge, and the attempt
   is counted against the client's address.
   1. Its hash must be stored and not revoked. Tokens are stored only as the lower-case hex SHA-256 of the UTF-8 token, and they match
      exactly (case matters).
   2. It must not be expired. It is expired from the exact instant of its expiry.
   3. Its tenant must exist and be active. A suspended tenant's token is refused here, and the serving gate is not asked.
   4. The tenant's serving gate, if a module installs one, must not refuse it.
4. **The store or the gate cannot answer** (an error, not a "no"): 503, not counted. Never served.
5. **Otherwise** the request goes on as that token's tenant.

In production a successful lookup is remembered for up to 5 seconds, so for that long a token just revoked, or a tenant just suspended,
may still be served. The memory never outlasts the token's own expiry or a time the gate gave. The vectors check the lookup itself,
without that memory.

| Code | Answer |
| --- | --- |
| `authenticated` | the request goes on, as the token's tenant |
| `no_credentials` | 401 with the challenge, not counted |
| `token_rejected` | 401 with the challenge, counted as a guess |
| `unavailable` | 503 |

Packmoat limits wrong guesses per client address and requests per token (429 with `Retry-After`). The limits are deployment settings and
are not in the vectors.

## 6. The vectors

`vectors/{area}/{group}.json` holds one group of cases, and `schema/{area}/{group}.json` is its JSON Schema (draft-07). Each vector
file is a JSON object:

```json
{
  "description": "optional text",
  "cases": [ { "name": "...", "now": "2026-10-10T12:00:00Z", "input": { }, "expected": { } } ]
}
```

- `name`: unique in its file, a plain sentence saying what the case shows.
- `now`: the time the case runs at. Times everywhere are UTC, written `YYYY-MM-DDTHH:MM:SSZ`. They may have a fraction of a second
  of at most 3 digits (milliseconds, as JavaScript's `Date` keeps them), and a time an implementation writes has a fraction only when
  there is one.
- `input` is what the implementation is given, and `expected` is what it must produce. A case may also have a `note` (text).
- The output is compared **as JSON**: the same values, whatever the property order or spacing. A field the case does not expect must
  not be there. Array order matters.

**What a loader must refuse.** It refuses the whole file when any of these is wrong:
- **The schema.** Every object is closed, so a missing, unknown or misspelled field at any depth is refused, and so is a value of the
  wrong type. Every code (verdict reasons, token outcomes, route kinds) and every enum (`gate`, `store`) must be one the schema lists.
  Every time must be in the form above. A file with no cases is refused. The one open object is a NuGet `body`: it is the upstream's
  JSON, so any JSON is allowed there.
- **What a schema cannot say.** Names must be unique in the file. Every time (`now`, `expiresAt`, `publishedAt`, and each value in
  `publishDates`) must be a real calendar date and time (no 30 February).
- **The groups.** Every file must be one of the groups below, and every group must have a file.

The .NET loader checks the schema with NJsonSchema (MIT, a test-only package), then makes the other checks itself.

The same rules are also checked without .NET, so the schemas are proven to work for any implementation: `check/check.mjs` uses Node
and Ajv (MIT, pinned in its lockfile) in strict mode. Run `npm ci` and then `npm run check` in `check/`. The conformance workflow runs it
on every pull request, beside the .NET implementation's run of every vector.

### `policy/decisions`

One version's verdict, from the tenant's policy, the time and the publish dates (section 3).

- `input.policy`: `cooldownDays`, and optionally:
  - `internalPrefixes` (strings);
  - `waivers` (`id`, `packageId`, `version`, `expiresAt`);
  - `packageOverrides` (`packageId`, `cooldownDays`).
- `input.package`: `id` and `version`, as the client asked.
- `input.publishDates`: what the date source returns for the package, by normalised version. It is `{}` when the source has no
  dates, and `null` when the date source fails.
- `expected`:
  - `decision` (`allow` or `hide`) and `reason` (a code);
  - `waiverId`, `cooldownDaysApplied` and `publishedAt`, each present exactly when the verdict reports it.

### `nuget/flat-container`, `nuget/registration`, `nuget/service-index`

One upstream JSON answer, filtered (section 2, steps 6 and 7).

- `input.upstreamBase` is the tenant's upstream base URL, and `input.proxyBase` is Packmoat's base plus `/v3/u/`.
- `input.body`: the upstream's JSON body.
- `input.verdicts` (not for the service index): the verdict reason code for each version, as the version is written in the body.
  - The code decides: `cooldown_elapsed` and `waiver` allow, and every other code hides.
  - A version missing here has no verdict. A verdict for a version the body doesn't list is ignored.
- `expected`: one of these:
  - `status` 200 with the filtered `body`;
  - `status` 503 (no body) when a listed version's verdict is `date_source_unavailable`;
  - `status` 502 (no body) when the body is not the expected JSON shape.

### `nuget/routes`

What an upstream path asks for (section 2, step 4).

- `input.path`: the upstream path, as it would be fetched.
- `expected`:
  - `kind`: one of `service_index`, `flat_container_versions`, `flat_container_content`, `registration_index`, `registration_page`,
    `registration_leaf`, `catalog_entry` or `unknown`;
  - `packageId` and `version`, each present exactly when the path names them (lower-cased and unescaped).

### `auth/token-check`

One feed request's token check (section 5).

- `input.authorization`: the `Authorization` header, or `null` for none.
- `input.tenants`: `id`, and optionally:
  - `active` (default `true`);
  - `gate`: `serve` (the default), `refuse`, or `fail` (it cannot answer).
- `input.tokens`: `token` (the plaintext) and `tenant`, and optionally `revoked` (default `false`) and `expiresAt` (default: never).
- `input.store`: `available` (the default) or `unavailable` (the token store cannot answer).
- `expected`:
  - `status` and `reason` (a code);
  - `tenant`, when authenticated;
  - `challenge` (the `WWW-Authenticate` value), on a 401.

### How the .NET implementation runs them

`dotnet/tests/Packmoat.Host.Tests/ConformanceTests.cs` runs every case through the production code that answers it on a real
request:

| Group | Production code |
| --- | --- |
| `policy/decisions` | the package gate and policy engine |
| `nuget/routes` | the route parser |
| the other `nuget/*` groups | the version filters, the verdict rule and the URL map, with any other failure of the transform answered 502 as the proxy does |
| `auth/token-check` | the token middleware in front of the SQLite token store |

`ConformanceVectors.cs` is the loader. It finds the files from the `core/` folder, so `core/` alone is enough to run them.
`ConformanceVectorLoaderTests.cs` proves the loader refuses malformed files, and that the schemas list exactly the codes the .NET code
maps.

### Adding a case

Add it to the group's file with a new name, run the .NET tests, and check that it fails when the behaviour it describes is broken. Use
made-up names only (Contoso, Fabrikam, Northwind, `example.com` hosts), never real customers, hosts or tokens.
