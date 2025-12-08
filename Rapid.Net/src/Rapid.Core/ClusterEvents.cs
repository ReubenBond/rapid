namespace Rapid;

/// <summary>
/// Event types to subscribe from the cluster.
/// </summary>
public enum ClusterEvents
{
    /// <summary>When a node announces a proposal using the multi node cut detector.</summary>
    ViewChangeProposal,

    /// <summary>When a fast-paxos quorum of identical proposals were received.</summary>
    ViewChange,

    /// <summary>When a fast-paxos quorum of identical proposals is unavailable.</summary>
    ViewChangeOneStepFailed,

    /// <summary>When a node detects that it has been removed from the network.</summary>
    Kicked
}
