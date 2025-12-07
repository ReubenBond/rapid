using System.Collections.Concurrent;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rapid.Pb;

namespace Rapid.Messaging;

/// <summary>
/// gRPC-based messaging client for Rapid.
/// </summary>
internal sealed partial class GrpcClient : IMessagingClient
{
    private readonly RapidProtocolOptions _options;
    private readonly ILogger<GrpcClient> _logger;
    private readonly ConcurrentDictionary<string, Pb.MembershipService.MembershipServiceClient> _clients = new();
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
        var client = GetOrCreateClient(remote);
        client.sendRequestAsync(request, cancellationToken: cancellationToken).Ignore();
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
}
