using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

// The clock and the handle every contract and every engine shares. Moved out of Kit.cs, which the hosted tests compile in
// for their own TestDb: they take these two from this assembly instead (a project reference), so a hosted TestDb is the same IEngineDb the
// core's contracts and engines take.

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
/// The handle a store contract holds on one fresh database, with nothing of any engine in it: the clock its stores are timed by and
/// the master key its stores encrypt with. Everything else a contract reaches through its engine.
/// </summary>
public interface IEngineDb : IAsyncDisposable
{
    FakeClock Clock { get; }
    AesGcmSecretProtector Protector { get; }
}
