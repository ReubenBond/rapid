using System.Collections.Concurrent;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Rapid.Pb;
using Xunit;

namespace Rapid.Tests.Integration;

/// <summary>
/// Extended integration tests for Rapid cluster scenarios.
/// </summary>
public sealed class ExtendedClusterIntegrationTests(ITestOutputHelper outputHelper) : IAsyncDisposable
{
    private readonly TestCluster _cluster = new(outputHelper);

    public async ValueTask DisposeAsync()
    {
        await _cluster.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    #region Sequential Join Tests

    [Fact]
    public async Task SequentialJoinsFiveNodesAllConverge()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, TestContext.Current.CancellationToken);

        var nodes = new List<IRapidCluster> { seed };
        for (var i = 0; i < 4; i++)
        {
            var joinerAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
            var (joinerApp, joiner) = await _cluster.CreateJoinerNodeAsync(joinerAddress, seedAddress, TestContext.Current.CancellationToken);
            await TestCluster.WaitForClusterSizeAsync(joiner, nodes.Count + 1, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            nodes.Add(joiner);
        }

        foreach (var node in nodes)
        {
            await TestCluster.WaitForClusterSizeAsync(node, 5, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            Assert.Equal(5, node.GetMembershipSize());
        }
    }

    [Fact]
    public async Task JoinThroughNonSeedNodeWorks()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joiner1Address = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joiner2Address = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, TestContext.Current.CancellationToken);
        var (joiner1App, joiner1) = await _cluster.CreateJoinerNodeAsync(joiner1Address, seedAddress, TestContext.Current.CancellationToken);

        await TestCluster.WaitForClusterSizeAsync(seed, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        var (joiner2App, joiner2) = await _cluster.CreateJoinerNodeAsync(joiner2Address, joiner1Address, TestContext.Current.CancellationToken);

        await TestCluster.WaitForClusterSizeAsync(seed, 3, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        await TestCluster.WaitForClusterSizeAsync(joiner1, 3, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        await TestCluster.WaitForClusterSizeAsync(joiner2, 3, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        Assert.Equal(3, seed.GetMembershipSize());
        Assert.Equal(3, joiner1.GetMembershipSize());
        Assert.Equal(3, joiner2.GetMembershipSize());
    }

    #endregion

    #region Memberlist Tests

    [Fact]
    public async Task GetMemberlistReturnsAllMembers()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joiner1Address = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, TestContext.Current.CancellationToken);
        var (joiner1App, joiner1) = await _cluster.CreateJoinerNodeAsync(joiner1Address, seedAddress, TestContext.Current.CancellationToken);

        await TestCluster.WaitForClusterSizeAsync(seed, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        var memberlist = seed.GetMemberlist();

        Assert.Equal(2, memberlist.Count);
        Assert.Contains(memberlist, e => e.Port == seedAddress.Port);
        Assert.Contains(memberlist, e => e.Port == joiner1Address.Port);
    }

    [Fact]
    public async Task GetMemberlistAllNodesConsistent()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joiner1Address = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joiner2Address = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, TestContext.Current.CancellationToken);
        var (joiner1App, joiner1) = await _cluster.CreateJoinerNodeAsync(joiner1Address, seedAddress, TestContext.Current.CancellationToken);
        var (joiner2App, joiner2) = await _cluster.CreateJoinerNodeAsync(joiner2Address, seedAddress, TestContext.Current.CancellationToken);

        await TestCluster.WaitForClusterSizeAsync(seed, 3, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        await TestCluster.WaitForClusterSizeAsync(joiner1, 3, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        await TestCluster.WaitForClusterSizeAsync(joiner2, 3, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        var seedList = seed.GetMemberlist().OrderBy(e => e.Port).ToList();
        var joiner1List = joiner1.GetMemberlist().OrderBy(e => e.Port).ToList();
        var joiner2List = joiner2.GetMemberlist().OrderBy(e => e.Port).ToList();

        Assert.Equal(seedList.Count, joiner1List.Count);
        Assert.Equal(seedList.Count, joiner2List.Count);

        for (var i = 0; i < seedList.Count; i++)
        {
            Assert.Equal(seedList[i].Port, joiner1List[i].Port);
            Assert.Equal(seedList[i].Port, joiner2List[i].Port);
        }
    }

    #endregion

    #region Metadata Tests

    [Fact]
    public async Task ComplexMetadataPropagatedCorrectly()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joinerAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, options =>
        {
            options.SetMetadata(new Dictionary<string, ByteString>
            {
                ["role"] = ByteString.CopyFromUtf8("seed"),
                ["region"] = ByteString.CopyFromUtf8("us-west-2"),
                ["zone"] = ByteString.CopyFromUtf8("a"),
                ["instance_type"] = ByteString.CopyFromUtf8("m5.large"),
                ["version"] = ByteString.CopyFromUtf8("1.0.0")
            });
        }, TestContext.Current.CancellationToken);

        var (joinerApp, joiner) = await _cluster.CreateJoinerNodeAsync(joinerAddress, seedAddress, options =>
        {
            options.SetMetadata(new Dictionary<string, ByteString>
            {
                ["role"] = ByteString.CopyFromUtf8("worker"),
                ["region"] = ByteString.CopyFromUtf8("us-east-1"),
                ["zone"] = ByteString.CopyFromUtf8("b"),
                ["instance_type"] = ByteString.CopyFromUtf8("c5.xlarge"),
                ["version"] = ByteString.CopyFromUtf8("1.0.0")
            });
        }, TestContext.Current.CancellationToken);

        await TestCluster.WaitForClusterSizeAsync(seed, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        var allMetadata = seed.GetClusterMetadata();

        Assert.Equal(2, allMetadata.Count);
        Assert.Equal("seed", allMetadata[seedAddress].Metadata_["role"].ToStringUtf8());
        Assert.Equal("worker", allMetadata[joinerAddress].Metadata_["role"].ToStringUtf8());
        Assert.Equal("us-west-2", allMetadata[seedAddress].Metadata_["region"].ToStringUtf8());
        Assert.Equal("us-east-1", allMetadata[joinerAddress].Metadata_["region"].ToStringUtf8());
    }

    [Fact]
    public async Task EmptyMetadataHandledCorrectly()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joinerAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, TestContext.Current.CancellationToken);

        var (joinerApp, joiner) = await _cluster.CreateJoinerNodeAsync(joinerAddress, seedAddress, TestContext.Current.CancellationToken);

        await TestCluster.WaitForClusterSizeAsync(seed, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        var allMetadata = seed.GetClusterMetadata();

        Assert.Equal(2, allMetadata.Count);
        Assert.Empty(allMetadata[seedAddress].Metadata_);
        Assert.Empty(allMetadata[joinerAddress].Metadata_);
    }

    #endregion

    #region Event Subscription Tests

    [Fact]
    public async Task MultipleSubscriptionsAllReceiveEvents()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joinerAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var callbackCount1 = 0;
        var callbackCount2 = 0;
        var callbackCount3 = 0;

        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, options =>
        {
            options.AddSubscription(ClusterEvents.ViewChange, _ => Interlocked.Increment(ref callbackCount1));
            options.AddSubscription(ClusterEvents.ViewChange, _ => Interlocked.Increment(ref callbackCount2));
            options.AddSubscription(ClusterEvents.ViewChange, _ => Interlocked.Increment(ref callbackCount3));
        }, TestContext.Current.CancellationToken);

