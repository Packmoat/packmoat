using Packmoat.Admin;

namespace Packmoat.Infrastructure.Tests;

/// <summary>The modules the tests' packmoat-admin runs with: none (the core alone).</summary>
public static class TestCli
{
    public static IReadOnlyList<IAdminCliModule> Modules { get; } = [];
}
