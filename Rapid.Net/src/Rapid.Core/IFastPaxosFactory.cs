using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rapid.Messaging;
using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Factory for creating FastPaxos instances.
/// This exists because FastPaxos instances are created per consensus round and need runtime configuration.
/// </summary>
internal interface IFastPaxosFactory
{
    /// <summary>
    /// Creates a new FastPaxos instance for a consensus round.
    /// </summary>
    /// <param name="myAddr">The local endpoint.</param>
    /// <param name="configurationId">The current configuration ID.</param>
    /// <param name="membershipSize">The current membership size.</param>
    /// <param name="broadcaster">The broadcaster to use for message distribution.</param>
    /// <returns>A new FastPaxos instance.</returns>
    FastPaxos Create(
        Endpoint myAddr,
        long configurationId,
        int membershipSize,
        IBroadcaster broadcaster);
}

/// <summary>
/// Default implementation of IFastPaxosFactory.
/// </summary>
internal sealed class FastPaxosFactory(
    IMessagingClient messagingClient,
    IOptions<RapidProtocolOptions> protocolOptions,
    SharedResources sharedResources,
    ILogger<FastPaxos> fastPaxosLogger,
    ILogger<Paxos> paxosLogger) : IFastPaxosFactory
{
    public FastPaxos Create(
        Endpoint myAddr,
        long configurationId,
        int membershipSize,
        IBroadcaster broadcaster)
    {
        return new FastPaxos(
            myAddr,
            configurationId,
            membershipSize,
            messagingClient,
            broadcaster,
            protocolOptions,
            sharedResources,
            fastPaxosLogger,
            paxosLogger);
    }
}
