namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// Times on PostgreSQL: every time column is timestamptz, held in UTC to the microsecond. A time goes in through
/// <see cref="ToDatabase"/>: converted to UTC, then cut (never rounded) to whole microseconds, so what is stored is never later than the
/// time given, and reading it back gives the same instant whatever the session's time zone. It comes back through <see cref="FromDatabase"/>
/// with offset 0, as the SQLite adapters' "O" text reads back.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public static class PostgresTime
{
    /// <summary>Ticks (100 ns) in one microsecond, timestamptz's resolution.</summary>
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMicrosecond;

    /// <summary>
    /// <paramref name="time"/> as the value to send for a timestamptz: the same instant in UTC (offset 0, which Npgsql requires), cut to
    /// whole microseconds. The ticks below a microsecond are dropped, so 12:00:00.0000019 is stored as 12:00:00.000001.
    /// <para>The two edges, decided: <see cref="DateTimeOffset.MinValue"/>, and any time in its first microsecond
    /// (which the cut makes it), is refused with <see cref="ArgumentOutOfRangeException"/>. Npgsql would write it as -infinity, which reads
    /// back as no instant at all (a DateTime that is not UTC), so it could be stored but never read; no store has a use for it.
    /// <see cref="DateTimeOffset.MaxValue"/> is stored as an ordinary time: cut to 9999-12-31T23:59:59.999999Z, it is no longer the
    /// DateTime.MaxValue Npgsql writes as infinity, and it reads back as that same finite instant. Nothing is clamped.</para>
    /// </summary>
    public static DateTimeOffset ToDatabase(DateTimeOffset time)
    {
        var utc = time.UtcTicks;
        var cut = utc - utc % TicksPerMicrosecond;
        if (cut == DateTime.MinValue.Ticks)
            throw new ArgumentOutOfRangeException(nameof(time), time,
                "DateTimeOffset.MinValue (or a time in its first microsecond) is not stored: PostgreSQL would hold it as -infinity, which reads back as no time.");
        return new DateTimeOffset(cut, TimeSpan.Zero);
    }

    /// <summary><see cref="ToDatabase(DateTimeOffset)"/> for a nullable time; null stays null.</summary>
    public static DateTimeOffset? ToDatabase(DateTimeOffset? time) => time is { } t ? ToDatabase(t) : null;

    /// <summary>
    /// A timestamptz as Npgsql reads it (a <see cref="DateTime"/> of kind UTC) as a time with offset 0. Any other kind is refused: it would
    /// mean the value did not come from a timestamptz column.
    /// </summary>
    public static DateTimeOffset FromDatabase(DateTime utc) => utc.Kind == DateTimeKind.Utc
        ? new DateTimeOffset(utc, TimeSpan.Zero)
        : throw new ArgumentException($"A timestamptz reads as a UTC DateTime; this one is {utc.Kind}.", nameof(utc));

    /// <summary>A timestamptz read as a <see cref="DateTimeOffset"/> (Npgsql gives offset 0): refused unless its offset is 0.</summary>
    public static DateTimeOffset FromDatabase(DateTimeOffset time) => time.Offset == TimeSpan.Zero
        ? time
        : throw new ArgumentException($"A timestamptz reads back with offset 0; this one has {time.Offset}.", nameof(time));
}
