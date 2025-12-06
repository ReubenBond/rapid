/*
 * Copyright © 2016 - 2025 VMware, Inc. All Rights Reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file
 * except in compliance with the License. You may obtain a copy of the License at
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software distributed under the
 * License is distributed on an "AS IS" BASIS, without warranties or conditions of any kind,
 * EITHER EXPRESS OR IMPLIED. See the License for the specific language governing
 * permissions and limitations under the License.
 */

using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Rapid.Tests.Integration;

/// <summary>
/// Integration tests for Cluster API
/// </summary>
public class ClusterIntegrationTests : IDisposable
{
    private readonly List<Cluster> _clusters = [];
    private readonly ILoggerFactory _loggerFactory;
    private int _nextPort = 9000;

    public ClusterIntegrationTests()
    {
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Warning);
        });
    }

    public void Dispose()
    {
        foreach (var cluster in _clusters)
        {
            try
            {
                cluster.Dispose();
            }
            catch
            {
                // Ignore disposal errors in tests
            }
        }
        _clusters.Clear();
        _loggerFactory.Dispose();
    }

    /// <summary>
    /// Test that a single seed node can start successfully
    /// </summary>
    [Fact]
    public async Task SingleSeedNodeStarts()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);
        
        var seed = await new Cluster.ClusterBuilder(seedAddress)
            .UseLoggerFactory(_loggerFactory)
            .StartAsync();
        _clusters.Add(seed);

        Assert.Equal(1, seed.GetMembershipSize());
    }

    /// <summary>
    /// Test with a single node joining through a seed
    /// </summary>
    [Fact]
    public async Task SingleNodeJoinsThroughSeed()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);
        var joinerAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);
        
        var seed = await new Cluster.ClusterBuilder(seedAddress)
            .UseLoggerFactory(_loggerFactory)
            .StartAsync();
        _clusters.Add(seed);

        Assert.Equal(1, seed.GetMembershipSize());

        var joiner = await new Cluster.ClusterBuilder(joinerAddress)
            .UseLoggerFactory(_loggerFactory)
            .JoinAsync(seedAddress);
        _clusters.Add(joiner);

        // Wait for cluster convergence
        await WaitForClusterSize(seed, 2, TimeSpan.FromSeconds(10));
        await WaitForClusterSize(joiner, 2, TimeSpan.FromSeconds(10));

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
        
        var seed = await new Cluster.ClusterBuilder(seedAddress)
            .UseLoggerFactory(_loggerFactory)
            .StartAsync();
        _clusters.Add(seed);

        var joiner1 = await new Cluster.ClusterBuilder(joiner1Address)
            .UseLoggerFactory(_loggerFactory)
            .JoinAsync(seedAddress);
        _clusters.Add(joiner1);

        var joiner2 = await new Cluster.ClusterBuilder(joiner2Address)
            .UseLoggerFactory(_loggerFactory)
            .JoinAsync(seedAddress);
        _clusters.Add(joiner2);

        // Wait for cluster convergence
        await WaitForClusterSize(seed, 3, TimeSpan.FromSeconds(10));
        await WaitForClusterSize(joiner1, 3, TimeSpan.FromSeconds(10));
        await WaitForClusterSize(joiner2, 3, TimeSpan.FromSeconds(10));

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
        
        var seed = await new Cluster.ClusterBuilder(seedAddress)
            .UseLoggerFactory(_loggerFactory)
            .StartAsync();
        _clusters.Add(seed);

        seed.RegisterSubscription(ClusterEvents.ViewChange, change =>
        {
            viewChanges.Add(change);
        });

        var joiner = await new Cluster.ClusterBuilder(joinerAddress)
            .UseLoggerFactory(_loggerFactory)
            .JoinAsync(seedAddress);
        _clusters.Add(joiner);

        // Wait for cluster convergence
        await WaitForClusterSize(seed, 2, TimeSpan.FromSeconds(10));

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
        
        var seed = await new Cluster.ClusterBuilder(seedAddress)
            .UseLoggerFactory(_loggerFactory)
            .SetMetadata(seedMetadataDict)
            .StartAsync();
        _clusters.Add(seed);

        var joinerMetadataDict = new Dictionary<string, Google.Protobuf.ByteString>
        {
            ["role"] = Google.Protobuf.ByteString.CopyFromUtf8("worker"),
            ["datacenter"] = Google.Protobuf.ByteString.CopyFromUtf8("us-east")
        };
        
        var joiner = await new Cluster.ClusterBuilder(joinerAddress)
            .UseLoggerFactory(_loggerFactory)
            .SetMetadata(joinerMetadataDict)
            .JoinAsync(seedAddress);
        _clusters.Add(joiner);

        // Wait for cluster convergence
        await WaitForClusterSize(seed, 2, TimeSpan.FromSeconds(10));

        // Verify metadata is available
        var allMetadata = seed.GetClusterMetadata();
        Assert.Contains(seedAddress, allMetadata.Keys);
        Assert.Contains(joinerAddress, allMetadata.Keys);
        
        Assert.Equal("seed", allMetadata[seedAddress].Metadata_["role"].ToStringUtf8());
        Assert.Equal("worker", allMetadata[joinerAddress].Metadata_["role"].ToStringUtf8());
    }

    /// <summary>
    /// Test graceful leave
    /// </summary>
    [Fact]
    public async Task NodeCanLeaveGracefully()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);
        var joinerAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);
        
        var seed = await new Cluster.ClusterBuilder(seedAddress)
            .UseLoggerFactory(_loggerFactory)
            .StartAsync();
        _clusters.Add(seed);

        var joiner = await new Cluster.ClusterBuilder(joinerAddress)
            .UseLoggerFactory(_loggerFactory)
            .JoinAsync(seedAddress);
        _clusters.Add(joiner);

        // Wait for cluster convergence
        await WaitForClusterSize(seed, 2, TimeSpan.FromSeconds(10));
        await WaitForClusterSize(joiner, 2, TimeSpan.FromSeconds(10));

        Assert.Equal(2, seed.GetMembershipSize());

        // Joiner leaves gracefully
        await joiner.LeaveGracefullyAsync();

        // Wait for seed to detect the leave - increased timeout for consensus
        await WaitForClusterSize(seed, 1, TimeSpan.FromSeconds(20));

        Assert.Equal(1, seed.GetMembershipSize());
    }

    /// <summary>
    /// Test that multiple nodes can join concurrently
    /// </summary>
    [Fact]
    public async Task MultipleNodesConcurrentJoin()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);
        
        var seed = await new Cluster.ClusterBuilder(seedAddress)
            .UseLoggerFactory(_loggerFactory)
            .StartAsync();
        _clusters.Add(seed);

        const int numJoiners = 5;
        var joinTasks = new List<Task<Cluster>>();

        for (int i = 0; i < numJoiners; i++)
        {
            var joinerAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);
            var joinTask = new Cluster.ClusterBuilder(joinerAddress)
                .UseLoggerFactory(_loggerFactory)
                .JoinAsync(seedAddress);
            joinTasks.Add(joinTask);
        }

        var joiners = await Task.WhenAll(joinTasks);
        _clusters.AddRange(joiners);

        // Wait for cluster convergence - increased timeout for concurrent joins
        await WaitForClusterSize(seed, numJoiners + 1, TimeSpan.FromSeconds(30));

        Assert.Equal(numJoiners + 1, seed.GetMembershipSize());
        
        foreach (var joiner in joiners)
        {
            await WaitForClusterSize(joiner, numJoiners + 1, TimeSpan.FromSeconds(30));
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
        
        var seed = await new Cluster.ClusterBuilder(seedAddress)
            .UseLoggerFactory(_loggerFactory)
            .StartAsync();
        _clusters.Add(seed);

        seed.RegisterSubscription(ClusterEvents.ViewChangeProposal, change =>
        {
            proposals.Add(change);
        });

        var joiner = await new Cluster.ClusterBuilder(joinerAddress)
            .UseLoggerFactory(_loggerFactory)
            .JoinAsync(seedAddress);
        _clusters.Add(joiner);

        // Wait for cluster convergence
        await WaitForClusterSize(seed, 2, TimeSpan.FromSeconds(10));

        // Should have received proposal events
        Assert.True(proposals.Count > 0);
    }

    private static async Task WaitForClusterSize(Cluster cluster, int expectedSize, TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (cluster.GetMembershipSize() == expectedSize)
            {
                return;
            }
            await Task.Delay(100);
        }
        
        throw new TimeoutException(
            $"Cluster did not reach expected size {expectedSize} within {timeout}. Current size: {cluster.GetMembershipSize()}");
    }
}