        var (joinerApp, joiner) = await _cluster.CreateJoinerNodeAsync(joinerAddress, seedAddress, TestContext.Current.CancellationToken);

        await TestCluster.WaitForClusterSizeAsync(seed, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.True(callbackCount1 > 0);
        Assert.True(callbackCount2 > 0);
        Assert.True(callbackCount3 > 0);
    }

    [Fact]
    public async Task SubscriptionReceivesCorrectMembership()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joinerAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        ClusterStatusChange? lastChange = null;

        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, options =>
        {
            options.AddSubscription(ClusterEvents.ViewChange, change => lastChange = change);
        }, TestContext.Current.CancellationToken);

        var (joinerApp, joiner) = await _cluster.CreateJoinerNodeAsync(joinerAddress, seedAddress, TestContext.Current.CancellationToken);

        await TestCluster.WaitForClusterSizeAsync(seed, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.NotNull(lastChange);
        Assert.True(lastChange.Membership.Count >= 2);
    }

    #endregion

    #region Configuration ID Tests

    [Fact]
    public async Task ConfigurationIdChangesOnJoin()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joinerAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var configIds = new ConcurrentBag<long>();

        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, options =>
        {
            options.AddSubscription(ClusterEvents.ViewChange, change => configIds.Add(change.ConfigurationId));
        }, TestContext.Current.CancellationToken);

        var (joinerApp, joiner) = await _cluster.CreateJoinerNodeAsync(joinerAddress, seedAddress, TestContext.Current.CancellationToken);

        await TestCluster.WaitForClusterSizeAsync(seed, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.NotEmpty(configIds);
    }

    #endregion

    #region Four Node Tests

    [Fact]
    public async Task FourNodesFormCluster()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joiner1Address = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joiner2Address = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joiner3Address = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, TestContext.Current.CancellationToken);
        var (joiner1App, joiner1) = await _cluster.CreateJoinerNodeAsync(joiner1Address, seedAddress, TestContext.Current.CancellationToken);
        var (joiner2App, joiner2) = await _cluster.CreateJoinerNodeAsync(joiner2Address, seedAddress, TestContext.Current.CancellationToken);
        var (joiner3App, joiner3) = await _cluster.CreateJoinerNodeAsync(joiner3Address, seedAddress, TestContext.Current.CancellationToken);

        await TestCluster.WaitForClusterSizeAsync(seed, 4, TimeSpan.FromSeconds(15)).ConfigureAwait(true);
        await TestCluster.WaitForClusterSizeAsync(joiner1, 4, TimeSpan.FromSeconds(15)).ConfigureAwait(true);
        await TestCluster.WaitForClusterSizeAsync(joiner2, 4, TimeSpan.FromSeconds(15)).ConfigureAwait(true);
        await TestCluster.WaitForClusterSizeAsync(joiner3, 4, TimeSpan.FromSeconds(15)).ConfigureAwait(true);

        Assert.Equal(4, seed.GetMembershipSize());
        Assert.Equal(4, joiner1.GetMembershipSize());
        Assert.Equal(4, joiner2.GetMembershipSize());
        Assert.Equal(4, joiner3.GetMembershipSize());
    }

    #endregion

    #region Node Status Tests

    [Fact]
    public async Task NodeStatusChangesTracked()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joinerAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var viewChangeCount = 0;

        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, options =>
        {
            options.AddSubscription(ClusterEvents.ViewChange, _ => Interlocked.Increment(ref viewChangeCount));
        }, TestContext.Current.CancellationToken);

        var (joinerApp, joiner) = await _cluster.CreateJoinerNodeAsync(joinerAddress, seedAddress, TestContext.Current.CancellationToken);

        await TestCluster.WaitForClusterSizeAsync(seed, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.True(viewChangeCount > 0);
    }

    #endregion
}
