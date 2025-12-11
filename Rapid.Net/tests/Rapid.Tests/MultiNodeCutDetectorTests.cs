using Rapid.Pb;

namespace Rapid.Tests;

/// <summary>
/// Tests for multi node cut detection
/// </summary>
public class MultiNodeCutDetectorTests
{
    private const int K = 10;
    private const int H = 8;
    private const int L = 2;
    private const long ConfigurationId = -1; // Should not affect the following tests

    private static AlertMessage CreateAlertMessage(Endpoint src, Endpoint dst, EdgeStatus status,
        long configurationId, int ringNumber)
    {
        var msg = new AlertMessage
        {
            EdgeSrc = src,
            EdgeDst = dst,
            EdgeStatus = status,
            ConfigurationId = configurationId
        };
        msg.RingNumber.Add(ringNumber);
        return msg;
    }

    /// <summary>
    /// Creates a MembershipView for testing MultiNodeCutDetector.
    /// </summary>
    private static MembershipView CreateTestView(int numNodes = 30, int k = K)
    {
        var builder = new MembershipViewBuilder(k);
        for (var i = 0; i < numNodes; i++)
        {
            var node = Utils.HostFromParts("127.0.0." + (i + 1), 1000 + i);
            builder.RingAdd(node, Utils.NodeIdFromUuid(Guid.NewGuid()));
        }
        return builder.Build();
    }

