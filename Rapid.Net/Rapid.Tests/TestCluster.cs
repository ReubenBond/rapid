using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Rapid.Tests;

/// <summary>
/// Represents a test cluster that tracks and manages nodes for integration tests.
/// Automatically shuts down all nodes when disposed.
/// </summary>
internal sealed class TestCluster : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = [];
    private readonly ILoggerFactory _loggerFactory;
    private readonly TestClusterPortAllocator _portAllocator = new();

    public TestCluster(ITestOutputHelper outputHelper)
    {
        _loggerFactory = LoggerFactory.Create(builder => builder
            .AddXUnit(outputHelper)
            .AddFilter("Microsoft.AspNetCore", LogLevel.Warning)
            .AddFilter("Grpc.AspNetCore", LogLevel.Warning)
            .SetMinimumLevel(LogLevel.Debug));
    }

    /// <summary>
    /// Gets the next available port for a node.
    /// </summary>
    public int GetNextPort() => _portAllocator.AllocatePort();

    /// <summary>
    /// Creates a seed node at the specified address.
    /// </summary>
    public async Task<(WebApplication App, IRapidCluster Cluster)> CreateSeedNodeAsync(Pb.Endpoint address, CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_loggerFactory);
        builder.ConfigureRapidKestrel(address.Port);

        builder.Services.AddRapid(options =>
        {
            options.ListenAddress = address;
            options.SeedAddress = address; // Same as listen = seed node
        });

        var app = builder.Build();
        app.MapRapidMembershipService();

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _apps.Add(app);

        var cluster = app.Services.GetRequiredService<IRapidCluster>();

        // Wait for the cluster to be initialized
        await WaitForClusterInitializedAsync(cluster, cancellationToken).ConfigureAwait(false);

        return (app, cluster);
    }

    /// <summary>
    /// Creates a seed node with custom configuration.
    /// </summary>
    public async Task<(WebApplication App, IRapidCluster Cluster)> CreateSeedNodeAsync(
        Pb.Endpoint address,
        Action<RapidOptions> configureOptions,
        CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_loggerFactory);
        builder.ConfigureRapidKestrel(address.Port);

        builder.Services.AddRapid(options =>
        {
            options.ListenAddress = address;
            options.SeedAddress = address;
            configureOptions(options);
        });

        var app = builder.Build();
        app.MapRapidMembershipService();

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _apps.Add(app);

        var cluster = app.Services.GetRequiredService<IRapidCluster>();

        // Wait for the cluster to be initialized
        await WaitForClusterInitializedAsync(cluster, cancellationToken).ConfigureAwait(false);

        return (app, cluster);
    }

    /// <summary>
    /// Creates a joiner node that joins through the specified seed.
    /// </summary>
    public async Task<(WebApplication App, IRapidCluster Cluster)> CreateJoinerNodeAsync(
        Pb.Endpoint address,
        Pb.Endpoint seedAddress,
        CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_loggerFactory);
        builder.ConfigureRapidKestrel(address.Port);

        builder.Services.AddRapid(options =>
        {
            options.ListenAddress = address;
            options.SeedAddress = seedAddress;
        });

        var app = builder.Build();
        app.MapRapidMembershipService();

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _apps.Add(app);

        var cluster = app.Services.GetRequiredService<IRapidCluster>();

        // Wait for the cluster to be initialized
        await WaitForClusterInitializedAsync(cluster, cancellationToken).ConfigureAwait(false);

        return (app, cluster);
    }

    /// <summary>
    /// Creates a joiner node with custom configuration.
    /// </summary>
    public async Task<(WebApplication App, IRapidCluster Cluster)> CreateJoinerNodeAsync(
        Pb.Endpoint address,
        Pb.Endpoint seedAddress,
        Action<RapidOptions> configureOptions,
        CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_loggerFactory);
        builder.ConfigureRapidKestrel(address.Port);

        builder.Services.AddRapid(options =>
        {
            options.ListenAddress = address;
            options.SeedAddress = seedAddress;
            configureOptions(options);
        });

        var app = builder.Build();
        app.MapRapidMembershipService();

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _apps.Add(app);

        var cluster = app.Services.GetRequiredService<IRapidCluster>();

        // Wait for the cluster to be initialized
        await WaitForClusterInitializedAsync(cluster, cancellationToken).ConfigureAwait(false);

        return (app, cluster);
    }

    /// <summary>
    /// Waits for a cluster to be initialized (has at least one member).
    /// </summary>
    private static async Task WaitForClusterInitializedAsync(IRapidCluster cluster, CancellationToken cancellationToken)
    {
        while (cluster.ViewAccessor.CurrentView.Size == 0)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for a cluster to reach at least the expected size.
    /// </summary>
    public static async Task WaitForClusterSizeAsync(IRapidCluster cluster, int expectedSize, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cluster.GetMembershipSize() >= expectedSize)
                return;
            await Task.Delay(10).ConfigureAwait(false);
        }
        throw new TimeoutException($"Cluster did not reach expected size {expectedSize} within {timeout}");
    }

    /// <summary>
    /// Waits for a cluster to reach exactly the expected size.
    /// </summary>
    public static async Task WaitForClusterSizeExactAsync(IRapidCluster cluster, int expectedSize, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cluster.GetMembershipSize() == expectedSize)
                return;
            await Task.Delay(10).ConfigureAwait(false);
        }
        throw new TimeoutException($"Cluster did not reach expected size {expectedSize} within {timeout}. Current size: {cluster.GetMembershipSize()}");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
        {
#pragma warning disable CA1031
            try
            {
                await app.StopAsync().ConfigureAwait(false);
                await app.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore disposal errors in tests
            }
#pragma warning restore CA1031
        }
        _apps.Clear();
        _loggerFactory.Dispose();
        _portAllocator.Dispose();
        GC.SuppressFinalize(this);
    }
}
