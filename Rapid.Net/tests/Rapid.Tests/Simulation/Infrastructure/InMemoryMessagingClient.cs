using Microsoft.Extensions.Logging;
using Rapid.Messaging;
using Rapid.Pb;
using Rapid.Tests.Simulation.Infrastructure.Logging;

namespace Rapid.Tests.Simulation.Infrastructure;

/// <summary>
/// In-memory messaging client for simulation testing.
/// Routes messages through the SimulationNetwork instead of gRPC.
/// 
/// Messages are delivered by scheduling work on the target node's SimulationTaskQueue.
/// This provides deterministic execution where messages are processed when the simulation steps.
/// </summary>
internal sealed class InMemoryMessagingClient : IMessagingClient
{
    private readonly SimulationHarness _harness;
    private readonly SimulationNode _sourceNode;
    private readonly Endpoint _localEndpoint;
    private readonly TimeSpan _messageTimeout;
    private readonly InMemoryMessagingClientLogger _log;
    private readonly CancellationTokenSource _disposeCts = new();
    private bool _disposed;

    public InMemoryMessagingClient(
        SimulationHarness harness,
        SimulationNode sourceNode,
        Endpoint localEndpoint,
        RapidProtocolOptions options)
    {
        _harness = harness;
        _sourceNode = sourceNode;
        _localEndpoint = localEndpoint;
        _messageTimeout = options.GrpcTimeout;
        _log = new InMemoryMessagingClientLogger(harness.LoggerFactory.CreateLogger<InMemoryMessagingClient>());
    }

    public void SendOneWayMessage(Endpoint remote, RapidRequest request, DeliveryFailureCallback? onDeliveryFailure, CancellationToken cancellationToken)
    {
        var task = SendOneWayCore(remote, request, cancellationToken);

        if (onDeliveryFailure == null)
        {
            task.Ignore();
            return;
        }

        task.ContinueWith(
            static (t, state) =>
            {
                var (callback, endpoint) = ((DeliveryFailureCallback, Endpoint))state!;
                if (t.IsFaulted)
                {
                    callback(endpoint);
                }
            },
            (onDeliveryFailure, remote),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            _harness.GetNodeContext(_sourceNode).TaskScheduler);
    }

