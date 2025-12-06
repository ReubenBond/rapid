namespace Rapid;

/// <summary>
/// Extension methods for TimeProvider to support testable delays.
/// </summary>
internal static class TimeProviderExtensions
{
    /// <summary>
    /// Creates a task that will complete after a time delay.
    /// </summary>
    /// <param name="timeProvider">The TimeProvider to use.</param>
    /// <param name="delay">The time span to wait before completing the returned task.</param>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the time delay.</returns>
    public static Task Delay(this TimeProvider timeProvider, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        if (timeProvider == TimeProvider.System)
        {
            return Task.Delay(delay, cancellationToken);
        }

        // For fake time providers, use a timer
        var tcs = new TaskCompletionSource<bool>();
        var timer = timeProvider.CreateTimer(_ => tcs.TrySetResult(true), null, delay, Timeout.InfiniteTimeSpan);

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                timer.Dispose();
                tcs.TrySetCanceled(cancellationToken);
            });
        }

        return tcs.Task.ContinueWith(_ => timer.Dispose(), TaskScheduler.Default);
    }
}
