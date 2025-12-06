/*
 * Copyright © 2016 - 2025 VMware, Inc. All Rights Reserved.
 */

#pragma warning disable CA1303 // Do not pass literals as localized parameters - Test code doesn't need localization

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Rapid.Tests.Integration;

/// <summary>
/// Debug test for leave protocol
/// </summary>
public sealed class LeaveProtocolDebugTest : IDisposable
{
    private readonly List<Cluster> _clusters = [];
    private readonly ILoggerFactory _loggerFactory;
    private int _nextPort = 9500;

    public LeaveProtocolDebugTest()
    {
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddConsole();
        });
    }

    public void Dispose()
    {
        foreach (var cluster in _clusters)
        {
#pragma warning disable CA1031
            try { cluster.Dispose(); }
            catch { }
#pragma warning restore CA1031
        }
        _clusters.Clear();
        _loggerFactory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task DebugLeaveProtocol()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);
        var joinerAddress = Utils.HostFromParts("127.0.0.1", _nextPort++);

        var viewChanges = new ConcurrentBag<ClusterStatusChange>();

        Console.WriteLine($"Creating seed at {seedAddress}");
        var seed = await new Cluster.ClusterBuilder(seedAddress)
            .UseLoggerFactory(_loggerFactory)
            .StartAsync().ConfigureAwait(true);
        _clusters.Add(seed);

        seed.RegisterSubscription(ClusterEvents.ViewChange, change =>
        {
            Console.WriteLine($"SEED ViewChange: Config={change.ConfigurationId}, {change.Delta.Count} changes");
            foreach (var nodeChange in change.Delta)
            {
                Console.WriteLine($"  - {nodeChange.Endpoint}: {nodeChange.Status}");
            }
            viewChanges.Add(change);
        });

        Console.WriteLine($"Creating joiner at {joinerAddress}");
        var joiner = await new Cluster.ClusterBuilder(joinerAddress)
            .UseLoggerFactory(_loggerFactory)
            .JoinAsync(seedAddress).ConfigureAwait(true);
        _clusters.Add(joiner);

        Console.WriteLine("Waiting for cluster convergence...");
        await Task.Delay(2000, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Console.WriteLine($"Seed size: {seed.GetMembershipSize()}");
        Console.WriteLine($"Joiner size: {joiner.GetMembershipSize()}");
        Assert.Equal(2, seed.GetMembershipSize());

        Console.WriteLine("Joiner leaving gracefully...");
        await joiner.LeaveGracefullyAsync().ConfigureAwait(true);

        Console.WriteLine("Waiting 5 seconds for leave to be processed...");
        await Task.Delay(5000, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Console.WriteLine($"Final seed size: {seed.GetMembershipSize()}");
        Console.WriteLine($"View changes received: {viewChanges.Count}");

        Assert.Equal(1, seed.GetMembershipSize());
    }

    /// <summary>
    /// Test leave protocol with 3 nodes - this should work because monitoring relationships are established
    /// </summary>
    [Fact]
    public async Task LeaveProtocolWith3Nodes()
    {
        var addr1 = Utils.HostFromParts("127.0.0.1", _nextPort++);
        var addr2 = Utils.HostFromParts("127.0.0.1", _nextPort++);
        var addr3 = Utils.HostFromParts("127.0.0.1", _nextPort++);

        Console.WriteLine("Creating 3-node cluster...");
        var node1 = await new Cluster.ClusterBuilder(addr1).UseLoggerFactory(_loggerFactory).StartAsync().ConfigureAwait(true);
        _clusters.Add(node1);

        var node2 = await new Cluster.ClusterBuilder(addr2).UseLoggerFactory(_loggerFactory).JoinAsync(addr1).ConfigureAwait(true);
        _clusters.Add(node2);

        var node3 = await new Cluster.ClusterBuilder(addr3).UseLoggerFactory(_loggerFactory).JoinAsync(addr1).ConfigureAwait(true);
        _clusters.Add(node3);

        await Task.Delay(2000, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Console.WriteLine($"Cluster sizes: {node1.GetMembershipSize()}, {node2.GetMembershipSize()}, {node3.GetMembershipSize()}");
        Assert.Equal(3, node1.GetMembershipSize());
        Assert.Equal(3, node2.GetMembershipSize());
        Assert.Equal(3, node3.GetMembershipSize());

        Console.WriteLine("Node 3 leaving gracefully...");
        await node3.LeaveGracefullyAsync().ConfigureAwait(true);

        await Task.Delay(3000, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Console.WriteLine($"Final sizes: Node1={node1.GetMembershipSize()}, Node2={node2.GetMembershipSize()}");
        Assert.Equal(2, node1.GetMembershipSize());
        Assert.Equal(2, node2.GetMembershipSize());
    }
}
