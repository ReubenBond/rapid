using Rapid.Tests.Simulation;

namespace Rapid.Tests;

/// <summary>
/// Tests for the simulation task scheduler and synchronization context.
/// </summary>
public sealed class SimulationSchedulerTests
{
    [Fact]
    public void QueuedTasksAreNotExecutedAutomatically()
    {
        var taskQueue = new SimulationTaskQueue();
        var scheduler = new SimulationTaskScheduler(taskQueue);
        var executed = false;

        var task = new Task(() => executed = true);
        task.Start(scheduler);

        Assert.False(executed);
        Assert.Equal(1, scheduler.PendingCount);
    }

    [Fact]
    public void StepExecutesSingleTask()
    {
        var taskQueue = new SimulationTaskQueue();
        var scheduler = new SimulationTaskScheduler(taskQueue);
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
        var taskQueue = new SimulationTaskQueue();
        var scheduler = new SimulationTaskScheduler(taskQueue);
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
        var taskQueue = new SimulationTaskQueue();
        var scheduler = new SimulationTaskScheduler(taskQueue);
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
    public void ClearRemovesAllPendingTasks()
    {
        var taskQueue = new SimulationTaskQueue();
        var scheduler = new SimulationTaskScheduler(taskQueue);

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
        var taskQueue = new SimulationTaskQueue();
        var scheduler = new SimulationTaskScheduler(taskQueue);
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
        var taskQueue = new SimulationTaskQueue();
        var scheduler = new SimulationTaskScheduler(taskQueue);
        var syncContext = new SimulationSynchronizationContext(scheduler);
        var executed = false;

        syncContext.Post(_ => executed = true, null);

        Assert.False(executed);
        scheduler.Step();
        Assert.True(executed);
    }

    [Fact]
    public void SynchronizationContextSendExecutesSynchronously()
    {
        var taskQueue = new SimulationTaskQueue();
        var scheduler = new SimulationTaskScheduler(taskQueue);
        var syncContext = new SimulationSynchronizationContext(scheduler);
        var executed = false;

        syncContext.Send(_ => executed = true, null);

        Assert.True(executed);
    }

    [Fact]
    public void SynchronizationContextCreateCopyReturnsNewInstance()
    {
        var taskQueue = new SimulationTaskQueue();
        var scheduler = new SimulationTaskScheduler(taskQueue);
        var syncContext = new SimulationSynchronizationContext(scheduler);

        var copy = syncContext.CreateCopy();

        Assert.NotSame(syncContext, copy);
        Assert.IsType<SimulationSynchronizationContext>(copy);
    }

    [Fact]
    public void SchedulerWithTaskQueueOrdersByTime()
    {
        var taskQueue = new SimulationTaskQueue();
        var scheduler = new SimulationTaskScheduler(taskQueue);
        var order = new List<string>();

        // Queue first task
        var task1 = new Task(() => order.Add("first"));
        task1.Start(scheduler);

        // Advance time and queue second task
        taskQueue.AdvanceTime(TimeSpan.FromSeconds(1).Ticks);
        var task2 = new Task(() => order.Add("second"));
        task2.Start(scheduler);

        scheduler.StepAll();

        // First task should execute before second (queued at earlier time)
        Assert.Equal(["first", "second"], order);
    }

    [Fact]
    public void TryExecuteOneReturnsFalseWhenEmpty()
    {
        var taskQueue = new SimulationTaskQueue();
        var scheduler = new SimulationTaskScheduler(taskQueue);

        var result = scheduler.TryExecuteOne();

        Assert.False(result);
    }

    [Fact]
    public void HasPendingTasksReflectsQueueState()
    {
        var taskQueue = new SimulationTaskQueue();
        var scheduler = new SimulationTaskScheduler(taskQueue);

        Assert.False(scheduler.HasPendingTasks);

        var task = new Task(() => { });
        task.Start(scheduler);

        Assert.True(scheduler.HasPendingTasks);

        scheduler.Step();

        Assert.False(scheduler.HasPendingTasks);
    }
}
