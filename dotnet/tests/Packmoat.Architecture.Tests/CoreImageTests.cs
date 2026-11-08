using System.Text.RegularExpressions;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// The core image (#197): dotnet/Dockerfile builds from dotnet/ alone, runs the core's own entry points, and pins its base images by
/// digest. CI's image smoke runs the built image; this catches a wrong name before the image is built.
/// </summary>
public class CoreImageTests
{
    private static string Dockerfile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Packmoat.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Packmoat.sln not found above the test output folder."), "Dockerfile"));
    }

    [Fact]   // mutations: a wrong DLL name in the ENTRYPOINT or the wrapper; publish the hosted entry points
    public void The_core_image_runs_Packmoat_Server_and_its_packmoat_admin_runs_Packmoat_Cli()
    {
        var dockerfile = Dockerfile();
        Assert.Contains("dotnet publish src/Packmoat.Server/Packmoat.Server.csproj ", dockerfile);
        Assert.Contains("dotnet publish src/Packmoat.Cli/Packmoat.Cli.csproj ", dockerfile);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"/app/host/Packmoat.Server.dll\"]", dockerfile);
        Assert.Contains("exec dotnet /app/admin/Packmoat.Cli.dll", dockerfile);
        Assert.DoesNotContain("Hosted", dockerfile);
    }

    /// <summary>The image each FROM line names, after an optional --platform= flag.</summary>
    internal static string[] BaseImages(string dockerfile) =>
        Regex.Matches(dockerfile, @"^FROM\s+(?:--platform=\S+\s+)?(\S+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToArray();

    [Fact]   // mutations: drop a digest from a FROM line; drop the build stage's --platform (the image is still read, and still checked)
    public void Every_base_image_is_pinned_by_digest()
    {
        var refs = BaseImages(Dockerfile());
        Assert.Equal(2, refs.Length);
        Assert.All(refs, r => Assert.Matches(@"@sha256:[0-9a-f]{64}$", r));
    }

    [Fact]
    public void The_base_image_reader_reads_past_a_platform_flag()
    {
        const string Digest = "@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317";
        Assert.Equal(["sdk" + Digest, "aspnet" + Digest], BaseImages($"FROM --platform=$BUILDPLATFORM sdk{Digest} AS build\nRUN x\nFROM aspnet{Digest}\n"));
        Assert.Equal(["sdk:10.0"], BaseImages("FROM --platform=linux/amd64 sdk:10.0 AS build\n"));   // so a missing digest still fails
    }

    /// <summary>
    /// Multi-arch (#233 E2): the build stage runs on the build machine's platform and publishes for the target's, so an arm64 image is
    /// built without running .NET under emulation, and its native files (SQLite) are arm64's. The health check follows PORT.
    /// </summary>
    [Fact]   // mutations: drop --platform=$BUILDPLATFORM; add it to the runtime FROM; drop -a "$TARGETARCH" from one publish; probe 8080 whatever PORT says
    public void The_image_builds_for_any_target_on_the_build_machines_platform_and_its_health_check_follows_PORT()
    {
        var dockerfile = Dockerfile();
        Assert.Matches(@"(?m)^FROM --platform=\$BUILDPLATFORM \S+ AS build$", dockerfile);
        Assert.Matches(@"(?m)^ARG TARGETARCH$", dockerfile);
        Assert.Equal(2, Regex.Matches(dockerfile, @"dotnet publish \S+ +-c Release -a ""\$TARGETARCH"" ").Count);
        // Only the build stage runs on the build machine's platform. A --platform on the runtime stage would ship that platform's base
        // (an amd64 runtime in the arm64 image, say), so there is exactly one --platform=, on the build stage, and the runtime FROM has none.
        Assert.Single(Regex.Matches(dockerfile, "--platform="));
        Assert.Matches(@"(?m)^FROM mcr\.microsoft\.com/dotnet/aspnet:\S+ AS runtime$", dockerfile);
        Assert.Contains("/dev/tcp/127.0.0.1/${PORT:-8080} ", dockerfile);
        Assert.DoesNotContain("/dev/tcp/127.0.0.1/8080", dockerfile);
    }
}
