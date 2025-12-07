using Rapid.Tests.Simulation;
using Microsoft.Extensions.Time.Testing;

namespace Rapid.Tests;

/// <summary>
/// Tests for the deterministic task scheduler and synchronization context.
/// </summary>
public sealed class DeterministicSchedulerTests
{
    [Fact]
    public void QueuedTasksAreNotExecutedAutomatically()
    {
        var scheduler = new DeterministicTaskScheduler();
        var executed = false;

        var task = new Task(() => executed = true);
        task.Start(scheduler);

        Assert.False(executed);
        Assert.Equal(1, scheduler.PendingCount);
    }

    [Fact]
    public void StepExecutesSingleTask()
    {
        var scheduler = new DeterministicTaskScheduler();
        var executed = false;

        var task = new Task(() => executed = true);
        task.Start(scheduler);

        var count = scheduler.Step();

        Assert.Equal(1, count);
        Assert.True(executed);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void StepAllExecutesAllTasks()
    {
        var scheduler = new DeterministicTaskScheduler();
        var count = 0;

        for (var i = 0; i < 5; i++)
        {
            var task = new Task(() => Interlocked.Increment(ref count));
            task.Start(scheduler);
        }

        var executed = scheduler.StepAll();

        Assert.Equal(5, executed);
        Assert.Equal(5, count);
        Assert.False(scheduler.HasPendingTasks);
    }

    [Fact]
    public void StepWithCountLimitsExecution()
    {
        var scheduler = new DeterministicTaskScheduler();
        var count = 0;

        for (var i = 0; i < 10; i++)
        {
            var task = new Task(() => Interlocked.Increment(ref count));
            task.Start(scheduler);
        }

        var executed = scheduler.Step(3);

        Assert.Equal(3, executed);
        Assert.Equal(3, count);
        Assert.Equal(7, scheduler.PendingCount);
    }

    [Fact]
    public void StepUntilStopsOnCondition()
    {
        var scheduler = new DeterministicTaskScheduler();
        var count = 0;

        for (var i = 0; i < 10; i++)
        {
            var task = new Task(() => Interlocked.Increment(ref count));
            task.Start(scheduler);
        }

        var executed = scheduler.StepUntil(() => count >= 5);

        Assert.Equal(5, executed);
        Assert.Equal(5, count);
        Assert.Equal(5, scheduler.PendingCount);
    }

    [Fact]
    public void ClearRemovesAllPendingTasks()
    {
        var scheduler = new DeterministicTaskScheduler();

        for (var i = 0; i < 5; i++)
        {
            var task = new Task(() => { });
            task.Start(scheduler);
        }

        scheduler.Clear();

        Assert.False(scheduler.HasPendingTasks);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void TasksExecuteInFifoOrder()
    {
        var scheduler = new DeterministicTaskScheduler();
        var order = new List<int>();

        for (var i = 0; i < 5; i++)
        {
            var index = i;
            var task = new Task(() => order.Add(index));
            task.Start(scheduler);
        }

        scheduler.StepAll();

        Assert.Equal([0, 1, 2, 3, 4], order);
    }

    [Fact]
    public void SynchronizationContextPostRoutesToScheduler()
    {
        var scheduler = new DeterministicTaskScheduler();
        var syncContext = new DeterministicSynchronizationContext(scheduler);
        var executed = false;

        syncContext.Post(_ => executed = true, null);

        Assert.False(executed);
        scheduler.Step();
        Assert.True(executed);
    }

    [Fact]
    public void SynchronizationContextSendExecutesSynchronously()
    {
        var scheduler = new DeterministicTaskScheduler();
        var syncContext = new DeterministicSynchronizationContext(scheduler);
        var executed = false;

        syncContext.Send(_ => executed = true, null);

        Assert.True(executed);
    }

    [Fact]
    public void SynchronizationContextCreateCopyReturnsNewInstance()
    {
        var scheduler = new DeterministicTaskScheduler();
        var syncContext = new DeterministicSynchronizationContext(scheduler);

        var copy = syncContext.CreateCopy();

        Assert.NotSame(syncContext, copy);
        Assert.IsType<DeterministicSynchronizationContext>(copy);
    }

    [Fact]
    public void SchedulerWithTimeProviderOrdersByTime()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var scheduler = new DeterministicTaskScheduler(timeProvider);
        var order = new List<string>();

        // Queue first task
        var task1 = new Task(() => order.Add("first"));
        task1.Start(scheduler);

        // Advance time and queue second task
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var task2 = new Task(() => order.Add("second"));
        task2.Start(scheduler);

        scheduler.StepAll();

        // First task should execute before second (queued at earlier time)
        Assert.Equal(["first", "second"], order);
    }

    [Fact]
    public void TryExecuteOneReturnsFalseWhenEmpty()
    {
        var scheduler = new DeterministicTaskScheduler();

        var result = scheduler.TryExecuteOne();

        Assert.False(result);
    }

    [Fact]
    public void HasPendingTasksReflectsQueueState()
    {
        var scheduler = new DeterministicTaskScheduler();

        Assert.False(scheduler.HasPendingTasks);

        var task = new Task(() => { });
        task.Start(scheduler);

        Assert.True(scheduler.HasPendingTasks);

        scheduler.Step();

        Assert.False(scheduler.HasPendingTasks);
    }
}
