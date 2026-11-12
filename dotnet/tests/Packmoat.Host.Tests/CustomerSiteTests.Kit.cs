using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Packmoat.Host.Customer;

namespace Packmoat.Host.Tests;

/// <summary>Stands in for Microsoft on the customer site: the callback signs the app cookie scheme in with whatever the "app" fake provider says.</summary>
public sealed class FakeAppEntraHandler : AuthenticationHandler<AuthenticationSchemeOptions>, IAuthenticationRequestHandler
{
    public FakeAppEntraHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : base(options, logger, encoder) { }

    public async Task<bool> HandleRequestAsync()
    {
        if (Request.Path != AppSite.CallbackPath) return false;
        var person = Context.RequestServices.GetRequiredKeyedService<FakeIdentityProvider>("app").Next;
        if (person is null) { Response.StatusCode = 400; return true; }
        // As the real handler does: the challenge's items (kept in the protected state) come back on the sign-in.
        var properties = new AuthenticationProperties();
        if (Context.RequestServices.GetRequiredKeyedService<FakeIdentityProvider>("app").LastChallenge is { } last)
            foreach (var item in last.Items.Where(i => i.Key != ".redirect")) properties.Items[item.Key] = item.Value;
        await Context.SignInAsync(AppSite.CookieScheme, person, properties);
        Response.Redirect(Context.RequestServices.GetRequiredKeyedService<FakeIdentityProvider>("app").LastChallenge?.RedirectUri ?? "/home");
        return true;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Context.RequestServices.GetRequiredKeyedService<FakeIdentityProvider>("app").LastChallenge = properties;
        Response.Redirect(AppSite.CallbackPath + "?fake=1");
        return Task.CompletedTask;
    }
}

/// <summary>Keeps every log line, to show that no claim, email or directory id is ever logged.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new L(this, categoryName);
    public void Dispose() { }
    private sealed class L : ILogger
    {
        private readonly CapturingLoggerProvider _p; private readonly string _c;
        public L(CapturingLoggerProvider p, string c) { _p = p; _c = c; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_p.Lines) _p.Lines.Add(_c + ": " + formatter(state, exception) + (exception is null ? "" : " " + exception));
        }
    }
}

/// <summary>The customer site's test names: the core's tests and the hosted tests (which compile this file in) share them.</summary>
public partial class CustomerSiteTests
{
    public const string AppHost = "app.packmoat.test";
    public const string AppClient = "55555555-5555-4555-8555-555555555555";
    public const string DirA = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";   // bound to acme
    public const string DirB = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";   // bound to globex
    public const string DirX = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";   // not bound
    public const string Consumer = "9188040d-6c67-4c5b-b112-36a304b66dad";
    public const string Oid = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
}
