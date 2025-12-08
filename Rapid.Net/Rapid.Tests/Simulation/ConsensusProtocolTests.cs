using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Rapid.Tests.Simulation;

namespace Rapid.Tests.SimulationTests;

/// <summary>
/// Tests for consensus protocol operations using the simulation harness.
/// Covers Fast Paxos basic operations, failure cases, and configuration changes.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
public sealed class ConsensusProtocolTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private SimulationHarness _harness = null!;
    private const int TestSeed = 55555;

    public ConsensusProtocolTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
    }

    public ValueTask InitializeAsync()
    {
        _output.WriteLine($"[ConsensusProtocolTests] Initializing with seed {TestSeed}");
        _harness = new SimulationHarness(seed: TestSeed, loggerFactory: _loggerFactory);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _output.WriteLine("[ConsensusProtocolTests] Disposing harness");
        await _harness.DisposeAsync();
        _loggerFactory.Dispose();
    }

    #region Fast Paxos Basic Operations (CONS-001 to CONS-004)

    [Fact]
    public async Task SingleProposalAcceptedInTwoNodeCluster()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(
            seedNode, 
            nodeId: 1, 
            cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(
            expectedSize: 2, 
            timeout: TimeSpan.FromSeconds(5), 
            cancellationToken: TestContext.Current.CancellationToken);

        // Both nodes should have accepted the membership change via consensus
        Assert.Equal(2, seedNode.MembershipSize);
        Assert.Equal(2, joiner.MembershipSize);
        
        // Configuration IDs should match, indicating consensus was reached
        Assert.Equal(seedNode.CurrentView.ConfigurationId, joiner.CurrentView.ConfigurationId);
    }

    [Fact(Skip = "Hanging - concurrent joins have consensus issues to debug")]
    public async Task ConflictingProposalsResolvedInSequentialJoins()
    {
        var seedNode = _harness.CreateSeedNode();
        
        // Start two joins nearly simultaneously (conflicting proposals)
        var join1Task = _harness.CreateJoinerNodeAsync(
            seedNode, 
            nodeId: 1, 
            cancellationToken: TestContext.Current.CancellationToken);
        var join2Task = _harness.CreateJoinerNodeAsync(
            seedNode, 
            nodeId: 2, 
            cancellationToken: TestContext.Current.CancellationToken);

        var joiner1 = await join1Task;
        var joiner2 = await join2Task;

        // Wait for full convergence
        await _harness.WaitForConvergenceAsync(
            expectedSize: 3, 
            timeout: TimeSpan.FromSeconds(10), 
            cancellationToken: TestContext.Current.CancellationToken);

        // All nodes should eventually reach consensus on membership
        Assert.Equal(3, seedNode.MembershipSize);
        Assert.Equal(3, joiner1.MembershipSize);
        Assert.Equal(3, joiner2.MembershipSize);
    }

    [Fact]
    public async Task ConsensusCompletesWithinTimeout()
    {
        var seedNode = _harness.CreateSeedNode();
        var startTime = DateTime.UtcNow;

        var joiner = await _harness.CreateJoinerNodeAsync(
            seedNode, 
            nodeId: 1, 
            cancellationToken: TestContext.Current.CancellationToken);

        var elapsed = DateTime.UtcNow - startTime;

        // Consensus should complete in reasonable time (5 seconds is generous)
        Assert.True(elapsed < TimeSpan.FromSeconds(5), 
            $"Consensus took too long: {elapsed.TotalSeconds} seconds");
        Assert.True(joiner.IsInitialized);
    }

    [Fact]
    public async Task DecisionPropagatedToAllNodes()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = await _harness.CreateJoinerNodeAsync(
            seedNode, 
            nodeId: 1, 
            cancellationToken: TestContext.Current.CancellationToken);
        
        // Small delay between joins
        await Task.Delay(50, TestContext.Current.CancellationToken);
        
        var joiner2 = await _harness.CreateJoinerNodeAsync(
            seedNode, 
            nodeId: 2, 
            cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(
            expectedSize: 3, 
            timeout: TimeSpan.FromSeconds(10), 
            cancellationToken: TestContext.Current.CancellationToken);

        // All nodes should have the same view of membership (consensus decision)
        var seedMembers = seedNode.CurrentView.Members.Select(m => $"{m.Hostname}:{m.Port}").OrderBy(x => x).ToList();
        var joiner1Members = joiner1.CurrentView.Members.Select(m => $"{m.Hostname}:{m.Port}").OrderBy(x => x).ToList();
        var joiner2Members = joiner2.CurrentView.Members.Select(m => $"{m.Hostname}:{m.Port}").OrderBy(x => x).ToList();

        Assert.Equal(seedMembers, joiner1Members);
        Assert.Equal(seedMembers, joiner2Members);
    }

    #endregion

    #region Fast Paxos Failure Cases (CONS-010 to CONS-013)

    [Fact]
    public async Task ConsensusSucceedsWithMinorityFailure()
    {
        // Create 5-node cluster
        var nodes = await _harness.CreateClusterAsync(
            size: 5, 
            cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(
            expectedSize: 5, 
            timeout: TimeSpan.FromSeconds(15), 
            cancellationToken: TestContext.Current.CancellationToken);

        // Crash 1 node (minority)
        _harness.CrashNode(nodes[4]);

        // Add a new node - consensus should still work with 4 healthy nodes
        var newJoiner = await _harness.CreateJoinerNodeAsync(
            nodes[0], 
            nodeId: 5, 
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(newJoiner.IsInitialized);
    }

    [Fact(Skip = "Requires advanced consensus failure simulation")]
    public async Task ConsensusBlockedWithMajorityFailure()
    {
        // Create 5-node cluster
        var nodes = await _harness.CreateClusterAsync(
            size: 5, 
            cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(
            expectedSize: 5, 
            timeout: TimeSpan.FromSeconds(15), 
            cancellationToken: TestContext.Current.CancellationToken);

        // Crash 3 nodes (majority)
        _harness.CrashNode(nodes[2]);
        _harness.CrashNode(nodes[3]);
        _harness.CrashNode(nodes[4]);

        // Consensus should not be possible with only 2 nodes remaining
        // This would require timeout/failure detection to verify
        Assert.Equal(2, _harness.Nodes.Count);
    }

    [Fact(Skip = "Hanging - sequential joins have consensus issues to debug")]
    public async Task NodeFailureDuringMembershipChangeHandled()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = await _harness.CreateJoinerNodeAsync(
            seedNode, 
            nodeId: 1, 
            cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(
            expectedSize: 2, 
            timeout: TimeSpan.FromSeconds(5), 
            cancellationToken: TestContext.Current.CancellationToken);

        // Start a new join
        var join2Task = _harness.CreateJoinerNodeAsync(
            seedNode, 
            nodeId: 2, 
            cancellationToken: TestContext.Current.CancellationToken);

        // Crash joiner1 during the join
        await Task.Delay(10, TestContext.Current.CancellationToken);
        _harness.CrashNode(joiner1);

        // The new join should still succeed
        var joiner2 = await join2Task;
        Assert.True(joiner2.IsInitialized);
    }

    [Fact(Skip = "Hanging - concurrent joins have consensus issues to debug")]
    public async Task MultipleSimultaneousProposalsEventuallyResolve()
    {
        var seedNode = _harness.CreateSeedNode();

        // Start 3 joins simultaneously
        var joinTasks = new List<Task<SimulationNode>>();
        for (var i = 1; i <= 3; i++)
        {
            joinTasks.Add(_harness.CreateJoinerNodeAsync(
                seedNode, 
                nodeId: i, 
                cancellationToken: TestContext.Current.CancellationToken));
        }

        var joiners = await Task.WhenAll(joinTasks);

        // Wait for convergence
        await _harness.WaitForConvergenceAsync(
            expectedSize: 4, 
            timeout: TimeSpan.FromSeconds(15), 
            cancellationToken: TestContext.Current.CancellationToken);

        // All nodes should eventually agree
        Assert.All(_harness.Nodes, n => Assert.Equal(4, n.MembershipSize));
    }

    #endregion

    #region Configuration Changes (CONS-020 to CONS-022)

    [Fact]
    public async Task ConfigurationIdChangesWithEachMembershipChange()
    {
        var seedNode = _harness.CreateSeedNode();
        var configIds = new HashSet<long> { seedNode.CurrentView.ConfigurationId };

        // Join 3 nodes, tracking config ID changes
        for (var i = 1; i <= 3; i++)
        {
            var joiner = await _harness.CreateJoinerNodeAsync(
                seedNode, 
                nodeId: i, 
                cancellationToken: TestContext.Current.CancellationToken);
            
            await _harness.WaitForNodeSizeAsync(
                seedNode, 
                expectedSize: i + 1, 
                timeout: TimeSpan.FromSeconds(5), 
                cancellationToken: TestContext.Current.CancellationToken);
            
            var newConfigId = seedNode.CurrentView.ConfigurationId;
            // Each membership change should produce a unique configuration ID
            Assert.DoesNotContain(newConfigId, configIds);
            configIds.Add(newConfigId);
            
            // Small delay between joins
            if (i < 3)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }

        // We should have 4 unique configuration IDs (initial + 3 joins)
        Assert.Equal(4, configIds.Count);
    }

    [Fact(Skip = "Requires stale proposal injection")]
    public async Task OldConfigurationProposalsRejected()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(
            seedNode, 
            nodeId: 1, 
            cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(
            expectedSize: 2, 
            timeout: TimeSpan.FromSeconds(5), 
            cancellationToken: TestContext.Current.CancellationToken);

        // This would require injecting a proposal with an old configuration ID
        // and verifying it's rejected - needs low-level protocol access
    }

    [Fact(Skip = "Hanging - concurrent joins have consensus issues to debug")]
    public async Task ConcurrentConfigChangesEventuallySerialize()
    {
        var seedNode = _harness.CreateSeedNode();
        
        // Create multiple concurrent membership changes
        var join1Task = _harness.CreateJoinerNodeAsync(
            seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);
        var join2Task = _harness.CreateJoinerNodeAsync(
            seedNode, nodeId: 2, cancellationToken: TestContext.Current.CancellationToken);
        var join3Task = _harness.CreateJoinerNodeAsync(
            seedNode, nodeId: 3, cancellationToken: TestContext.Current.CancellationToken);

        await Task.WhenAll(join1Task, join2Task, join3Task);

        await _harness.WaitForConvergenceAsync(
            expectedSize: 4, 
            timeout: TimeSpan.FromSeconds(15), 
            cancellationToken: TestContext.Current.CancellationToken);

        // All nodes should have the same final configuration
        var configId = seedNode.CurrentView.ConfigurationId;
        Assert.All(_harness.Nodes, 
            n => Assert.Equal(configId, n.CurrentView.ConfigurationId));
    }

    #endregion

    #region Consensus Under Network Conditions

    [Fact]
    public async Task ConsensusWithMessageDelaysCompletesCorrectly()
    {
        // Enable message delays
        _harness.Network.EnableDelays = true;
        _harness.Network.BaseMessageDelay = TimeSpan.FromMilliseconds(10);
        _harness.Network.MaxJitter = TimeSpan.FromMilliseconds(20);

        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(
            seedNode, 
            nodeId: 1, 
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(joiner.IsInitialized);
        Assert.Equal(2, joiner.MembershipSize);
    }

    [Fact]
    public async Task ConsensusWithLowMessageLossSucceeds()
    {
        // Enable 5% message loss - this tests the retry logic in join protocol
        _harness.Network.MessageDropRate = 0.05;

        var seedNode = _harness.CreateSeedNode();
        
        // The join should succeed despite message loss due to retry logic
        var joiner = await _harness.CreateJoinerNodeAsync(
            seedNode, 
            nodeId: 1, 
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(joiner.IsInitialized, "Joiner should be initialized after join with retries");

        // Wait for convergence
        await _harness.WaitForConvergenceAsync(
            expectedSize: 2, 
            timeout: TimeSpan.FromSeconds(10), 
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, seedNode.MembershipSize);
        Assert.Equal(2, joiner.MembershipSize);
    }

    #endregion
}
