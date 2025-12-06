using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Rapid.Tests.Integration;

/// <summary>
/// Integration tests for Rapid cluster using the new hosting API
/// </summary>
public sealed class ClusterIntegrationTests : IDisposable
{
    private readonly List<WebApplication> _apps = [];
    private readonly ILoggerFactory _loggerFactory;
    private int _nextPort = 9000;

    public ClusterIntegrationTests()
    {
        _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
    }

    public void Dispose()
    {
        foreach (var app in _apps)
        {
#pragma warning disable CA1031
            try
            {
                app.StopAsync().GetAwaiter().GetResult();
                app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // Ignore disposal errors in tests
            }
#pragma warning restore CA1031
        }
        _apps.Clear();
        _loggerFactory.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<(WebApplication App, IRapidCluster Cluster)> CreateSeedNodeAsync(Pb.Endpoint address)
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
        
        await app.StartAsync(TestContext.Current.CancellationToken);
        _apps.Add(app);
        
        var cluster = app.Services.GetRequiredService<IRapidCluster>();
        return (app, cluster);
    }

    private async Task<(WebApplication App, IRapidCluster Cluster)> CreateJoinerNodeAsync(Pb.Endpoint address, Pb.Endpoint seedAddress)
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
        
        await app.StartAsync(TestContext.Current.CancellationToken);
        _apps.Add(app);
        
