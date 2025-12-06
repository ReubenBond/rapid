/*
 * Copyright © 2016 - 2025 VMware, Inc. All Rights Reserved.
 */

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rapid.Messaging;
using Rapid.Monitoring;
using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Extension methods for adding Rapid services to dependency injection.
/// </summary>
public static class RapidServiceCollectionExtensions
{
    /// <summary>
    /// Adds Rapid cluster services to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configuration action for Rapid options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddRapid(
        this IServiceCollection services,
        Action<RapidOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // Configure options
        services.Configure(configure);

        // Add core services
        services.AddGrpc();
        services.AddSingleton<SharedResources>();
        services.AddSingleton<IMessagingClient, GrpcClient>();
        services.AddSingleton<IEdgeFailureDetectorFactory>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RapidOptions>>().Value;
            var client = sp.GetRequiredService<IMessagingClient>();
            var loggerFactory = sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
            return new PingPongFailureDetectorFactory(options.ListenAddress, client, loggerFactory);
        });

        // Register the membership service handler
        services.AddSingleton<IMembershipServiceHandler>(sp =>
        {
            var clusterService = sp.GetRequiredService<RapidClusterService>();
            return clusterService.MembershipService ?? throw new InvalidOperationException("Cluster service not initialized");
        });

        // Register the gRPC service implementation
        services.AddSingleton<MembershipServiceImpl>();

        // Register the cluster service as a hosted service
        services.AddSingleton<RapidClusterService>();
        services.AddHostedService(sp => sp.GetRequiredService<RapidClusterService>());

        // Register the cluster interface for application access
        services.AddSingleton<IRapidCluster, RapidCluster>();

        return services;
    }

    /// <summary>
    /// Adds Rapid gRPC services to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddRapidGrpc(this IServiceCollection services)
    {
        services.AddGrpc();
        return services;
    }
}
