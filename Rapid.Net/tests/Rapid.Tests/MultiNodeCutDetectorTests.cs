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
}
