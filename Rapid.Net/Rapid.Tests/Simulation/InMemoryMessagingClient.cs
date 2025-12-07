using System.Collections.Concurrent;
using Rapid.Messaging;
using Rapid.Pb;

namespace Rapid.Tests.Simulation;

/// <summary>
/// In-memory messaging client for simulation testing.
/// Routes messages through the SimulationNetwork instead of gRPC.
/// </summary>
internal sealed class InMemoryMessagingClient : IMessagingClient
{
    private readonly SimulationEnvironment _environment;
    private readonly Endpoint _localEndpoint;
    private readonly ConcurrentDictionary<int, Task> _pendingTasks = new();
    private int _taskIdCounter;
    private bool _disposed;

    public InMemoryMessagingClient(SimulationEnvironment environment, Endpoint localEndpoint)
    {
        _environment = environment;
        _localEndpoint = localEndpoint;
    }

    public async Task<RapidResponse> SendMessageAsync(Endpoint remote, RapidRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var localAddr = RapidUtils.Loggable(_localEndpoint);
        var remoteAddr = RapidUtils.Loggable(remote);

        // Check if message can be delivered
        if (!_environment.Network.CanDeliver(localAddr, remoteAddr))
        {
            throw new InvalidOperationException($"Network partition: {localAddr} cannot reach {remoteAddr}");
        }

        // Simulate network delay
        var delay = _environment.Network.GetMessageDelay();
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _environment.TimeProvider, cancellationToken).ConfigureAwait(true);
        }

        // Find the target node and dispatch the message
        var targetNode = _environment.GetNode(remoteAddr);
        if (targetNode == null)
        {
            throw new InvalidOperationException($"Target node not found: {remoteAddr}");
        }

        return await targetNode.HandleRequestAsync(request, cancellationToken).ConfigureAwait(true);
    }

    public async Task<RapidResponse> SendMessageBestEffortAsync(Endpoint remote, RapidRequest request, CancellationToken cancellationToken)
    {
#pragma warning disable CA1031
        try
        {
            return await SendMessageAsync(remote, request, cancellationToken).ConfigureAwait(true);
        }
        catch
        {
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
