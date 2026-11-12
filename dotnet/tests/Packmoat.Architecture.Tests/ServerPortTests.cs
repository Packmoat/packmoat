using System.Text.RegularExpressions;
using Packmoat.Infrastructure;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// PORT: the core server's entry point (Packmoat.Server's Program.cs) moves to http://+:PORT when PORT is set, and changes
/// nothing when it is not. A bad value stops the server at start, naming PORT but not the value. CI's PORT smoke runs the image with it.
/// </summary>
public class ServerPortTests
{
    [Fact]   // mutation: Apply adds --urls with PORT unset
    public void With_PORT_unset_the_arguments_are_unchanged()
    {
        string[] args = ["--contentRoot", "/app/host"];
        Assert.Same(args, ServerPort.Apply(args, null));
    }

    [Theory]
    [InlineData("1", "http://+:1")]
    [InlineData("8080", "http://+:8080")]
    [InlineData("9090", "http://+:9090")]
    [InlineData("65535", "http://+:65535")]
    public void PORT_adds_urls_on_every_interface_after_the_other_arguments(string port, string url) =>
        Assert.Equal(["--x", "y", "--urls", url], ServerPort.Apply(["--x", "y"], port));

    [Theory]   // mutations: accept 0; accept 65536; parse with NumberStyles.Integer (accepts " 80" and "+80")
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("+80")]
    [InlineData(" 80")]
    [InlineData("80 ")]
    [InlineData("")]
    [InlineData("8080abc")]
    [InlineData("1e3")]
    [InlineData("99999999999")]
    public void A_PORT_outside_1_to_65535_stops_the_start_and_the_error_does_not_echo_it(string port)
    {
        var ex = Assert.Throws<SettingsException>(() => ServerPort.Apply([], port));   // mutation: InvalidOperationException
        Assert.Equal("PORT must be a whole number from 1 to 65535.", ex.Message);
    }

    // mutations: Program.cs reads another variable, calls PackmoatApp.Build with args as they are, runs the server outside StartupFailure, or keeps the line only in a comment
    [Fact]
    public void The_entry_point_reads_PORT_and_passes_what_Apply_returns()
    {
        Assert.Equal("PORT", ServerPort.Variable);
        var program = WithoutComments(CoreRoot.Read("dotnet", "src", "Packmoat.Server", "Program.cs"));
        Assert.Contains(
            "return StartupFailure.Run(() => PackmoatApp.Build(ServerPort.Apply(args, Settings.Get(ServerPort.Variable)), []), Console.Error);",
            program);
    }

    // mutations: docker run --init in port-smoke.sh (an abort then ends the container, so the smoke no longer sees the hang); drop
    // the no-master-key run
    [Fact]
    public void The_port_smoke_runs_its_failing_starts_with_no_init()
    {
        var smoke = CoreRoot.Read(".github", "scripts", "port-smoke.sh");
        Assert.DoesNotMatch(@"docker run[^\n]*--init", smoke);
        Assert.Contains("stops_at_start \"PORT=0\" \"error: PORT must be a whole number from 1 to 65535.\"", smoke);
        Assert.Contains("stops_at_start \"No master key\" \"error: PACKMOAT_MASTER_KEY is not set.\"", smoke);
    }

    [Fact]
    public void Comments_are_stripped_and_string_literals_are_kept()
    {
        Assert.Equal("a();  b(\"http://x\", \"/* y */\"); ",
            WithoutComments("a(); // c();\n/* d(); */ b(\"http://x\", \"/* y */\"); /// e();"));
    }

    // A string literal is matched first and kept whole, so "http://" in one is not taken for the start of a comment.
    private static readonly Regex CommentOrString = new("""
        "(?:[^"\\\n]|\\.)*"|//[^\n]*\n?|/\*.*?\*/
        """, RegexOptions.Singleline);

    private static string WithoutComments(string source) =>
        CommentOrString.Replace(source, m => m.Value.StartsWith('"') ? m.Value : "");
}
