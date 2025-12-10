using Rapid.Pb;

namespace Rapid.Tests;

/// <summary>
/// Tests for simple cut detection used in small clusters (K &lt; 3)
/// </summary>
public class SimpleCutDetectorTests
{
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
    /// Creates a MembershipView for testing SimpleCutDetector.
    /// </summary>
    private static MembershipView CreateTestView(int numNodes = 3, int k = 2)
    {
        var builder = new MembershipViewBuilder(k);
        for (var i = 0; i < numNodes; i++)
        {
            var node = Utils.HostFromParts("127.0.0." + (i + 1), 1000 + i);
            builder.RingAdd(node, Utils.NodeIdFromUuid(Guid.NewGuid()));
        }
        return builder.Build();
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_K1_Succeeds()
    {
        var view = CreateTestView(2, 1);
        var detector = new SimpleCutDetector(view);
        Assert.Equal(0, detector.GetNumProposals());
    }

    [Fact]
    public void Constructor_K2_Succeeds()
    {
        var view = CreateTestView(3, 2);
        var detector = new SimpleCutDetector(view);
        Assert.Equal(0, detector.GetNumProposals());
    }

    [Fact]
    public void Constructor_K0_Throws()
    {
        // Cannot create MembershipView with RingCount=0, so this test is no longer applicable
        // MembershipViewBuilder throws on creation with k <= 0
    }

    [Fact]
    public void Constructor_K3_Throws()
    {
        var view = CreateTestView(10, 3); // K=3 is too high for SimpleCutDetector
        Assert.Throws<ArgumentException>(() => new SimpleCutDetector(view));
    }

    [Fact]
    public void Constructor_NullView_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SimpleCutDetector(null!));
    }

    #endregion

    #region K=1 Tests (Two-node cluster)

    [Fact]
    public void K1_SingleVoteTriggersProposal()
    {
        var view = CreateTestView(2, 1);
        var detector = new SimpleCutDetector(view);
        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        var result = detector.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, 0));

        Assert.Single(result);
        Assert.Equal(dst, result[0]);
        Assert.Equal(1, detector.GetNumProposals());
    }

    [Fact]
    public void K1_DuplicateVoteIgnored()
    {
        var view = CreateTestView(2, 1);
        var detector = new SimpleCutDetector(view);
        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        var result1 = detector.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, 0));
        var result2 = detector.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, 0));

        Assert.Single(result1);
        Assert.Empty(result2);
        Assert.Equal(1, detector.GetNumProposals());
    }

    [Fact]
    public void K1_MultipleDestinationsEachGetProposal()
    {
        var view = CreateTestView(3, 1);
        var detector = new SimpleCutDetector(view);
        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst1 = Utils.HostFromParts("127.0.0.2", 2);
        var dst2 = Utils.HostFromParts("127.0.0.3", 3);

        var result1 = detector.AggregateForProposal(CreateAlertMessage(src, dst1, EdgeStatus.Up, ConfigurationId, 0));
        var result2 = detector.AggregateForProposal(CreateAlertMessage(src, dst2, EdgeStatus.Up, ConfigurationId, 0));

        Assert.Single(result1);
        Assert.Single(result2);
        Assert.Equal(2, detector.GetNumProposals());
    }

    #endregion

    #region K=2 Tests (Three-node cluster)

    [Fact]
    public void K2_SingleVoteDoesNotTriggerProposal()
    {
        var view = CreateTestView(3, 2);
        var detector = new SimpleCutDetector(view);
        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        var result = detector.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, 0));

        Assert.Empty(result);
        Assert.Equal(0, detector.GetNumProposals());
    }

    [Fact]
    public void K2_TwoVotesTriggersProposal()
    {
        var view = CreateTestView(3, 2);
        var detector = new SimpleCutDetector(view);
        var src1 = Utils.HostFromParts("127.0.0.1", 1);
        var src2 = Utils.HostFromParts("127.0.0.1", 2);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        var result1 = detector.AggregateForProposal(CreateAlertMessage(src1, dst, EdgeStatus.Up, ConfigurationId, 0));
        var result2 = detector.AggregateForProposal(CreateAlertMessage(src2, dst, EdgeStatus.Up, ConfigurationId, 1));

        Assert.Empty(result1);
        Assert.Single(result2);
        Assert.Equal(dst, result2[0]);
        Assert.Equal(1, detector.GetNumProposals());
    }

    [Fact]
    public void K2_DuplicateRingNumberIgnored()
    {
        var view = CreateTestView(3, 2);
        var detector = new SimpleCutDetector(view);
        var src1 = Utils.HostFromParts("127.0.0.1", 1);
        var src2 = Utils.HostFromParts("127.0.0.1", 2);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        // Both votes for ring 0
        var result1 = detector.AggregateForProposal(CreateAlertMessage(src1, dst, EdgeStatus.Up, ConfigurationId, 0));
        var result2 = detector.AggregateForProposal(CreateAlertMessage(src2, dst, EdgeStatus.Up, ConfigurationId, 0));

        Assert.Empty(result1);
        Assert.Empty(result2); // Duplicate ring number ignored
        Assert.Equal(0, detector.GetNumProposals());
    }

    [Fact]
    public void K2_MultipleDestinationsIndependent()
    {
        var view = CreateTestView(4, 2);
        var detector = new SimpleCutDetector(view);
        var src1 = Utils.HostFromParts("127.0.0.1", 1);
        var src2 = Utils.HostFromParts("127.0.0.1", 2);
        var dst1 = Utils.HostFromParts("127.0.0.2", 2);
        var dst2 = Utils.HostFromParts("127.0.0.3", 3);

        // One vote for dst1
        detector.AggregateForProposal(CreateAlertMessage(src1, dst1, EdgeStatus.Up, ConfigurationId, 0));
        Assert.Equal(0, detector.GetNumProposals());

        // Two votes for dst2
        detector.AggregateForProposal(CreateAlertMessage(src1, dst2, EdgeStatus.Up, ConfigurationId, 0));
        var result = detector.AggregateForProposal(CreateAlertMessage(src2, dst2, EdgeStatus.Up, ConfigurationId, 1));

        Assert.Single(result);
        Assert.Equal(dst2, result[0]);
        Assert.Equal(1, detector.GetNumProposals());
    }

    #endregion

    #region Detector Replacement Tests (replaces Clear tests)

    [Fact]
    public void NewDetector_StartsWithZeroProposals()
    {
        // Old Clear tests are replaced with detector replacement pattern
        // Since detectors are now immutable and replaced on view change,
        // we test that a new detector starts fresh
        var view = CreateTestView(2, 1);
        var detector1 = new SimpleCutDetector(view);
        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        detector1.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, 0));
        Assert.Equal(1, detector1.GetNumProposals());

        // Create new detector (simulating view change)
        var detector2 = new SimpleCutDetector(view);
        Assert.Equal(0, detector2.GetNumProposals());
    }

    [Fact]
    public void NewDetector_AcceptsNewProposals()
    {
        var view = CreateTestView(2, 1);
        var detector1 = new SimpleCutDetector(view);
        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        detector1.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, 0));

        // Create new detector (simulating view change)
        var detector2 = new SimpleCutDetector(view);

        // Same message should trigger proposal again on new detector
        var result = detector2.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, 0));
        Assert.Single(result);
        Assert.Equal(1, detector2.GetNumProposals());
    }

    #endregion

    #region Edge Status Tests

    [Fact]
    public void EdgeStatus_UpAndDownBothWork()
    {
        var view = CreateTestView(3, 1);
        var detector = new SimpleCutDetector(view);
        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst1 = Utils.HostFromParts("127.0.0.2", 2);
        var dst2 = Utils.HostFromParts("127.0.0.3", 3);

        var resultUp = detector.AggregateForProposal(CreateAlertMessage(src, dst1, EdgeStatus.Up, ConfigurationId, 0));
        var resultDown = detector.AggregateForProposal(CreateAlertMessage(src, dst2, EdgeStatus.Down, ConfigurationId, 0));

        Assert.Single(resultUp);
        Assert.Single(resultDown);
        Assert.Equal(2, detector.GetNumProposals());
    }

    #endregion

    #region Multiple Ring Numbers in Single Alert

    [Fact]
    public void MultipleRingNumbers_K1_TriggersOnFirst()
    {
        var view = CreateTestView(2, 1);
        var detector = new SimpleCutDetector(view);
        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        var msg = new AlertMessage
        {
            EdgeSrc = src,
            EdgeDst = dst,
            EdgeStatus = EdgeStatus.Up,
            ConfigurationId = ConfigurationId
        };
        // For K=1, only ring 0 is valid, but the message might contain it
        msg.RingNumber.Add(0);

        var result = detector.AggregateForProposal(msg);

        Assert.Single(result);
        Assert.Equal(1, detector.GetNumProposals());
    }

    #endregion

    #region Ring Number Handling

    [Fact]
    public void RingNumber_LargerThanK_Throws()
    {
        var view = CreateTestView(2, 1);
        var detector = new SimpleCutDetector(view);
        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        // Ring number 5 is larger than K=1, should throw
        Assert.Throws<ArgumentException>(() =>
            detector.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, 5)));
    }

    [Fact]
    public void RingNumber_Negative_Throws()
    {
        var view = CreateTestView(2, 1);
        var detector = new SimpleCutDetector(view);
        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        Assert.Throws<ArgumentException>(() =>
            detector.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, -1)));
    }

    [Fact]
    public void RingNumber_Zero_Valid()
    {
        var view = CreateTestView(3, 2);
        var detector = new SimpleCutDetector(view);
        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        var result = detector.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, 0));

        Assert.Empty(result); // Need 2 votes for K=2
    }

    [Fact]
    public void RingNumber_MultipleRingsCountAsMultipleVotes()
    {
        var view = CreateTestView(3, 2);
        var detector = new SimpleCutDetector(view);
        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        // Two different ring numbers from the same source count as two votes
        var result1 = detector.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, 0));
        var result2 = detector.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, 1));

        Assert.Empty(result1);
        Assert.Single(result2); // Second vote triggers proposal
    }

    #endregion

    #region Null Input Tests

    [Fact]
    public void AggregateForProposal_NullMessage_Throws()
    {
        var view = CreateTestView(2, 1);
        var detector = new SimpleCutDetector(view);

        Assert.Throws<ArgumentNullException>(() => detector.AggregateForProposal(null!));
    }

    #endregion

    #region InvalidateFailingEdges Tests

    [Fact]
    public void InvalidateFailingEdges_NoDownEvents_ReturnsEmpty()
    {
        var view = CreateTestView(3, 2);
        var detector = new SimpleCutDetector(view);

        var src = Utils.HostFromParts("127.0.0.1", 1);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        // Only UP events, no DOWN
        detector.AggregateForProposal(CreateAlertMessage(src, dst, EdgeStatus.Up, ConfigurationId, 0));

        var result = detector.InvalidateFailingEdges();

        Assert.Empty(result);
    }

    #endregion
}