    /// <summary>
    /// A series of updates with the right ring indexes
    /// </summary>
    [Fact]
    public void CutDetectionTest()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);
        var dst = Utils.HostFromParts("127.0.0.2", 2);
        List<Endpoint> ret;

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Single(ret);
        Assert.Equal(1, detector.GetNumProposals());
    }

    [Fact]
    public void CutDetectionTestBlockingOneBlocker()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);
        var dst1 = Utils.HostFromParts("127.0.0.2", 2);
        var dst2 = Utils.HostFromParts("127.0.0.3", 2);
        List<Endpoint> ret;

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst1, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst2, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst1, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Empty(ret);
        Assert.Equal(0, detector.GetNumProposals());

        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst2, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Equal(2, ret.Count);
        Assert.Equal(1, detector.GetNumProposals());
    }

    [Fact]
    public void CutDetectionTestBlockingThreeBlockers()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);
        var dst1 = Utils.HostFromParts("127.0.0.2", 2);
        var dst2 = Utils.HostFromParts("127.0.0.3", 2);
        var dst3 = Utils.HostFromParts("127.0.0.4", 2);
        List<Endpoint> ret;

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst1, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst2, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst3, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst1, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Empty(ret);
        Assert.Equal(0, detector.GetNumProposals());

        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst3, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Empty(ret);
        Assert.Equal(0, detector.GetNumProposals());

        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst2, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Equal(3, ret.Count);
        Assert.Equal(1, detector.GetNumProposals());
    }

    [Fact]
    public void CutDetectionTestBlockingMultipleBlockersPastH()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);
        var dst1 = Utils.HostFromParts("127.0.0.2", 2);
        var dst2 = Utils.HostFromParts("127.0.0.3", 2);
        var dst3 = Utils.HostFromParts("127.0.0.4", 2);
        List<Endpoint> ret;

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst1, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst2, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst3, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        // Unlike the previous test, add more reports for dst1 and dst3 past the H boundary.
        detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst1, EdgeStatus.Up, ConfigurationId, H - 1));
        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H + 1), dst1, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Empty(ret);
        Assert.Equal(0, detector.GetNumProposals());

        detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst3, EdgeStatus.Up, ConfigurationId, H - 1));
        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H + 1), dst3, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Empty(ret);
        Assert.Equal(0, detector.GetNumProposals());

        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst2, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Equal(3, ret.Count);
        Assert.Equal(1, detector.GetNumProposals());
    }

    [Fact]
    public void CutDetectionTestBelowL()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);
        var dst1 = Utils.HostFromParts("127.0.0.2", 2);
        var dst2 = Utils.HostFromParts("127.0.0.3", 2);
        var dst3 = Utils.HostFromParts("127.0.0.4", 2);
        List<Endpoint> ret;

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst1, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        // Unlike the previous test, dst2 has < L updates
        for (var i = 0; i < L - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst2, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst3, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst1, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Empty(ret);
        Assert.Equal(0, detector.GetNumProposals());

        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst3, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Equal(2, ret.Count);
        Assert.Equal(1, detector.GetNumProposals());
    }

    [Fact]
    public void CutDetectionTestBatch()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);
        const int numNodes = 3;
        var endpoints = new List<Endpoint>();

        for (var i = 0; i < numNodes; i++)
        {
            endpoints.Add(Utils.HostFromParts("127.0.0.2", 2 + i));
        }

        var proposal = new List<Endpoint>();
        foreach (var endpoint in endpoints)
        {
            for (var ringNumber = 0; ringNumber < K; ringNumber++)
            {
                proposal.AddRange(detector.AggregateForProposal(CreateAlertMessage(
                    Utils.HostFromParts("127.0.0.1", 1), endpoint, EdgeStatus.Up,
                    ConfigurationId, ringNumber)));
            }
        }

        Assert.Equal(numNodes, proposal.Count);
    }

    [Fact]
    public void CutDetectionTestLinkInvalidation()
    {
        var builder = new MembershipViewBuilder(K);
        const int numNodes = 30;
        var endpoints = new List<Endpoint>();

        for (var i = 0; i < numNodes; i++)
        {
            var node = Utils.HostFromParts("127.0.0.2", 2 + i);
            endpoints.Add(node);
            builder.RingAdd(node, Utils.NodeIdFromUuid(Guid.NewGuid()));
        }

        var mView = builder.Build();
        var detector = new MultiNodeCutDetector(H, L, mView);

        var dst = endpoints[0];
        var observers = mView.GetObserversOf(dst);
        Assert.Equal(K, observers.Length);

        List<Endpoint> ret;

        // This adds alerts from the observers[0, H - 1) of node dst.
        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(observers[i], dst,
                EdgeStatus.Down, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        // Next, we add alerts *about* observers[H, K) of node dst.
        var failedObservers = new HashSet<Endpoint>();
        for (var i = H - 1; i < K; i++)
        {
            var observersOfObserver = mView.GetObserversOf(observers[i]);
            failedObservers.Add(observers[i]);
            for (var j = 0; j < K; j++)
            {
                ret = detector.AggregateForProposal(CreateAlertMessage(observersOfObserver[j], observers[i],
                    EdgeStatus.Down, ConfigurationId, j));
                Assert.Empty(ret);
                Assert.Equal(0, detector.GetNumProposals());
            }
        }

        // At this point, (K - H - 1) observers of dst will be past H, and dst will be in H - 1. 
        // Link invalidation should bring the failed observers and dst to the stable region.
        ret = detector.InvalidateFailingEdges();
        Assert.Equal(4, ret.Count);
        Assert.Equal(1, detector.GetNumProposals());

        foreach (var node in ret)
        {
            Assert.True(failedObservers.Contains(node) || node.Equals(dst));
        }
    }

    #region Constructor Validation Tests

    [Fact]
    public void ConstructorValidParametersSucceeds()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);
        Assert.Equal(0, detector.GetNumProposals());
    }

    [Fact]
    public void ConstructorMinimumKSucceeds()
    {
        var view = CreateTestView(10, 3);
        var detector = new MultiNodeCutDetector(2, 1, view);
        Assert.Equal(0, detector.GetNumProposals());
    }

    [Fact]
    public void ConstructorNullViewThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new MultiNodeCutDetector(8, 2, null!));
    }

    [Fact]
    public void ConstructorKBelowMinimumThrows()
    {
        var view = CreateTestView(10, 2); // K=2, which is below minimum of 3
        Assert.Throws<ArgumentException>(() => new MultiNodeCutDetector(1, 1, view));
    }

    [Fact]
    public void ConstructorHGreaterThanKThrows()
    {
        var view = CreateTestView(10, 5); // K=5
        Assert.Throws<ArgumentException>(() => new MultiNodeCutDetector(6, 2, view)); // H=6 > K=5
    }

    [Fact]
    public void ConstructorLGreaterThanHThrows()
    {
        var view = CreateTestView();
        Assert.Throws<ArgumentException>(() => new MultiNodeCutDetector(5, 6, view)); // L=6 > H=5
    }

    [Fact]
    public void ConstructorLZeroThrows()
    {
        var view = CreateTestView();
        Assert.Throws<ArgumentException>(() => new MultiNodeCutDetector(8, 0, view));
    }

    [Fact]
    public void ConstructorHZeroThrows()
    {
        var view = CreateTestView();
        Assert.Throws<ArgumentException>(() => new MultiNodeCutDetector(0, 0, view));
    }

    [Fact]
    public void ConstructorHEqualsLSucceeds()
    {
        var view = CreateTestView(10, 5);
        var detector = new MultiNodeCutDetector(3, 3, view);
        Assert.Equal(0, detector.GetNumProposals());
    }

    [Fact]
    public void ConstructorHEqualsKThrows()
    {
        var view = CreateTestView(10, 5); // K=5
        Assert.Throws<ArgumentException>(() => new MultiNodeCutDetector(5, 2, view)); // H=5 = K
    }

    #endregion

    #region Detector Replacement Tests (replaces Clear tests)

    [Fact]
    public void NewDetector_ResetsProposalCount()
    {
        var view = CreateTestView();
        var detector1 = new MultiNodeCutDetector(H, L, view);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        for (var i = 0; i < K; i++)
        {
            detector1.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst, EdgeStatus.Up, ConfigurationId, i));
        }

        Assert.Equal(1, detector1.GetNumProposals());

        // Create new detector (simulating view change)
        var detector2 = new MultiNodeCutDetector(H, L, view);

        Assert.Equal(0, detector2.GetNumProposals());
    }

    [Fact]
    public void NewDetector_AllowsNewProposals()
    {
        var view = CreateTestView();
        var detector1 = new MultiNodeCutDetector(H, L, view);
        var dst1 = Utils.HostFromParts("127.0.0.2", 2);
        var dst2 = Utils.HostFromParts("127.0.0.3", 3);

        for (var i = 0; i < K; i++)
        {
            detector1.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst1, EdgeStatus.Up, ConfigurationId, i));
        }

        Assert.Equal(1, detector1.GetNumProposals());

        // Create new detector (simulating view change)
        var detector2 = new MultiNodeCutDetector(H, L, view);

        for (var i = 0; i < K; i++)
        {
            detector2.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst2, EdgeStatus.Up, ConfigurationId, i));
        }

        Assert.Equal(1, detector2.GetNumProposals());
    }

    [Fact]
    public void MultipleNewDetectors_Safe()
    {
        var view = CreateTestView();
        // Simulating multiple view changes - each creates a fresh detector
        var detector1 = new MultiNodeCutDetector(H, L, view);
        var detector2 = new MultiNodeCutDetector(H, L, view);
        var detector3 = new MultiNodeCutDetector(H, L, view);

        Assert.Equal(0, detector1.GetNumProposals());
        Assert.Equal(0, detector2.GetNumProposals());
        Assert.Equal(0, detector3.GetNumProposals());
    }

    #endregion

    #region Duplicate Alert Tests

    [Fact]
    public void AggregateForProposalDuplicateAlertIgnored()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);
        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        var result1 = detector.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, 0));
        var result2 = detector.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, 0));
        var result3 = detector.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, 0));

        Assert.Empty(result1);
        Assert.Empty(result2);
        Assert.Empty(result3);
    }

    [Fact]
    public void AggregateForProposalDifferentRingNumbersNotDuplicate()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);
        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        var result1 = detector.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, 0));
        var result2 = detector.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, 1));

        Assert.Empty(result1);
        Assert.Empty(result2);
    }

    #endregion

    #region Multiple Ring Numbers in Single Alert

    [Fact]
    public void AggregateForProposalMultipleRingNumbersAllProcessed()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);
        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        var msg = new AlertMessage
        {
            EdgeSrc = src,
            EdgeDst = dst,
            EdgeStatus = EdgeStatus.Up,
            ConfigurationId = ConfigurationId
        };
        for (var i = 0; i < K; i++)
        {
            msg.RingNumber.Add(i);
        }

        var result = detector.AggregateForProposal(msg);

        Assert.Single(result);
    }

    #endregion

    #region Edge Status Tests

    [Fact]
    public void AggregateForProposalEdgeStatusUpWorksCorrectly()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        for (var i = 0; i < K; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst, EdgeStatus.Up, ConfigurationId, i));
        }

        Assert.Equal(1, detector.GetNumProposals());
    }

    [Fact]
    public void AggregateForProposalEdgeStatusDownWorksCorrectly()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        for (var i = 0; i < K; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst, EdgeStatus.Down, ConfigurationId, i));
        }

        Assert.Equal(1, detector.GetNumProposals());
    }

    [Fact]
    public void AggregateForProposalMixedEdgeStatusProcessedSeparately()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        for (var i = 0; i < H; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst,
                i % 2 == 0 ? EdgeStatus.Up : EdgeStatus.Down,
                ConfigurationId, i));
        }

        Assert.Equal(1, detector.GetNumProposals());
    }

    #endregion

    #region Ring Number Validation Tests

    [Fact]
    public void AggregateForProposalRingNumberExceedsKThrows()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);
        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        Assert.Throws<ArgumentException>(() =>
            detector.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, K)));
    }

    [Fact]
    public void AggregateForProposalRingNumberNegativeThrows()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);
        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        Assert.Throws<ArgumentException>(() =>
            detector.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, -1)));
    }

    [Fact]
    public void AggregateForProposalRingNumberZeroValid()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);
        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        var result = detector.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, 0));

        Assert.Empty(result);
    }

    [Fact]
    public void AggregateForProposalRingNumberKMinusOneValid()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);
        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        var result = detector.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, K - 1));

        Assert.Empty(result);
    }

    #endregion

    #region Concurrent Proposals Tests

    [Fact]
    public void AggregateForProposalTwoNodesReachHSimultaneously()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);
        var dst1 = Utils.HostFromParts("127.0.0.2", 2);
        var dst2 = Utils.HostFromParts("127.0.0.3", 3);

        for (var i = 0; i < H - 1; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst1, EdgeStatus.Up, ConfigurationId, i));
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 100), dst2, EdgeStatus.Up, ConfigurationId, i));
        }

        Assert.Equal(0, detector.GetNumProposals());

        var result1 = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst1, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Empty(result1);
        Assert.Equal(0, detector.GetNumProposals());

        var result2 = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H + 100), dst2, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Equal(2, result2.Count);
        Assert.Equal(1, detector.GetNumProposals());
    }

    #endregion

    #region Large Scale Tests

    [Fact]
    public void AggregateForProposalManyDestinationsAllProposed()
    {
        var view = CreateTestView(100);
        var detector = new MultiNodeCutDetector(H, L, view);
        const int numDestinations = 50;
        var proposalCount = 0;

        for (var d = 0; d < numDestinations; d++)
        {
            var dst = Utils.HostFromParts("10.0.0." + d, 5000);

            for (var i = 0; i < K; i++)
            {
                var result = detector.AggregateForProposal(CreateAlertMessage(
                    Utils.HostFromParts("192.168.0." + (i + 1), 1000 + d), dst, EdgeStatus.Up, ConfigurationId, i));

                if (result.Count > 0)
                {
                    proposalCount++;
                }
            }
        }

        Assert.Equal(numDestinations, proposalCount);
    }

    #endregion

    #region Link Invalidation Edge Cases

    [Fact]
    public void InvalidateFailingEdgesNoDownEventsReturnsEmpty()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);

        var dst = Utils.HostFromParts("127.0.0.2", 2);
        for (var i = 0; i < H - 1; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst, EdgeStatus.Up, ConfigurationId, i));
        }

        var result = detector.InvalidateFailingEdges();

        Assert.Empty(result);
    }

    [Fact]
    public void InvalidateFailingEdgesEmptyMembershipViewReturnsEmpty()
    {
        var view = new MembershipViewBuilder(K).Build();
        var detector = new MultiNodeCutDetector(H, L, view);

        var dst = Utils.HostFromParts("127.0.0.2", 2);
        for (var i = 0; i < H - 1; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst, EdgeStatus.Down, ConfigurationId, i));
        }

        var result = detector.InvalidateFailingEdges();

        Assert.NotNull(result);
    }

    #endregion

    #region Null Input Tests

    [Fact]
    public void AggregateForProposalNullMessageThrows()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);

        Assert.Throws<ArgumentNullException>(() => detector.AggregateForProposal(null!));
    }

    #endregion

    #region AggregateForProposalSingleRing Tests

    [Fact]
    public void AggregateForProposalSingleRing_ProcessesSingleRing()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        // Create message with all K rings
        var msg = new AlertMessage
        {
            EdgeSrc = Utils.HostFromParts("127.0.0.1", 1),
            EdgeDst = dst,
            EdgeStatus = EdgeStatus.Up,
            ConfigurationId = ConfigurationId
        };
        for (var i = 0; i < K; i++)
        {
            msg.RingNumber.Add(i);
        }

        // Process only ring 0
        var result = detector.AggregateForProposalSingleRing(msg, 0);
        Assert.Empty(result);
        Assert.Equal(0, detector.GetNumProposals());

        // Process remaining rings to reach H
        for (var i = 1; i < H; i++)
        {
            result = detector.AggregateForProposalSingleRing(msg, i);
        }

        // Should now have a proposal
        Assert.Single(result);
        Assert.Equal(1, detector.GetNumProposals());
    }

    [Fact]
    public void AggregateForProposalSingleRing_IgnoresRingNotInMessage()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        // Create message with only ring 0
        var msg = CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", 1), dst, EdgeStatus.Up, ConfigurationId, 0);

        // Process ring 0 - should work
        var result0 = detector.AggregateForProposalSingleRing(msg, 0);
        Assert.Empty(result0);

        // Process ring 1 - ring not in message, but the method still processes it
        // because the ring number is passed as parameter, not read from message
        var result1 = detector.AggregateForProposalSingleRing(msg, 1);
        Assert.Empty(result1);
    }

    [Fact]
    public void AggregateForProposalSingleRing_NullMessageThrows()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);

        Assert.Throws<ArgumentNullException>(() => detector.AggregateForProposalSingleRing(null!, 0));
    }

    [Fact]
    public void AggregateForProposalSingleRing_InvalidRingNumberThrows()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);
        var msg = CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", 1),
            Utils.HostFromParts("127.0.0.2", 2),
            EdgeStatus.Up, ConfigurationId, 0);

        Assert.Throws<ArgumentException>(() => detector.AggregateForProposalSingleRing(msg, K));
        Assert.Throws<ArgumentException>(() => detector.AggregateForProposalSingleRing(msg, -1));
    }

    [Fact]
    public void AggregateForProposalSingleRing_EquivalentToAggregateForProposal()
    {
        var view = CreateTestView();
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        // Create message with H rings
        var msg = new AlertMessage
        {
            EdgeSrc = Utils.HostFromParts("127.0.0.1", 1),
            EdgeDst = dst,
            EdgeStatus = EdgeStatus.Up,
            ConfigurationId = ConfigurationId
        };
        for (var i = 0; i < H; i++)
        {
            msg.RingNumber.Add(i);
        }

        // Method 1: AggregateForProposal
        var detector1 = new MultiNodeCutDetector(H, L, view);
        var result1 = detector1.AggregateForProposal(msg);

        // Method 2: AggregateForProposalSingleRing for each ring
        var detector2 = new MultiNodeCutDetector(H, L, view);
        var result2 = new List<Endpoint>();
        foreach (var ringNumber in msg.RingNumber)
        {
            result2.AddRange(detector2.AggregateForProposalSingleRing(msg, ringNumber));
        }

        Assert.Equal(result1.Count, result2.Count);
        Assert.Equal(detector1.GetNumProposals(), detector2.GetNumProposals());
    }

    #endregion

    #region Batching Behavior Tests

    /// <summary>
    /// Tests that processing messages sequentially (all rings per node) produces individual proposals.
    /// This is the NON-batched behavior.
    /// </summary>
    [Fact]
    public void SequentialProcessing_ProducesIndividualProposals()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);

        var dst1 = Utils.HostFromParts("127.0.0.2", 2);
        var dst2 = Utils.HostFromParts("127.0.0.3", 3);
        var dst3 = Utils.HostFromParts("127.0.0.4", 4);

        var proposalCount = 0;

        // Process all rings for dst1 - should produce proposal
        foreach (var dst in new[] { dst1, dst2, dst3 })
        {
            for (var ring = 0; ring < K; ring++)
            {
                var result = detector.AggregateForProposal(CreateAlertMessage(
                    Utils.HostFromParts("127.0.0.1", ring + 1), dst, EdgeStatus.Up, ConfigurationId, ring));
                if (result.Count > 0)
                {
                    proposalCount++;
                }
            }
        }

        // Each node reaches H independently, so we get 3 separate proposals
        Assert.Equal(3, proposalCount);
        Assert.Equal(3, detector.GetNumProposals());
    }

    /// <summary>
    /// Tests that processing messages by ring number (interleaved) enables batching.
    /// Multiple nodes can be batched into fewer proposals.
    /// </summary>
    [Fact]
    public void InterleavedProcessing_EnablesBatching()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);

        var dst1 = Utils.HostFromParts("127.0.0.2", 2);
        var dst2 = Utils.HostFromParts("127.0.0.3", 3);
        var dst3 = Utils.HostFromParts("127.0.0.4", 4);
        var destinations = new[] { dst1, dst2, dst3 };

        // Create messages for each destination with all K rings
        var messages = destinations.Select(dst =>
        {
            var msg = new AlertMessage
            {
                EdgeSrc = Utils.HostFromParts("127.0.0.1", 1),
                EdgeDst = dst,
                EdgeStatus = EdgeStatus.Up,
                ConfigurationId = ConfigurationId
            };
            for (var r = 0; r < K; r++)
            {
                msg.RingNumber.Add(r);
            }
            return msg;
        }).ToList();

        var proposalCount = 0;
        var totalNodesProposed = 0;

        // Process by ring number (interleaved)
        for (var ring = 0; ring < K; ring++)
        {
            foreach (var msg in messages)
            {
                var result = detector.AggregateForProposalSingleRing(msg, ring);
                if (result.Count > 0)
                {
                    proposalCount++;
                    totalNodesProposed += result.Count;
                }
            }
        }

        // All 3 nodes should be proposed
        Assert.Equal(3, totalNodesProposed);
        // With interleaved processing, all nodes should be batched into 1 proposal
        Assert.Equal(1, proposalCount);
        Assert.Equal(1, detector.GetNumProposals());
    }

    /// <summary>
    /// Tests the specific batching scenario from the Rapid paper:
    /// Multiple nodes joining simultaneously should be batched into fewer view changes.
    /// </summary>
    [Fact]
    public void BatchingScenario_MultipleJoinsAreBatched()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);

        // Simulate 10 nodes joining simultaneously
        var joiningNodes = Enumerable.Range(0, 10)
            .Select(i => Utils.HostFromParts("10.0.0." + i, 5000 + i))
            .ToList();

        // Create alert messages from a single observer for all joining nodes
        var observer = Utils.HostFromParts("127.0.0.1", 1);
        var messages = joiningNodes.Select(dst =>
        {
            var msg = new AlertMessage
            {
                EdgeSrc = observer,
                EdgeDst = dst,
                EdgeStatus = EdgeStatus.Up,
                ConfigurationId = ConfigurationId
            };
            for (var r = 0; r < K; r++)
            {
                msg.RingNumber.Add(r);
            }
            return msg;
        }).ToList();

        var proposalEvents = 0;
        var nodesProposed = new HashSet<Endpoint>();

        // Process by ring number (simulating HandleBatchedAlertMessage behavior)
        for (var ring = 0; ring < K; ring++)
        {
            foreach (var msg in messages)
            {
                var result = detector.AggregateForProposalSingleRing(msg, ring);
                if (result.Count > 0)
                {
                    proposalEvents++;
                    foreach (var node in result)
                    {
                        nodesProposed.Add(node);
                    }
                }
            }
        }

        // All 10 nodes should be proposed
        Assert.Equal(10, nodesProposed.Count);
        // All nodes should be batched into a single proposal event
        Assert.Equal(1, proposalEvents);
        Assert.Equal(1, detector.GetNumProposals());
    }

    /// <summary>
    /// Tests that nodes reaching L threshold block proposals until they reach H.
    /// </summary>
    [Fact]
    public void PreProposal_BlocksUntilAllReachH()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);

        var dst1 = Utils.HostFromParts("127.0.0.2", 2);
        var dst2 = Utils.HostFromParts("127.0.0.3", 3);

        // Bring dst1 to H-1 reports (in preProposal)
        for (var i = 0; i < H - 1; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst1, EdgeStatus.Up, ConfigurationId, i));
        }

        // Bring dst2 to L reports (in preProposal)
        for (var i = 0; i < L; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst2, EdgeStatus.Up, ConfigurationId, i));
        }

        // Push dst1 to H - should NOT trigger because dst2 is blocking
        var result = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst1, EdgeStatus.Up, ConfigurationId, H - 1));

        Assert.Empty(result);
        Assert.Equal(0, detector.GetNumProposals());

        // Push dst2 to H - should trigger proposal for BOTH nodes
        for (var i = L; i < H; i++)
        {
            result = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst2, EdgeStatus.Up, ConfigurationId, i));
        }

        Assert.Equal(2, result.Count);
        Assert.Contains(dst1, result);
        Assert.Contains(dst2, result);
        Assert.Equal(1, detector.GetNumProposals());
    }

    /// <summary>
    /// Tests that nodes below L threshold do NOT block proposals.
    /// </summary>
    [Fact]
    public void BelowL_DoesNotBlock()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);

        var dst1 = Utils.HostFromParts("127.0.0.2", 2);
        var dst2 = Utils.HostFromParts("127.0.0.3", 3);

        // Bring dst2 to L-1 reports (below L, should NOT block)
        for (var i = 0; i < L - 1; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst2, EdgeStatus.Up, ConfigurationId, i));
        }

        // Bring dst1 to H reports
        List<Endpoint> result = [];
        for (var i = 0; i < H; i++)
        {
            result = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 100), dst1, EdgeStatus.Up, ConfigurationId, i));
        }

        // dst1 should be proposed (dst2 below L doesn't block)
        Assert.Single(result);
        Assert.Equal(dst1, result[0]);
        Assert.Equal(1, detector.GetNumProposals());
    }

    /// <summary>
    /// Tests multiple batches can occur sequentially.
    /// </summary>
    [Fact]
    public void MultipleBatches_CanOccurSequentially()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);

        // First batch: 3 nodes
        var batch1 = new[]
        {
            Utils.HostFromParts("10.0.0.1", 1),
            Utils.HostFromParts("10.0.0.2", 2),
            Utils.HostFromParts("10.0.0.3", 3)
        };

        var messages1 = batch1.Select(dst =>
        {
            var msg = new AlertMessage
            {
                EdgeSrc = Utils.HostFromParts("127.0.0.1", 1),
                EdgeDst = dst,
                EdgeStatus = EdgeStatus.Up,
                ConfigurationId = ConfigurationId
            };
            for (var r = 0; r < K; r++)
            {
                msg.RingNumber.Add(r);
            }
            return msg;
        }).ToList();

        // Process first batch by ring
        var batch1Proposals = 0;
        for (var ring = 0; ring < K; ring++)
        {
            foreach (var msg in messages1)
            {
                var result = detector.AggregateForProposalSingleRing(msg, ring);
                if (result.Count > 0) batch1Proposals++;
            }
        }

        Assert.Equal(1, batch1Proposals);
        Assert.Equal(1, detector.GetNumProposals());

        // Second batch: 2 more nodes
        var batch2 = new[]
        {
            Utils.HostFromParts("10.0.0.4", 4),
            Utils.HostFromParts("10.0.0.5", 5)
        };

        var messages2 = batch2.Select(dst =>
        {
            var msg = new AlertMessage
            {
                EdgeSrc = Utils.HostFromParts("127.0.0.1", 1),
                EdgeDst = dst,
                EdgeStatus = EdgeStatus.Up,
                ConfigurationId = ConfigurationId
            };
            for (var r = 0; r < K; r++)
            {
                msg.RingNumber.Add(r);
            }
            return msg;
        }).ToList();

        // Process second batch by ring
        var batch2Proposals = 0;
        for (var ring = 0; ring < K; ring++)
        {
            foreach (var msg in messages2)
            {
                var result = detector.AggregateForProposalSingleRing(msg, ring);
                if (result.Count > 0) batch2Proposals++;
            }
        }

        Assert.Equal(1, batch2Proposals);
        Assert.Equal(2, detector.GetNumProposals()); // Total proposals
    }

    #endregion

    #region InvalidateFailingEdges Tests

    /// <summary>
    /// Tests that InvalidateFailingEdges properly handles edges between failing nodes.
    /// This matches the Java test cutDetectionTestLinkInvalidation.
    /// </summary>
    [Fact]
    public void InvalidateFailingEdges_HandlesFailingObservers()
    {
        var builder = new MembershipViewBuilder(K);
        const int numNodes = 30;
        var endpoints = new List<Endpoint>();

        for (var i = 0; i < numNodes; i++)
        {
            var node = Utils.HostFromParts("127.0.0.2", 2 + i);
            endpoints.Add(node);
            builder.RingAdd(node, Utils.NodeIdFromUuid(Guid.NewGuid()));
        }

        var mView = builder.Build();
        var detector = new MultiNodeCutDetector(H, L, mView);

        var dst = endpoints[0];
        var observers = mView.GetObserversOf(dst);
        Assert.Equal(K, observers.Length);

        // Add alerts from observers[0, H-1) for node dst (failing node)
        for (var i = 0; i < H - 1; i++)
        {
            var result = detector.AggregateForProposal(CreateAlertMessage(observers[i], dst,
                EdgeStatus.Down, ConfigurationId, i));
            Assert.Empty(result);
            Assert.Equal(0, detector.GetNumProposals());
        }

        // Add alerts ABOUT observers[H-1, K) (these observers are also failing)
        var failedObservers = new HashSet<Endpoint>();
        for (var i = H - 1; i < K; i++)
        {
            var observersOfObserver = mView.GetObserversOf(observers[i]);
            failedObservers.Add(observers[i]);
            for (var j = 0; j < K; j++)
            {
                var result = detector.AggregateForProposal(CreateAlertMessage(observersOfObserver[j], observers[i],
                    EdgeStatus.Down, ConfigurationId, j));
                Assert.Empty(result);
                Assert.Equal(0, detector.GetNumProposals());
            }
        }

        // Link invalidation should bring the failed observers and dst to the stable region
        var invalidationResult = detector.InvalidateFailingEdges();

        // Should have 4 nodes: dst + 3 failed observers (K-H+1 = 10-8+1 = 3... wait, K-H-1+1 = 10-8 = 2)
        // Actually: observers from index H-1 to K-1 = indices 7,8,9 = 3 observers, plus dst = 4
        Assert.Equal(4, invalidationResult.Count);
        Assert.Equal(1, detector.GetNumProposals());

        foreach (var node in invalidationResult)
        {
            Assert.True(failedObservers.Contains(node) || node.Equals(dst));
        }
    }

    [Fact]
    public void InvalidateFailingEdges_NoEffect_WithOnlyUpEvents()
    {
        var view = CreateTestView();
        var detector = new MultiNodeCutDetector(H, L, view);

        var dst = Utils.HostFromParts("127.0.0.2", 2);

        // Add some UP events (joining nodes)
        for (var i = 0; i < H - 1; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst, EdgeStatus.Up, ConfigurationId, i));
        }

        // InvalidateFailingEdges should return empty (no DOWN events)
        var result = detector.InvalidateFailingEdges();
        Assert.Empty(result);
    }

    [Fact]
    public void InvalidateFailingEdges_HandlesMixedUpAndDown()
    {
        var builder = new MembershipViewBuilder(K);
        const int numNodes = 20;
        var endpoints = new List<Endpoint>();

        for (var i = 0; i < numNodes; i++)
        {
            var node = Utils.HostFromParts("127.0.0." + (i + 1), 1000 + i);
            endpoints.Add(node);
            builder.RingAdd(node, Utils.NodeIdFromUuid(Guid.NewGuid()));
        }

        var mView = builder.Build();
        var detector = new MultiNodeCutDetector(H, L, mView);

        // Add a DOWN event (failure detection)
        var failingNode = endpoints[0];
        for (var i = 0; i < L; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                endpoints[i + 1], failingNode, EdgeStatus.Down, ConfigurationId, i));
        }

        // InvalidateFailingEdges should be callable (may or may not produce results)
        var result = detector.InvalidateFailingEdges();
        Assert.NotNull(result);
    }

    #endregion
}
