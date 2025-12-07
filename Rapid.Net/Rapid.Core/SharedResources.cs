using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rapid;

/// <summary>
/// Holds all resources that are shared across a single instance of Rapid.
/// </summary>
public sealed partial class SharedResources : IDisposable
{
    private readonly ILogger<SharedResources> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Channel<Func<Task>> _protocolExecutor;
    private readonly List<Task> _backgroundTasks = [];
    private readonly Lock _backgroundTasksLock = new();

    /// <summary>
    /// Gets the TimeProvider used for all time-related operations.
    /// </summary>
    public TimeProvider TimeProvider { get; }

    private Channel<Func<Task>> ProtocolExecutor => _protocolExecutor;

    [LoggerMessage(Level = LogLevel.Error, Message = "Error executing protocol message")]
    private partial void LogProtocolMessageError(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error executing protocol task")]
    private partial void LogProtocolTaskError(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to queue protocol action - channel may be closed")]
    private partial void LogFailedToQueueAction();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Waiting for {Count} background tasks to complete")]
    private partial void LogWaitingForBackgroundTasks(int Count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Timeout waiting for background tasks to complete")]
    private partial void LogBackgroundTaskTimeout();

    public SharedResources(ILoggerFactory? loggerFactory = null, TimeProvider? timeProvider = null)
    {
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SharedResources>();
        TimeProvider = timeProvider ?? TimeProvider.System;

        // Create protocol executor channel for async tasks
        _protocolExecutor = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        // Start the protocol executor and track it
        var protocolTask = Task.Run(ProcessProtocolMessagesAsync);
        TrackBackgroundTask(protocolTask);
    }

    private async Task ProcessProtocolMessagesAsync()
    {
        try
        {
            await foreach (var taskFunc in _protocolExecutor.Reader.ReadAllAsync(_shutdownCts.Token).ConfigureAwait(false))
            {
#pragma warning disable CA1031 // Do not catch general exception types
                try
                {
                    await taskFunc().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogProtocolTaskError(ex);
                }
#pragma warning restore CA1031 // Do not catch general exception types
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
    }

    public async Task ScheduleAsyncCallback(Func<Task> asyncFunc) => await ProtocolExecutor.Writer.WriteAsync(asyncFunc).ConfigureAwait(false);

    public void ScheduleCallback(Func<Task> asyncFunc) => ProtocolExecutor.Writer.TryWrite(asyncFunc);

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
            await Task.WhenAll(tasks).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
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
        _protocolExecutor.Writer.Complete();
        _shutdownCts.Dispose();
    }
}
