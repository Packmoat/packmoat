namespace Packmoat.Application;

/// <summary>
/// A failure talking to a service outside Packmoat that a module uses (for example the hosted payment provider). The command line reports
/// it as "error: " and its message, so the message names the problem and never a secret.
/// </summary>
public abstract class ExternalServiceException : Exception
{
    protected ExternalServiceException(string message, Exception? inner = null) : base(message, inner) { }
}
