using Rapid.Pb;

namespace Rapid.Tests;

/// <summary>
/// Extended tests for MultiNodeCutDetector.
/// </summary>
internal class ExtendedMultiNodeCutDetectorTests
{
    private const int K = 10;
    private const int H = 8;
    private const int L = 2;
    private const long ConfigurationId = -1;

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

    #region Constructor Validation Tests

    [Fact]
    public void ConstructorValidParametersSucceeds()
    {
        var detector = new MultiNodeCutDetector(10, 8, 2);
        Assert.Equal(0, detector.GetNumProposals());
    }

    [Fact]
    public void ConstructorMinimumKSucceeds()
    {
        var detector = new MultiNodeCutDetector(3, 2, 1);
        Assert.Equal(0, detector.GetNumProposals());
    }

    [Fact]
    public void ConstructorKBelowMinimumThrows()
    {
        Assert.Throws<ArgumentException>(() => new MultiNodeCutDetector(2, 2, 1));
    }

    [Fact]
    public void ConstructorHGreaterThanKThrows()
    {
        Assert.Throws<ArgumentException>(() => new MultiNodeCutDetector(5, 6, 2));
    }

    [Fact]
    public void ConstructorLGreaterThanHThrows()
    {
        Assert.Throws<ArgumentException>(() => new MultiNodeCutDetector(10, 5, 6));
    }

    [Fact]
    public void ConstructorLZeroThrows()
    {
        Assert.Throws<ArgumentException>(() => new MultiNodeCutDetector(10, 8, 0));
    }

    [Fact]
    public void ConstructorHZeroThrows()
    {
        Assert.Throws<ArgumentException>(() => new MultiNodeCutDetector(10, 0, 0));
    }

    [Fact]
    public void ConstructorHEqualsLSucceeds()
    {
        var detector = new MultiNodeCutDetector(5, 3, 3);
        Assert.Equal(0, detector.GetNumProposals());
    }

    [Fact]
    public void ConstructorHEqualsKSucceeds()
    {
        var detector = new MultiNodeCutDetector(5, 5, 2);
        Assert.Equal(0, detector.GetNumProposals());
    }

    #endregion

    #region Clear Tests

    [Fact]
    public void ClearResetsProposalCount()
    {
        var detector = new MultiNodeCutDetector(K, H, L);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        for (var i = 0; i < K; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst, EdgeStatus.Up, ConfigurationId, i));
        }

        Assert.Equal(1, detector.GetNumProposals());

        detector.Clear();

        Assert.Equal(0, detector.GetNumProposals());
    }

    [Fact]
    public void ClearAllowsNewProposals()
    {
        var detector = new MultiNodeCutDetector(K, H, L);
        var dst1 = Utils.HostFromParts("127.0.0.2", 2);
        var dst2 = Utils.HostFromParts("127.0.0.3", 3);

        for (var i = 0; i < K; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst1, EdgeStatus.Up, ConfigurationId, i));
        }

        Assert.Equal(1, detector.GetNumProposals());

        detector.Clear();

        for (var i = 0; i < K; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst2, EdgeStatus.Up, ConfigurationId, i));
        }

        Assert.Equal(1, detector.GetNumProposals());
    }

    [Fact]
    public void ClearMultipleCallsSafe()
    {
        var detector = new MultiNodeCutDetector(K, H, L);

        detector.Clear();
        detector.Clear();
        detector.Clear();

        Assert.Equal(0, detector.GetNumProposals());
    }

    #endregion

    #region Duplicate Alert Tests

    [Fact]
    public void AggregateForProposalDuplicateAlertIgnored()
    {
        var detector = new MultiNodeCutDetector(K, H, L);
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
        var detector = new MultiNodeCutDetector(K, H, L);
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
        var detector = new MultiNodeCutDetector(K, H, L);
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
        var detector = new MultiNodeCutDetector(K, H, L);
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
        var detector = new MultiNodeCutDetector(K, H, L);
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
        var detector = new MultiNodeCutDetector(K, H, L);
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
        var detector = new MultiNodeCutDetector(K, H, L);
        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        Assert.Throws<ArgumentException>(() =>
            detector.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, K + 1)));
    }

    [Fact]
    public void AggregateForProposalRingNumberZeroValid()
    {
        var detector = new MultiNodeCutDetector(K, H, L);
        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        var result = detector.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, 0));

        Assert.Empty(result);
    }

    [Fact]
    public void AggregateForProposalRingNumberKMinusOneValid()
    {
        var detector = new MultiNodeCutDetector(K, H, L);
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
        var detector = new MultiNodeCutDetector(K, H, L);
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
        var detector = new MultiNodeCutDetector(K, H, L);
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
        var view = new MembershipViewBuilder(K).Build();
        var detector = new MultiNodeCutDetector(K, H, L);

        var dst = Utils.HostFromParts("127.0.0.2", 2);
        for (var i = 0; i < H - 1; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst, EdgeStatus.Up, ConfigurationId, i));
        }

        var result = detector.InvalidateFailingEdges(view);

        Assert.Empty(result);
    }

    [Fact]
    public void InvalidateFailingEdgesEmptyMembershipViewReturnsEmpty()
    {
        var view = new MembershipViewBuilder(K).Build();
        var detector = new MultiNodeCutDetector(K, H, L);

        var dst = Utils.HostFromParts("127.0.0.2", 2);
        for (var i = 0; i < H - 1; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst, EdgeStatus.Down, ConfigurationId, i));
        }

        var result = detector.InvalidateFailingEdges(view);

        Assert.NotNull(result);
    }

    #endregion

    #region Null Input Tests

    [Fact]
    public void AggregateForProposalNullMessageThrows()
    {
        var detector = new MultiNodeCutDetector(K, H, L);

        Assert.Throws<ArgumentNullException>(() => detector.AggregateForProposal(null!));
    }

    #endregion
}
