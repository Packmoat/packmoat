using System.Reflection;
using System.Text.RegularExpressions;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Packmoat.TestSupport;

/// <summary>
/// Fails a test run that leaves anything in the temporary folder. Every test project names it as its xunit test framework (an
/// assembly-level <c>Xunit.TestFrameworkAttribute</c>, set by TempLeakGuard.props, which dotnet/tests/Directory.Build.props imports, as
/// does every tests folder built on the core: a project there cannot leave it out). When the framework is made, before any test runs, it
/// points the process's temporary folder (TMPDIR; TMP and TEMP on Windows) at a new, empty folder of its own,
/// <c>&lt;temp&gt;/pmrun-&lt;assembly&gt;-&lt;guid&gt;/</c>; child processes inherit it. After the last test it closes SQLite's pooled
/// connections, lists that folder and reports every entry left there (outside <see cref="Allowed"/>) as an error, which fails the run and
/// names the path, with every delete that failed after its test had failed (<see cref="TempDir.Failing(string)"/>). Then it deletes the
/// folder. A problem with the folder itself (gone, unreadable) is reported the same way; it never stops the test host.
/// </summary>
public sealed class TempLeakGuard : XunitTestFramework
{
    /// <summary>
    /// What may be left in the folder, as files only (a folder with such a name is reported): on macOS only, the throwaway keychains .NET makes there to load a certificate with its private key
    /// (<c>&lt;guid&gt;.keychain</c>) and the security framework's lock files beside them (<c>.fl&lt;hex&gt;</c>). The runtime makes and
    /// keeps them, not a test.
    /// </summary>
    internal static bool Allowed(string name) =>
        OperatingSystem.IsMacOS() && (KeychainName.IsMatch(name) || KeychainLockName.IsMatch(name));

    private static readonly Regex KeychainName = new("^[0-9a-f]{32}\\.keychain$", RegexOptions.CultureInvariant);
    private static readonly Regex KeychainLockName = new("^\\.fl[0-9A-F]{8}$", RegexOptions.CultureInvariant);

    private static readonly object Gate = new();
    private static string? _runFolder;

    /// <summary>
    /// The folder this run's temporary files go to; the process is pointed at it the first time it is asked for. It is made only when
    /// <paramref name="make"/> is set, as the tests start: an assembly none of whose tests runs (a filter matches none) leaves nothing.
    /// </summary>
    internal static string RunFolder(string assembly, bool make = false)
    {
        lock (Gate)
        {
            if (_runFolder is null)
            {
                var original = Path.GetTempPath();
                _runFolder = Path.Combine(original, $"pmrun-{assembly}-{Guid.NewGuid():N}");
                if (OperatingSystem.IsWindows())
                {
                    Environment.SetEnvironmentVariable("TMP", _runFolder);
                    Environment.SetEnvironmentVariable("TEMP", _runFolder);
                }
                else
                {
                    Environment.SetEnvironmentVariable("TMPDIR", _runFolder + Path.DirectorySeparatorChar);
                }
            }
            if (make) Directory.CreateDirectory(_runFolder);
            return _runFolder;
        }
    }

    public TempLeakGuard(IMessageSink messageSink) : base(messageSink) { }

    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName)
    {
        RunFolder(assemblyName.Name!);
        return new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);
    }

    protected override ITestFrameworkDiscoverer CreateDiscoverer(IAssemblyInfo assemblyInfo)
    {
        // Point the process away from the real temporary folder from the start, before any test code runs.
        RunFolder(new AssemblyName(assemblyInfo.Name).Name!);
        return base.CreateDiscoverer(assemblyInfo);
    }

    private sealed class Executor(AssemblyName assemblyName, ISourceInformationProvider sourceInformationProvider, IMessageSink diagnosticMessageSink)
        : XunitTestFrameworkExecutor(assemblyName, sourceInformationProvider, diagnosticMessageSink)
    {
        protected override async void RunTestCases(IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
        {
            using var runner = new Runner(TestAssembly, testCases, DiagnosticMessageSink, executionMessageSink, executionOptions);
            await runner.RunAsync();
        }
    }

    private sealed class Runner(ITestAssembly testAssembly, IEnumerable<IXunitTestCase> testCases, IMessageSink diagnosticMessageSink,
        IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
        : XunitTestAssemblyRunner(testAssembly, testCases, diagnosticMessageSink, executionMessageSink, executionOptions)
    {
        protected override async Task<RunSummary> RunTestCollectionsAsync(IMessageBus messageBus, CancellationTokenSource cancellationTokenSource)
        {
            var folder = RunFolder(new AssemblyName(TestAssembly.Assembly.Name).Name!, make: true);
            var summary = await base.RunTestCollectionsAsync(messageBus, cancellationTokenSource);
            IReadOnlyList<string> problems;
            try { problems = Check(folder, endOfRun: true); }
            catch (Exception e) { problems = [$"The temporary-folder check of {folder} failed: {e}"]; }
            foreach (var problem in problems)
                messageBus.QueueMessage(new ErrorMessage(TestCases, ["Packmoat.TestSupport.TempLeak"], [problem], [null], [-1]));
            return summary;
        }
    }

    /// <summary>
    /// What is wrong with <paramref name="folder"/> (nothing, when the tests cleaned up); the folder is deleted. Only files may match
    /// <see cref="Allowed"/>. <paramref name="endOfRun"/> is set by the guard alone, after the last test: it first closes SQLite's pooled
    /// connections (process-wide, so never while a test runs) and adds the deletes <see cref="TempDir"/> deferred.
    /// </summary>
    internal static IReadOnlyList<string> Check(string folder, bool endOfRun)
    {
        var problems = new List<string>();
        if (endOfRun)
        {
            TempDir.ClearSqlitePools();
            problems.AddRange(TempDir.TakeDeferred());
        }
        try
        {
            problems.AddRange(new DirectoryInfo(folder).EnumerateFileSystemInfos()
                .Where(entry => !(entry is FileInfo && Allowed(entry.Name)))
                .Select(entry => entry.FullName)
                .Order(StringComparer.Ordinal)
                .Select(entry => $"A test left this in the temporary folder: {entry}. Delete it in a using or a finally (Packmoat.TestSupport.TempDir)."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"The run's temporary folder {folder} could not be listed, so what the tests left in it is unknown: {e.GetType().Name}: {e.Message}");
        }
        try { TempDir.Delete(folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"The run's temporary folder {folder} could not be deleted: {e.Message}");
        }
        return problems;
    }
}
