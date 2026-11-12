namespace Packmoat.Application;

/// <summary>
/// The master-key check: the database records, encrypted with the master key it was made with, a marker that only that
/// key reads. A wrong key stops the start instead of failing later, one customer at a time.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public interface IMasterKeyCheck
{
    /// <summary>
    /// The start's check: records the marker when the database has none, and otherwise throws <see cref="InvalidOperationException"/>
    /// unless the master key in use reads it.
    /// </summary>
    Task EnsureAsync(CancellationToken ct);

    /// <summary>
    /// The readiness check's: true when the master key in use reads the marker. Read only: it never records a marker, and a
    /// database with none is false (the start always records one).
    /// </summary>
    Task<bool> MatchesAsync(CancellationToken ct);
}
