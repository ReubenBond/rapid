using System.Diagnostics.CodeAnalysis;
using Rapid.Tests.Simulation;

namespace Rapid.Tests.SimulationTests;

/// <summary>
/// Tests for determinism verification using the simulation harness.
/// Ensures that simulation runs are reproducible with the same seed.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
public sealed class DeterminismTests(ITestOutputHelper output) : IAsyncLifetime
{
    private SimulationHarness _harness = null!;
    private const int TestSeed = 89012;

    public ValueTask InitializeAsync()
    {
        output.WriteLine($"[DeterminismTests] Initializing with seed {TestSeed}");
        _harness = new SimulationHarness(seed: TestSeed, output);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        output.WriteLine("[DeterminismTests] Disposing harness");
        await _harness.DisposeAsync();
    }

    #region Reproducibility Tests (DET-001 to DET-004)

    [Fact]
    public async Task SameSeedProducesSameRandomSequence()
    {
        await using var harness1 = new SimulationHarness(seed: 11111, output);
        await using var harness2 = new SimulationHarness(seed: 11111, output);

#pragma warning disable CA5394 // Do not use insecure randomness
        var seq1 = Enumerable.Range(0, 100).Select(_ => harness1.Random.Next()).ToList();
#pragma warning restore CA5394 // Do not use insecure randomness
#pragma warning disable CA5394 // Do not use insecure randomness
        var seq2 = Enumerable.Range(0, 100).Select(_ => harness2.Random.Next()).ToList();
#pragma warning restore CA5394 // Do not use insecure randomness

        Assert.Equal(seq1, seq2);
    }

    [Fact]
    public async Task DifferentSeedsProduceDifferentSequences()
    {
        await using var harness1 = new SimulationHarness(seed: 11111, output);
        await using var harness2 = new SimulationHarness(seed: 22222, output);

#pragma warning disable CA5394 // Do not use insecure randomness
        var seq1 = Enumerable.Range(0, 100).Select(_ => harness1.Random.Next()).ToList();
#pragma warning restore CA5394 // Do not use insecure randomness
#pragma warning disable CA5394 // Do not use insecure randomness
        var seq2 = Enumerable.Range(0, 100).Select(_ => harness2.Random.Next()).ToList();
#pragma warning restore CA5394 // Do not use insecure randomness

        Assert.NotEqual(seq1, seq2);
    }

    [Fact]
    public void ForkedRandomIsDeterministic()
    {
        var random = _harness.Random;

        // Fork at same state should produce same results
        var fork1 = random.Fork();
#pragma warning disable CA5394 // Do not use insecure randomness
        var state = random.Next(); // Consume one value
#pragma warning restore CA5394 // Do not use insecure randomness
        var fork2 = random.Fork();

        // fork1 and fork2 should produce different sequences (since base random advanced)
#pragma warning disable CA5394 // Do not use insecure randomness
        var seq1 = Enumerable.Range(0, 10).Select(_ => fork1.Next()).ToList();
#pragma warning restore CA5394 // Do not use insecure randomness
#pragma warning disable CA5394 // Do not use insecure randomness
        var seq2 = Enumerable.Range(0, 10).Select(_ => fork2.Next()).ToList();
#pragma warning restore CA5394 // Do not use insecure randomness

        Assert.NotEqual(seq1, seq2);
    }

    [Fact]
    public void TimeAdvancementIsDeterministic()
    {
        var initialTime = _harness.TimeProvider.GetUtcNow();

        _harness.TimeProvider.Advance(TimeSpan.FromMinutes(5));

        var newTime = _harness.TimeProvider.GetUtcNow();
        Assert.Equal(initialTime + TimeSpan.FromMinutes(5), newTime);
    }

    #endregion

    #region Event Logging (DET-020 to DET-023)

    [Fact]
    public void EventsAreLoggedWithCorrectLogicalTime()
    {
        var initialLogicalTime = _harness.LogicalTime;

        _harness.CreateSeedNode();

        var events = _harness.EventLog;
        var nodeCreatedEvent = events.FirstOrDefault(e => e.Type == SimulationEventType.NodeCreated);

        Assert.NotEqual(default, nodeCreatedEvent);
        Assert.Equal(initialLogicalTime, nodeCreatedEvent.LogicalTime);
    }

    [Fact]
    public void EventsAreLoggedWithCorrectSimulatedTime()
    {
        var initialTime = _harness.TimeProvider.GetUtcNow();

        _harness.CreateSeedNode();

        var events = _harness.EventLog;
        var nodeCreatedEvent = events.FirstOrDefault(e => e.Type == SimulationEventType.NodeCreated);

        Assert.NotEqual(default, nodeCreatedEvent);
        Assert.Equal(initialTime, nodeCreatedEvent.SimulatedTime);
    }

    [Fact]
    public void EventLogIsImmutableCopy()
    {
        _harness.CreateSeedNode();

        var log1 = _harness.EventLog;
        var log2 = _harness.EventLog;

        Assert.NotSame(log1, log2);
        Assert.Equal(log1.Count, log2.Count);
    }

    [Fact]
    public void HarnessCreatedEventIsFirstEvent()
    {
        var events = _harness.EventLog;

        Assert.NotEmpty(events);
        Assert.Equal(SimulationEventType.HarnessCreated, events[0].Type);
    }

    #endregion

    #region RunUntil Tests

    [Fact]
    public void RunUntilReturnsWhenConditionMet()
    {
        var conditionMet = false;
        var task = new Task(() => conditionMet = true);
        task.Start(_harness.Scheduler);

        var result = _harness.RunUntil(() => conditionMet);

        Assert.True(result);
        Assert.True(conditionMet);
    }

    [Fact]
    public void RunUntilReturnsFalseWhenMaxStepsReached()
    {
        var result = _harness.RunUntil(() => false, maxIterations: 10);

        Assert.False(result);
    }

    [Fact]
    public void RunUntilConvergedWorks()
    {
        _harness.CreateSeedNode();

        // Should return true immediately since single node is already converged
        var result = _harness.RunUntilConverged(expectedSize: 1, maxIterations: 10);

        Assert.True(result);
    }

    #endregion

    #region Seed Access Tests

    [Fact]
    public void SeedIsAccessible()
    {
        Assert.Equal(TestSeed, _harness.Seed);
    }

    #endregion
}
