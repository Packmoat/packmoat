using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Auth;
using Packmoat.Host.Proxy;
using Packmoat.Infrastructure;
using Packmoat.Policy;
using Packmoat.Protocols.NuGet;
using static Packmoat.Host.Tests.ConformanceVectors;

namespace Packmoat.Host.Tests;

/// <summary>
/// The .NET implementation against the shared vectors in spec/vectors: each case runs through the production code that answers
/// it on a real request, and its output is compared as JSON with the case's expected output. See spec/README.md.
/// </summary>
public class ConformanceTests
{
    public static TheoryData<string> PolicyCases => Names(PolicyDecisions);
    public static TheoryData<string> FlatCases => Names(FlatContainer);
    public static TheoryData<string> RegistrationCases => Names(Registration);
    public static TheoryData<string> ServiceIndexCases => Names(ServiceIndex);
    public static TheoryData<string> RouteCases => Names(Routes);

    /// <summary>What the proxy makes of an upstream path: the route parser, which decides which filter (if any) an answer gets.</summary>
    [Theory, MemberData(nameof(RouteCases))]
    public void NuGet_routes(string name)
    {
        var c = Case(Routes, name);
        var route = NuGetRouteParser.Parse((string)c.Input["path"]!);
        var actual = new JsonObject { ["kind"] = KindCodes[route.Kind] };
        if (route.PackageId is { } id) actual["packageId"] = id;
        if (route.Version is { } version) actual["version"] = version;
        AssertJson(c.Expected, actual, name);
    }

    /// <summary>The package gate the proxy asks (the policy engine, and the date source only when a rule needs a date), at the case's time.</summary>
    [Theory, MemberData(nameof(PolicyCases))]
    public async Task Policy_decisions(string name)
    {
        var c = Case(PolicyDecisions, name);
        var p = c.Input["policy"]!.AsObject();
        var policy = new TenantPolicy(
            (int)p["cooldownDays"]!,
            p["internalPrefixes"]?.AsArray().Select(x => (string)x!).ToArray(),
            p["waivers"]?.AsArray().Select(w => new Waiver((string)w!["id"]!, (string)w["packageId"]!, (string)w["version"]!,
                Time((string?)w["expiresAt"]) ?? throw new MalformedVectorException($"{name}: a waiver's expiresAt is not a UTC time"),
                "conformance vector", "conformance")).ToArray(),
            p["packageOverrides"]?.AsArray().Select(o => new PackageOverride((string)o!["packageId"]!, (int)o["cooldownDays"]!)).ToArray());
        var tenant = new Tenant("contoso", "Contoso", "https://upstream.example.com/", "contoso/",
            "https://upstream.example.com/contoso/_packaging/feed/nuget/v3/index.json", policy);
        var package = c.Input["package"]!.AsObject();
        var gate = new PackageGate(PolicyEngine.CreateDefault(new FakeClock { UtcNow = c.Now }), new VectorDates(c.Input["publishDates"] as JsonObject));

        var verdict = await gate.DecideAsync(tenant, (string)package["id"]!, (string)package["version"]!, default);

        AssertJson(c.Expected, VerdictJson(verdict), name);
    }

    [Theory, MemberData(nameof(FlatCases))]
    public void NuGet_flat_container(string name) => RunNuGet(Case(FlatContainer, name),
        (body, hidden, map) => VersionFilters.FilterFlat(body, hidden, map));

    [Theory, MemberData(nameof(RegistrationCases))]
    public void NuGet_registration(string name) => RunNuGet(Case(Registration, name),
        (body, hidden, map) => VersionFilters.FilterRegistration(body, hidden, map));

    [Theory, MemberData(nameof(ServiceIndexCases))]
    public void NuGet_service_index(string name) => RunNuGet(Case(ServiceIndex, name),
        (body, _, map) => ServiceIndexRewriter.Rewrite(body, map));

