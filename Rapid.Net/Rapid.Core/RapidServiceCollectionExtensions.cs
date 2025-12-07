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
    /// <param name="configureProtocol">Optional configuration action for protocol options.</param>
    /// <param name="timeProvider">Optional TimeProvider for testing and time control.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddRapid(
        this IServiceCollection services,
        Action<RapidOptions> configure,
        Action<RapidProtocolOptions>? configureProtocol = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // Configure options
        services.Configure(configure);

        // Configure protocol options
        if (configureProtocol != null)
        {
            services.Configure(configureProtocol);
        }
        else
        {
            services.Configure<RapidProtocolOptions>(_ => { });
        }

        // Add validation
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<RapidProtocolOptions>, RapidProtocolOptionsValidator>();

        // Register TimeProvider
        var provider = timeProvider ?? TimeProvider.System;
        services.AddSingleton(provider);

        // Add core services
        services.AddGrpc();
        services.AddSingleton<SharedResources>(sp =>
            new SharedResources(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),
                sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IMessagingClient, GrpcClient>();
        services.AddSingleton<IEdgeFailureDetectorFactory>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RapidOptions>>().Value;
            var client = sp.GetRequiredService<IMessagingClient>();
            var sharedResources = sp.GetRequiredService<SharedResources>();
            var loggerFactory = sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
            return new PingPongFailureDetectorFactory(options.ListenAddress, client, sharedResources, loggerFactory);
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
