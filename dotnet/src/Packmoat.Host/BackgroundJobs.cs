namespace Packmoat.Host;

/// <summary>
/// Whether this server runs the scheduled background jobs (#233, PACKMOAT_BACKGROUND_JOBS): <c>on</c> or <c>off</c>, on when unset or
/// empty. Off is for running several copies of the server on one database: one copy keeps them on, or the operator runs
/// <c>packmoat-admin jobs run retention</c> on a schedule.
/// <para>Off leaves out the core's daily retention pass (<see cref="RetentionService"/>) and a module's own scheduled jobs (each module
/// reads <see cref="PackmoatOptions.BackgroundJobs"/>). What serves requests or starts the server still runs: the audit writer, the
/// retention blanking at start-up, and a module's alert sender.</para>
/// <para>Strict: anything else stops the start, naming the setting and never the value. Not registered in DI.</para>
/// </summary>
public static class BackgroundJobs
{
    public const string Variable = "PACKMOAT_BACKGROUND_JOBS";

    /// <summary>True for on (and unset or empty), false for off. Anything else throws.</summary>
    public static bool Parse(string? value) => value switch
    {
        null or "" or "on" => true,
        "off" => false,
        _ => throw new InvalidOperationException($"{Variable} must be on or off."),
    };

    /// <summary>Whether <paramref name="options"/> turn the background jobs on (see <see cref="Parse"/>).</summary>
    public static bool On(PackmoatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Parse(options.BackgroundJobs);
    }
}
