using System.Globalization;
using Packmoat.Host;
using Packmoat.Infrastructure;

// The core server: the core's composition with no modules (#197). PORT (#233 E2) is read here and nowhere else.
// A bad PORT throws before anything is built, as every other bad setting does (PackmoatApp.Build). Any error at start prints one
// error: line and exits 1 (#240, StartupFailure).
return StartupFailure.Run(() => PackmoatApp.Build(ServerPort.Apply(args, Settings.Get(ServerPort.Variable)), []), Console.Error);

// Lets the integration tests reference this assembly's entry type.
public partial class Program;

/// <summary>
/// PORT (#233 E2): the port that platforms such as Cloud Run, Render or Fly name for a container. When it is set, the server listens on it
/// on every interface (<c>--urls http://+:PORT</c>, which wins over ASPNETCORE_URLS). When it is not set, nothing changes: the image
/// listens on 127.0.0.1:8080, as the deploy kit expects. A value that is not a whole number from 1 to 65535 stops the server at start.
/// It is read through <see cref="Settings.Get"/>, like every other setting, so PORT_FILE works too. An empty PORT is not a whole number,
/// so it stops the start too.
/// </summary>
public static class ServerPort
{
    public const string Variable = "PORT";

    /// <summary>The arguments to start with: <paramref name="args"/> as they are when <paramref name="port"/> is null, else with
    /// <c>--urls http://+:port</c> added at the end.</summary>
    /// <exception cref="SettingsException">PORT is set but is not a whole number from 1 to 65535.</exception>
    public static string[] Apply(string[] args, string? port)
    {
        if (port is null) return args;
        if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > 65535)
            throw new SettingsException($"{Variable} must be a whole number from 1 to 65535.");
        return [.. args, "--urls", $"http://+:{number}"];
    }
}
