/*
 * Copyright © 2016 - 2025 VMware, Inc. All Rights Reserved.
 */

using System.Collections.Concurrent;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Rapid.Pb;

namespace Rapid.Messaging;

/// <summary>
/// gRPC-based messaging client for Rapid.
/// </summary>
public sealed class GrpcClient(Settings settings, ILoggerFactory? loggerFactory = null) : IMessagingClient
{
    private readonly Settings _settings = settings;
    private readonly ILogger<GrpcClient> _logger = (loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)
            .CreateLogger<GrpcClient>();
    private readonly ConcurrentDictionary<string, Pb.MembershipService.MembershipServiceClient> _clients = new();
    private bool _disposed;

    public async Task<RapidResponse> SendMessageAsync(Endpoint remote, RapidRequest request,
        CancellationToken cancellationToken = default)
    {
        var client = GetOrCreateClient(remote);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_settings.GrpcTimeoutMs);

            var response = await client.sendRequestAsync(request, cancellationToken: cts.Token);
            return response;
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "RPC failed to {Remote}", Utils.Loggable(remote));
            throw;
        }
    }

    public async Task<RapidResponse> SendMessageBestEffortAsync(Endpoint remote, RapidRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await SendMessageAsync(remote, request, cancellationToken);
        }
        catch
        {
            return RapidResponse.Parser.ParseFrom(Array.Empty<byte>());
        }
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

    public void Shutdown()
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _clients.Clear();
    }
}
