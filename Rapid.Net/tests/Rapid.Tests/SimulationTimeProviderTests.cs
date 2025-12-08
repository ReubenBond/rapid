using Rapid.Tests.Simulation;

namespace Rapid.Tests;

/// <summary>
/// Tests for SimulationTimeProvider, modeled after Microsoft's FakeTimeProviderTests.
/// </summary>
public class SimulationTimeProviderTests
{
    private static readonly TimeSpan InfiniteTimeout = TimeSpan.FromMilliseconds(-1);

    #region Constructor Tests

    [Fact]
    public void ConstructorDefaultInitializationSetsExpectedValues()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        var now = timeProvider.GetUtcNow();
        var timestamp = timeProvider.GetTimestamp();
        var frequency = timeProvider.TimestampFrequency;

        Assert.Equal(2000, now.Year);
        Assert.Equal(1, now.Month);
        Assert.Equal(1, now.Day);
        Assert.Equal(0, now.Hour);
        Assert.Equal(0, now.Minute);
        Assert.Equal(0, now.Second);
        Assert.Equal(0, now.Millisecond);
        Assert.Equal(TimeSpan.Zero, now.Offset);
        Assert.Equal(TimeSpan.TicksPerSecond, frequency);

        var timestamp2 = timeProvider.GetTimestamp();
        var frequency2 = timeProvider.TimestampFrequency;
        var now2 = timeProvider.GetUtcNow();

