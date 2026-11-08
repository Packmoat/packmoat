using System.Text.RegularExpressions;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// Npgsql logs nothing of ours (#288): it logs only through a logger factory it is given, and parameter values only when told to. No code
/// in the core's src/ gives it one or tells it to, so a password, a token hash or a row's values cannot reach a log through it. The options
/// a connection string could turn on instead are refused by PostgresConnectionString.Parse (its tests are in Packmoat.Infrastructure.Tests).
/// </summary>
public class PostgresLoggingTests
{
    private static readonly Regex Logging = new(@"\b(UseLoggerFactory|EnableParameterLogging|NpgsqlLoggingConfiguration|InitializeLogging)\b", RegexOptions.CultureInvariant);

    /// <summary>Each file whose code (comments aside) hands Npgsql a logger or turns on its parameter logging, as "path: the names".</summary>
    internal static string[] Offenders(IEnumerable<(string Named, string Source)> files) =>
        files.Select(f => (f.Named, Found: Logging.Matches(EngineTypeScan.WithoutComments(f.Source)).Select(m => m.Value).Distinct().ToArray()))
            .Where(f => f.Found.Length > 0)
            .Select(f => $"{f.Named}: {string.Join(", ", f.Found)}")
            .Order(StringComparer.Ordinal).ToArray();

    // mutation: .UseLoggerFactory(loggerFactory) in PostgresDatabase.Create
    [Fact]
    public void No_code_gives_Npgsql_a_logger_or_turns_on_parameter_logging()
    {
        var files = EngineTypeScan.SourceFiles(Path.Combine(CoreRoot.Path(), "dotnet", "src"));
        Assert.Contains(files, f => f.Named.StartsWith("Packmoat.Infrastructure.Postgres/", StringComparison.Ordinal));
        var found = Offenders(files);
        Assert.True(found.Length == 0, "Npgsql must not log (it would write connection details and, with parameter logging, values):\n  " + string.Join("\n  ", found));
    }

    [Fact]
    public void The_logging_scan_sees_each_call_in_code_and_not_in_a_comment()
    {
        Assert.Equal(
            ["A.cs: UseLoggerFactory, EnableParameterLogging", "B.cs: NpgsqlLoggingConfiguration, InitializeLogging"],
            Offenders([
                ("A.cs", "new NpgsqlDataSourceBuilder(s).UseLoggerFactory(f).EnableParameterLogging().Build();"),
                ("B.cs", "NpgsqlLoggingConfiguration.InitializeLogging(f, parameterLoggingEnabled: true);"),
                ("C.cs", "// never UseLoggerFactory here\n/* EnableParameterLogging */ var x = 1;"),
            ]));
    }
}
