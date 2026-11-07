using Microsoft.AspNetCore.Builder;

namespace Packmoat.Host;

/// <summary>
/// How a server's entry point builds and runs the server (#240), so that an error at start ends the process with exit code 1 and one
/// <c>error:</c> line. Left unhandled, .NET ends such an error with an abort, which the kernel ignores for a container's first process, so
/// without an init the container would keep running instead of exiting.
/// <para>An <see cref="InvalidOperationException"/> (a <c>SettingsException</c>, PORT, PACKMOAT_HOSTS and the other start checks) prints its
/// message. Those messages name the setting and are written never to hold a secret, though some do hold a value that is not secret, such
/// as a folder path or a mode. Any other exception prints only its type and stack trace, never its message, which could hold a secret.
/// The same holds for an exception thrown while the server stops. A run that starts and then stops normally is unchanged: nothing is
/// printed and the exit code is the one .NET would have used.</para>
/// </summary>
public static class StartupFailure
{
    /// <summary>Builds the server with <paramref name="build"/> and runs it until it stops.</summary>
    /// <returns>The process's exit code: 1 when building, starting, running or stopping threw, else <see cref="Environment.ExitCode"/>.</returns>
    public static int Run(Func<WebApplication> build, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            build().Run();
            return Environment.ExitCode;
        }
        catch (InvalidOperationException e)
        {
            error.WriteLine($"error: {e.Message}");
            return 1;
        }
        catch (Exception e)
        {
            // The message is left out: an unknown exception's message could hold a secret. The wording fits an error at start and at stop.
            error.WriteLine($"error: the server stopped with an error ({e.GetType().FullName}).");
            error.WriteLine(e.StackTrace);
            return 1;
        }
    }
}