    public Task<RapidResponse> SendMessageAsync(Endpoint remote, RapidRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var localAddr = RapidUtils.Loggable(_localEndpoint);
        var remoteAddr = RapidUtils.Loggable(remote);

        var (targetNode, targetContext, error) = ValidateAndGetTarget(remote, request.ContentCase, localAddr, remoteAddr);
        if (error != null)
        {
            return Task.FromException<RapidResponse>(error);
        }

        var sourceContext = _harness.GetNodeContext(_sourceNode);
        var delay = _harness.Network.GetMessageDelay();

        return ScheduleRequestAsync(
            targetNode: targetNode!,
            targetContext: targetContext!,
            request: request,
            contentCase: request.ContentCase,
            localAddr: localAddr,
            remoteAddr: remoteAddr,
            delay: delay,
            timeout: _messageTimeout,
            sourceContext: sourceContext,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Core one-way send: schedules delivery, returns task that completes when handler finishes.
    /// No timeout - fire-and-forget semantics.
    /// </summary>
    private Task SendOneWayCore(Endpoint remote, RapidRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var localAddr = RapidUtils.Loggable(_localEndpoint);
        var remoteAddr = RapidUtils.Loggable(remote);

        var (targetNode, targetContext, error) = ValidateAndGetTarget(remote, request.ContentCase, localAddr, remoteAddr);
        if (error != null)
        {
            return Task.FromException(error);
        }

        var delay = _harness.Network.GetMessageDelay();

        return ScheduleOneWayAsync(
            targetNode!,
            targetContext!,
            request,
            request.ContentCase,
            localAddr,
            remoteAddr,
            delay,
            cancellationToken);
    }

    private Task<RapidResponse> ScheduleRequestAsync(
        SimulationNode targetNode,
        SimulationNodeContext targetContext,
        RapidRequest request,
        RapidRequest.ContentOneofCase contentCase,
        string localAddr,
        string remoteAddr,
        TimeSpan delay,
        TimeSpan timeout,
        SimulationNodeContext sourceContext,
        CancellationToken cancellationToken)
    {
#pragma warning disable CA2000 // CTS is disposed via completion continuation
        var messageCts = CreateLinkedCts(cancellationToken);
#pragma warning restore CA2000
        var messageToken = messageCts.Token;

        var responseTcs = new TaskCompletionSource<RapidResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        var timeoutItem = sourceContext.TaskQueue.EnqueueAfter(
            new ScheduledActionItem(() =>
            {
                if (responseTcs.TrySetException(new TimeoutException($"Message to {remoteAddr} timed out after {timeout}")))
                {
                    _log.MessageTimedOut(contentCase, localAddr, remoteAddr, timeout);
                }
            }),
            timeout + delay);

        responseTcs.Task.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            messageCts,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            sourceContext.TaskScheduler).Ignore();

        ScheduleDeliveryAndComplete(
            targetNode: targetNode,
            targetContext: targetContext,
            request: request,
            contentCase: contentCase,
            localAddr: localAddr,
            remoteAddr: remoteAddr,
            delay: delay,
            responseTcs: responseTcs,
            skipIfCompleted: true,
            messageToken: messageToken);

        responseTcs.Task.ContinueWith(
            static (_, state) => ((ScheduledActionItem)state!).Dispose(),
            timeoutItem,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            sourceContext.TaskScheduler).Ignore();

        return responseTcs.Task;
    }

    private Task ScheduleOneWayAsync(
        SimulationNode targetNode,
        SimulationNodeContext targetContext,
        RapidRequest request,
        RapidRequest.ContentOneofCase contentCase,
        string localAddr,
        string remoteAddr,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
#pragma warning disable CA2000 // CTS is disposed via completion continuation
        var messageCts = CreateLinkedCts(cancellationToken);
#pragma warning restore CA2000
        var messageToken = messageCts.Token;

        var completionTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ScheduleDeliveryAndComplete(
            targetNode: targetNode,
            targetContext: targetContext,
            request: request,
            contentCase: contentCase,
            localAddr: localAddr,
            remoteAddr: remoteAddr,
            delay: delay,
            completionTcs: completionTcs,
            skipIfCompleted: false,
            messageToken: messageToken);

        completionTcs.Task.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            messageCts,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            targetContext.TaskScheduler).Ignore();

        return completionTcs.Task;
    }

    private void ScheduleDeliveryAndComplete(
        SimulationNode targetNode,
        SimulationNodeContext targetContext,
        RapidRequest request,
        RapidRequest.ContentOneofCase contentCase,
        string localAddr,
        string remoteAddr,
        TimeSpan delay,
        TaskCompletionSource<RapidResponse> responseTcs,
        bool skipIfCompleted,
        CancellationToken messageToken)
    {
        targetContext.TaskQueue.EnqueueAfter(() =>
        {
            if (skipIfCompleted && responseTcs.Task.IsCompleted)
            {
                return;
            }

            _log.DeliveringMessage(contentCase, localAddr, remoteAddr);
            var responseTask = targetNode.HandleRequestAsync(request, messageToken);

            if (responseTask.IsCompleted)
            {
                CompleteResponseTcs(responseTcs, responseTask);
                return;
            }

            responseTask.ContinueWith(
                static (task, state) =>
                {
                    var tcs = (TaskCompletionSource<RapidResponse>)state!;
                    CompleteResponseTcs(tcs, task);
                },
                responseTcs,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                targetContext.TaskScheduler);
        }, delay);
    }

