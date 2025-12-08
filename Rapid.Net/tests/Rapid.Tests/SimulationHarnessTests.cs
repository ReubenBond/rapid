using Rapid.Tests.Simulation;

namespace Rapid.Tests;

/// <summary>
/// Tests for the simulation harness.
/// </summary>
public sealed class SimulationHarnessTests : IAsyncLifetime
{
    private SimulationHarness _harness = null!;

    public ValueTask InitializeAsync()
    {
        _harness = new SimulationHarness(seed: 54321);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public void CreateSeedNodeWorksWithDeterministicHarness()
    {
        var node = _harness.CreateSeedNode();

        Assert.NotNull(node);
        Assert.True(node.IsInitialized);
        Assert.Equal(1, node.MembershipSize);
    }

    [Fact]
    public void LogicalTimeStartsAtZero() => Assert.Equal(0, _harness.LogicalTime);

    [Fact]
    public void StepIncrementsLogicalTime()
    {
        var scheduler = _harness.Scheduler;
        var task = new Task(() => { });
        task.Start(scheduler);

        _harness.Step();

        Assert.Equal(1, _harness.LogicalTime);
    }

    [Fact]
    public void StepAllIncrementsLogicalTimeByExecutedCount()
    {
        var scheduler = _harness.Scheduler;

        for (var i = 0; i < 5; i++)
        {
            var task = new Task(() => { });
            task.Start(scheduler);
        }

        _harness.StepAll();

        Assert.Equal(5, _harness.LogicalTime);
    }

    [Fact]
    public void EventLogRecordsEvents()
    {
        _harness.CreateSeedNode();

        var events = _harness.EventLog;

        Assert.Contains(events, e => e.Type == SimulationEventType.HarnessCreated);
        Assert.Contains(events, e => e.Type == SimulationEventType.NodeCreated);
    }

    [Fact]
    public void TimeProviderIsAvailable() => Assert.NotNull(_harness.TimeProvider);

    [Fact]
    public void AdvanceTimeAndStepAdvancesTimeProvider()
    {
        var initialTime = _harness.TimeProvider.GetUtcNow();

        _harness.AdvanceTimeAndStep(TimeSpan.FromMinutes(5));

        var newTime = _harness.TimeProvider.GetUtcNow();
        Assert.Equal(initialTime + TimeSpan.FromMinutes(5), newTime);
    }

    [Fact]
    public void SeedIsAccessible() => Assert.Equal(54321, _harness.Seed);

    [Fact]
    public async Task RandomIsDeterministic()
    {
        // Create two harnesses with same seed
        await using var harness1 = new SimulationHarness(seed: 99999);
        await using var harness2 = new SimulationHarness(seed: 99999);

        var values1 = Enumerable.Range(0, 10).Select(_ => harness1.Random.Next()).ToList();
        var values2 = Enumerable.Range(0, 10).Select(_ => harness2.Random.Next()).ToList();

        Assert.Equal(values1, values2);
    }

    [Fact]
    public void RunUntilReturnsWhenConditionMet()
    {
        var conditionMet = false;
        var scheduler = _harness.Scheduler;

        // Queue a task that sets the condition
        var task = new Task(() => conditionMet = true);
        task.Start(scheduler);

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
    public void NetworkIsAvailable() => Assert.NotNull(_harness.Network);

    [Fact]
    public void PartitionNodesRecordsEvent()
    {
        var seed = _harness.CreateSeedNode(0);

        // Create a second harness to get a second node since we can't await in the test
        // Just test that the method doesn't throw
        _harness.IsolateNode(seed);

        var events = _harness.EventLog;
        Assert.Contains(events, e => e.Type == SimulationEventType.NodeIsolated);
    }

    [Fact]
    public void ReconnectNodeRecordsEvent()
    {
        var seed = _harness.CreateSeedNode(0);
        _harness.IsolateNode(seed);
        _harness.ReconnectNode(seed);

        var events = _harness.EventLog;
        Assert.Contains(events, e => e.Type == SimulationEventType.NodeReconnected);
    }

    [Fact]
    public void CrashNodeRecordsEvent()
    {
        var seed = _harness.CreateSeedNode(0);
        _harness.CrashNode(seed);

        var events = _harness.EventLog;
        Assert.Contains(events, e => e.Type == SimulationEventType.NodeCrashed);
    }

    #region RunUntilIdle Tests

    [Fact]
    public void RunUntilIdleReturnsTrueWhenNoTasks()
    {
        var result = _harness.RunUntilIdle();
        Assert.True(result);
    }

    [Fact]
    public void RunUntilIdleExecutesAllPendingTasks()
    {
        var executionCount = 0;
        var scheduler = _harness.Scheduler;

        for (var i = 0; i < 5; i++)
        {
            var task = new Task(() => Interlocked.Increment(ref executionCount));
            task.Start(scheduler);
        }

        var result = _harness.RunUntilIdle();

        Assert.True(result);
        Assert.Equal(5, executionCount);
    }

    [Fact]
    public void RunUntilIdleAdvancesTimeForDelayedTasks()
    {
        var executed = false;
        var scheduler = _harness.Scheduler;
        var initialTime = _harness.TimeProvider.GetUtcNow();

        // Schedule a task for 1 minute in the future
        scheduler.TaskQueue.EnqueueAfter(() => executed = true, TimeSpan.FromMinutes(1).Ticks);

        var result = _harness.RunUntilIdle();

        Assert.True(result);
        Assert.True(executed);
        Assert.True(_harness.TimeProvider.GetUtcNow() >= initialTime + TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void RunUntilIdleRespectsMaxSimulatedTime()
    {
        var scheduler = _harness.Scheduler;
        var initialTime = _harness.TimeProvider.GetUtcNow();

        // Schedule a task for 10 minutes in the future
        scheduler.TaskQueue.EnqueueAfter(() => { }, TimeSpan.FromMinutes(10).Ticks);

        // Limit to 5 minutes
        var result = _harness.RunUntilIdle(maxSimulatedTime: TimeSpan.FromMinutes(5));

        Assert.False(result);
        // Time should not have advanced beyond 5 minutes
        Assert.True(_harness.TimeProvider.GetUtcNow() < initialTime + TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void SchedulerIsIdlePropertyWorks()
    {
        Assert.True(_harness.Scheduler.IsIdle);

        var task = new Task(() => { });
        task.Start(_harness.Scheduler);

        Assert.False(_harness.Scheduler.IsIdle);

        _harness.StepAll();

        Assert.True(_harness.Scheduler.IsIdle);
    }

    [Fact]
    public void DriveToCompletionWorks()
    {
        var seedNode = _harness.CreateSeedNode();

        // DriveToCompletion should complete synchronously for already-started tasks
        var result = _harness.DriveToCompletion(() => Task.FromResult(42));

        Assert.Equal(42, result);
    }

    #endregion
}
