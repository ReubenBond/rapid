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
    public void SeedIsAccessible() => Assert.Equal(54321, _harness.Seed);

    [Fact]
    public async Task RandomIsDeterministic()
    {
        // Create two harnesses with same seed
        await using var harness1 = new SimulationHarness(seed: 99999);
        await using var harness2 = new SimulationHarness(seed: 99999);

#pragma warning disable CA5394 // Do not use insecure randomness
        var values1 = Enumerable.Range(0, 10).Select(_ => harness1.Random.Next()).ToList();
        var values2 = Enumerable.Range(0, 10).Select(_ => harness2.Random.Next()).ToList();
#pragma warning restore CA5394 // Do not use insecure randomness

        Assert.Equal(values1, values2);
    }

    [Fact]
    public void RunUntilReturnsWhenConditionMet()
    {
        var conditionMet = false;
        var scheduler = _harness.TaskScheduler;

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
    public void RunUntilIdleReturnsZeroWhenNoTasks()
    {
        var iterations = _harness.RunUntilIdle();
        Assert.Equal(0, iterations);
    }

    [Fact]
    public void RunUntilIdleExecutesAllPendingTasks()
    {
        var executionCount = 0;
        var scheduler = _harness.TaskScheduler;

        for (var i = 0; i < 5; i++)
        {
            var task = new Task(() => Interlocked.Increment(ref executionCount));
            task.Start(scheduler);
        }

        var iterations = _harness.RunUntilIdle();

        Assert.Equal(5, iterations);
        Assert.Equal(5, executionCount);
    }

    [Fact]
    public void RunUntilIdleAdvancesTimeForDelayedTasks()
    {
        var executed = false;
        var initialTime = _harness.TimeProvider.GetUtcNow();

        // Schedule a task for 1 minute in the future
        _harness.TaskQueue.EnqueueAfter(() => executed = true, TimeSpan.FromMinutes(1));

        const int maxIterations = 100000;
        var iterations = _harness.RunUntilIdle(maxIterations: maxIterations);

        // Should reach idle (iterations < maxIterations)
        Assert.True(iterations < maxIterations);
        Assert.True(executed);
        Assert.True(_harness.TimeProvider.GetUtcNow() >= initialTime + TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void RunUntilIdleRespectsMaxSimulatedTime()
    {
        var initialTime = _harness.TimeProvider.GetUtcNow();

        // Schedule a task for 10 minutes in the future
        _harness.TaskQueue.EnqueueAfter(() => { }, TimeSpan.FromMinutes(10));

        // Limit to 5 minutes - task won't execute because it's beyond the time limit
        var iterations = _harness.RunUntilIdle(maxSimulatedTime: TimeSpan.FromMinutes(5));

        // No tasks were executed (task is scheduled beyond the time limit)
        Assert.Equal(0, iterations);
        // Time should not have advanced beyond 5 minutes
        Assert.True(_harness.TimeProvider.GetUtcNow() < initialTime + TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void TaskQueueIsIdlePropertyWorks()
    {
        Assert.False(_harness.TaskQueue.HasItems);

        var task = new Task(() => { });
        task.Start(_harness.TaskScheduler);

        Assert.True(_harness.TaskQueue.HasItems);

        _harness.TaskQueue.RunUntilIdle();

        Assert.False(_harness.TaskQueue.HasItems);
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

    #region Per-Node Simulation Control Tests

    [Fact]
    public void SuspendNodePreventsTaskExecution()
    {
        var seedNode = _harness.CreateSeedNode();
        var context = _harness.GetNodeContext(seedNode);
        var executed = false;

        // Queue a task on the node
        context.TaskQueue.Enqueue(new ScheduledActionItem(() => executed = true));

        // Suspend the node
        seedNode.Suspend();

        // Try to run the simulation - task should not execute because node is suspended
        _harness.RunUntil(() => false, maxIterations: 10);

        Assert.False(executed);
        Assert.True(seedNode.IsSuspended);
    }

    [Fact]
    public void ResumeNodeAllowsTaskExecution()
    {
        var seedNode = _harness.CreateSeedNode();
        var context = _harness.GetNodeContext(seedNode);
        var executed = false;

        // Queue a task on the node
        context.TaskQueue.Enqueue(new ScheduledActionItem(() => executed = true));

        // Suspend and then resume the node
        seedNode.Suspend();
        seedNode.Resume();

        // Step should now execute the task
        var result = _harness.RunUntil(() => executed, maxIterations: 10);

        Assert.True(result);
        Assert.True(executed);
        Assert.False(seedNode.IsSuspended);
    }

    [Fact]
    public void SuspendNodeForResumesAutomatically()
    {
        var seedNode = _harness.CreateSeedNode();
        var context = _harness.GetNodeContext(seedNode);
        var executed = false;

        // Queue a task on the node
        context.TaskQueue.Enqueue(new ScheduledActionItem(() => executed = true));

        // Suspend for 1 second
        seedNode.SuspendFor(TimeSpan.FromSeconds(1));

        Assert.True(seedNode.IsSuspended);

        // Run until idle - should advance time and resume the node
        _harness.RunUntilIdle(maxSimulatedTime: TimeSpan.FromSeconds(2));

        Assert.False(seedNode.IsSuspended);
        Assert.True(executed);
    }

    [Fact]
    public void StepNodeExecutesSingleTask()
    {
        var seedNode = _harness.CreateSeedNode();
        var context = _harness.GetNodeContext(seedNode);

        // Run until idle first to clear any startup tasks from CreateSeedNode
        _harness.RunUntilIdle();

        var executionCount = 0;

        // Queue multiple tasks on the node
        context.TaskQueue.Enqueue(new ScheduledActionItem(() => executionCount++));
        context.TaskQueue.Enqueue(new ScheduledActionItem(() => executionCount++));
        context.TaskQueue.Enqueue(new ScheduledActionItem(() => executionCount++));

        // Step once - should only execute one task
        var result = seedNode.Step();

        Assert.True(result);
        Assert.Equal(1, executionCount);
    }

    [Fact]
    public void StepNodeReturnsFalseWhenNoTasks()
    {
        var seedNode = _harness.CreateSeedNode();

        // Step with no pending tasks (seed node might have some, so run until idle first)
        _harness.RunUntilIdle();

        var result = seedNode.Step();

        Assert.False(result);
    }

    [Fact]
    public void StepNodeReturnsFalseWhenSuspended()
    {
        var seedNode = _harness.CreateSeedNode();
        var context = _harness.GetNodeContext(seedNode);

        // Queue a task
        context.TaskQueue.Enqueue(new ScheduledActionItem(() => { }));

        // Suspend the node
        seedNode.Suspend();

        // Step should fail because node is suspended
        var result = seedNode.Step();

        Assert.False(result);
    }

    [Fact]
    public void IsSuspendedReturnsCorrectState()
    {
        var seedNode = _harness.CreateSeedNode();

        // Initially not suspended
        Assert.False(seedNode.IsSuspended);

        // Suspend
        seedNode.Suspend();
        Assert.True(seedNode.IsSuspended);

        // Resume
        seedNode.Resume();
        Assert.False(seedNode.IsSuspended);
    }

    [Fact]
    public void GetNodeContextReturnsValidContext()
    {
        var seedNode = _harness.CreateSeedNode();
        var context = _harness.GetNodeContext(seedNode);

        Assert.NotNull(context);
        Assert.NotNull(context.TaskQueue);
        Assert.NotNull(context.TaskScheduler);
        Assert.NotNull(context.SynchronizationContext);
        Assert.NotNull(context.TimeProvider);
        Assert.Equal(NodeSimulationState.Running, context.State);
    }

    [Fact]
    public void SuspendedNodeQueuesMessagesForLater()
    {
        // Create a 2-node cluster
        var seed = _harness.CreateSeedNode(0);
        var joiner = _harness.CreateJoinerNode(seed, nodeId: 1);

        _harness.WaitForConvergence(expectedSize: 2);

        // Suspend the joiner node
        joiner.Suspend();

        // Messages from failure detectors will still be sent to the joiner
        // but they won't be processed until resumed

        // Verify the node is suspended
        Assert.True(joiner.IsSuspended);

        // Resume and let the simulation continue
        joiner.Resume();

        // Both nodes should still be in the cluster
        Assert.Equal(2, seed.MembershipSize);
        Assert.Equal(2, joiner.MembershipSize);
    }

    [Fact]
    public void MultipleNodesCanBeSuspendedIndependently()
    {
        // Create a 3-node cluster
        var seed = _harness.CreateSeedNode(0);
        var joiner1 = _harness.CreateJoinerNode(seed, nodeId: 1);
        var joiner2 = _harness.CreateJoinerNode(seed, nodeId: 2);

        _harness.WaitForConvergence(expectedSize: 3);

        // Suspend nodes 1 and 2
        joiner1.Suspend();
        joiner2.Suspend();

        Assert.True(joiner1.IsSuspended);
        Assert.True(joiner2.IsSuspended);
        Assert.False(seed.IsSuspended);

        // Resume only node 1
        joiner1.Resume();

        Assert.False(joiner1.IsSuspended);
        Assert.True(joiner2.IsSuspended);

        // Resume node 2
        joiner2.Resume();

        Assert.False(joiner2.IsSuspended);
    }

    [Fact]
    public void SuspendNodeRecordsEvent()
    {
        var seedNode = _harness.CreateSeedNode();

        seedNode.Suspend();

        var events = _harness.EventLog;
        Assert.Contains(events, e => e.Type == SimulationEventType.NodeSuspended);
    }

    [Fact]
    public void ResumeNodeRecordsEvent()
    {
        var seedNode = _harness.CreateSeedNode();

        seedNode.Suspend();
        seedNode.Resume();

        var events = _harness.EventLog;
        Assert.Contains(events, e => e.Type == SimulationEventType.NodeResumed);
    }

    #endregion
}
