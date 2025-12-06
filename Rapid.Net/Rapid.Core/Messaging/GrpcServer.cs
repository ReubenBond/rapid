/*
 * Copyright © 2016 - 2025 VMware, Inc. All Rights Reserved.
 */

using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rapid.Pb;

namespace Rapid.Messaging;

/// <summary>
/// gRPC-based messaging server for Rapid using ASP.NET Core hosting.
/// </summary>
public sealed class GrpcServer : IMessagingServer
{
    private readonly Endpoint _listenAddress;
    private readonly ILogger<GrpcServer> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly MembershipServiceImpl _serviceImpl;
    private WebApplication? _app;
    
    private static readonly RapidResponse BootstrappingMessage = new()
    {
        ProbeResponse = new ProbeResponse { Status = NodeStatus.Bootstrapping }
    };

    public GrpcServer(Endpoint listenAddress, SharedResources sharedResources, Settings settings, 
                     ILoggerFactory? loggerFactory = null)
    {
        _listenAddress = listenAddress;
        _loggerFactory = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<GrpcServer>();
        _serviceImpl = new MembershipServiceImpl(this);
    }

    public void SetMembershipService(IMembershipServiceHandler service)
    {
        _serviceImpl.SetHandler(service);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var hostname = _listenAddress.Hostname.ToStringUtf8();
        var port = _listenAddress.Port;

        var builder = WebApplication.CreateBuilder();
        
        // Configure Kestrel to listen on the specified address and port
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(port, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http2;
            });
        });

        // Configure logging
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_loggerFactory);

        // Add gRPC services
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(_serviceImpl);

        _app = builder.Build();

        // Map gRPC service
        _app.MapGrpcService<MembershipServiceImpl>();

        await _app.StartAsync(cancellationToken);
        _logger.LogInformation("gRPC server started on {Hostname}:{Port}", hostname, port);
    }

    public void Shutdown()
    {
        _app?.StopAsync().Wait();
        _app?.DisposeAsync().AsTask().Wait();
    }

    public void Dispose()
    {
        Shutdown();
    }

    private class MembershipServiceImpl(GrpcServer server) : Pb.MembershipService.MembershipServiceBase
    {
        private readonly GrpcServer _server = server;
        private IMembershipServiceHandler? _handler;

        public void SetHandler(IMembershipServiceHandler handler)
        {
            _handler = handler;
        }

        public override async Task<RapidResponse> sendRequest(RapidRequest request, ServerCallContext context)
        {
            if (_handler != null)
            {
                return await _handler.HandleMessageAsync(request);
            }
            else if (request.ContentCase == RapidRequest.ContentOneofCase.ProbeMessage)
            {
                // Special case: Node is bootstrapping. Respond to probe messages
                // to indicate the node is coming up but not yet ready.
                return BootstrappingMessage;
            }
            else
            {
                // No handler yet, return empty response
                return new RapidResponse();
            }
        }
    }
}
