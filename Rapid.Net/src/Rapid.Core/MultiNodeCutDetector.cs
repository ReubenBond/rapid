using Rapid.Pb;

namespace Rapid;

/// <summary>
/// A filter that outputs a view change proposal about a node only if:
/// - there are H reports about a node.
/// - there is no other node about which there are more than L but less than H reports.
/// 
/// The output of this filter gives us almost-everywhere agreement.
/// 
/// This detector requires K >= 3 observers per subject to function correctly.
/// For smaller clusters, use <see cref="SimpleCutDetector"/> instead.
/// 
/// A new instance is created for each view, so no Clear() method is needed.
/// </summary>
internal sealed class MultiNodeCutDetector : ICutDetector
{
    private readonly int _observersPerSubject; // Number of observers per subject and vice versa
    private readonly int _highWaterMark; // High watermark
    private readonly int _lowWaterMark; // Low watermark
    private readonly MembershipView _membershipView;
    private readonly Lock _lock = new();
    private int _proposalCount;
    private int _updatesInProgress;
    private readonly Dictionary<Endpoint, Dictionary<int, Endpoint>> _reportsPerHost = [];
    private readonly HashSet<Endpoint> _proposal = [];
    private readonly HashSet<Endpoint> _preProposal = [];
    private bool _seenLinkDownEvents;

    /// <summary>
    /// Creates a MultiNodeCutDetector for larger clusters.
    /// </summary>
    /// <param name="observersPerSubject">Number of observers per subject (K, must be at least 3)</param>
    /// <param name="highWaterMark">High watermark threshold (H)</param>
    /// <param name="lowWaterMark">Low watermark threshold (L)</param>
    /// <param name="membershipView">The current membership view for observer lookups</param>
    /// <exception cref="ArgumentException">If constraints K greater than H, H at least L, L at least 1 are not satisfied, or K less than 3</exception>
    public MultiNodeCutDetector(int observersPerSubject, int highWaterMark, int lowWaterMark, MembershipView membershipView)
    {
        // Multi-node cut detection requires K >= 3 for proper H/L watermark behavior
        // For K < 3, use SimpleCutDetector instead
        if (observersPerSubject < 3)
        {
            throw new ArgumentException(
                $"MultiNodeCutDetector requires at least 3 observers per subject, got {observersPerSubject}. Use SimpleCutDetector for smaller clusters.",
                nameof(observersPerSubject));
        }

        // Constraints: K > H >= L >= 1
        if (highWaterMark < 1 || lowWaterMark < 1 ||
            highWaterMark >= observersPerSubject || lowWaterMark > highWaterMark)
        {
            throw new ArgumentException($"Arguments do not satisfy K > H >= L >= 1: (K: {observersPerSubject}, H: {highWaterMark}, L: {lowWaterMark})");
        }

        ArgumentNullException.ThrowIfNull(membershipView);

        _observersPerSubject = observersPerSubject;
        _highWaterMark = highWaterMark;
        _lowWaterMark = lowWaterMark;
        _membershipView = membershipView;
    }

    public int GetNumProposals()
    {
        lock (_lock)
        {
            return _proposalCount;
        }
    }

    /// <summary>
    /// Apply a AlertMessage against the cut detector. When an update moves a host
    /// past the H threshold of reports, and no other host has between H and L reports, the
    /// method returns a view change proposal.
    /// </summary>
    /// <param name="msg">A AlertMessage to apply against the filter</param>
    /// <returns>A list of endpoints about which a view change has been recorded. Empty list if there is no proposal.</returns>
    public List<Endpoint> AggregateForProposal(AlertMessage msg)
    {
        ArgumentNullException.ThrowIfNull(msg);

        var proposals = new List<Endpoint>();
        foreach (var ringNumber in msg.RingNumber)
        {
            proposals.AddRange(AggregateForProposal(msg.EdgeSrc, msg.EdgeDst, msg.EdgeStatus, ringNumber));
        }
        return proposals;
    }

    private List<Endpoint> AggregateForProposal(Endpoint linkSrc, Endpoint linkDst,
                                                EdgeStatus edgeStatus, int ringNumber)
    {
        if (ringNumber >= _observersPerSubject)
            throw new ArgumentException($"Ring number {ringNumber} exceeds K={_observersPerSubject}");

        lock (_lock)
        {
            if (edgeStatus == EdgeStatus.Down)
            {
                _seenLinkDownEvents = true;
            }

            if (!_reportsPerHost.TryGetValue(linkDst, out var reportsForHost))
            {
                reportsForHost = new Dictionary<int, Endpoint>(_observersPerSubject);
                _reportsPerHost[linkDst] = reportsForHost;
            }

            if (!reportsForHost.TryAdd(ringNumber, linkSrc))
            {
                return []; // duplicate announcement, ignore.
            }

            var numReportsForHost = reportsForHost.Count;

            if (numReportsForHost == _lowWaterMark)
            {
                _updatesInProgress++;
                _preProposal.Add(linkDst);
            }

            if (numReportsForHost == _highWaterMark)
            {
                // Enough reports about linkDst have been received that it is safe to act upon,
                // provided there are no other nodes with L < #reports < H.
                _preProposal.Remove(linkDst);
                _proposal.Add(linkDst);
                _updatesInProgress--;

                if (_updatesInProgress == 0)
                {
                    // No outstanding updates, so all nodes that have crossed the H threshold of reports are
                    // now part of a single proposal.
                    _proposalCount++;
                    var ret = new List<Endpoint>(_proposal);
                    _proposal.Clear();
                    return ret;
                }
            }

            return [];
        }
    }

    /// <summary>
    /// Invalidates edges between nodes that are failing or have failed. This step may be skipped safely
    /// when there are no failing nodes.
    /// </summary>
    /// <returns>A list of endpoints representing a view change proposal.</returns>
    public List<Endpoint> InvalidateFailingEdges()
    {
        lock (_lock)
        {
            // Link invalidation is only required when we have failing nodes
            if (!_seenLinkDownEvents)
            {
                return [];
            }

            var proposalsToReturn = new List<Endpoint>();
            var preProposalCopy = new List<Endpoint>(_preProposal);

            foreach (var nodeInFlux in preProposalCopy)
            {
                var observers = _membershipView.IsHostPresent(nodeInFlux)
                    ? _membershipView.GetObserversOf(nodeInFlux)          // For failing nodes
                    : _membershipView.GetExpectedObserversOf(nodeInFlux); // For joining nodes

                // Account for all edges between nodes that are past the L threshold
                var ringNumber = 0;
                foreach (var observer in observers)
                {
                    if (_proposal.Contains(observer) || _preProposal.Contains(observer))
                    {
                        // Implicit detection of edges between observer and nodeInFlux
                        var edgeStatus = _membershipView.IsHostPresent(nodeInFlux) ? EdgeStatus.Down : EdgeStatus.Up;
                        proposalsToReturn.AddRange(AggregateForProposal(observer, nodeInFlux, edgeStatus, ringNumber));
                    }
                    ringNumber++;
                }
            }

            return proposalsToReturn;
        }
    }
}
