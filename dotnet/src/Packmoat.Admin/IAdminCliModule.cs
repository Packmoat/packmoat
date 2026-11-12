using Packmoat.Application;
using Packmoat.Infrastructure;
using Packmoat.Infrastructure.Lifecycle;

namespace Packmoat.Admin;

/// <summary>
/// A group of commands a module adds to packmoat-admin, under one first word of its own. The core owns the database, the master key check,
/// the operator's name and the error handling; a module's command runs through <see cref="AdminCliContext"/> only. A group that repeats
/// or names one of the core's commands stops the tool (exit 2) before anything runs.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public interface IAdminCliModule
{
    /// <summary>The first word of the module's commands.</summary>
    string Group { get; }

    /// <summary>The module's lines of the help, placed after the core's commands and before audit, decision and the backups.</summary>
    string Usage { get; }

    /// <summary>The module's lines under "Environment:" at the end of the help, or "" for none.</summary>
    string EnvironmentUsage { get; }

    /// <summary>
    /// Runs one of the group's commands and returns its exit code. An <see cref="ArgumentException"/> (or the other errors the core
    /// reports) is printed as "error: ..." with exit 1; its message must name the problem, never a secret.
    /// </summary>
    Task<int> RunAsync(AdminCliContext context);

    /// <summary>
    /// The serving gate the tool's token service asks, or null for none. At most one module may give one (more stops the tool, exit 2);
    /// with none, every tenant serves (<see cref="ServeAllGate"/>). <paramref name="warn"/> takes one-line warnings the tool prints to its
    /// error output (for example a tenant state the gate does not know); ids and states only, never a secret.
    /// </summary>
    IServingGate? Gate(IRelationalDatabase db, CliEnvironment env, Action<string> warn);

    /// <summary>
    /// The tenant lifecycle's parts (creation participants, purge contributors and the purge eligibility), or null for none. At
    /// most one module may give one (more stops the tool, exit 2); with none, a new tenant gets only its own row and nobody is purged
    /// (<see cref="TenantLifecycle.None"/>). It must be cheap: it is asked on every run, and the eligibility's settings should be read
    /// only when the eligibility is made. <paramref name="note"/> takes lines a refused purge prints (what a payment provider logged).
    /// </summary>
    TenantLifecycle? Lifecycle(IRelationalDatabase db, CliEnvironment env, Action<string> note);

    /// <summary>
    /// The module's own migration track, or null for none: the same track its server module declares. Every command that opens
    /// the database runs the core's migrations and then each module's track; `db check-migrations` runs them on a copy. Two modules may
    /// not declare the same track, and a track table no module declares stops the tool.
    /// </summary>
    MigrationTrack? Migrations { get; }

    /// <summary>
    /// What the module's purge eligibility asks before anything is deleted, in the operator's words, or null for nothing: the help's
    /// <c>tenant purge</c> line and the hint under <c>tenant purge-due</c> name it.
    /// </summary>
    PurgeCheckNote? PurgeCheck => null;
}

/// <summary>
/// The words for a module's last check before a purge. <paramref name="Help"/> goes in the help's <c>tenant purge</c> line, between
/// "only when due" and "recorded"; <paramref name="Due"/> goes after "take a backup first" in the hint under <c>tenant purge-due</c>.
/// <paramref name="DueLines"/>, when given, replace the core's words for <c>tenant purge-due</c> in the help: the first line after the
/// command, each later one in the help's column. At most one module may give them (more stops the tool, exit 2).
/// </summary>
public sealed record PurgeCheckNote(string Help, string Due, IReadOnlyList<string>? DueLines = null);

/// <summary>
/// What a module's command is given: its arguments, the operator, the migrated database, the admin action log its changes are recorded in
/// (the core's, over that database), the environment and the two writers.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed record AdminCliContext(AdminCliArgs Args, ActorScope Actor, IRelationalDatabase Db, IAdminActionLog Actions, CliEnvironment Env,
    TextWriter Output, TextWriter Error, CancellationToken CancellationToken);

/// <summary>
/// The command line as the core reads it: the command and its sub-command, then positional arguments and --options. The flags (options
/// without a value) are the core's list.
/// </summary>
public sealed class AdminCliArgs
{
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "force", "developer-bearer", "admin-bearer", "next", "allow-shared-directory", "prefix" };
    private readonly List<string> _pos = new();
    private readonly Dictionary<string, List<string>> _opts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The command and its sub-command ("" when there is none).</summary>
    public (string, string) Cmd { get; }

    public AdminCliArgs(string[] args)
    {
        Cmd = (args[0], args.Length > 1 ? args[1] : "");
        for (var i = 2; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) { _pos.Add(args[i]); continue; }
            var name = args[i][2..];
            string value;
            if (Flags.Contains(name)) value = "true";
            else if (i + 1 < args.Length) value = args[++i];
            else throw new ArgumentException($"{args[i]} needs a value.");
            if (!_opts.TryGetValue(name, out var list)) _opts[name] = list = new List<string>();
            list.Add(value);
        }
    }

    /// <summary>The positional argument after the sub-command at <paramref name="i"/>; missing is an error that points at --help.</summary>
    public string Pos(int i) => i < _pos.Count ? _pos[i] : throw new ArgumentException("A required argument is missing. Run with --help.");
    /// <summary>The last value of --<paramref name="name"/>, or null.</summary>
    public string? Opt(string name) => _opts.TryGetValue(name, out var l) ? l[^1] : null;
    /// <summary>Every value of --<paramref name="name"/>, in order.</summary>
    public IReadOnlyList<string> Opts(string name) => _opts.TryGetValue(name, out var l) ? l : Array.Empty<string>();
    /// <summary>True when --<paramref name="name"/> was given.</summary>
    public bool Flag(string name) => _opts.ContainsKey(name);
}
