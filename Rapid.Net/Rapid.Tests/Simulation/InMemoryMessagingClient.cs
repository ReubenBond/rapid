using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Rapid.Messaging;
using Rapid.Pb;

namespace Rapid.Tests.Simulation;

/// <summary>
/// In-memory messaging client for simulation testing.
/// Routes messages through the SimulationNetwork instead of gRPC.
/// 
/// IMPORTANT: This client dispatches message handlers asynchronously using the simulation's
/// TaskScheduler to break synchronous call chains and prevent deadlocks. The pattern is:
/// 1. Caller invokes SendMessageAsync
/// 2. Client validates the message can be delivered (network partition check)
/// 3. Client schedules the handler to run on the simulation TaskScheduler
/// 4. Caller awaits the response via TaskCompletionSource
/// 5. Handler runs independently and completes the TCS when done
/// </summary>
internal sealed class InMemoryMessagingClient : IMessagingClient
{
    private readonly SimulationEnvironment _environment;
    private readonly Endpoint _localEndpoint;
    private readonly ILogger<InMemoryMessagingClient> _logger;
    private readonly ConcurrentDictionary<int, Task> _pendingTasks = new();
    private int _taskIdCounter;
    private bool _disposed;

    /// <summary>
    /// Default timeout for message delivery. This prevents indefinite hangs when
    /// consensus cannot be reached (e.g., due to network partitions or node failures).
    /// </summary>
    public TimeSpan MessageTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public InMemoryMessagingClient(SimulationEnvironment environment, Endpoint localEndpoint)
    {
        _environment = environment;
        _localEndpoint = localEndpoint;
        _logger = environment.LoggerFactory?.CreateLogger<InMemoryMessagingClient>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<InMemoryMessagingClient>.Instance;
    }

    public Task<RapidResponse> SendMessageAsync(Endpoint remote, RapidRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var localAddr = RapidUtils.Loggable(_localEndpoint);
        var remoteAddr = RapidUtils.Loggable(remote);

        _logger.LogTrace("Attempting to send {MessageType} from {Local} to {Remote}", 
            request.ContentCase, localAddr, remoteAddr);

        // Check if message can be delivered
        if (!_environment.Network.CanDeliver(localAddr, remoteAddr))
        {
            _logger.LogWarning("Message {MessageType} from {Local} to {Remote} blocked by network partition",
                request.ContentCase, localAddr, remoteAddr);
            return Task.FromException<RapidResponse>(
                new InvalidOperationException($"Network partition: {localAddr} cannot reach {remoteAddr}"));
        }

        // Find the target node
        var targetNode = _environment.GetNode(remoteAddr);
        if (targetNode == null)
        {
            _logger.LogError("Target node {Remote} not found when sending from {Local}",
                remoteAddr, localAddr);
            return Task.FromException<RapidResponse>(
                new InvalidOperationException($"Target node not found: {remoteAddr}"));
        }

        // Create a TCS for the response - this breaks the synchronous call chain
        var responseTcs = new TaskCompletionSource<RapidResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        _logger.LogTrace("Scheduling delivery of {MessageType} from {Local} to {Remote}",
            request.ContentCase, localAddr, remoteAddr);

        // Schedule the handler to run on the simulation's TaskScheduler
        // This breaks the synchronous call chain and allows other async operations to proceed
        // Note: timeoutCts ownership is transferred to ScheduleMessageDelivery which will dispose it
        ScheduleMessageDelivery(targetNode, request, responseTcs, cancellationToken, localAddr, remoteAddr);

        return responseTcs.Task;
    }

