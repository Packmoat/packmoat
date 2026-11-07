using Packmoat.Application;

namespace Packmoat.Application.Tests;

/// <summary>Records every alert; can be told to throw (a broken sink).</summary>
internal sealed class RecordingAlerts : IOpsAlerts
{
    public List<OpsAlert> Alerts { get; } = new();
    public bool Throw { get; set; }

    public void Notify(OpsAlert alert)
    {
        Alerts.Add(alert);
        if (Throw) throw new InvalidOperationException("sink down");
    }
}
