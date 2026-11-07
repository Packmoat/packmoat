using Packmoat.Application;

namespace Packmoat.Host;

/// <summary>
/// The core's alert sink (#194): each alert is a warning in the server's log ("Ops alert: " and its text), so a self-hosted server with no
/// alert channel still tells its operator. The text never holds a person, an email or a directory (<see cref="OpsAlert"/>). Never throws:
/// a broken log provider loses the alert, never the work that raised it. A module's sink replaces it.
/// </summary>
internal sealed class LogOpsAlerts : IOpsAlerts
{
    public const string Category = "Packmoat.Alerts";

    private readonly ILogger _log;

    public LogOpsAlerts(ILogger log) => _log = log;

    public void Notify(OpsAlert alert)
    {
        try { _log.LogWarning("Ops alert: {Text}", alert.Text); }
        catch (Exception) { /* best effort by contract */ }
    }
}
