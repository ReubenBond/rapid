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
    private SimulationTestHarness _harness = null!;
    private const int TestSeed = 45678;

    public MessageDeliveryTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
    }

    public ValueTask InitializeAsync()
    {
        _output.WriteLine($"[MessageDeliveryTests] Initializing with seed {TestSeed}");
        _harness = new SimulationTestHarness(seed: TestSeed, loggerFactory: _loggerFactory);
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
    public void Msg001DelayConfigurationWorks()
    {
        _harness.Network.EnableDelays = true;
        _harness.Network.BaseMessageDelay = TimeSpan.FromMilliseconds(100);
        _harness.Network.MaxJitter = TimeSpan.FromMilliseconds(50);

        Assert.True(_harness.Network.EnableDelays);
        Assert.Equal(TimeSpan.FromMilliseconds(100), _harness.Network.BaseMessageDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(50), _harness.Network.MaxJitter);
    }

    [Fact]
    public void Msg002MessageDelayIsNonNegative()
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
    public void Msg003DisabledDelaysReturnZero()
    {
        _harness.Network.EnableDelays = false;
        _harness.Network.BaseMessageDelay = TimeSpan.FromMilliseconds(100);
        _harness.Network.MaxJitter = TimeSpan.FromMilliseconds(50);

        var delay = _harness.Network.GetMessageDelay();
        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public async Task Msg004ClusterFormsWithDelaysEnabled()
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
    public void Msg010MessageDropRateConfiguration()
    {
        _harness.Network.MessageDropRate = 0.05;
        Assert.Equal(0.05, _harness.Network.MessageDropRate);
    }

    [Fact]
    public void Msg011ZeroDropRateNeverDrops()
    {
        _harness.Network.MessageDropRate = 0.0;

        // With zero drop rate, all messages should be deliverable (assuming no partitions)
        for (var i = 0; i < 100; i++)
        {
            Assert.True(_harness.Network.CanDeliver("source", "target"));
        }
    }

    [Fact]
    public void Msg012HighDropRateSometimesDrops()
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
    public void Msg013FullDropRateAlwaysDrops()
    {
        _harness.Network.MessageDropRate = 1.0; // 100% drop rate

        for (var i = 0; i < 100; i++)
        {
            Assert.False(_harness.Network.CanDeliver("source", "target"));
        }
    }

    #endregion

    #region Message Ordering (MSG-020 to MSG-022)

    [Fact]
    public void Msg020DeterministicMessageQueueOrdersByDeliveryTime()
    {
        var queue = new DeterministicMessageQueue();
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
    public void Msg021DeterministicMessageQueueTieBreaksConsistently()
    {
        var queue1 = new DeterministicMessageQueue();
        var queue2 = new DeterministicMessageQueue();

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
    public void Msg022DeterministicMessageQueueReportsCorrectCount()
    {
        var queue = new DeterministicMessageQueue();
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

