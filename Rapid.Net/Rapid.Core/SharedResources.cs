using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rapid;

/// <summary>
/// Holds all resources that are shared across a single instance of Rapid.
/// </summary>
public sealed partial class SharedResources(
    ILoggerFactory? loggerFactory = null,
    TimeProvider? timeProvider = null,
    TaskScheduler? taskScheduler = null,
    Random? random = null,
    Func<Guid>? guidFactory = null) : IAsyncDisposable, IDisposable
{
    private readonly ILogger<SharedResources> _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SharedResources>();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly List<Task> _backgroundTasks = [];
    private readonly Lock _backgroundTasksLock = new();
    private readonly Random _random = random ?? Random.Shared;
    private readonly Func<Guid> _guidFactory = guidFactory ?? Guid.NewGuid;
    private int _disposed;

    /// <summary>
    /// Gets the TimeProvider used for all time-related operations.
    /// </summary>
    public TimeProvider TimeProvider { get; } = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Gets the TaskScheduler used for scheduling tasks.
    /// </summary>
    public TaskScheduler TaskScheduler { get; } = taskScheduler ?? TaskScheduler.Default;

    /// <summary>
    /// Generates a random double value between 0.0 and 1.0.
    /// </summary>
#pragma warning disable CA5394 // Do not use insecure randomness. Justification: this is not security-sensitive code.
    public double NextRandomDouble() => _random.NextDouble();
#pragma warning restore CA5394

    /// <summary>
    /// Generates a new GUID.
    /// </summary>
    public Guid NewGuid() => _guidFactory();

    /// <summary>
    /// Gets a cancellation token that is cancelled when shutdown begins.
    /// </summary>
    public CancellationToken ShuttingDownToken
    {
        get
        {
            try
            {
                return _shutdownCts.Token;
            }
            catch (ObjectDisposedException)
            {
                // If the CTS is disposed, return a cancelled token
                return new CancellationToken(canceled: true);
            }
        }
    }

    /// <summary>
    /// Gets whether shutdown has been initiated.
    /// </summary>
    public bool IsShuttingDown => Volatile.Read(ref _disposed) != 0 || _shutdownCts.IsCancellationRequested;

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

    /// <summary>
    /// Initiates shutdown by cancelling the shutdown token.
    /// </summary>
    public void StartShutdown()
    {
        try
        {
            _shutdownCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, shutdown already happened
        }
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

    /// <summary>
    /// Asynchronously disposes the shared resources, waiting for background tasks to complete.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // Already disposed
        }

        StartShutdown();
        await WaitForBackgroundTasksAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _shutdownCts.Dispose();
    }

    /// <summary>
    /// Synchronously disposes the shared resources.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // Already disposed
        }

        StartShutdown();
        _shutdownCts.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Waiting for {Count} background tasks to complete")]
    private partial void LogWaitingForBackgroundTasks(int Count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Timeout waiting for background tasks to complete")]
    private partial void LogBackgroundTaskTimeout();
}