    /// <summary>
    /// What the proxy does with an upstream JSON answer: the filter, asking the verdicts the way the proxy does (a version with no verdict is
    /// hidden; a date-source outage turns the answer into a 503), then the URL map.
    /// </summary>
    private static void RunNuGet(VectorCase c, Func<string, Func<string, bool>, UrlMap, string> filter)
    {
        var map = new UrlMap((string)c.Input["upstreamBase"]!, (string)c.Input["proxyBase"]!);
        var verdicts = (c.Input["verdicts"] as JsonObject ?? new JsonObject())
            .ToDictionary(kv => kv.Key, kv => ConformanceVectors.Verdict((string)kv.Value!), StringComparer.Ordinal);
        JsonObject actual;
        string? error = null;
        try
        {
            var output = filter(c.Input["body"]!.ToJsonString(), v => VerdictFilter.IsHidden(verdicts, v), map);
            actual = new JsonObject { ["status"] = 200, ["body"] = JsonNode.Parse(output) };
        }
        catch (TemporarilyUnavailableException)
        {
            actual = new JsonObject { ["status"] = 503 };
        }
        catch (Exception e)
        {
            // The proxy answers any other failure of the transform (JSON not of the expected shape) with 502.
            actual = new JsonObject { ["status"] = 502 };
            error = e.GetType().Name + ": " + e.Message;
        }
        AssertJson(c.Expected, actual, c.Name + (error is null ? "" : "\n(the transform threw " + error + ")"));
    }

    internal static JsonObject VerdictJson(Verdict v)
    {
        var o = new JsonObject { ["decision"] = v.IsAllowed ? "allow" : "hide", ["reason"] = Code(v.Reason) };
        if (v.WaiverId is { } w) o["waiverId"] = w;
        if (v.CooldownDaysApplied is { } d) o["cooldownDaysApplied"] = d;
        if (v.PublishedAt is { } p) o["publishedAt"] = Format(p);
        return o;
    }

    /// <summary>The case's publish dates (keys are normalised versions); null input means the source fails, as a real outage does.</summary>
    private sealed class VectorDates(JsonObject? dates) : IPublishDateSource
    {
        public Task<IReadOnlyDictionary<string, DateTimeOffset>?> GetPublishDatesAsync(Ecosystem ecosystem, string packageId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>?>(dates?.ToDictionary(kv => kv.Key,
                kv => Time((string?)kv.Value) ?? throw new MalformedVectorException($"publish date for {kv.Key} is not a UTC time")));
    }
}

/// <summary>
/// The token check: the real middleware in front of the real SQLite token store, holding exactly the case's tenants and tokens. A 401 is
/// told apart by the guess limiter: a presented token that matched no one is counted (token_rejected), a request with no token is not
/// (no_credentials).
/// </summary>
[Collection("sqlite-pools")]
public class TokenCheckConformanceTests
{
    public static TheoryData<string> TokenCases => Names(TokenCheck);

    private static readonly IPAddress Client = IPAddress.Parse("192.0.2.10");

