using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Rapid.Messaging;
using Rapid.Pb;

namespace Rapid.Tests.Simulation;

/// <summary>
/// In-memory messaging client for simulation testing.
/// Routes messages through the SimulationNetwork instead of gRPC.
/// 
/// IMPORTANT: This client dispatches message handlers by enqueuing work on the target node's
/// SimulationTaskQueue. This provides:
/// 1. Per-node scheduling - messages are processed in the target node's execution context
/// 2. Deterministic execution - work is queued and executed during simulation stepping
/// 3. Suspension support - messages to suspended nodes queue up until resumed
/// </summary>
internal sealed class InMemoryMessagingClient : IMessagingClient
{
    private readonly SimulationHarness _harness;
    private readonly SimulationNode _sourceNode;
    private readonly Endpoint _localEndpoint;
    private readonly TimeSpan _messageTimeout;
    private readonly ILogger<InMemoryMessagingClient> _logger;
    private readonly ConcurrentDictionary<int, Task> _pendingTasks = new();
    private int _taskIdCounter;
    private bool _disposed;

    /// <summary>
    /// Creates an in-memory messaging client for simulation testing.
    /// </summary>
    /// <param name="harness">The simulation harness.</param>
    /// <param name="sourceNode">The source node for this client.</param>
    /// <param name="localEndpoint">The local endpoint address.</param>
    /// <param name="options">Protocol options containing GrpcTimeout for message delivery timeout.</param>
    public InMemoryMessagingClient(SimulationHarness harness, SimulationNode sourceNode, Endpoint localEndpoint, RapidProtocolOptions options)
    {
        _harness = harness;
        _sourceNode = sourceNode;
        _localEndpoint = localEndpoint;
        _messageTimeout = options.GrpcTimeout;
        _logger = harness.LoggerFactory?.CreateLogger<InMemoryMessagingClient>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<InMemoryMessagingClient>.Instance;
    }

    public Task<RapidResponse> SendMessageAsync(Endpoint remote, RapidRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var localAddr = RapidUtils.Loggable(_localEndpoint);
        var remoteAddr = RapidUtils.Loggable(remote);

        _logger.LogTrace("Attempting to send {MessageType} from {Local} to {Remote}",
            request.ContentCase, localAddr, remoteAddr);

        // Check delivery status (partitions, random drops)
        var deliveryStatus = _harness.Network.CheckDelivery(localAddr, remoteAddr);
        switch (deliveryStatus)
        {
            case DeliveryStatus.Partitioned:
                _logger.LogWarning("Message {MessageType} from {Local} to {Remote} blocked by network partition",
                    request.ContentCase, localAddr, remoteAddr);
                return Task.FromException<RapidResponse>(
                    new InvalidOperationException($"Network partition: {localAddr} cannot reach {remoteAddr}"));

            case DeliveryStatus.Dropped:
                _logger.LogDebug("Message {MessageType} from {Local} to {Remote} dropped (simulated packet loss)",
                    request.ContentCase, localAddr, remoteAddr);
                return Task.FromException<RapidResponse>(
                    new TimeoutException($"Message from {localAddr} to {remoteAddr} was dropped (simulated packet loss)"));

            case DeliveryStatus.Success:
            default:
                break;
        }

        // Find the target node
        var targetNode = _harness.GetNode(remoteAddr);
        if (targetNode == null)
        {
            _logger.LogError("Target node {Remote} not found when sending from {Local}",
                remoteAddr, localAddr);
            return Task.FromException<RapidResponse>(
                new InvalidOperationException($"Target node not found: {remoteAddr}"));
        }

        // Create a TCS for the response
        var responseTcs = new TaskCompletionSource<RapidResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        _logger.LogTrace("Scheduling delivery of {MessageType} from {Local} to {Remote}",
            request.ContentCase, localAddr, remoteAddr);

        // Schedule message delivery on the target node's task queue
        ScheduleMessageDelivery(targetNode, request, responseTcs, cancellationToken, localAddr, remoteAddr);

        return responseTcs.Task;
    }

    /// <summary>
    /// Schedules message delivery on the target node's task queue.
    /// The message handler runs when the target node is stepped during simulation.
    /// </summary>
#pragma warning disable CA1031 // Catch general exception - required for TCS completion
#pragma warning disable CA1068 // CancellationToken not last - grouping with TCS for clarity
    private void ScheduleMessageDelivery(
        SimulationNode targetNode,
        RapidRequest request,
        TaskCompletionSource<RapidResponse> responseTcs,
        CancellationToken cancellationToken,
        string localAddr,
        string remoteAddr)
#pragma warning restore CA1068
    {
        // Get the target node's context for message delivery
        var targetContext = _harness.GetNodeContext(targetNode);
        var targetQueue = targetContext.TaskQueue;

        // Get the source node's context for timeout scheduling
        // This ensures timeouts fire even if the target node is suspended
        var sourceContext = _harness.GetNodeContext(_sourceNode);
        var sourceQueue = sourceContext.TaskQueue;

        // Apply network delay if configured
        var delay = _harness.Network.GetMessageDelay();

        // Create the delivery action
        void DeliverMessage()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                responseTcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                _logger.LogTrace("Delivering {MessageType} from {Local} to {Remote}",
                    request.ContentCase, localAddr, remoteAddr);

                // Handle the request synchronously within the simulation context
                // The task returned by HandleRequestAsync will be driven by the simulation
                var responseTask = targetNode.HandleRequestAsync(request, cancellationToken);

                // If already completed, set result immediately.
                if (responseTask.IsCompleted)
                {
                    responseTcs.TrySetFromTask(responseTask);
                }
                else
                {
                    responseTask.ContinueWith(
                        (task, state) => responseTcs.TrySetFromTask(task),
                        state: null,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        targetContext.TaskScheduler);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Message {MessageType} from {Local} to {Remote} failed: {Error}",
                    request.ContentCase, localAddr, remoteAddr, ex.Message);
                responseTcs.TrySetException(ex);
            }
        }

        // Schedule timeout on the SOURCE node's queue, not the target's.
        // This ensures timeouts fire even if the target node is suspended,
        // which is critical for proper simulation of node failures and suspensions.
        var timeoutItem = sourceQueue.EnqueueAfter(
            new ScheduledActionItem(() =>
            {
                if (!responseTcs.Task.IsCompleted)
                {
                    _logger.LogWarning("Message {MessageType} from {Local} to {Remote} timed out after {Timeout}",
                        request.ContentCase, localAddr, remoteAddr, _messageTimeout);
                    responseTcs.TrySetException(new TimeoutException($"Message to {remoteAddr} timed out after {_messageTimeout}"));
                }
            }),
            _messageTimeout + delay);

        // Cancel timeout when response is received
        responseTcs.Task.ContinueWith(
            (t, _) => timeoutItem.Dispose(),
            state: null,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            sourceContext.TaskScheduler);

        // Schedule the delivery (with optional delay)
        if (delay > TimeSpan.Zero)
        {
            _logger.LogTrace("Simulating {Delay}ms delay for message from {Local} to {Remote}",
                delay.TotalMilliseconds, localAddr, remoteAddr);
            targetQueue.EnqueueAfter(DeliverMessage, delay);
        }
        else
        {
            targetQueue.Enqueue(new ScheduledActionItem(DeliverMessage));
        }
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