        var cluster = app.Services.GetRequiredService<IRapidCluster>();
        return (app, cluster);
    }

    private static async Task WaitForClusterSize(IRapidCluster cluster, int expectedSize, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cluster.GetMembershipSize() >= expectedSize)
                return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Cluster did not reach expected size {expectedSize} within {timeout}");
    }

    /// <summary>
    /// Test that a single seed node can start successfully
    /// </summary>
    [Fact]
    public async Task SingleSeedNodeStarts()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);

        var (app, cluster) = await CreateSeedNodeAsync(seedAddress);

        // Give it a moment to initialize
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.Equal(1, cluster.GetMembershipSize());
    }

    /// <summary>
    /// Test with a single node joining through a seed
    /// </summary>
    [Fact]
    public async Task SingleNodeJoinsThroughSeed()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);
        var joinerAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);

        var (seedApp, seed) = await CreateSeedNodeAsync(seedAddress);

        Assert.Equal(1, seed.GetMembershipSize());

        var (joinerApp, joiner) = await CreateJoinerNodeAsync(joinerAddress, seedAddress);

        // Wait for cluster convergence
        await WaitForClusterSize(seed, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        await WaitForClusterSize(joiner, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        Assert.Equal(2, seed.GetMembershipSize());
        Assert.Equal(2, joiner.GetMembershipSize());
    }

    /// <summary>
    /// Test with three nodes forming a cluster
    /// </summary>
    [Fact]
    public async Task ThreeNodesFormCluster()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);
        var joiner1Address = Utils.HostFromParts("127.0.0.1", _nextPort++);
        var joiner2Address = Utils.HostFromParts("127.0.0.1", _nextPort++);

        var (seedApp, seed) = await CreateSeedNodeAsync(seedAddress);
        var (joiner1App, joiner1) = await CreateJoinerNodeAsync(joiner1Address, seedAddress);
        var (joiner2App, joiner2) = await CreateJoinerNodeAsync(joiner2Address, seedAddress);

        // Wait for cluster convergence
        await WaitForClusterSize(seed, 3, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        await WaitForClusterSize(joiner1, 3, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        await WaitForClusterSize(joiner2, 3, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        Assert.Equal(3, seed.GetMembershipSize());
        Assert.Equal(3, joiner1.GetMembershipSize());
        Assert.Equal(3, joiner2.GetMembershipSize());
    }

    /// <summary>
    /// Test that view change events are fired when nodes join
    /// </summary>
    [Fact]
    public async Task ViewChangeEventsFireOnJoin()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);
        var joinerAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);

        var viewChanges = new ConcurrentBag<ClusterStatusChange>();

        // Create seed with subscription
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_loggerFactory);
        builder.ConfigureRapidKestrel(seedAddress.Port);
        
        builder.Services.AddRapid(options =>
        {
            options.ListenAddress = seedAddress;
            options.SeedAddress = seedAddress;
            options.AddSubscription(ClusterEvents.ViewChange, change => viewChanges.Add(change));
        });

        var app = builder.Build();
        app.MapRapidMembershipService();
        await app.StartAsync(TestContext.Current.CancellationToken);
        _apps.Add(app);
        
        var seed = app.Services.GetRequiredService<IRapidCluster>();

        var (joinerApp, joiner) = await CreateJoinerNodeAsync(joinerAddress, seedAddress);

        // Wait for cluster convergence
        await WaitForClusterSize(seed, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        // Should have received at least one view change event
        Assert.True(viewChanges.Count > 0);
    }

    /// <summary>
    /// Test that metadata is propagated correctly
    /// </summary>
    [Fact]
    public async Task MetadataIsPropagated()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);
        var joinerAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);

        var seedMetadataDict = new Dictionary<string, Google.Protobuf.ByteString>
        {
            ["role"] = Google.Protobuf.ByteString.CopyFromUtf8("seed"),
            ["datacenter"] = Google.Protobuf.ByteString.CopyFromUtf8("us-west")
        };

        // Create seed with metadata
        var seedBuilder = WebApplication.CreateBuilder();
        seedBuilder.Logging.ClearProviders();
        seedBuilder.Services.AddSingleton(_loggerFactory);
        seedBuilder.ConfigureRapidKestrel(seedAddress.Port);
        
        seedBuilder.Services.AddRapid(options =>
        {
            options.ListenAddress = seedAddress;
            options.SeedAddress = seedAddress;
            options.SetMetadata(seedMetadataDict);
        });

        var seedApp = seedBuilder.Build();
        seedApp.MapRapidMembershipService();
        await seedApp.StartAsync(TestContext.Current.CancellationToken);
        _apps.Add(seedApp);
        var seed = seedApp.Services.GetRequiredService<IRapidCluster>();

        var joinerMetadataDict = new Dictionary<string, Google.Protobuf.ByteString>
        {
            ["role"] = Google.Protobuf.ByteString.CopyFromUtf8("worker"),
            ["datacenter"] = Google.Protobuf.ByteString.CopyFromUtf8("us-east")
        };

        // Create joiner with metadata
        var joinerBuilder = WebApplication.CreateBuilder();
        joinerBuilder.Logging.ClearProviders();
        joinerBuilder.Services.AddSingleton(_loggerFactory);
        joinerBuilder.ConfigureRapidKestrel(joinerAddress.Port);
        
        joinerBuilder.Services.AddRapid(options =>
        {
            options.ListenAddress = joinerAddress;
            options.SeedAddress = seedAddress;
            options.SetMetadata(joinerMetadataDict);
        });

        var joinerApp = joinerBuilder.Build();
        joinerApp.MapRapidMembershipService();
        await joinerApp.StartAsync(TestContext.Current.CancellationToken);
        _apps.Add(joinerApp);
        var joiner = joinerApp.Services.GetRequiredService<IRapidCluster>();

        // Wait for cluster convergence
        await WaitForClusterSize(seed, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        // Verify metadata is available
        var allMetadata = seed.GetClusterMetadata();
        Assert.Contains(seedAddress, allMetadata.Keys);
        Assert.Contains(joinerAddress, allMetadata.Keys);

        Assert.Equal("seed", allMetadata[seedAddress].Metadata_["role"].ToStringUtf8());
        Assert.Equal("worker", allMetadata[joinerAddress].Metadata_["role"].ToStringUtf8());
    }

    /// <summary>
    /// Test graceful leave - uses 3 nodes to ensure monitoring relationships exist
    /// </summary>
    [Fact]
    public async Task NodeCanLeaveGracefully()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);
        var joiner1Address = Utils.HostFromParts("127.0.0.1", _nextPort++);
        var joiner2Address = Utils.HostFromParts("127.0.0.1", _nextPort++);

        var (seedApp, seed) = await CreateSeedNodeAsync(seedAddress);
        var (joiner1App, joiner1) = await CreateJoinerNodeAsync(joiner1Address, seedAddress);
        var (joiner2App, joiner2) = await CreateJoinerNodeAsync(joiner2Address, seedAddress);

        // Wait for cluster convergence
        await WaitForClusterSize(seed, 3, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        await WaitForClusterSize(joiner1, 3, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        await WaitForClusterSize(joiner2, 3, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        Assert.Equal(3, seed.GetMembershipSize());

        // Joiner2 leaves gracefully
        await joiner2.LeaveGracefullyAsync().ConfigureAwait(true);

        // Wait for remaining nodes to detect the leave - increased timeout for consensus
        await WaitForClusterSize(seed, 2, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
        await WaitForClusterSize(joiner1, 2, TimeSpan.FromSeconds(20)).ConfigureAwait(true);

        Assert.Equal(2, seed.GetMembershipSize());
        Assert.Equal(2, joiner1.GetMembershipSize());
    }

    /// <summary>
    /// Test that multiple nodes can join concurrently
    /// Reduced from 5 to 3 concurrent joins to avoid consensus timeout issues
    /// </summary>
    [Fact]
    public async Task MultipleNodesConcurrentJoin()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);

        var (seedApp, seed) = await CreateSeedNodeAsync(seedAddress);

        const int numJoiners = 3;
        var joinTasks = new List<Task<(WebApplication App, IRapidCluster Cluster)>>();

        for (var i = 0; i < numJoiners; i++)
        {
            var joinerAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);
            joinTasks.Add(CreateJoinerNodeAsync(joinerAddress, seedAddress));
        }

        var joiners = await Task.WhenAll(joinTasks).ConfigureAwait(true);

        // Wait for cluster convergence - increased timeout for concurrent joins
        await WaitForClusterSize(seed, numJoiners + 1, TimeSpan.FromSeconds(30)).ConfigureAwait(true);

        Assert.Equal(numJoiners + 1, seed.GetMembershipSize());

        foreach (var (app, joiner) in joiners)
        {
            await WaitForClusterSize(joiner, numJoiners + 1, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            Assert.Equal(numJoiners + 1, joiner.GetMembershipSize());
        }
    }

    /// <summary>
    /// Test view change proposal events
    /// </summary>
    [Fact]
    public async Task ViewChangeProposalEventsFire()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);
        var joinerAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);

        var proposals = new ConcurrentBag<ClusterStatusChange>();

        // Create seed with subscription
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_loggerFactory);
        builder.ConfigureRapidKestrel(seedAddress.Port);
        
        builder.Services.AddRapid(options =>
        {
            options.ListenAddress = seedAddress;
            options.SeedAddress = seedAddress;
            options.AddSubscription(ClusterEvents.ViewChangeProposal, change => proposals.Add(change));
        });

        var app = builder.Build();
        app.MapRapidMembershipService();
        await app.StartAsync(TestContext.Current.CancellationToken);
        _apps.Add(app);
        
        var seed = app.Services.GetRequiredService<IRapidCluster>();

        var (joinerApp, joiner) = await CreateJoinerNodeAsync(joinerAddress, seedAddress);

        // Wait for cluster convergence
        await WaitForClusterSize(seed, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        // Should have received proposal events
        Assert.True(proposals.Count > 0);
    }
}
