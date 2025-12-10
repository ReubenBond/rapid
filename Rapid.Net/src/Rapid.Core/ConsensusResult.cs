using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Base type for consensus round results.
/// Use pattern matching to handle the different outcomes.
/// </summary>
internal abstract record ConsensusResult
{
    /// <summary>
    /// Consensus succeeded with a decided value.
    /// </summary>
    /// <param name="Value">The decided list of endpoints.</param>
    public sealed record Decided(List<Endpoint> Value) : ConsensusResult;

    /// <summary>
    /// Fast round failed due to vote split (multiple proposals, none reached threshold).
    /// Classic Paxos rounds should be attempted.
    /// </summary>
    public sealed record VoteSplit : ConsensusResult
    {
        /// <summary>Singleton instance.</summary>
        public static VoteSplit Instance { get; } = new();
        private VoteSplit() { }
    }

    /// <summary>
    /// Fast round failed due to too many delivery failures.
    /// Classic Paxos rounds should be attempted.
    /// </summary>
    public sealed record DeliveryFailure : ConsensusResult
    {
        /// <summary>Singleton instance.</summary>
        public static DeliveryFailure Instance { get; } = new();
        private DeliveryFailure() { }
    }

    /// <summary>
    /// Consensus round timed out without reaching a decision.
    /// Another round should be attempted.
    /// </summary>
    public sealed record Timeout : ConsensusResult
    {
        /// <summary>Singleton instance.</summary>
        public static Timeout Instance { get; } = new();
        private Timeout() { }
    }

    /// <summary>
    /// Consensus was cancelled (e.g., due to shutdown).
    /// </summary>
    public sealed record Cancelled : ConsensusResult
    {
        /// <summary>Singleton instance.</summary>
        public static Cancelled Instance { get; } = new();
        private Cancelled() { }
    }
}
