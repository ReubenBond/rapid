using Rapid.Pb;

namespace Rapid;

/// <summary>
/// A filter that outputs a view change proposal about a node only if:
/// - there are H reports about a node.
/// - there is no other node about which there are more than L but less than H reports.
/// 
/// The output of this filter gives us almost-everywhere agreement
/// </summary>
internal sealed class MultiNodeCutDetector
{
    private const int KMin = 3;
    private readonly int _k; // Number of observers per subject and vice versa
    private readonly int _h; // High watermark
    private readonly int _l; // Low watermark
    private readonly Lock _lock = new();
    private int _proposalCount;
    private int _updatesInProgress;
    private readonly Dictionary<Endpoint, Dictionary<int, Endpoint>> _reportsPerHost = [];
    private readonly HashSet<Endpoint> _proposal = [];
    private readonly HashSet<Endpoint> _preProposal = [];
    private bool _seenLinkDownEvents;

    public MultiNodeCutDetector(int k, int h, int l)
    {
        if (h > k || l > h || k < KMin || l <= 0 || h <= 0)
        {
            throw new ArgumentException($"Arguments do not satisfy K > H >= L >= 0: (K: {k}, H: {h}, L: {l})");
        }
        _k = k;
        _h = h;
        _l = l;
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
        if (ringNumber > _k)
            throw new ArgumentException($"Ring number {ringNumber} exceeds K={_k}");

        lock (_lock)
        {
            if (edgeStatus == EdgeStatus.Down)
            {
                _seenLinkDownEvents = true;
            }

            if (!_reportsPerHost.TryGetValue(linkDst, out var reportsForHost))
            {
                reportsForHost = new Dictionary<int, Endpoint>(_k);
                _reportsPerHost[linkDst] = reportsForHost;
            }

            if (!reportsForHost.TryAdd(ringNumber, linkSrc))
            {
                return [];  // duplicate announcement, ignore.
            }

            var numReportsForHost = reportsForHost.Count;

            if (numReportsForHost == _l)
            {
                _updatesInProgress++;
                _preProposal.Add(linkDst);
            }

            if (numReportsForHost == _h)
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
    /// <param name="view">MembershipView object required to find observer-subject relationships between failing nodes.</param>
    /// <returns>A list of endpoints representing a view change proposal.</returns>
    public List<Endpoint> InvalidateFailingEdges(MembershipView view)
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
                var observers = view.IsHostPresent(nodeInFlux)
                    ? view.GetObserversOf(nodeInFlux)          // For failing nodes
                    : view.GetExpectedObserversOf(nodeInFlux); // For joining nodes

                // Account for all edges between nodes that are past the L threshold
                var ringNumber = 0;
                foreach (var observer in observers)
                {
                    if (_proposal.Contains(observer) || _preProposal.Contains(observer))
                    {
                        // Implicit detection of edges between observer and nodeInFlux
                        var edgeStatus = view.IsHostPresent(nodeInFlux) ? EdgeStatus.Down : EdgeStatus.Up;
                        proposalsToReturn.AddRange(AggregateForProposal(observer, nodeInFlux, edgeStatus, ringNumber));
                    }
                    ringNumber++;
                }
            }

            return proposalsToReturn;
        }
    }

    /// <summary>
    /// Clears all view change reports being tracked. To be used right after a view change.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _reportsPerHost.Clear();
            _proposal.Clear();
            _updatesInProgress = 0;
            _proposalCount = 0;
            _preProposal.Clear();
            _seenLinkDownEvents = false;
        }
    }
}
