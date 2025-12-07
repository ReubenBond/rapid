using System.Collections.Concurrent;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rapid.Pb;

namespace Rapid.Messaging;

/// <summary>
/// gRPC-based messaging client for Rapid.
/// Implements IHostedService to ensure proper shutdown ordering.
/// </summary>
internal sealed partial class GrpcClient : IMessagingClient, IHostedService
{
    private readonly RapidProtocolOptions _options;
    private readonly ILogger<GrpcClient> _logger;
    private readonly ConcurrentDictionary<string, Pb.MembershipService.MembershipServiceClient> _clients = new();
    private readonly ConcurrentDictionary<int, Task> _pendingTasks = new();
    private int _taskIdCounter;
    private bool _disposed;

    public GrpcClient(IOptions<RapidProtocolOptions> options, ILoggerFactory? loggerFactory = null)
    {
        _options = options.Value;
        _logger = (loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)
            .CreateLogger<GrpcClient>();
    }

    private readonly struct LoggableEndpoint(Endpoint endpoint)
    {
        private readonly Endpoint _endpoint = endpoint;
        public override readonly string ToString() => RapidUtils.Loggable(_endpoint);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "RPC failed to {Remote}")]
    private partial void LogRpcFailed(Exception ex, LoggableEndpoint Remote);

    [LoggerMessage(Level = LogLevel.Debug, Message = "GrpcClient stopping, waiting for {Count} pending tasks")]
    private partial void LogStopping(int Count);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Wait for all pending tasks to complete (with a timeout)
        var pendingTasks = _pendingTasks.Values.ToArray();
        if (pendingTasks.Length > 0)
        {
            LogStopping(pendingTasks.Length);
            try
            {
                await Task.WhenAll(pendingTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Timeout waiting for pending tasks
            }
        }
    }

    public async Task<RapidResponse> SendMessageAsync(Endpoint remote, RapidRequest request,
        CancellationToken cancellationToken)
    {
        var client = GetOrCreateClient(remote);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_options.GrpcTimeout);

            var response = await client.sendRequestAsync(request, cancellationToken: cts.Token);
            return response;
        }
        catch (RpcException ex)
        {
            LogRpcFailed(ex, new LoggableEndpoint(remote));
            throw;
        }
    }

    public async Task<RapidResponse> SendMessageBestEffortAsync(Endpoint remote, RapidRequest request,
        CancellationToken cancellationToken)
    {
#pragma warning disable CA1031
        try
        {
            return await SendMessageAsync(remote, request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return RapidResponse.Parser.ParseFrom([]);
        }
#pragma warning restore CA1031
    }

    public void SendOneWayMessage(Endpoint remote, RapidRequest request, CancellationToken cancellationToken)
    {
        var taskId = Interlocked.Increment(ref _taskIdCounter);
        var task = SendOneWayMessageInternalAsync(remote, request, taskId, cancellationToken);
        _pendingTasks.TryAdd(taskId, task);
    }

    private async Task SendOneWayMessageInternalAsync(Endpoint remote, RapidRequest request, int taskId, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.GrpcTimeout);

        var client = GetOrCreateClient(remote);
#pragma warning disable CA1031 // Do not catch general exception types
        try
        {
            await client.sendRequestAsync(request, cancellationToken: cts.Token);
        }
        catch
        {
            // Ignore.
        }
        finally
        {
            _pendingTasks.TryRemove(taskId, out _);
        }
#pragma warning restore CA1031 // Do not catch general exception types
    }

    private Pb.MembershipService.MembershipServiceClient GetOrCreateClient(Endpoint remote)
    {
        var key = $"{remote.Hostname.ToStringUtf8()}:{remote.Port}";
        return _clients.GetOrAdd(key, _ =>
        {
            var channel = Grpc.Net.Client.GrpcChannel.ForAddress($"http://{key}");
            return new Pb.MembershipService.MembershipServiceClient(channel);
        });
    }

    public void Shutdown() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _clients.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Wait for all pending tasks to complete (with a timeout)
        var pendingTasks = _pendingTasks.Values.ToArray();
        if (pendingTasks.Length > 0)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await Task.WhenAll(pendingTasks).WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Timeout waiting for pending tasks
            }
        }

        _clients.Clear();
    }
}
