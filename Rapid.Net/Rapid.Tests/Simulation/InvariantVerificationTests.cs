using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Rapid.Tests.Simulation;

namespace Rapid.Tests.SimulationTests;

/// <summary>
/// Tests for cluster invariants using the simulation harness.
/// Verifies safety and liveness properties hold during various scenarios.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
public sealed class InvariantVerificationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private DeterministicSimulationHarness _harness = null!;
    private InvariantChecker _checker = null!;
    private const int TestSeed = 56789;

    public InvariantVerificationTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
    }

    public ValueTask InitializeAsync()
    {
        _output.WriteLine($"[InvariantVerificationTests] Initializing with seed {TestSeed}");
        _harness = new DeterministicSimulationHarness(seed: TestSeed, loggerFactory: _loggerFactory, testOutput: _output);
        _checker = new InvariantChecker(_harness);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _output.WriteLine("[InvariantVerificationTests] Disposing harness");
        await _harness.DisposeAsync();
        _loggerFactory.Dispose();
    }

    #region Membership Invariants (INV-001 to INV-004)

    [Fact]
    public void Inv001MembershipViewNeverEmptyForInitializedNode()
    {
        var seedNode = _harness.CreateSeedNode();

        Assert.NotNull(seedNode.CurrentView);
        Assert.True(seedNode.CurrentView.Size > 0);
    }

    [Fact]
    public void Inv002SelfAlwaysInMembershipView()
    {
        var seedNode = _harness.CreateSeedNode();

        var view = seedNode.CurrentView;
        var selfAddress = $"{seedNode.Address.Hostname}:{seedNode.Address.Port}";
        var viewAddresses = view.Members.Select(m => $"{m.Hostname}:{m.Port}").ToHashSet();

        Assert.Contains(selfAddress, viewAddresses);
    }

    [Fact]
    public async Task Inv003AllNodesInViewAreKnownNodes()
    {
        var seedNode = _harness.InnerHarness.CreateSeedNode();
        var joiner = await _harness.InnerHarness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.InnerHarness.WaitForConvergenceAsync(expectedSize: 2, timeout: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        var knownAddresses = _harness.Nodes
            .Select(n => $"{n.Address.Hostname}:{n.Address.Port}")
            .ToHashSet();

        var viewAddresses = seedNode.CurrentView.Members
            .Select(m => $"{m.Hostname}:{m.Port}")
            .ToHashSet();

        // All addresses in view should be known nodes
        Assert.Subset(viewAddresses, knownAddresses);
    }

    #endregion

    #region Safety Invariants (INV-010 to INV-013)

    [Fact]
    public void Inv010NoSplitBrainWithSingleNode()
    {
        _harness.CreateSeedNode();

        var result = _checker.CheckNoSplitBrain();

        Assert.True(result);
        Assert.False(_checker.HasViolations);
    }

    [Fact]
    public async Task Inv011NoSplitBrainWithTwoNodes()
    {
        var seedNode = _harness.InnerHarness.CreateSeedNode();
        var joiner = await _harness.InnerHarness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.InnerHarness.WaitForConvergenceAsync(expectedSize: 2, timeout: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        var result = _checker.CheckNoSplitBrain();

        Assert.True(result);
        Assert.False(_checker.HasViolations);
    }

    [Fact]
    public void Inv012ConfigurationIdMonotonicityWithSingleNode()
    {
        _harness.CreateSeedNode();

        var result = _checker.CheckConfigurationIdMonotonicity();

        Assert.True(result);
    }

    [Fact]
    public async Task Inv013ConfigurationIdMonotonicityAfterJoin()
    {
        var seedNode = _harness.InnerHarness.CreateSeedNode();
        var initialConfigId = seedNode.CurrentView.ConfigurationId;

        var joiner = await _harness.InnerHarness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.InnerHarness.WaitForConvergenceAsync(expectedSize: 2, timeout: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Config ID should have increased
        Assert.True(seedNode.CurrentView.ConfigurationId >= initialConfigId);

        var result = _checker.CheckConfigurationIdMonotonicity();
        Assert.True(result);
    }

    #endregion

    #region Membership Consistency (INV-012 specific tests)

    [Fact]
    public void Inv012A_MembershipConsistencyWithNoNodes()
    {
        var result = _checker.CheckMembershipConsistency();
        Assert.True(result);
    }

    [Fact]
    public void Inv012B_MembershipConsistencyWithSingleNode()
    {
        _harness.CreateSeedNode();

        var result = _checker.CheckMembershipConsistency();
        Assert.True(result);
    }

    [Fact]
    public async Task Inv012C_MembershipConsistencyWithConvergedCluster()
    {
        var seedNode = _harness.InnerHarness.CreateSeedNode();
        var joiner = await _harness.InnerHarness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.InnerHarness.WaitForConvergenceAsync(expectedSize: 2, timeout: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        var result = _checker.CheckMembershipConsistency();
        Assert.True(result);
    }

    #endregion

    #region Check All Invariants

    [Fact]
    public void InvCheckAllWithEmptyCluster()
    {
        var result = _checker.CheckAll();
        Assert.True(result);
        Assert.False(_checker.HasViolations);
    }

    [Fact]
    public void InvCheckAllWithSingleNode()
    {
        _harness.CreateSeedNode();

        var result = _checker.CheckAll();
        Assert.True(result);
        Assert.False(_checker.HasViolations);
    }

    [Fact]
    public async Task InvCheckAllWithTwoNodes()
    {
        var seedNode = _harness.InnerHarness.CreateSeedNode();
        var joiner = await _harness.InnerHarness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.InnerHarness.WaitForConvergenceAsync(expectedSize: 2, timeout: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        var result = _checker.CheckAll();
        Assert.True(result);
        Assert.False(_checker.HasViolations);
    }

    #endregion

    #region Violation Management

    [Fact]
    public void InvViolationsClearWorks()
    {
        // Initially no violations
        Assert.False(_checker.HasViolations);
        Assert.Empty(_checker.Violations);

        // Clear should not throw even when empty
        _checker.Clear();

        Assert.False(_checker.HasViolations);
        Assert.Empty(_checker.Violations);
    }

    [Fact]
    public void InvViolationsListIsImmutableCopy()
    {
        var violations1 = _checker.Violations;
        var violations2 = _checker.Violations;

        // Each call should return a new copy
        Assert.NotSame(violations1, violations2);
    }

    #endregion
}


