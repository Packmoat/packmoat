namespace Packmoat.Domain;

/// <summary>Source of time, so rules can be tested with a fake clock.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
