namespace Rapid.Tests.Simulation;

/// <summary>
/// A seeded random number generator for deterministic simulation testing.
/// Wraps System.Random with a known seed for reproducibility.
/// </summary>
/// <remarks>
/// This class intentionally uses System.Random for reproducibility in deterministic simulation testing.
/// It is NOT intended for security-sensitive operations.
/// </remarks>
#pragma warning disable CA5394 // Do not use insecure randomness - intentionally deterministic for simulation testing
internal sealed class DeterministicRandom
{
    private readonly Random _random;
    private readonly int _seed;

    /// <summary>
    /// Creates a new deterministic random with the specified seed.
    /// </summary>
    /// <param name="seed">The seed for reproducible random sequences.</param>
    public DeterministicRandom(int seed)
    {
        _seed = seed;
        _random = new Random(seed);
    }

    /// <summary>
    /// Gets the seed used to initialize this random instance.
    /// </summary>
    public int Seed => _seed;

    /// <summary>
    /// Returns a non-negative random integer.
    /// </summary>
    public int Next() => _random.Next();

    /// <summary>
    /// Returns a non-negative random integer less than the specified maximum.
    /// </summary>
    public int Next(int maxValue) => _random.Next(maxValue);

    /// <summary>
    /// Returns a random integer within the specified range.
    /// </summary>
    public int Next(int minValue, int maxValue) => _random.Next(minValue, maxValue);

    /// <summary>
    /// Returns a random double between 0.0 and 1.0.
    /// </summary>
    public double NextDouble() => _random.NextDouble();

    /// <summary>
    /// Fills the specified byte array with random bytes.
    /// </summary>
    public void NextBytes(byte[] buffer) => _random.NextBytes(buffer);

    /// <summary>
    /// Fills the specified span with random bytes.
    /// </summary>
    public void NextBytes(Span<byte> buffer) => _random.NextBytes(buffer);

    /// <summary>
    /// Returns a random boolean value.
    /// </summary>
    public bool NextBool() => _random.Next(2) == 1;

    /// <summary>
    /// Returns a random TimeSpan between zero and maxValue.
    /// </summary>
    public TimeSpan NextTimeSpan(TimeSpan maxValue)
    {
        var ticks = (long)(_random.NextDouble() * maxValue.Ticks);
        return TimeSpan.FromTicks(ticks);
    }

    /// <summary>
    /// Returns a random TimeSpan between minValue and maxValue.
    /// </summary>
    public TimeSpan NextTimeSpan(TimeSpan minValue, TimeSpan maxValue)
    {
        var range = maxValue.Ticks - minValue.Ticks;
        var ticks = minValue.Ticks + (long)(_random.NextDouble() * range);
        return TimeSpan.FromTicks(ticks);
    }

    /// <summary>
    /// Shuffles the elements of the list in place.
    /// </summary>
    public void Shuffle<T>(IList<T> list)
    {
        ArgumentNullException.ThrowIfNull(list);
        var n = list.Count;
        while (n > 1)
        {
            n--;
            var k = _random.Next(n + 1);
            (list[k], list[n]) = (list[n], list[k]);
        }
    }

    /// <summary>
    /// Returns a random element from the list.
    /// </summary>
    public T Choose<T>(IList<T> list)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (list.Count == 0)
            throw new ArgumentException("List cannot be empty", nameof(list));
        return list[_random.Next(list.Count)];
    }

    /// <summary>
    /// Returns true with the specified probability (0.0 to 1.0).
    /// </summary>
    public bool Chance(double probability)
    {
        if (probability <= 0) return false;
        if (probability >= 1) return true;
        return _random.NextDouble() < probability;
    }

    /// <summary>
    /// Creates a new DeterministicRandom derived from this one.
    /// Useful for creating independent random streams.
    /// </summary>
    public DeterministicRandom Fork() => new(_random.Next());
}
#pragma warning restore CA5394
