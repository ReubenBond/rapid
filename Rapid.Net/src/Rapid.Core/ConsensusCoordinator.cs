using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rapid.Messaging;
using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Coordinates consensus for a single configuration change decision.
/// Manages the progression from fast round (round 1) through multiple
/// classic Paxos rounds (rounds 2, 3, ...) until a decision is reached.
/// 
/// This class creates and coordinates FastPaxos (for round 1) and Paxos
/// (for classic rounds 2, 3, ...) instances. It runs an async loop that
/// continues until either:
/// - A decision is reached (from FastPaxos or Paxos)
/// - The operation is cancelled (e.g., system shutdown)
/// </summary>
internal sealed partial class ConsensusCoordinator : IAsyncDisposable
{
    private readonly ILogger<ConsensusCoordinator> _coordinatorLogger;
    private readonly ILogger<FastPaxos> _fastPaxosLogger;
    private readonly ILogger<Paxos> _paxosLogger;
    private readonly double _jitterRate;
    private readonly Endpoint _myAddr;
    private readonly long _configurationId;
    private readonly int _membershipSize;
    private readonly IMessagingClient _client;
    private readonly IBroadcaster _broadcaster;
    private readonly RapidProtocolOptions _options;
    private readonly SharedResources _sharedResources;

    // The FastPaxos instance for round 1
    // Created in constructor so it can receive votes before Propose() is called
    private readonly FastPaxos _fastPaxos;

    // The Paxos instance for classic rounds (2, 3, ...)
    // Also holds acceptor state shared across all rounds
    // Created in constructor so it can receive messages before Propose() is called
    private readonly Paxos _paxos;

    // Synchronization
    private readonly Lock _lock = new();
    private Task? _consensusLoopTask;
    private int _disposed;

    // Decision
    private readonly TaskCompletionSource<List<Endpoint>> _onDecidedTcs = new();

    /// <summary>
    /// Task that completes when consensus is reached.
    /// </summary>
    public Task<List<Endpoint>> Decided => _onDecidedTcs.Task;

    // Logging helpers
    private readonly struct LoggableEndpoints(IEnumerable<Endpoint> endpoints)
    {
        private readonly IEnumerable<Endpoint> _endpoints = endpoints;
        public override readonly string ToString() => string.Join(", ", _endpoints.Select(RapidUtils.Loggable));
    }

