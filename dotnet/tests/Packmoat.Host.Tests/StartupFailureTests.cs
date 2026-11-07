using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// #240: an error at start ends the server with exit code 1 and one error: line, instead of an abort that a container's first process
/// ignores. A known error (an InvalidOperationException, which names a setting but never its value) prints its message; any other prints
/// only its type and stack trace, never its message.
/// </summary>
public class StartupFailureTests
{
    private const string Value = "s3cr3t-value-from-a-setting";

    [Fact]   // mutation: catch nothing
    public void A_settings_error_prints_its_message_and_exits_1()
    {
        var error = new StringWriter();
        var code = StartupFailure.Run(() => throw new SettingsException("PACKMOAT_MASTER_KEY is not set."), error);
        Assert.Equal(1, code);
        Assert.Equal("error: PACKMOAT_MASTER_KEY is not set." + Environment.NewLine, error.ToString());
    }

    [Fact]
    public void Any_invalid_operation_error_prints_its_message_and_exits_1()
    {
        var error = new StringWriter();
        Assert.Equal(1, StartupFailure.Run(() => throw new InvalidOperationException("PACKMOAT_HOSTS is not set."), error));
        Assert.Equal("error: PACKMOAT_HOSTS is not set." + Environment.NewLine, error.ToString());
    }

    [Theory]   // mutation: print ex.Message for every exception
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void Any_other_error_prints_its_type_and_stack_but_never_its_message_and_exits_1(Type type)
    {
        var error = new StringWriter();
        var code = StartupFailure.Run(() => throw (Exception)Activator.CreateInstance(type, "could not read " + Value)!, error);
        Assert.Equal(1, code);
        var text = error.ToString();
        Assert.StartsWith($"error: the server stopped with an error ({type.FullName}).", text);
        Assert.Contains(nameof(Any_other_error_prints_its_type_and_stack_but_never_its_message_and_exits_1), text);   // the stack trace
        Assert.DoesNotContain(Value, text);
        Assert.DoesNotContain("could not read", text);
    }

    [Fact]
    public void An_unknown_error_wrapping_a_known_one_prints_neither_message()
    {
        var error = new StringWriter();
        var inner = new InvalidOperationException("inner " + Value);
        Assert.Equal(1, StartupFailure.Run(() => throw new AggregateException("outer " + Value, inner), error));
        Assert.DoesNotContain(Value, error.ToString());
    }

    [Fact]   // mutation: return 1 (or anything but the .NET exit code) after a normal run
    public void A_server_that_starts_and_stops_prints_nothing_and_exits_with_the_usual_code()
    {
        var error = new StringWriter();
        var code = StartupFailure.Run(() =>
        {
            var app = WebApplication.Create(["--urls", "http://127.0.0.1:0"]);
            app.Lifetime.ApplicationStarted.Register(() => app.Lifetime.StopApplication());
            return app;
        }, error);
        Assert.Equal(Environment.ExitCode, code);
        Assert.Equal(0, code);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void An_error_while_starting_after_building_exits_1()
    {
        var error = new StringWriter();
        var code = StartupFailure.Run(() =>
        {
            var builder = WebApplication.CreateBuilder(["--urls", "http://127.0.0.1:0"]);
            builder.Services.AddHostedService<FailsToStart>();
            return builder.Build();
        }, error);
        Assert.Equal(1, code);
        Assert.StartsWith($"error: the server stopped with an error ({typeof(FormatException).FullName}).", error.ToString());
        Assert.DoesNotContain(Value, error.ToString());
    }

    [Fact]   // mutation: the unknown-error line says the server could not start, which is untrue for an error while stopping
    public void An_error_while_stopping_exits_1_and_does_not_say_the_server_could_not_start()
    {
        var error = new StringWriter();
        var code = StartupFailure.Run(() =>
        {
            var builder = WebApplication.CreateBuilder(["--urls", "http://127.0.0.1:0"]);
            builder.Services.AddHostedService<FailsToStop>();
            var app = builder.Build();
            app.Lifetime.ApplicationStarted.Register(() => app.Lifetime.StopApplication());
            return app;
        }, error);
        Assert.Equal(1, code);
        var text = error.ToString();
        Assert.StartsWith("error: the server stopped with an error (", text);
        Assert.DoesNotContain("could not start", text);
        Assert.DoesNotContain(Value, text);
    }

    private sealed class FailsToStop : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => throw new FormatException(Value);
    }

    private sealed class FailsToStart : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw new FormatException(Value);
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
