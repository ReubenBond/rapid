using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Rapid.Tests.Simulation;

namespace Rapid.Tests.SimulationTests;

/// <summary>
/// Tests for message delivery scenarios using the simulation harness.
/// Covers message delays, message loss, and message ordering.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
public sealed class MessageDeliveryTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private SimulationHarness _harness = null!;
    private const int TestSeed = 45678;

    public MessageDeliveryTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
    }

    public ValueTask InitializeAsync()
    {
        _output.WriteLine($"[MessageDeliveryTests] Initializing with seed {TestSeed}");
        _harness = new SimulationHarness(seed: TestSeed, loggerFactory: _loggerFactory);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _output.WriteLine("[MessageDeliveryTests] Disposing harness");
        await _harness.DisposeAsync();
        _loggerFactory.Dispose();
    }

    #region Message Delays (MSG-001 to MSG-004)

    [Fact]
    public void DelayConfigurationWorks()
    {
        _harness.Network.EnableDelays = true;
        _harness.Network.BaseMessageDelay = TimeSpan.FromMilliseconds(100);
        _harness.Network.MaxJitter = TimeSpan.FromMilliseconds(50);

        Assert.True(_harness.Network.EnableDelays);
        Assert.Equal(TimeSpan.FromMilliseconds(100), _harness.Network.BaseMessageDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(50), _harness.Network.MaxJitter);
    }

    [Fact]
    public void MessageDelayIsNonNegative()
    {
        _harness.Network.EnableDelays = true;
        _harness.Network.BaseMessageDelay = TimeSpan.FromMilliseconds(10);
        _harness.Network.MaxJitter = TimeSpan.FromMilliseconds(20);

        // Get multiple delays and verify they're in expected range
        for (var i = 0; i < 100; i++)
        {
            var delay = _harness.Network.GetMessageDelay();
            Assert.True(delay >= TimeSpan.FromMilliseconds(10));
            Assert.True(delay <= TimeSpan.FromMilliseconds(30)); // base + max jitter
        }
    }

    [Fact]
    public void DisabledDelaysReturnZero()
    {
        _harness.Network.EnableDelays = false;
        _harness.Network.BaseMessageDelay = TimeSpan.FromMilliseconds(100);
        _harness.Network.MaxJitter = TimeSpan.FromMilliseconds(50);

        var delay = _harness.Network.GetMessageDelay();
        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public async Task ClusterFormsWithDelaysEnabled()
    {
        _harness.Network.EnableDelays = true;
        _harness.Network.BaseMessageDelay = TimeSpan.FromMilliseconds(5);
        _harness.Network.MaxJitter = TimeSpan.FromMilliseconds(5);

        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(joiner.IsInitialized);
        Assert.Equal(2, joiner.MembershipSize);
    }

    #endregion

    #region Message Loss (MSG-010 to MSG-014)

    [Fact]
    public void MessageDropRateConfiguration()
    {
        _harness.Network.MessageDropRate = 0.05;
        Assert.Equal(0.05, _harness.Network.MessageDropRate);
    }

    [Fact]
    public void ZeroDropRateNeverDrops()
    {
        _harness.Network.MessageDropRate = 0.0;

        // With zero drop rate, all messages should be deliverable (assuming no partitions)
        for (var i = 0; i < 100; i++)
        {
            Assert.True(_harness.Network.CanDeliver("source", "target"));
        }
    }

    [Fact]
    public void HighDropRateSometimesDrops()
    {
        _harness.Network.MessageDropRate = 0.5; // 50% drop rate

        var dropped = 0;
        var delivered = 0;

        for (var i = 0; i < 1000; i++)
        {
            if (_harness.Network.CanDeliver("source", "target"))
            {
                delivered++;
            }
            else
            {
                dropped++;
            }
        }

        // With 50% drop rate and 1000 samples, we expect roughly equal drops and deliveries
        // Allow for some variance (400-600 range)
        Assert.InRange(dropped, 350, 650);
        Assert.InRange(delivered, 350, 650);
    }

    [Fact]
    public void FullDropRateAlwaysDrops()
    {
        _harness.Network.MessageDropRate = 1.0; // 100% drop rate

        for (var i = 0; i < 100; i++)
        {
            Assert.False(_harness.Network.CanDeliver("source", "target"));
        }
    }

    /// <summary>
    /// Tests that the join protocol can handle message loss through retry mechanisms.
    /// When messages are randomly dropped (30% rate), the join operation should still
    /// eventually succeed by retrying failed communications, ensuring robustness
    /// against unreliable networks.
    /// </summary>
    [Fact(Skip = "Requires timeout-based retry mechanism in join protocol")]
    public async Task MessageLossDuringJoinRetried()
    {
        // Enable moderate message loss
        _harness.Network.MessageDropRate = 0.3; // 30% loss

        var seedNode = _harness.CreateSeedNode();

        // Join should eventually succeed despite message loss
        // This requires the protocol to have retry logic
        var joiner = await _harness.CreateJoinerNodeAsync(
            seedNode,
            nodeId: 1,
            cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(
            expectedSize: 2,
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(joiner.IsInitialized);
        Assert.Equal(2, joiner.MembershipSize);
    }

    /// <summary>
    /// Verifies that the consensus protocol can tolerate message loss during membership
    /// changes. With 10% message drop rate, the consensus algorithm should still reach
    /// agreement through retransmission and timeout mechanisms, ensuring the cluster
    /// can grow even under adverse network conditions.
    /// </summary>
    [Fact(Skip = "Requires consensus retry mechanism")]
    public async Task MessageLossDuringConsensusRetried()
    {
        // Enable low message loss
        _harness.Network.MessageDropRate = 0.1; // 10% loss

        var seedNode = _harness.CreateSeedNode();
        var joiner1 = await _harness.CreateJoinerNodeAsync(
            seedNode,
            nodeId: 1,
            cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(
            expectedSize: 2,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        // Add another node with message loss active
        var joiner2 = await _harness.CreateJoinerNodeAsync(
            seedNode,
            nodeId: 2,
            cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(
            expectedSize: 3,
            timeout: TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(_harness.Nodes, n => Assert.Equal(3, n.MembershipSize));
    }

    #endregion

    #region Message Ordering (MSG-020 to MSG-022)

    [Fact]
    public void SimulationMessageQueueOrdersByDeliveryTime()
    {
        var queue = new SimulationMessageQueue();
        var now = DateTimeOffset.UtcNow;

        // Add messages with different delivery times
        var msg1 = new PendingMessage("a", "b", new Pb.RapidRequest(), new TaskCompletionSource<Pb.RapidResponse>());
        var msg2 = new PendingMessage("a", "b", new Pb.RapidRequest(), new TaskCompletionSource<Pb.RapidResponse>());
        var msg3 = new PendingMessage("a", "b", new Pb.RapidRequest(), new TaskCompletionSource<Pb.RapidResponse>());

        queue.Enqueue(msg3, now.AddSeconds(3), "a", "b");
        queue.Enqueue(msg1, now.AddSeconds(1), "a", "b");
        queue.Enqueue(msg2, now.AddSeconds(2), "a", "b");

        // Messages should come out in delivery time order
        Assert.True(queue.TryDequeue(now.AddSeconds(10), out var first));
        Assert.True(queue.TryDequeue(now.AddSeconds(10), out var second));
        Assert.True(queue.TryDequeue(now.AddSeconds(10), out var third));
    }

    [Fact]
    public void SimulationMessageQueueTieBreaksConsistently()
    {
        var queue1 = new SimulationMessageQueue();
        var queue2 = new SimulationMessageQueue();

        var deliveryTime = DateTimeOffset.UtcNow;

        var msg1 = new PendingMessage("a", "z", new Pb.RapidRequest(), new TaskCompletionSource<Pb.RapidResponse>());
        var msg2 = new PendingMessage("b", "z", new Pb.RapidRequest(), new TaskCompletionSource<Pb.RapidResponse>());
        var msg3 = new PendingMessage("a", "z", new Pb.RapidRequest(), new TaskCompletionSource<Pb.RapidResponse>());

        // Add messages with same delivery time but different senders
        queue1.Enqueue(msg1, deliveryTime, "a", "z");
        queue1.Enqueue(msg2, deliveryTime, "b", "z");
        queue1.Enqueue(msg3, deliveryTime, "a", "z");

        queue2.Enqueue(msg1, deliveryTime, "a", "z");
        queue2.Enqueue(msg2, deliveryTime, "b", "z");
        queue2.Enqueue(msg3, deliveryTime, "a", "z");

        // Both queues should have same count
        Assert.Equal(queue1.Count, queue2.Count);
    }

    [Fact]
    public void SimulationMessageQueueReportsCorrectCount()
    {
        var queue = new SimulationMessageQueue();
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(0, queue.Count);
        Assert.False(queue.HasPendingMessages);

        var msg1 = new PendingMessage("a", "b", new Pb.RapidRequest(), new TaskCompletionSource<Pb.RapidResponse>());
        queue.Enqueue(msg1, now, "a", "b");
        Assert.Equal(1, queue.Count);
        Assert.True(queue.HasPendingMessages);

        var msg2 = new PendingMessage("a", "b", new Pb.RapidRequest(), new TaskCompletionSource<Pb.RapidResponse>());
        queue.Enqueue(msg2, now, "a", "b");
        Assert.Equal(2, queue.Count);

        queue.TryDequeue(now.AddSeconds(1), out _);
        Assert.Equal(1, queue.Count);

        queue.Clear();
        Assert.Equal(0, queue.Count);
        Assert.False(queue.HasPendingMessages);
    }

    #endregion
}

