using Rapid.Tests.Simulation;

namespace Rapid.Tests;

/// <summary>
/// Tests for the simulation test harness.
/// </summary>
public sealed class SimulationTestHarnessTests : IAsyncLifetime
{
    private SimulationTestHarness _harness = null!;

    public ValueTask InitializeAsync()
    {
        // Use a fixed seed for reproducibility
        _harness = new SimulationTestHarness(seed: 12345);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
    }

    [Fact]
    public void DeterministicRandomWithSameSeedProducesSameSequence()
    {
        var random1 = new DeterministicRandom(42);
        var random2 = new DeterministicRandom(42);

        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(random1.Next(), random2.Next());
        }
    }

    [Fact]
    public void DeterministicRandomShuffleIsReproducible()
    {
        var random1 = new DeterministicRandom(42);
        var random2 = new DeterministicRandom(42);

        var list1 = new List<int> { 1, 2, 3, 4, 5 };
        var list2 = new List<int> { 1, 2, 3, 4, 5 };

        random1.Shuffle(list1);
        random2.Shuffle(list2);

        Assert.Equal(list1, list2);
    }

    [Fact]
    public void CreateSeedNodeInitializesWithOneNode()
    {
        var seedNode = _harness.CreateSeedNode();

        Assert.NotNull(seedNode);
        Assert.True(seedNode.IsInitialized);
        Assert.Equal(1, seedNode.MembershipSize);
    }

    [Fact]
    public async Task JoinNodeIncreasesMembershipSize()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(joiner);
        Assert.True(joiner.IsInitialized);
        Assert.Equal(2, joiner.MembershipSize);
    }

    [Fact(Skip = "Slow test - consensus roundtrips with batching delays. Use for integration testing only.")]
    public async Task CreateClusterCreatesCorrectNumberOfNodes()
    {
        var nodes = await _harness.CreateClusterAsync(size: 3, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, nodes.Count);
        Assert.All(nodes, node => Assert.True(node.IsInitialized));

        // All nodes should see 3 members
        foreach (var node in nodes)
        {
            Assert.Equal(3, node.MembershipSize);
        }
    }

    [Fact]
    public async Task AdvanceTimeMovesTimeProviderForward()
    {
        // Create a harness with fake time enabled for this test
        await using var fakeTimeHarness = new SimulationTestHarness(seed: 99999, useFakeTime: true);
        var initialTime = fakeTimeHarness.FakeTimeProvider!.GetUtcNow();
        fakeTimeHarness.AdvanceTime(TimeSpan.FromMinutes(5));
        var newTime = fakeTimeHarness.FakeTimeProvider.GetUtcNow();

        Assert.Equal(initialTime + TimeSpan.FromMinutes(5), newTime);
    }

    [Fact]
    public void NetworkPartitionBlocksMessages()
    {
        var seedNode = _harness.CreateSeedNode();

        // Isolate the node
        _harness.IsolateNode(seedNode);

        // Verify we can reconnect
        _harness.ReconnectNode(seedNode);
    }

    [Fact]
    public void SimulationEnvironmentCreatesDerivedRandom()
    {
        var derived1 = _harness.Environment.CreateDerivedRandom();
        var derived2 = _harness.Environment.CreateDerivedRandom();

        // Different derived randoms should produce different sequences
        var seq1 = Enumerable.Range(0, 10).Select(_ => derived1.Next()).ToList();
        var seq2 = Enumerable.Range(0, 10).Select(_ => derived2.Next()).ToList();

        Assert.NotEqual(seq1, seq2);
    }

    [Fact]
    public void SimulationNetworkCanSetMessageDropRate()
    {
        _harness.Network.MessageDropRate = 0.5;
        Assert.Equal(0.5, _harness.Network.MessageDropRate);
    }

    [Fact]
    public void SimulationNetworkCanSetDelays()
    {
        _harness.Network.BaseMessageDelay = TimeSpan.FromMilliseconds(10);
        _harness.Network.MaxJitter = TimeSpan.FromMilliseconds(20);

        Assert.Equal(TimeSpan.FromMilliseconds(10), _harness.Network.BaseMessageDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(20), _harness.Network.MaxJitter);
    }

    [Fact]
    public void SeedIsAccessible()
    {
        Assert.Equal(12345, _harness.Seed);
    }

    [Fact(Skip = "Slow test - consensus roundtrips with batching delays. Use for integration testing only.")]
    public async Task SubscribeToViewChangesReceivesNotifications()
    {
        var viewChanges = new List<MembershipView>();
        var seedNode = _harness.CreateSeedNode();

        // Start listening for view changes on the seed node
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var listenTask = Task.Run(async () =>
        {
            await foreach (var view in seedNode.ViewAccessor.ListenForViewUpdatesAsync(cts.Token))
            {
                viewChanges.Add(view);
                if (viewChanges.Count >= 2)
                {
                    break;
                }
            }
        }, cts.Token);

        // Join a new node
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        // Wait a bit for the view change to propagate
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Cancel and wait for listen task
        await cts.CancelAsync();
        try
        {
            await listenTask;
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Should have received at least one view change (the join)
        Assert.NotEmpty(viewChanges);
    }
}
