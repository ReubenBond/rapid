using Microsoft.Extensions.Logging;
using Rapid.Pb;

namespace Rapid.Logging;

internal sealed partial class FastPaxosLogger(ILogger<FastPaxos> logger)
{
    private readonly ILogger _logger = logger;

    // Logging helpers
    internal readonly struct LoggableEndpoints(IEnumerable<Endpoint> endpoints)
    {
        private readonly IEnumerable<Endpoint> _endpoints = endpoints;
        public override readonly string ToString() => string.Join(", ", _endpoints.Select(RapidUtils.Loggable));
    }

    internal readonly struct LoggableEndpoint(Endpoint endpoint)
    {
        private readonly Endpoint _endpoint = endpoint;
        public override readonly string ToString() => RapidUtils.Loggable(_endpoint);
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Configuration ID mismatch for proposal: current_config:{CurrentConfig}")]
    public partial void ConfigurationMismatch(long currentConfig);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Decided on a view change: {Proposal}")]
    public partial void DecidedViewChange(LoggableEndpoints proposal);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Fast round may not succeed for proposal")]
    public partial void FastRoundMayNotSucceed();

    [LoggerMessage(Level = LogLevel.Debug, Message = "FastPaxos initialized: myAddr={MyAddr}, configId={ConfigId}, membershipSize={MembershipSize}")]
    public partial void FastPaxosInitialized(LoggableEndpoint myAddr, long configId, long membershipSize);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Propose: broadcasting fast round proposal={Proposal}")]
    public partial void Propose(LoggableEndpoints proposal);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleFastRoundProposal: received from {Sender}, endpoints={Endpoints}, configId={ConfigId}")]
    public partial void HandleFastRoundProposalReceived(LoggableEndpoint sender, LoggableEndpoints endpoints, long configId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleFastRoundProposal: duplicate vote from {Sender}, ignoring")]
    public partial void DuplicateFastRoundVote(LoggableEndpoint sender);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleFastRoundProposal: already decided, ignoring")]
    public partial void FastRoundAlreadyDecided();

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleFastRoundProposal: vote count for proposal={Count}, total votes received={TotalVotes}, threshold={Threshold}, f={F}")]
    public partial void FastRoundVoteCount(int count, int totalVotes, long threshold, int f);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleFastRoundProposal: fast round succeeded")]
    public partial void FastRoundSucceeded();

    [LoggerMessage(Level = LogLevel.Information, Message = "Early fallback needed: {FailureCount} delivery failures (f={F}, need at least {Threshold} for Fast Paxos)")]
    public partial void EarlyFallbackNeeded(int failureCount, int f, long threshold);
}
