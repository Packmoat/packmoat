using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Npgsql;
using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host;
using Packmoat.Host.Admin;
using Packmoat.Host.Web;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// Two copies of the server starting at the same moment on a PostgreSQL database that holds no cookie key: one first key is stored, both
/// key rings start with it, and what one copy protects (a session cookie, an antiforgery token, the sign-in's state and nonce) the other
/// reads. Left to the key rings, each copy would make its own first key and keep a ring without the other's for up to a day (the ring
/// re-reads the store on an unknown key only in its first two minutes).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresCookieKeyColdStartTests
{
    private const string Tid = "11111111-1111-4111-8111-111111111111";
    private const string Oid = "22222222-2222-4222-8222-222222222222";

    /// <summary>A store whose first read waits for the other copy's first read, so both starts see the empty table every time.</summary>
    private sealed class FirstReadTogether(ICookieKeyStore inner, Barrier together) : ICookieKeyStore
    {
        private int _reads;

        public async Task<IReadOnlyList<CookieKey>> GetAllAsync(CancellationToken ct)
        {
            var keys = await inner.GetAllAsync(ct);
            if (Interlocked.Increment(ref _reads) == 1 && !await Task.Run(() => together.SignalAndWait(TimeSpan.FromSeconds(60)), ct))
                throw new TimeoutException("the other copy never read the keys");
            return keys;
        }

        public Task AddAsync(string friendlyName, string xml, CancellationToken ct) => inner.AddAsync(friendlyName, xml, ct);
        public Task<int> ImportIfEmptyAsync(IReadOnlyList<CookieKey> keys, CancellationToken ct) => inner.ImportIfEmptyAsync(keys, ct);
        public Task<bool> AddUnlessAnyAsync(CookieKey key, Func<CookieKey, bool> usable, CancellationToken ct) => inner.AddUnlessAnyAsync(key, usable, ct);
    }

    private static WebApplication Build(string connectionString, string key, Barrier together) =>
        PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], [], s =>
        {
            var registered = s.Last(d => d.ServiceType == typeof(ICookieKeyStore)).ImplementationFactory!;
            s.AddSingleton<ICookieKeyStore>(sp => new FirstReadTogether((ICookieKeyStore)registered(sp), together));
        }, o =>
        {
            o.Database = connectionString;
            o.DatabaseEngine = "postgres";
            o.CookieKeys = "database";
            o.MasterKeyBase64 = key;
            o.AllowedHosts = "feeds.example.test";
            o.AdminHost = "admin.example.test";
            o.AdminTenantId = Tid;
            o.AdminClientId = "33333333-3333-4333-8333-333333333333";
            o.AdminClientSecret = "not-a-real-secret";
            o.AdminAllow = Tid + ":" + Oid;
        });

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static HttpContext Context(WebApplication app) => new DefaultHttpContext { RequestServices = app.Services, Request = { Scheme = "https", Host = new HostString("admin.example.test") } };

    /// <summary>An antiforgery pair from <paramref name="from"/>, checked by <paramref name="to"/> as a form post.</summary>
    private static async Task<bool> AntiforgeryCrossesAsync(WebApplication from, WebApplication to)
    {
        var tokens = from.Services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(Context(from));
        var post = Context(to);
        post.Request.Method = "POST";
        post.Request.ContentType = "application/x-www-form-urlencoded";
        post.Request.Headers.Cookie = $"{SignedInSite.AntiforgeryCookieName}={tokens.CookieToken}";
        post.Request.Form = new FormCollection(new Dictionary<string, StringValues> { [SignedInSite.AntiforgeryField] = tokens.RequestToken });
        return await to.Services.GetRequiredService<IAntiforgery>().IsRequestValidAsync(post);
    }

    // mutations: the first key stored with a plain insert (two keys, each copy's ring holding its own); no first key made at start (each
    // ring makes its own); the keys not read back
    [PostgresFact]
    public async Task Two_copies_cold_starting_at_once_share_one_key_and_read_each_others_cookies_tokens_and_sign_in_state()
    {
        await using var roles = await PostgresTestRoles.CreateAsync();
        await using var t = await PostgresScratchDatabase.CreateAsync(owner: roles.Owner);
        var key = AesGcmSecretProtector.GenerateKeyBase64();
        var owner = new CliEnvironment(roles.ConnectionString(t, roles.Owner), () => AesGcmSecretProtector.FromBase64(key), new Packmoat.Domain.SystemClock(), "e2e-op") { Engine = DatabaseEngine.Postgres };
        var (o, e) = (new StringWriter(), new StringWriter());
        Assert.True(await AdminCli.RunAsync(["db", "migrate", "--grant-to", roles.App], o, e, owner, TestCli.Modules) == 0, e.ToString());
        Assert.Equal(0L, await ScalarAsync(roles.ConnectionString(t, roles.Owner), "SELECT COUNT(*) FROM cookie_keys"));

        using var together = new Barrier(2);
        var app = roles.ConnectionString(t, roles.App);
        var builds = new[] { Task.Run(() => Build(app, key, together)), Task.Run(() => Build(app, key, together)) };
        await Task.WhenAny(Task.WhenAll(builds), Task.Delay(TimeSpan.FromMinutes(2)));
        var built = builds.Where(b => b.IsCompletedSuccessfully).Select(b => b.Result).ToList();
        try
        {
            Assert.All(builds, b => Assert.True(b.IsCompletedSuccessfully, b.Exception?.ToString() ?? "still starting"));
            var (a, b) = (built[0], built[1]);
            Assert.Equal(1L, await ScalarAsync(roles.ConnectionString(t, roles.Owner), "SELECT COUNT(*) FROM cookie_keys"));

            // The key rings, read only now (as on a first request), protect under the one key and read each other's payloads.
            IDataProtector P(WebApplication x) => x.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("cold-start");
            var fromA = P(a).Protect("a"u8.ToArray());
            var fromB = P(b).Protect("b"u8.ToArray());
            Assert.Equal(new Guid(fromA.AsSpan(4, 16)), new Guid(fromB.AsSpan(4, 16)));
            Assert.Equal("a"u8.ToArray(), P(b).Unprotect(fromA));
            Assert.Equal("b"u8.ToArray(), P(a).Unprotect(fromB));

            // The admin session cookie's ticket.
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", Oid)], "test"));
            var ticket = new AuthenticationTicket(principal, AdminSite.CookieScheme);
            CookieAuthenticationOptions Cookie(WebApplication x) => x.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AdminSite.CookieScheme);
            Assert.Equal(Oid, Cookie(b).TicketDataFormat.Unprotect(Cookie(a).TicketDataFormat.Protect(ticket))?.Principal.FindFirst("oid")?.Value);
            Assert.Equal(Oid, Cookie(a).TicketDataFormat.Unprotect(Cookie(b).TicketDataFormat.Protect(ticket))?.Principal.FindFirst("oid")?.Value);

            // The antiforgery cookie and form token, both ways.
            Assert.True(await AntiforgeryCrossesAsync(a, b));
            Assert.True(await AntiforgeryCrossesAsync(b, a));

            // Microsoft sign-in: the state (which carries the correlation cookie's id) and the nonce cookie, made by the copy that sent the
            // person to Microsoft, read by the copy their return reaches.
            OpenIdConnectOptions Oidc(WebApplication x) => x.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(AdminSite.RemoteScheme);
            var state = new AuthenticationProperties(new Dictionary<string, string?> { [".xsrf"] = "correlation-id", [".redirect"] = "/tenants" });
            Assert.Equal("correlation-id", Oidc(b).StateDataFormat.Unprotect(Oidc(a).StateDataFormat.Protect(state))?.Items[".xsrf"]);
            Assert.Equal("correlation-id", Oidc(a).StateDataFormat.Unprotect(Oidc(b).StateDataFormat.Protect(state))?.Items[".xsrf"]);
            Assert.Equal("nonce", Oidc(b).StringDataFormat.Unprotect(Oidc(a).StringDataFormat.Protect("nonce")));

            Assert.Equal(1L, await ScalarAsync(roles.ConnectionString(t, roles.Owner), "SELECT COUNT(*) FROM cookie_keys"));   // no ring made another
        }
        finally
        {
            foreach (var x in built) await x.DisposeAsync();
        }
    }
}
