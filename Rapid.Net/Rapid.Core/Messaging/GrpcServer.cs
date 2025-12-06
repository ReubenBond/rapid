/*
 * Copyright © 2016 - 2025 VMware, Inc. All Rights Reserved.
 */

using Grpc.Core;
using Microsoft.Extensions.Logging;
using Rapid.Messaging;
using Rapid.Pb;

namespace Rapid.Messaging;

/// <summary>
/// gRPC-based messaging server for Rapid.
/// </summary>
public sealed class GrpcServer : IMessagingServer
{
    private readonly Endpoint _listenAddress;
    private readonly ILogger<GrpcServer> _logger;
    private IMembershipServiceHandler? _membershipService;
    private Server? _server;

    public GrpcServer(Endpoint listenAddress, SharedResources sharedResources, Settings settings, 
                     ILoggerFactory? loggerFactory = null)
    {
        _listenAddress = listenAddress;
        _logger = (loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)
            .CreateLogger<GrpcServer>();
    }

    public void SetMembershipService(IMembershipServiceHandler service)
    {
        _membershipService = service;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        var hostname = _listenAddress.Hostname.ToStringUtf8();
        var port = _listenAddress.Port;

        _server = new Server
        {
            Services = { Pb.MembershipService.BindService(new MembershipServiceImpl(_membershipService!)) },
            Ports = { new ServerPort(hostname, port, ServerCredentials.Insecure) }
        };

        _server.Start();
        _logger.LogInformation("gRPC server started on {Hostname}:{Port}", hostname, port);
        
        return Task.CompletedTask;
    }

    public void Shutdown()
    {
        _server?.ShutdownAsync().Wait();
    }

    public void Dispose()
    {
        Shutdown();
    }

    private class MembershipServiceImpl : Pb.MembershipService.MembershipServiceBase
    {
        private readonly IMembershipServiceHandler _handler;

        public MembershipServiceImpl(IMembershipServiceHandler handler)
        {
            _handler = handler;
        }

        public override async Task<RapidResponse> sendRequest(RapidRequest request, ServerCallContext context)
        {
            return await _handler.HandleMessageAsync(request);
        }
    }
}
