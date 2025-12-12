namespace Rapid;

/// <summary>
/// Holds all resources that are shared across a single instance of Rapid.
/// </summary>
public sealed class SharedResources(
    TimeProvider? timeProvider = null,
    TaskScheduler? taskScheduler = null,
    Random? random = null,
    Func<Guid>? guidFactory = null) : IDisposable
{
    private readonly CancellationTokenSource _shutdownCts = new();
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
    /// Disposes the shared resources.
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
}
