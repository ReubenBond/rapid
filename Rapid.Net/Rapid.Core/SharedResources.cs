using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rapid;

/// <summary>
/// Holds all resources that are shared across a single instance of Rapid.
/// </summary>
public sealed partial class SharedResources(ILoggerFactory? loggerFactory = null, TimeProvider? timeProvider = null) : IDisposable
{
    private readonly ILogger<SharedResources> _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SharedResources>();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly List<Task> _backgroundTasks = [];
    private readonly Lock _backgroundTasksLock = new();

    /// <summary>
    /// Gets the TimeProvider used for all time-related operations.
    /// </summary>
    public TimeProvider TimeProvider { get; } = timeProvider ?? TimeProvider.System;

    public CancellationToken ShuttingDown => _shutdownCts.Token;

    /// <summary>
    /// Tracks a background task to ensure it can be awaited during shutdown.
    /// </summary>
    /// <param name="task">The task to track.</param>
    public void TrackBackgroundTask(Task task)
    {
        lock (_backgroundTasksLock)
        {
            _backgroundTasks.Add(task);
        }
    }

    public void StartShutdown()
    {
        _shutdownCts.Cancel();
    }

    /// <summary>
    /// Waits for all tracked background tasks to complete.
    /// </summary>
    /// <param name="timeout">Maximum time to wait for tasks to complete.</param>
    /// <param name="cancellationToken">Cancellation token to observe.</param>
    /// <returns>A task that completes when all background tasks finish or timeout occurs.</returns>
    public async Task WaitForBackgroundTasksAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Task[] tasks;
        lock (_backgroundTasksLock)
        {
            tasks = [.. _backgroundTasks];
        }

        if (tasks.Length == 0)
        {
            return;
        }

        LogWaitingForBackgroundTasks(tasks.Length);

        try
        {
            await Task.WhenAll(tasks).WaitAsync(timeout, cancellationToken).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            LogBackgroundTaskTimeout();
        }
        catch (OperationCanceledException)
        {
            // Expected during forced shutdown
        }
    }

    public void Dispose()
    {
        _shutdownCts.Cancel();
        _shutdownCts.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Waiting for {Count} background tasks to complete")]
    private partial void LogWaitingForBackgroundTasks(int Count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Timeout waiting for background tasks to complete")]
    private partial void LogBackgroundTaskTimeout();
}
