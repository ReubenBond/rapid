using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Rapid.Messaging;
using Rapid.Pb;
using Rapid.Tests.Simulation.Logging;

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
    private readonly InMemoryMessagingClientLogger _log;
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
        _log = new InMemoryMessagingClientLogger(harness.LoggerFactory?.CreateLogger<InMemoryMessagingClient>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<InMemoryMessagingClient>.Instance);
    }

    public Task<RapidResponse> SendMessageAsync(Endpoint remote, RapidRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var localAddr = RapidUtils.Loggable(_localEndpoint);
        var remoteAddr = RapidUtils.Loggable(remote);

        _log.AttemptingSend(request.ContentCase, localAddr, remoteAddr);

        // Check delivery status (partitions, random drops)
        var deliveryStatus = _harness.Network.CheckDelivery(localAddr, remoteAddr);
        switch (deliveryStatus)
        {
            case DeliveryStatus.Partitioned:
                _log.MessageBlockedByPartition(request.ContentCase, localAddr, remoteAddr);
                return Task.FromException<RapidResponse>(
                    new SimulatedNetworkException($"Network partition: {localAddr} cannot reach {remoteAddr}"));

            case DeliveryStatus.Dropped:
                _log.MessageDroppedPacketLoss(request.ContentCase, localAddr, remoteAddr);
                return Task.FromException<RapidResponse>(
                    new SimulatedNetworkException($"Message from {localAddr} to {remoteAddr} was dropped (simulated packet loss)"));

            case DeliveryStatus.Success:
            default:
                break;
        }

        // Find the target node
        var targetNode = _harness.GetNode(remoteAddr);
        if (targetNode == null)
        {
            _log.TargetNodeNotFound(remoteAddr, localAddr);
            return Task.FromException<RapidResponse>(
                new SimulatedNetworkException($"Target node not found: {remoteAddr}"));
        }

        // Create a TCS for the response
        var responseTcs = new TaskCompletionSource<RapidResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Ensure any exception on the task is observed to prevent UnobservedTaskException
        // if the caller abandons the task or the simulation tears down.
        responseTcs.Task.Ignore();

        _log.SchedulingDelivery(request.ContentCase, localAddr, remoteAddr);

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
        // If the target node was crashed/disposed, this will fail - handle gracefully
        SimulationNodeContext targetContext;
        try
        {
            targetContext = _harness.GetNodeContext(targetNode);
        }
        catch (ArgumentException)
        {
            // Node was crashed/disposed between GetNode and GetNodeContext
            _log.TargetNodeCrashedBeforeDelivery(remoteAddr, localAddr);
            responseTcs.TrySetException(
                new SimulatedNetworkException($"Target node {remoteAddr} is no longer available"));
            return;
        }

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
                _log.DeliveringMessage(request.ContentCase, localAddr, remoteAddr);

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
                _log.MessageDeliveryFailed(request.ContentCase, localAddr, remoteAddr, ex.Message);
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
                    _log.MessageTimedOut(request.ContentCase, localAddr, remoteAddr, _messageTimeout);
                    responseTcs.TrySetException(new TimeoutException($"Message to {remoteAddr} timed out after {_messageTimeout}"));
                }
            }),
            _messageTimeout + delay);

        // Cancel timeout when response is received
        responseTcs.Task.ContinueWith(
            static (_, state) => ((IDisposable)state!).Dispose(),
            state: timeoutItem,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            sourceContext.TaskScheduler);

        // Schedule the delivery (with optional delay)
        if (delay > TimeSpan.Zero)
        {
            _log.SimulatingDelay(delay.TotalMilliseconds, localAddr, remoteAddr);
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
            _log.BestEffortTimedOut(RapidUtils.Loggable(remote), ex.Message);
            return RapidResponse.Parser.ParseFrom([]);
        }
        catch (Exception ex)
        {
            _log.BestEffortFailed(RapidUtils.Loggable(remote), ex.Message);
            return RapidResponse.Parser.ParseFrom([]);
        }
#pragma warning restore CA1031
    }

    public void SendOneWayMessage(Endpoint remote, RapidRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var taskId = Interlocked.Increment(ref _taskIdCounter);
        var task = SendOneWayMessageInternalAsync(remote, request, taskId, onDeliveryFailure: null, cancellationToken);
        _pendingTasks.TryAdd(taskId, task);
    }

    public void SendOneWayMessage(Endpoint remote, RapidRequest request, DeliveryFailureCallback? onDeliveryFailure, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var taskId = Interlocked.Increment(ref _taskIdCounter);
        var task = SendOneWayMessageInternalAsync(remote, request, taskId, onDeliveryFailure, cancellationToken);
        _pendingTasks.TryAdd(taskId, task);
    }

    private async Task SendOneWayMessageInternalAsync(Endpoint remote, RapidRequest request, int taskId, DeliveryFailureCallback? onDeliveryFailure, CancellationToken cancellationToken)
    {
#pragma warning disable CA1031
        try
        {
            await SendMessageAsync(remote, request, cancellationToken).ConfigureAwait(true);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("partition", StringComparison.OrdinalIgnoreCase))
        {
            onDeliveryFailure?.Invoke(remote);
        }
        catch (TimeoutException)
        {
            onDeliveryFailure?.Invoke(remote);
        }
        catch (OperationCanceledException)
        {
            // User cancellation - don't invoke callback
        }
        catch
        {
            onDeliveryFailure?.Invoke(remote);
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

    private sealed class SimulatedNetworkException : Exception
    {
        public SimulatedNetworkException(string message) : base(message)
        {
        }

        public SimulatedNetworkException(string message, Exception innerException) : base(message, innerException)
        {
        }

        public SimulatedNetworkException()
        {
        }
    }
}