        Assert.Equal(now, now2);
        Assert.Equal(frequency, frequency2);
        Assert.Equal(timestamp, timestamp2);
    }

    [Fact]
    public void ConstructorInitializesWithCustomDateTimeOffset()
    {
        var taskQueue = new SimulationTaskQueue();
        var customTime = new DateTimeOffset(2023, 6, 15, 10, 30, 45, TimeSpan.Zero);
        var timeProvider = new SimulationTimeProvider(taskQueue, customTime);

        var now = timeProvider.GetUtcNow();

        Assert.Equal(2023, now.Year);
        Assert.Equal(6, now.Month);
        Assert.Equal(15, now.Day);
        Assert.Equal(10, now.Hour);
        Assert.Equal(30, now.Minute);
        Assert.Equal(45, now.Second);
        Assert.Equal(customTime, timeProvider.Start);
    }

    #endregion

    #region GetTimestamp Tests

    [Fact]
    public void GetTimestampWithoutAdvanceDoesNotChange()
    {
        var taskQueue = new SimulationTaskQueue();
        var nowOffset = new DateTimeOffset(2000, 1, 1, 0, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new SimulationTimeProvider(taskQueue, nowOffset);

        var timestamp1 = timeProvider.GetTimestamp();
        var timestamp2 = timeProvider.GetTimestamp();

        Assert.Equal(timestamp1, timestamp2);
    }

    [Fact]
    public void GetTimestampAfterAdvanceChanges()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var timestamp1 = timeProvider.GetTimestamp();

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var timestamp2 = timeProvider.GetTimestamp();

        Assert.True(timestamp2 > timestamp1);
    }

    [Fact]
    public void GetElapsedTimeAfterAdvanceReturnsCorrectDuration()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var start = timeProvider.GetTimestamp();

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var elapsed = timeProvider.GetElapsedTime(start);

        Assert.Equal(TimeSpan.FromSeconds(1), elapsed);
    }

    #endregion

    #region Advance Tests

    [Fact]
    public void AdvanceForwardAdvancesByProperAmount()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue, new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero));

        var initialTimeUtcNow = timeProvider.GetUtcNow();
        var initialTimestamp = timeProvider.GetTimestamp();

        timeProvider.Advance(TimeSpan.FromMilliseconds(1234));

        var finalTimeUtcNow = timeProvider.GetUtcNow();
        var finalTimeTimestamp = timeProvider.GetTimestamp();

        var utcDelta = finalTimeUtcNow - initialTimeUtcNow;
        var perfDelta = finalTimeTimestamp - initialTimestamp;
        var elapsedTime = timeProvider.GetElapsedTime(initialTimestamp, finalTimeTimestamp);

        Assert.Equal(1, utcDelta.Seconds);
        Assert.Equal(234, utcDelta.Milliseconds);
        Assert.Equal(1234D, utcDelta.TotalMilliseconds);
        Assert.Equal(1.234D, (double)perfDelta / timeProvider.TimestampFrequency, 3);
        Assert.Equal(1234, elapsedTime.TotalMilliseconds);
    }

    [Fact]
    public void AdvanceByZeroDoesNotChangeTime()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var before = timeProvider.GetUtcNow();

        timeProvider.Advance(TimeSpan.Zero);

        Assert.Equal(before, timeProvider.GetUtcNow());
    }

    [Fact]
    public void AdvanceBackwardsThrowsArgumentOutOfRangeException()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        Assert.Throws<ArgumentOutOfRangeException>(() => timeProvider.Advance(TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public void AdvanceMultipleSmallIncrementsAccumulatesCorrectly()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var start = timeProvider.GetUtcNow();

        for (int i = 0; i < 100; i++)
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(10));
        }

        var elapsed = timeProvider.GetUtcNow() - start;
        Assert.Equal(1000, elapsed.TotalMilliseconds);
    }

    #endregion

    #region SetUtcNow Tests

    [Fact]
    public void SetUtcNowForwardAdvancesByProperAmount()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue, new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero));

        var initialTimeUtcNow = timeProvider.GetUtcNow();
        var initialTimestamp = timeProvider.GetTimestamp();

        timeProvider.SetUtcNow(timeProvider.GetUtcNow().AddMilliseconds(1234));

        var finalTimeUtcNow = timeProvider.GetUtcNow();
        var finalTimeTimestamp = timeProvider.GetTimestamp();

        var utcDelta = finalTimeUtcNow - initialTimeUtcNow;
        var perfDelta = finalTimeTimestamp - initialTimestamp;
        var elapsedTime = timeProvider.GetElapsedTime(initialTimestamp, finalTimeTimestamp);

        Assert.Equal(1, utcDelta.Seconds);
        Assert.Equal(234, utcDelta.Milliseconds);
        Assert.Equal(1234D, utcDelta.TotalMilliseconds);
        Assert.Equal(1.234D, (double)perfDelta / timeProvider.TimestampFrequency, 3);
        Assert.Equal(1234, elapsedTime.TotalMilliseconds);
    }

    [Fact]
    public void SetUtcNowBackwardsThrowsArgumentOutOfRangeException()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            timeProvider.SetUtcNow(timeProvider.GetUtcNow() - TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void SetUtcNowToSameTimeDoesNotThrow()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var currentTime = timeProvider.GetUtcNow();

        timeProvider.SetUtcNow(currentTime);

        Assert.Equal(currentTime, timeProvider.GetUtcNow());
    }

    #endregion

    #region Timer Basic Tests

    [Fact]
    public void CreateTimerWithDueTimeCreatesWaiter()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var callCount = 0;

        using var timer = timeProvider.CreateTimer(_ => callCount++, null, TimeSpan.FromSeconds(1), TimeSpan.Zero);

        Assert.Equal(0, callCount);
        Assert.True(taskQueue.WaitingTimerCount > 0);
        Assert.Equal(1, taskQueue.WaitingTimerCount);
    }

    [Fact]
    public void CreateTimerWithZeroDueTimeSchedulesCallback()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var callCount = 0;

        using var timer = timeProvider.CreateTimer(_ => callCount++, null, TimeSpan.Zero, TimeSpan.Zero);

        // Timer with TimeSpan.Zero schedules callback to task queue
        Assert.Equal(0, callCount);
        taskQueue.TryExecuteNext();
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void TimerCallbackFiresAfterAdvance()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var callCount = 0;

        using var timer = timeProvider.CreateTimer(_ => callCount++, null, TimeSpan.FromSeconds(1), TimeSpan.Zero);

        Assert.Equal(0, callCount);

        timeProvider.Advance(TimeSpan.FromMilliseconds(999));
        taskQueue.ExecuteAll();
        Assert.Equal(0, callCount);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        taskQueue.ExecuteAll();
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void TimerCallbackPassesState()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        object? receivedState = null;
        var expectedState = new object();

        using var timer = timeProvider.CreateTimer(state => receivedState = state, expectedState, TimeSpan.Zero, TimeSpan.Zero);
        taskQueue.TryExecuteNext();

        Assert.Same(expectedState, receivedState);
    }

    [Fact]
    public void PeriodicTimerFiresRepeatedly()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var callCount = 0;

        using var timer = timeProvider.CreateTimer(_ => callCount++, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        taskQueue.ExecuteAll();
        Assert.Equal(1, callCount);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        taskQueue.ExecuteAll();
        Assert.Equal(2, callCount);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        taskQueue.ExecuteAll();
        Assert.Equal(3, callCount);
    }

    [Fact]
    public void OneShotTimerFiresOnlyOnce()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var callCount = 0;

        using var timer = timeProvider.CreateTimer(_ => callCount++, null, TimeSpan.FromSeconds(1), TimeSpan.Zero);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        taskQueue.ExecuteAll();
        Assert.Equal(1, callCount);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        taskQueue.ExecuteAll();
        Assert.Equal(1, callCount); // Should still be 1

        timeProvider.Advance(TimeSpan.FromSeconds(10));
        taskQueue.ExecuteAll();
        Assert.Equal(1, callCount); // Should still be 1
    }

    [Fact]
    public void DisposedTimerDoesNotFire()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var callCount = 0;

        var timer = timeProvider.CreateTimer(_ => callCount++, null, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        timer.Dispose();

        timeProvider.Advance(TimeSpan.FromSeconds(10));
        taskQueue.ExecuteAll();
        Assert.Equal(0, callCount);
    }

    [Fact]
    public void ChangedTimerUsesNewValues()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var callCount = 0;

        using var timer = timeProvider.CreateTimer(_ => callCount++, null, TimeSpan.FromSeconds(10), TimeSpan.Zero);

        // Change to fire sooner
        timer.Change(TimeSpan.FromMilliseconds(100), TimeSpan.Zero);

        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        taskQueue.ExecuteAll();
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void TimerChangedToInfiniteDoesNotFire()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var callCount = 0;

        using var timer = timeProvider.CreateTimer(_ => callCount++, null, TimeSpan.FromSeconds(1), TimeSpan.Zero);

        // Change to infinite (disabled)
        timer.Change(Timeout.InfiniteTimeSpan, TimeSpan.Zero);

        timeProvider.Advance(TimeSpan.FromSeconds(100));
        taskQueue.ExecuteAll();
        Assert.Equal(0, callCount);
    }

    #endregion

    #region Timer Edge Cases

    [Fact]
    public void MultipleTimersFireInOrder()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var firedOrder = new List<int>();

        using var timer1 = timeProvider.CreateTimer(_ => firedOrder.Add(1), null, TimeSpan.FromSeconds(3), TimeSpan.Zero);
        using var timer2 = timeProvider.CreateTimer(_ => firedOrder.Add(2), null, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        using var timer3 = timeProvider.CreateTimer(_ => firedOrder.Add(3), null, TimeSpan.FromSeconds(2), TimeSpan.Zero);

        timeProvider.Advance(TimeSpan.FromSeconds(5));
        taskQueue.ExecuteAll();

        Assert.Equal([2, 3, 1], firedOrder);
    }

    [Fact]
    public void TimersWithSameDueTimeFireInScheduledOrder()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var firedOrder = new List<int>();

        using var timer1 = timeProvider.CreateTimer(_ => firedOrder.Add(1), null, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        using var timer2 = timeProvider.CreateTimer(_ => firedOrder.Add(2), null, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        using var timer3 = timeProvider.CreateTimer(_ => firedOrder.Add(3), null, TimeSpan.FromSeconds(1), TimeSpan.Zero);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        taskQueue.ExecuteAll();

        Assert.Equal([1, 2, 3], firedOrder);
    }

    [Fact]
    public void AdvanceTimeInCallbackPreventsInfiniteLoop()
    {
        var taskQueue = new SimulationTaskQueue();
        var oneSecond = TimeSpan.FromSeconds(1);
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var callCount = 0;

        using var timer = timeProvider.CreateTimer(_ =>
        {
            callCount++;
            // Advance the time with exactly the same amount as the period of the timer.
            // This could lead to an infinite loop where this callback repeatedly gets invoked.
            // A correct implementation will adjust the timer's wake time.
            timeProvider.Advance(oneSecond);
        }, null, TimeSpan.Zero, oneSecond);

        // Execute only the currently ready items (not items added during execution)
        // This prevents infinite loops when callbacks enqueue more items
        taskQueue.ExecuteAllCurrently();

        // Should not hang and call count should be limited
        Assert.True(callCount >= 1, "Timer should have fired at least once");
        Assert.True(callCount < 1000, "Timer should not have fired infinitely");
    }

    [Fact]
    public void TimerCallbackExceptionPropagates()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        using var timer1 = timeProvider.CreateTimer(_ => throw new InvalidOperationException("Test exception"),
            null, TimeSpan.FromSeconds(1), TimeSpan.Zero);

        timeProvider.Advance(TimeSpan.FromSeconds(3));
        // This should throw due to timer1's callback - exceptions propagate from timer callbacks
        var ex = Assert.Throws<InvalidOperationException>(() => taskQueue.ExecuteAll());
        Assert.Equal("Test exception", ex.Message);
    }

    #endregion

    #region AdvanceToNextTimer Tests

    [Fact]
    public void AdvanceToNextTimerWithPendingTimer()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var callCount = 0;

        using var timer = timeProvider.CreateTimer(_ => callCount++, null, TimeSpan.FromSeconds(5), TimeSpan.Zero);

        var result = timeProvider.AdvanceToNextTimer();
        taskQueue.ExecuteAll();

        Assert.True(result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void AdvanceToNextTimerWithNoTimersReturnsFalse()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        var result = timeProvider.AdvanceToNextTimer();

        Assert.False(result);
    }

    [Fact]
    public void AdvanceToNextTimerMultipleTimersFiresOnlyNext()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var firedTimers = new List<int>();

        using var timer1 = timeProvider.CreateTimer(_ => firedTimers.Add(1), null, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        using var timer2 = timeProvider.CreateTimer(_ => firedTimers.Add(2), null, TimeSpan.FromSeconds(2), TimeSpan.Zero);
        using var timer3 = timeProvider.CreateTimer(_ => firedTimers.Add(3), null, TimeSpan.FromSeconds(3), TimeSpan.Zero);

        timeProvider.AdvanceToNextTimer();
        taskQueue.ExecuteAll();
        Assert.Equal([1], firedTimers);

        timeProvider.AdvanceToNextTimer();
        taskQueue.ExecuteAll();
        Assert.Equal([1, 2], firedTimers);

        timeProvider.AdvanceToNextTimer();
        taskQueue.ExecuteAll();
        Assert.Equal([1, 2, 3], firedTimers);
    }

    #endregion

    #region TimeUntilNextTimer Tests

    [Fact]
    public void TimeUntilNextTimerNoPendingTimersReturnsNull()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        Assert.Null(timeProvider.TimeUntilNextTimer);
    }

    [Fact]
    public void TimeUntilNextTimerWithPendingTimerReturnsCorrectDuration()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        using var timer = timeProvider.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(5), TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(5), timeProvider.TimeUntilNextTimer);
    }

    [Fact]
    public void TimeUntilNextTimerAfterPartialAdvanceReturnsRemainingTime()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        using var timer = timeProvider.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(5), TimeSpan.Zero);

        timeProvider.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.FromSeconds(2), timeProvider.TimeUntilNextTimer);
    }

    [Fact]
    public void TimeUntilNextTimerPeriodicTimerShowsNextPeriod()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var callCount = 0;

        // Use a periodic timer so it stays registered
        using var timer = timeProvider.CreateTimer(_ => callCount++, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));

        // After timer fires, next time should be period away
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        taskQueue.ExecuteAll();
        Assert.Equal(1, callCount);
        Assert.Equal(TimeSpan.FromSeconds(10), timeProvider.TimeUntilNextTimer);
    }

    #endregion

    #region GetPendingTimers Tests

    [Fact]
    public void GetPendingTimersNoPendingReturnsEmpty()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        var timers = timeProvider.GetPendingTimers();

        Assert.Empty(timers);
    }

    [Fact]
    public void GetPendingTimersWithTimersReturnsAllPending()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        using var timer1 = timeProvider.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        using var timer2 = timeProvider.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(2), TimeSpan.Zero);

        var timers = timeProvider.GetPendingTimers();

        Assert.Equal(2, timers.Count);
    }

    [Fact]
    public void GetPendingTimersOrderedByWakeTime()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        using var timer1 = timeProvider.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(3), TimeSpan.Zero);
        using var timer2 = timeProvider.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        using var timer3 = timeProvider.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(2), TimeSpan.Zero);

        var timers = timeProvider.GetPendingTimers();

        Assert.Equal(3, timers.Count);
        Assert.True(timers[0].WakeupTime < timers[1].WakeupTime);
        Assert.True(timers[1].WakeupTime < timers[2].WakeupTime);
    }

    #endregion

    #region Task.Delay Integration Tests

    [Fact]
    public async Task DelayZeroDelayCompletesImmediately()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        var task = Task.Delay(TimeSpan.Zero, timeProvider, TestContext.Current.CancellationToken);

        Assert.True(task.IsCompleted);
        await task;
    }

    [Fact]
    public async Task DelayAwaitedCompletesSuccessfully()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        var delay = Task.Delay(TimeSpan.FromMilliseconds(1), timeProvider, TestContext.Current.CancellationToken);
        Assert.False(delay.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        taskQueue.ExecuteAll();
        await delay;

        Assert.True(delay.IsCompleted);
        Assert.False(delay.IsFaulted);
        Assert.False(delay.IsCanceled);
    }

    [Fact]
    public async Task DelayCancelledTokenThrowsTaskCanceledException()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        using var cts = new CancellationTokenSource();
        var delay = Task.Delay(InfiniteTimeout, timeProvider, cts.Token);
        Assert.False(delay.IsCompleted);

        await cts.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(async () => await delay);
    }

    [Fact]
    public async Task DelayWhenTimeAdvancedCompletesWithoutCancellation()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1000));

        var task = Task.Delay(TimeSpan.FromMilliseconds(10000), timeProvider, cts.Token);

        timeProvider.Advance(TimeSpan.FromMilliseconds(10000));
        taskQueue.ExecuteAll();

        await task;

        Assert.False(cts.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task DelayMultipleDelaysCompleteInCorrectOrder()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var completionOrder = new List<int>();

        var delay1 = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3), timeProvider);
            completionOrder.Add(1);
        }, TestContext.Current.CancellationToken);

        var delay2 = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider);
            completionOrder.Add(2);
        }, TestContext.Current.CancellationToken);

        var delay3 = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), timeProvider);
            completionOrder.Add(3);
        }, TestContext.Current.CancellationToken);

        // Give tasks time to start
        await Task.Delay(50, TestContext.Current.CancellationToken);

        timeProvider.Advance(TimeSpan.FromSeconds(5));
        taskQueue.ExecuteAll();

        await Task.WhenAll(delay1, delay2, delay3);

        Assert.Equal([2, 3, 1], completionOrder);
    }

    #endregion

    #region ToString Tests

    [Fact]
    public void ToStringDefaultReturnsProperFormat()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        var result = timeProvider.ToString();

        Assert.Equal("2000-01-01T00:00:00.000", result);
    }

    [Fact]
    public void ToStringCustomTimeReturnsProperFormat()
    {
        var taskQueue = new SimulationTaskQueue();
        var dto = new DateTimeOffset(new DateTime(2022, 1, 2, 3, 4, 5, 6), TimeSpan.Zero);
        var timeProvider = new SimulationTimeProvider(taskQueue, dto);

        Assert.Equal("2022-01-02T03:04:05.006", timeProvider.ToString());
    }

    [Fact]
    public void ToStringAfterAdvanceReturnsUpdatedTime()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));

        timeProvider.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(30)));

        Assert.Equal("2000-01-01T01:30:00.000", timeProvider.ToString());
    }

    #endregion

    #region HasPendingTimers and PendingTimerCount Tests

    [Fact]
    public void HasPendingTimersNoTimersReturnsFalse()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        Assert.Equal(0, taskQueue.WaitingTimerCount);
    }

    [Fact]
    public void HasPendingTimersWithTimerReturnsTrue()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        using var timer = timeProvider.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(1), TimeSpan.Zero);

        Assert.True(taskQueue.WaitingTimerCount > 0);
    }

    [Fact]
    public void HasPendingTimersAfterTimerFiresReturnsFalse()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        using var timer = timeProvider.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(1), TimeSpan.Zero);

        timeProvider.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0, taskQueue.WaitingTimerCount);
    }

    [Fact]
    public void PendingTimerCountTracksCorrectly()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        Assert.Equal(0, taskQueue.WaitingTimerCount);

        using var timer1 = timeProvider.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        Assert.Equal(1, taskQueue.WaitingTimerCount);

        using var timer2 = timeProvider.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(2), TimeSpan.Zero);
        Assert.Equal(2, taskQueue.WaitingTimerCount);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, taskQueue.WaitingTimerCount);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, taskQueue.WaitingTimerCount);
    }

    [Fact]
    public void PendingTimerCountWithPeriodicTimerStaysConstant()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        using var timer = timeProvider.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        Assert.Equal(1, taskQueue.WaitingTimerCount);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        taskQueue.ExecuteAll(); // Execute to trigger rescheduling of periodic timer
        Assert.Equal(1, taskQueue.WaitingTimerCount); // Periodic timer stays registered

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        taskQueue.ExecuteAll();
        Assert.Equal(1, taskQueue.WaitingTimerCount);
    }

    #endregion

    #region Timer Disposal Tests

    [Fact]
    public void TimerDisposeRemovesFromPending()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        var timer = timeProvider.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        Assert.Equal(1, taskQueue.WaitingTimerCount);

        timer.Dispose();
        Assert.Equal(0, taskQueue.WaitingTimerCount);
    }

    [Fact]
    public async Task TimerDisposeAsyncRemovesFromPending()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        var timer = timeProvider.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        Assert.Equal(1, taskQueue.WaitingTimerCount);

        await timer.DisposeAsync();
        Assert.Equal(0, taskQueue.WaitingTimerCount);
    }

    [Fact]
    public void TimerDoubleDisposeDoesNotThrow()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        var timer = timeProvider.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        timer.Dispose();
        timer.Dispose(); // Should not throw
    }

    [Fact]
    public void TimerChangeAfterDisposeReturnsFalse()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);

        var timer = timeProvider.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        timer.Dispose();

        var result = timer.Change(TimeSpan.FromSeconds(1), TimeSpan.Zero);
        Assert.False(result);
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public async Task ConcurrentAdvanceDoesNotCorruptState()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var callCount = 0;

        using var timer = timeProvider.CreateTimer(_ => Interlocked.Increment(ref callCount),
            null, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));

        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    timeProvider.Advance(TimeSpan.FromMilliseconds(1));
                    taskQueue.ExecuteAll();
                }
            }, TestContext.Current.CancellationToken));
        }

        await Task.WhenAll(tasks);

        // Should have advanced 1000ms total, timer should have fired many times
        Assert.True(callCount > 0, "Timer should have fired at least once");
    }

    [Fact]
    public async Task ConcurrentTimerCreationDoesNotCorruptState()
    {
        var taskQueue = new SimulationTaskQueue();
        var timeProvider = new SimulationTimeProvider(taskQueue);
        var timers = new List<ITimer>();
        var lockObj = new object();

        var tasks = new List<Task>();
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                var timer = timeProvider.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(1), TimeSpan.Zero);
                lock (lockObj)
                {
                    timers.Add(timer);
                }
            }, TestContext.Current.CancellationToken));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(100, taskQueue.WaitingTimerCount);

        foreach (var timer in timers)
        {
            timer.Dispose();
        }

        Assert.Equal(0, taskQueue.WaitingTimerCount);
    }

    #endregion

    #region Start Property Tests

    [Fact]
    public void StartReturnsInitialTime()
    {
        var taskQueue = new SimulationTaskQueue();
        var startTime = new DateTimeOffset(2023, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new SimulationTimeProvider(taskQueue, startTime);

        Assert.Equal(startTime, timeProvider.Start);
    }

    [Fact]
    public void StartUnchangedAfterAdvance()
    {
        var taskQueue = new SimulationTaskQueue();
        var startTime = new DateTimeOffset(2023, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new SimulationTimeProvider(taskQueue, startTime);

        timeProvider.Advance(TimeSpan.FromHours(5));

        Assert.Equal(startTime, timeProvider.Start);
        Assert.NotEqual(startTime, timeProvider.GetUtcNow());
    }

    #endregion

}
