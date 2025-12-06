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
internal sealed partial class GrpcServer : IMessagingServer
{
    private readonly Endpoint _listenAddress;
    private readonly ILogger<GrpcServer> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly MembershipServiceImpl _serviceImpl;
    private WebApplication? _app;

    [LoggerMessage(Level = LogLevel.Information, Message = "gRPC server started on {Hostname}:{Port}")]
    private partial void LogServerStarted(string Hostname, int Port);

    private static readonly RapidResponse BootstrappingMessage = new()
    {
        ProbeResponse = new ProbeResponse { Status = NodeStatus.Bootstrapping }
    };

    public GrpcServer(Endpoint listenAddress, SharedResources sharedResources, MembershipService membershipService, Settings settings,
                     ILoggerFactory? loggerFactory = null)
    {
        _listenAddress = listenAddress;
        _loggerFactory = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<GrpcServer>();
        _serviceImpl = new MembershipServiceImpl(membershipService);
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
        LogServerStarted(hostname, port);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app != null)
        {
            await _app.StopAsync(cancellationToken);
            await _app.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private sealed class MembershipServiceImpl : Pb.MembershipService.MembershipServiceBase
    {
        private IMembershipServiceHandler _handler;

        public MembershipServiceImpl(IMembershipServiceHandler handler)
        {
            _handler = handler;
        }

        public void SetHandler(IMembershipServiceHandler handler)
        {
            _handler = handler;
        }

        public override async Task<RapidResponse> sendRequest(RapidRequest request, ServerCallContext context)
        {
            return await _handler.HandleMessageAsync(request, context.CancellationToken);
        }
    }
}