    /// <summary>
    /// Schedules message delivery on the simulation's TaskScheduler using Task.Factory.StartNew.
    /// This method is void-returning to ensure the caller doesn't block waiting for it.
    /// The async lambda is scheduled on the provided TaskScheduler, breaking the synchronous call chain.
    /// </summary>
#pragma warning disable CA1031 // Catch general exception - required for TCS completion in message delivery
#pragma warning disable CA1068 // CancellationToken not last - intentional grouping with TCS for clarity
    private void ScheduleMessageDelivery(
        SimulationNode targetNode,
        RapidRequest request,
        TaskCompletionSource<RapidResponse> responseTcs,
        CancellationToken cancellationToken,
        string localAddr,
        string remoteAddr)
#pragma warning restore CA1068
    {
        // Get the task scheduler to use
        var scheduler = _environment.TaskScheduler ?? TaskScheduler.Default;

        // Apply network delay if configured
        var delay = _environment.Network.GetMessageDelay();

        // Schedule the delivery using Task.Factory.StartNew with the simulation's TaskScheduler
        // The .Unwrap() is needed because StartNew returns Task<Task> for async delegates
        _ = Task.Factory.StartNew(
            async () =>
            {
                // Create a CTS that we can cancel from multiple sources
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                // Start a timeout task using the environment's TimeProvider
                // This ensures timeouts work correctly with FakeTimeProvider in tests
                var timeoutTask = Task.Delay(MessageTimeout, _environment.TimeProvider, CancellationToken.None)
                    .ContinueWith(_ =>
                    {
                        if (!responseTcs.Task.IsCompleted && !cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogWarning("Message {MessageType} from {Local} to {Remote} timed out after {Timeout}",
                                request.ContentCase, localAddr, remoteAddr, MessageTimeout);
                            responseTcs.TrySetException(new TimeoutException($"Message to {remoteAddr} timed out after {MessageTimeout}"));
                            timeoutCts.Cancel();
                        }
                    }, TaskScheduler.Default);

                try
                {
                    // Apply network delay
                    if (delay > TimeSpan.Zero)
                    {
                        _logger.LogTrace("Simulating {Delay}ms delay for message from {Local} to {Remote}",
                            delay.TotalMilliseconds, localAddr, remoteAddr);
                        await Task.Delay(delay, _environment.TimeProvider, timeoutCts.Token).ConfigureAwait(true);
                    }

                    _logger.LogTrace("Delivering {MessageType} from {Local} to {Remote}",
                        request.ContentCase, localAddr, remoteAddr);

                    var response = await targetNode.HandleRequestAsync(request, timeoutCts.Token).ConfigureAwait(true);

                    _logger.LogTrace("Received response for {MessageType} from {Remote} to {Local}",
                        request.ContentCase, remoteAddr, localAddr);

                    responseTcs.TrySetResult(response);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !responseTcs.Task.IsCompleted)
                {
                    // If the original token was cancelled, propagate that
                    if (cancellationToken.IsCancellationRequested)
                    {
                        responseTcs.TrySetCanceled(cancellationToken);
                    }
                    // Otherwise timeout was already handled by the timeout task
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Message {MessageType} from {Local} to {Remote} failed: {Error}",
                        request.ContentCase, localAddr, remoteAddr, ex.Message);
                    responseTcs.TrySetException(ex);
                }
            },
            cancellationToken,
            TaskCreationOptions.None,
            scheduler).Unwrap();
    }
#pragma warning restore CA1031

    public async Task<RapidResponse> SendMessageBestEffortAsync(Endpoint remote, RapidRequest request, CancellationToken cancellationToken)
    {
#pragma warning disable CA1031
        try
        {
            return await SendMessageAsync(remote, request, cancellationToken).ConfigureAwait(true);
        }
        catch (TimeoutException ex)
        {
            _logger.LogDebug("Best-effort message to {Remote} timed out: {Message}", 
                RapidUtils.Loggable(remote), ex.Message);
            return RapidResponse.Parser.ParseFrom([]);
        }
        catch (Exception ex)
        {
            _logger.LogTrace("Best-effort message to {Remote} failed: {Message}", 
                RapidUtils.Loggable(remote), ex.Message);
            return RapidResponse.Parser.ParseFrom([]);
        }
#pragma warning restore CA1031
    }

    public void SendOneWayMessage(Endpoint remote, RapidRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var taskId = Interlocked.Increment(ref _taskIdCounter);
        var task = SendOneWayMessageInternalAsync(remote, request, taskId, cancellationToken);
        _pendingTasks.TryAdd(taskId, task);
    }

    private async Task SendOneWayMessageInternalAsync(Endpoint remote, RapidRequest request, int taskId, CancellationToken cancellationToken)
    {
#pragma warning disable CA1031
        try
        {
            await SendMessageAsync(remote, request, cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            // Ignore failures for one-way messages
        }
        finally
        {
            _pendingTasks.TryRemove(taskId, out _);
        }
#pragma warning restore CA1031
    }

    public void Shutdown() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Wait for pending tasks
        var pending = _pendingTasks.Values.ToArray();
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                // Ignore timeout
            }
        }
    }
}
