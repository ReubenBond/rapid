using Rapid.Pb;

namespace Rapid;

/// <summary>
/// A simple cut detector for small clusters where the full multi-node cut detection
/// algorithm (with H/L watermarks) is not applicable (K &lt; 3).
/// 
/// This detector uses a simple voting threshold:
/// - For 2-node clusters: 1 vote required (the only observer)
/// - For 3-node clusters: 2 votes required (majority of 2 observers per subject)
/// 
/// Unlike <see cref="MultiNodeCutDetector"/>, this does not implement the
/// "unstable mode" waiting behavior - it simply triggers a proposal once
/// the required vote threshold is reached for a subject.
/// 
/// A new instance is created for each view, so no Clear() method is needed.
/// </summary>
internal sealed class SimpleCutDetector : ICutDetector
{
    private readonly int _requiredVotes;
    private readonly MembershipView _membershipView;
    private readonly Lock _lock = new();
    private int _proposalCount;
    private readonly Dictionary<Endpoint, Dictionary<int, Endpoint>> _reportsPerHost = [];
    private readonly HashSet<Endpoint> _pendingProposals = [];
    private readonly HashSet<Endpoint> _alreadyProposed = [];
    private bool _seenLinkDownEvents;

    /// <summary>
    /// Creates a SimpleCutDetector for small clusters.
    /// </summary>
    /// <param name="observersPerSubject">Number of observers per subject (1 or 2)</param>
    /// <param name="membershipView">The current membership view for observer lookups</param>
    /// <exception cref="ArgumentException">If observersPerSubject is not 1 or 2</exception>
    public SimpleCutDetector(int observersPerSubject, MembershipView membershipView)
    {
        if (observersPerSubject < 1 || observersPerSubject > 2)
        {
            throw new ArgumentException(
                $"SimpleCutDetector is for small clusters with 1-2 observers per subject, got {observersPerSubject}",
                nameof(observersPerSubject));
        }

        ArgumentNullException.ThrowIfNull(membershipView);

        _membershipView = membershipView;
        // For K=1: require 1 vote (the only observer)
        // For K=2: require 2 votes (both observers must agree)
        _requiredVotes = observersPerSubject;
    }

    public int GetNumProposals()
    {
        lock (_lock)
        {
            return _proposalCount;
        }
    }

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
        // Note: We don't validate ringNumber against _observersPerSubject here because
        // the ring numbers come from MembershipView which uses the configured K (e.g., 10)
        // while this detector may be using effectiveK (1 or 2) for small clusters.
        // We just count unique votes per ring number.

        lock (_lock)
        {
            if (edgeStatus == EdgeStatus.Down)
            {
                _seenLinkDownEvents = true;
            }

            if (!_reportsPerHost.TryGetValue(linkDst, out var reportsForHost))
            {
                reportsForHost = [];
                _reportsPerHost[linkDst] = reportsForHost;
            }

            if (!reportsForHost.TryAdd(ringNumber, linkSrc))
            {
                return []; // duplicate announcement, ignore.
            }

            var numReportsForHost = reportsForHost.Count;

            // Track nodes that have at least one report (for edge invalidation)
            if (numReportsForHost == 1 && _requiredVotes > 1)
            {
                _pendingProposals.Add(linkDst);
            }

            if (numReportsForHost >= _requiredVotes)
            {
                // Threshold reached - propose this node for view change (but only once)
                _pendingProposals.Remove(linkDst);
                if (_alreadyProposed.Add(linkDst))
                {
                    _proposalCount++;
                    return [linkDst];
                }
            }

            return [];
        }
    }

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
            var pendingCopy = new List<Endpoint>(_pendingProposals);

            foreach (var nodeInFlux in pendingCopy)
            {
                var observers = _membershipView.IsHostPresent(nodeInFlux)
                    ? _membershipView.GetObserversOf(nodeInFlux)          // For failing nodes
                    : _membershipView.GetExpectedObserversOf(nodeInFlux); // For joining nodes

                // Account for all edges between nodes that have pending reports
                var ringNumber = 0;
                foreach (var observer in observers)
                {
                    // Check if the observer itself has reports (meaning it may be failing)
                    if (_reportsPerHost.ContainsKey(observer))
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