    [Theory, MemberData(nameof(TokenCases))]
    public async Task Token_check(string name)
    {
        var c = Case(TokenCheck, name);
        var dir = Directory.CreateTempSubdirectory("packmoat-vector-");
        try
        {
            var clock = new FakeClock { UtcNow = c.Now };
            var db = new SqliteDatabase(Path.Combine(dir.FullName, "packmoat.db"));
            await db.MigrateAsync([]);
            var tenants = new SqliteTenantStore(db, clock, AesGcmSecretProtector.FromBase64(AesGcmSecretProtector.GenerateKeyBase64()), []);
            var gates = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in c.Input["tenants"]?.AsArray() ?? [])
            {
                var id = (string)t!["id"]!;
                await tenants.CreateAsync(new NewTenant(id, id, "https://upstream.example.com/", id + "/",
                    $"https://upstream.example.com/{id}/_packaging/feed/nuget/v3/index.json", 14, TenantOrigin.Operator), default);
                if (t["active"] is { } active && !(bool)active!) await tenants.SetActiveAsync(id, false, default);
                gates[id] = (string?)t["gate"] ?? "serve";
            }
            var store = new SqliteTokenService(db, tenants, clock, new VectorGate(gates));
            foreach (var k in c.Input["tokens"]?.AsArray() ?? [])
                await AddToken(db, store, (string)k!["tenant"]!, (string)k["token"]!, k["revoked"] is { } r && (bool)r!, (string?)k["expiresAt"], clock);

            ITenantAuthenticator authenticator = (string?)c.Input["store"] switch
            {
                null or "available" => store,
                "unavailable" => new FailingStore(),
                var other => throw new MalformedVectorException($"{name}: unknown store \"{other}\""),
            };
            var limiter = new TokenGuessLimiter(clock);
            var middleware = new TokenAuthMiddleware(ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; },
                authenticator, limiter, new TokenRateLimiter(clock, 1000), NullLogger<TokenAuthMiddleware>.Instance);
            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = "/v3/index.json";
            context.Connection.RemoteIpAddress = Client;
            if ((string?)c.Input["authorization"] is { } header) context.Request.Headers.Authorization = header;

            await middleware.InvokeAsync(context);

            var status = context.Response.StatusCode;
            var actual = new JsonObject
            {
                ["status"] = status,
                ["reason"] = status switch
                {
                    200 => "authenticated",
                    503 => "unavailable",
                    401 => limiter.TokenKeysFor(ClientAddress.Key(Client)).Count > 0 ? "token_rejected" : "no_credentials",
                    _ => "unexpected",
                },
            };
            if (status == 200) actual["tenant"] = TokenAuthMiddleware.GetTenant(context).Id;
            if (context.Response.Headers.WWWAuthenticate.ToString() is { Length: > 0 } challenge) actual["challenge"] = challenge;
            AssertJson(c.Expected, actual, name);
        }
        catch when (TempDir.Failing(dir.FullName)) { throw; }
        finally { TempDir.Delete(dir.FullName, clearSqlitePools: true); }
    }

    /// <summary>
    /// Issues a token the way the store does, then gives it the case's plaintext, revocation and expiry. The stored hash is computed here,
    /// independently of the product's hasher (lower-case hex SHA-256 of the UTF-8 token), so a change to the hashing fails the vectors.
    /// </summary>
    private static async Task AddToken(SqliteDatabase db, SqliteTokenService store, string tenant, string token, bool revoked, string? expiresAt, FakeClock clock)
    {
        var issued = await store.IssueAsync(tenant, "vector", default);
        var expires = expiresAt is null ? (DateTimeOffset?)null : Time(expiresAt) ?? throw new MalformedVectorException($"expiresAt \"{expiresAt}\" is not a UTC time");
        await using var connection = await db.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE tokens SET token_hash=@h, revoked_at=@r, expires_at=@e WHERE id=@id";
        cmd.Parameters.AddWithValue("@h", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant());
        cmd.Parameters.AddWithValue("@r", revoked ? clock.UtcNow.UtcDateTime.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("@e", expires is { } e ? e.UtcDateTime.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("@id", issued.TokenId);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
    }

    /// <summary>Each tenant's serving gate: serve, refuse, or fail (throw, as a gate that cannot answer does).</summary>
    private sealed class VectorGate(IReadOnlyDictionary<string, string> gates) : IServingGate
    {
        public Task<ServingAnswer> AskAsync(string tenantId, CancellationToken cancellationToken) => gates[tenantId] switch
        {
            "serve" => Task.FromResult<ServingAnswer>(new ServingAnswer.Serve()),
            "refuse" => Task.FromResult<ServingAnswer>(new ServingAnswer.Refuse()),
            "fail" => throw new InvalidOperationException("the gate cannot answer"),
            var other => throw new MalformedVectorException($"unknown gate \"{other}\""),
        };
    }

    private sealed class FailingStore : ITenantAuthenticator
    {
        public ValueTask<AuthResult?> AuthenticateAsync(string token, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the token store cannot answer");
    }
}