    private void ScheduleDeliveryAndComplete(
        SimulationNode targetNode,
        SimulationNodeContext targetContext,
        RapidRequest request,
        RapidRequest.ContentOneofCase contentCase,
        string localAddr,
        string remoteAddr,
        TimeSpan delay,
        TaskCompletionSource completionTcs,
        bool skipIfCompleted,
        CancellationToken messageToken)
    {
        targetContext.TaskQueue.EnqueueAfter(() =>
        {
            if (skipIfCompleted && completionTcs.Task.IsCompleted)
            {
                return;
            }

            if (messageToken.IsCancellationRequested)
            {
                completionTcs.TrySetCanceled(messageToken);
                return;
            }

            _log.DeliveringMessage(contentCase, localAddr, remoteAddr);
            var responseTask = targetNode.HandleRequestAsync(request, messageToken);

            if (responseTask.IsCompleted)
            {
                CompleteVoidTcs(completionTcs, responseTask);
                return;
            }

            responseTask.ContinueWith(
                static (task, state) =>
                {
                    var tcs = (TaskCompletionSource)state!;
                    CompleteVoidTcs(tcs, task);
                },
                completionTcs,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                targetContext.TaskScheduler);
        }, delay);
    }

    private static void CompleteResponseTcs(TaskCompletionSource<RapidResponse> tcs, Task<RapidResponse> task)
    {
        if (task.IsFaulted)
        {
            tcs.TrySetException(task.Exception!.InnerExceptions);
        }
        else if (task.IsCanceled)
        {
            tcs.TrySetCanceled();
        }
        else
        {
            tcs.TrySetResult(task.Result);
        }
    }

    private static void CompleteVoidTcs(TaskCompletionSource tcs, Task task)
    {
        if (task.IsFaulted)
        {
            tcs.TrySetException(task.Exception!.InnerExceptions);
        }
        else if (task.IsCanceled)
        {
            tcs.TrySetCanceled();
        }
        else
        {
            tcs.TrySetResult();
        }
    }

    /// <summary>
    /// Validates delivery conditions and returns target node/context, or an error.
    /// </summary>
    private (SimulationNode? node, SimulationNodeContext? context, Exception? error) ValidateAndGetTarget(
        Endpoint remote,
        RapidRequest.ContentOneofCase contentCase,
        string localAddr,
        string remoteAddr)
    {
        _log.AttemptingSend(contentCase, localAddr, remoteAddr);

        // Check for network issues
        var deliveryStatus = _harness.Network.CheckDelivery(localAddr, remoteAddr);
        if (deliveryStatus == DeliveryStatus.Partitioned)
        {
            _log.MessageBlockedByPartition(contentCase, localAddr, remoteAddr);
            return (null, null, new SimulatedNetworkException($"Network partition: {localAddr} cannot reach {remoteAddr}"));
        }
        if (deliveryStatus == DeliveryStatus.Dropped)
        {
            _log.MessageDroppedPacketLoss(contentCase, localAddr, remoteAddr);
            return (null, null, new SimulatedNetworkException($"Message from {localAddr} to {remoteAddr} was dropped (simulated packet loss)"));
        }

        // Find target node
        var targetNode = _harness.GetNode(remoteAddr);
        if (targetNode == null)
        {
            _log.TargetNodeNotFound(remoteAddr, localAddr);
            return (null, null, new SimulatedNetworkException($"Target node not found: {remoteAddr}"));
        }

        // Get target context
        SimulationNodeContext targetContext;
        try
        {
            targetContext = _harness.GetNodeContext(targetNode);
        }
        catch (ArgumentException)
        {
            _log.TargetNodeCrashedBeforeDelivery(remoteAddr, localAddr);
            return (null, null, new SimulatedNetworkException($"Target node {remoteAddr} is no longer available"));
        }

        _log.SchedulingDelivery(contentCase, localAddr, remoteAddr);
        return (targetNode, targetContext, null);
    }

    private CancellationTokenSource CreateLinkedCts(CancellationToken cancellationToken)
    {
        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
    }


    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

#pragma warning disable CA1849 // CancelAsync posts to SynchronizationContext - use Cancel() for determinism
        _disposeCts.Cancel();
#pragma warning restore CA1849
        _disposeCts.Dispose();

        return ValueTask.CompletedTask;
    }

    private sealed class SimulatedNetworkException : Exception
    {
        public SimulatedNetworkException() { }
        public SimulatedNetworkException(string message) : base(message) { }
        public SimulatedNetworkException(string message, Exception innerException) : base(message, innerException) { }
    }
}
