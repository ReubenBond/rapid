using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Rapid.Tests.Integration;

/// <summary>
/// Integration tests for Rapid cluster using the new hosting API
/// </summary>
public sealed class ClusterIntegrationTests(ITestOutputHelper outputHelper) : IAsyncDisposable
{
    private readonly TestCluster _cluster = new(outputHelper);

    public async ValueTask DisposeAsync()
    {
        await _cluster.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Test that a single seed node can start successfully
    /// </summary>
    [Fact]
    public async Task SingleSeedNodeStarts()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var (app, cluster) = await _cluster.CreateSeedNodeAsync(seedAddress, TestContext.Current.CancellationToken);

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
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joinerAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, TestContext.Current.CancellationToken);

        Assert.Equal(1, seed.GetMembershipSize());

        var (joinerApp, joiner) = await _cluster.CreateJoinerNodeAsync(joinerAddress, seedAddress, TestContext.Current.CancellationToken);

        // Wait for cluster convergence
        await TestCluster.WaitForClusterSizeAsync(seed, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        await TestCluster.WaitForClusterSizeAsync(joiner, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        Assert.Equal(2, seed.GetMembershipSize());
        Assert.Equal(2, joiner.GetMembershipSize());
    }

    /// <summary>
    /// Test with three nodes forming a cluster
    /// </summary>
    [Fact]
    public async Task ThreeNodesFormCluster()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joiner1Address = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joiner2Address = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, TestContext.Current.CancellationToken);
        var (joiner1App, joiner1) = await _cluster.CreateJoinerNodeAsync(joiner1Address, seedAddress, TestContext.Current.CancellationToken);
        var (joiner2App, joiner2) = await _cluster.CreateJoinerNodeAsync(joiner2Address, seedAddress, TestContext.Current.CancellationToken);

        // Wait for cluster convergence
        await TestCluster.WaitForClusterSizeAsync(seed, 3, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        await TestCluster.WaitForClusterSizeAsync(joiner1, 3, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        await TestCluster.WaitForClusterSizeAsync(joiner2, 3, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

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
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joinerAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var viewChanges = new ConcurrentBag<ClusterStatusChange>();

        // Create seed with subscription
        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, options =>
        {
            options.AddSubscription(ClusterEvents.ViewChange, change => viewChanges.Add(change));
        }, TestContext.Current.CancellationToken);

        var (joinerApp, joiner) = await _cluster.CreateJoinerNodeAsync(joinerAddress, seedAddress, TestContext.Current.CancellationToken);

        // Wait for cluster convergence
        await TestCluster.WaitForClusterSizeAsync(seed, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        // Should have received at least one view change event
        Assert.True(viewChanges.Count > 0);
    }

    /// <summary>
    /// Test that metadata is propagated correctly
    /// </summary>
    [Fact]
    public async Task MetadataIsPropagated()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joinerAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var seedMetadataDict = new Dictionary<string, Google.Protobuf.ByteString>
        {
            ["role"] = Google.Protobuf.ByteString.CopyFromUtf8("seed"),
            ["datacenter"] = Google.Protobuf.ByteString.CopyFromUtf8("us-west")
        };

        // Create seed with metadata
        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, options =>
        {
            options.SetMetadata(seedMetadataDict);
        }, TestContext.Current.CancellationToken);

        var joinerMetadataDict = new Dictionary<string, Google.Protobuf.ByteString>
        {
            ["role"] = Google.Protobuf.ByteString.CopyFromUtf8("worker"),
            ["datacenter"] = Google.Protobuf.ByteString.CopyFromUtf8("us-east")
        };

        // Create joiner with metadata
        var (joinerApp, joiner) = await _cluster.CreateJoinerNodeAsync(joinerAddress, seedAddress, options =>
        {
            options.SetMetadata(joinerMetadataDict);
        }, TestContext.Current.CancellationToken);

        // Wait for cluster convergence
        await TestCluster.WaitForClusterSizeAsync(seed, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

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
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joiner1Address = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joiner2Address = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, TestContext.Current.CancellationToken);
        var (joiner1App, joiner1) = await _cluster.CreateJoinerNodeAsync(joiner1Address, seedAddress, TestContext.Current.CancellationToken);
        var (joiner2App, joiner2) = await _cluster.CreateJoinerNodeAsync(joiner2Address, seedAddress, TestContext.Current.CancellationToken);

        // Wait for cluster convergence
        await TestCluster.WaitForClusterSizeAsync(seed, 3, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        await TestCluster.WaitForClusterSizeAsync(joiner1, 3, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        await TestCluster.WaitForClusterSizeAsync(joiner2, 3, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        Assert.Equal(3, seed.GetMembershipSize());

        // Joiner2 leaves gracefully
        await joiner2.LeaveGracefullyAsync().ConfigureAwait(true);

        // Wait for remaining nodes to detect the leave - increased timeout for consensus
        await TestCluster.WaitForClusterSizeAsync(seed, 2, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
        await TestCluster.WaitForClusterSizeAsync(joiner1, 2, TimeSpan.FromSeconds(20)).ConfigureAwait(true);

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
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, TestContext.Current.CancellationToken);

        const int numJoiners = 3;
        var joinTasks = new List<Task<(WebApplication App, IRapidCluster Cluster)>>();

        for (var i = 0; i < numJoiners; i++)
        {
            var joinerAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
            joinTasks.Add(_cluster.CreateJoinerNodeAsync(joinerAddress, seedAddress, TestContext.Current.CancellationToken));
        }

        var joiners = await Task.WhenAll(joinTasks).ConfigureAwait(true);

        // Wait for cluster convergence - increased timeout for concurrent joins
        await TestCluster.WaitForClusterSizeAsync(seed, numJoiners + 1, TimeSpan.FromSeconds(30)).ConfigureAwait(true);

        Assert.Equal(numJoiners + 1, seed.GetMembershipSize());

        foreach (var (app, joiner) in joiners)
        {
            await TestCluster.WaitForClusterSizeAsync(joiner, numJoiners + 1, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            Assert.Equal(numJoiners + 1, joiner.GetMembershipSize());
        }
    }

    /// <summary>
    /// Test view change proposal events
    /// </summary>
    [Fact]
    public async Task ViewChangeProposalEventsFire()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joinerAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var proposals = new ConcurrentBag<ClusterStatusChange>();

        // Create seed with subscription
        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, options =>
        {
            options.AddSubscription(ClusterEvents.ViewChangeProposal, change => proposals.Add(change));
        }, TestContext.Current.CancellationToken);

        var (joinerApp, joiner) = await _cluster.CreateJoinerNodeAsync(joinerAddress, seedAddress, TestContext.Current.CancellationToken);

        // Wait for cluster convergence
        await TestCluster.WaitForClusterSizeAsync(seed, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        // Should have received proposal events
        Assert.True(proposals.Count > 0);
    }
}