    private readonly struct LoggableEndpoint(Endpoint endpoint)
    {
        private readonly Endpoint _endpoint = endpoint;
        public override readonly string ToString() => RapidUtils.Loggable(_endpoint);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "ConsensusCoordinator initialized: myAddr={MyAddr}, configId={ConfigId}, membershipSize={MembershipSize}")]
    private static partial void LogInitialized(ILogger logger, LoggableEndpoint MyAddr, long ConfigId, int MembershipSize);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Propose: starting consensus loop with proposal={Proposal}")]
    private static partial void LogPropose(ILogger logger, LoggableEndpoints Proposal);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Starting fast round (round 1)")]
    private static partial void LogStartingFastRound(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fast round decided: {Decision}")]
    private static partial void LogFastRoundDecided(ILogger logger, LoggableEndpoints Decision);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fast round timed out or cancelled after {Timeout}, falling back to classic Paxos")]
    private static partial void LogFastRoundTimeout(ILogger logger, TimeSpan Timeout);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fast round failed early, falling back to classic Paxos")]
    private static partial void LogFastRoundFailedEarly(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Classic round {Round} timed out after {Timeout}")]
    private static partial void LogClassicRoundTimeout(ILogger logger, int Round, TimeSpan Timeout);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Starting classic round {Round} with delay {Delay}")]
    private static partial void LogStartingClassicRound(ILogger logger, int Round, TimeSpan Delay);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Classic round {Round} decided: {Decision}")]
    private static partial void LogClassicRoundDecided(ILogger logger, int Round, LoggableEndpoints Decision);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Consensus loop cancelled")]
    private static partial void LogConsensusCancelled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consensus failed: exhausted all {MaxRounds} rounds without reaching decision")]
    private static partial void LogConsensusExhausted(ILogger logger, int MaxRounds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleMessages: received {MessageType}")]
    private static partial void LogHandleMessages(ILogger logger, RapidRequest.ContentOneofCase MessageType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dispose: cleaning up ConsensusCoordinator resources")]
    private static partial void LogDispose(ILogger logger);

    public ConsensusCoordinator(
        Endpoint myAddr,
        long configurationId,
        int membershipSize,
        IMessagingClient client,
        IBroadcaster broadcaster,
        IOptions<RapidProtocolOptions> options,
        SharedResources sharedResources,
        ILogger<ConsensusCoordinator> logger,
        ILogger<FastPaxos> fastPaxosLogger,
        ILogger<Paxos> paxosLogger)
    {
        _myAddr = myAddr;
        _configurationId = configurationId;
        _membershipSize = membershipSize;
        _client = client;
        _broadcaster = broadcaster;
        _options = options.Value;
        _sharedResources = sharedResources;
        _coordinatorLogger = logger;
        _fastPaxosLogger = fastPaxosLogger;
        _paxosLogger = paxosLogger;

        // The rate of a random expovariate variable, used to determine jitter
        _jitterRate = 1 / (double)membershipSize;

        // Create FastPaxos and Paxos instances up front so they can receive votes
        // before this node has locally decided to propose
        _fastPaxos = new FastPaxos(
            myAddr,
            configurationId,
            membershipSize,
            broadcaster,
            fastPaxosLogger);

        _paxos = new Paxos(
            myAddr,
            configurationId,
            membershipSize,
            client,
            broadcaster,
            paxosLogger);

        LogInitialized(_coordinatorLogger, new LoggableEndpoint(myAddr), configurationId, membershipSize);
    }

    /// <summary>
    /// Propose a value for consensus, starting the consensus loop.
    /// </summary>
    public void Propose(List<Endpoint> proposal, CancellationToken cancellationToken = default)
    {
        LogPropose(_coordinatorLogger, new LoggableEndpoints(proposal));

        // Register our fast round vote in the acceptor state
        _paxos.RegisterFastRoundVote(proposal);

        // Start the consensus loop
        _consensusLoopTask = RunConsensusLoopAsync(proposal, _sharedResources.ShuttingDownToken);
    }

    /// <summary>
    /// The main consensus loop. Runs until a decision is reached or cancelled.
    /// </summary>
    private async Task RunConsensusLoopAsync(List<Endpoint> proposal, CancellationToken cancellationToken)
    {
        try
        {
            // Phase 1: Fast round
            LogStartingFastRound(_coordinatorLogger);

            var fastRoundTimeout = GetRandomDelay();

            // Create a CancellationTokenSource that times out after the fast round delay.
            // When cancelled, FastPaxos.Result will complete with ConsensusResult.Cancelled.
            using var fastRoundTimeoutCts = new CancellationTokenSource(fastRoundTimeout, _sharedResources.TimeProvider);
            using var fastRoundCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, fastRoundTimeoutCts.Token);

            // Register the timeout token with FastPaxos
            _fastPaxos.RegisterTimeoutToken(fastRoundCts.Token);

            // Broadcast fast round proposal
            _fastPaxos.Propose(proposal, cancellationToken);

            // Wait for fast round result - will complete when decided, failed, or timeout (via cancellation)
            var fastRoundResult = await _fastPaxos.Result.ConfigureAwait(true);

            switch (fastRoundResult)
            {
                case ConsensusResult.Decided decided:
                    LogFastRoundDecided(_coordinatorLogger, new LoggableEndpoints(decided.Value));
                    _onDecidedTcs.TrySetResult(decided.Value);
                    return;

                case ConsensusResult.VoteSplit or ConsensusResult.DeliveryFailure:
                    LogFastRoundFailedEarly(_coordinatorLogger);
                    // Fall through to classic rounds
                    break;

                case ConsensusResult.Cancelled:
                    if (cancellationToken.IsCancellationRequested)
                    {
                        LogConsensusCancelled(_coordinatorLogger);
                        _onDecidedTcs.TrySetCanceled(cancellationToken);
                        return;
                    }
                    // Otherwise it was a timeout - fall through to classic rounds
                    LogFastRoundTimeout(_coordinatorLogger, fastRoundTimeout);
                    break;

                case ConsensusResult.Timeout:
                    LogFastRoundTimeout(_coordinatorLogger, fastRoundTimeout);
                    break;
            }

            // Phase 2+: Classic Paxos rounds
            var roundNumber = 2;
            var maxRounds = _options.MaxConsensusRounds;

            while (!cancellationToken.IsCancellationRequested && roundNumber <= maxRounds)
            {
                // Check if Paxos already decided (from a previous round's messages arriving late)
                if (_paxos!.Decided.IsCompletedSuccessfully)
                {
                    var paxosResult = await _paxos.Decided.ConfigureAwait(true);
                    if (paxosResult is ConsensusResult.Decided decided)
                    {
                        LogClassicRoundDecided(_coordinatorLogger, roundNumber - 1, new LoggableEndpoints(decided.Value));
                        _onDecidedTcs.TrySetResult(decided.Value);
                        return;
                    }
                }

                var delay = GetRetryDelay(roundNumber);
                LogStartingClassicRound(_coordinatorLogger, roundNumber, delay);

                // Start the classic round
                _paxos.StartPhase1a(roundNumber, cancellationToken);

                // Wait for decision or timeout using delay
                try
                {
                    await Task.Delay(delay, _sharedResources.TimeProvider, cancellationToken).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // This shouldn't happen in current flow
                }

                // Check if we decided during the delay
                if (_paxos.Decided.IsCompletedSuccessfully)
                {
                    var paxosResult = await _paxos.Decided.ConfigureAwait(true);
                    if (paxosResult is ConsensusResult.Decided decided)
                    {
                        LogClassicRoundDecided(_coordinatorLogger, roundNumber, new LoggableEndpoints(decided.Value));
                        _onDecidedTcs.TrySetResult(decided.Value);
                        return;
                    }
                    else if (paxosResult is ConsensusResult.Cancelled && cancellationToken.IsCancellationRequested)
                    {
                        LogConsensusCancelled(_coordinatorLogger);
                        _onDecidedTcs.TrySetCanceled(cancellationToken);
                        return;
                    }
                }

                // Round timed out, try next round
                LogClassicRoundTimeout(_coordinatorLogger, roundNumber, delay);
                roundNumber++;
            }

            // Exhausted all rounds without decision
            if (cancellationToken.IsCancellationRequested)
            {
                LogConsensusCancelled(_coordinatorLogger);
                _onDecidedTcs.TrySetCanceled(cancellationToken);
            }
            else
            {
                // All rounds exhausted without reaching consensus - this can happen during
                // network partitions where this node can't communicate with enough peers.
                // Signal failure so MembershipService can handle appropriately.
                LogConsensusExhausted(_coordinatorLogger, maxRounds);
                _onDecidedTcs.TrySetException(new InvalidOperationException(
                    $"Consensus failed: exhausted all {maxRounds} rounds without reaching decision for configId={_configurationId}"));
            }
        }
        catch (OperationCanceledException)
        {
            LogConsensusCancelled(_coordinatorLogger);
            _onDecidedTcs.TrySetCanceled(cancellationToken);
        }
    }

    /// <summary>
    /// Handle an incoming consensus message, routing to the appropriate handler.
    /// </summary>
    public RapidResponse HandleMessages(RapidRequest request, CancellationToken cancellationToken = default)
    {
        LogHandleMessages(_coordinatorLogger, request.ContentCase);

        lock (_lock)
        {
            switch (request.ContentCase)
            {
                case RapidRequest.ContentOneofCase.FastRoundPhase2BMessage:
                    _fastPaxos.HandleFastRoundProposal(request.FastRoundPhase2BMessage);
                    break;
                case RapidRequest.ContentOneofCase.Phase1AMessage:
                    _paxos.HandlePhase1aMessage(request.Phase1AMessage, cancellationToken);
                    break;
                case RapidRequest.ContentOneofCase.Phase1BMessage:
                    _paxos.HandlePhase1bMessage(request.Phase1BMessage, cancellationToken);
                    break;
                case RapidRequest.ContentOneofCase.Phase2AMessage:
                    _paxos.HandlePhase2aMessage(request.Phase2AMessage, cancellationToken);
                    break;
                case RapidRequest.ContentOneofCase.Phase2BMessage:
                    _paxos.HandlePhase2bMessage(request.Phase2BMessage);
                    break;
                default:
                    throw new ArgumentException($"Unexpected message case: {request.ContentCase}");
            }
        }

        return RapidUtils.ToRapidResponse(new ConsensusResponse());
    }

    /// <summary>
    /// Random expovariate variable plus a base delay for fast round timeout.
    /// </summary>
    private TimeSpan GetRandomDelay()
    {
        var jitter = (long)(-1000 * Math.Log(1 - _sharedResources.NextRandomDouble()) / _jitterRate);
        return TimeSpan.FromMilliseconds(jitter + (long)_options.ConsensusFallbackTimeoutBaseDelay.TotalMilliseconds);
    }

    /// <summary>
    /// Calculate delay for a retry round with exponential backoff and jitter.
    /// </summary>
    private TimeSpan GetRetryDelay(int roundNumber)
    {
        var baseMs = _options.ConsensusFallbackTimeoutBaseDelay.TotalMilliseconds;
        var multiplier = Math.Min(Math.Pow(1.5, roundNumber - 2), 8); // Cap at 8x
        var jitter = (long)(-1000 * Math.Log(1 - _sharedResources.NextRandomDouble()) / _jitterRate);
        return TimeSpan.FromMilliseconds(baseMs * multiplier + jitter);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        LogDispose(_coordinatorLogger);

        // Cancel both FastPaxos and Paxos to unblock any waiters
        _fastPaxos.Cancel();
        _paxos.Cancel();

        if (_consensusLoopTask is { } task)
        {
            await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _onDecidedTcs.TrySetCanceled();
    }
}
