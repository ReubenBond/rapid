using Google.Protobuf;
using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Utility methods for Rapid.
/// </summary>
public static class RapidUtils
{
    /// <summary>
    /// Converts a UUID to a NodeId.
    /// </summary>
    public static NodeId NodeIdFromUuid(Guid uuid)
    {
        var bytes = uuid.ToByteArray();
        var high = BitConverter.ToInt64(bytes, 0);
        var low = BitConverter.ToInt64(bytes, 8);
        return new NodeId { High = high, Low = low };
    }

    /// <summary>
    /// Creates an Endpoint from a host:port string.
    /// </summary>
    public static Endpoint HostFromString(string hostString)
    {
        ArgumentNullException.ThrowIfNull(hostString);
        var parts = hostString.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
        {
            throw new ArgumentException($"Invalid host:port string: {hostString}");
        }
        return HostFromParts(parts[0], port);
    }

    /// <summary>
    /// Creates an Endpoint from hostname and port.
    /// </summary>
    public static Endpoint HostFromParts(string hostname, int port)
    {
        return new Endpoint
        {
            Hostname = ByteString.CopyFromUtf8(hostname),
            Port = port
        };
    }

    /// <summary>
    /// Creates a loggable string representation of an endpoint.
    /// </summary>
    public static string Loggable(Endpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return $"{endpoint.Hostname.ToStringUtf8()}:{endpoint.Port}";
    }

    /// <summary>
    /// Creates a loggable string representation of endpoints.
    /// </summary>
    public static string Loggable(IEnumerable<Endpoint> endpoints)
    {
        return $"[{string.Join(", ", endpoints.Select(Loggable))}]";
    }

    // Helper methods to construct RapidRequest/RapidResponse
    public static RapidRequest ToRapidRequest(PreJoinMessage msg) =>
        new() { PreJoinMessage = msg };

    public static RapidRequest ToRapidRequest(JoinMessage msg) =>
        new() { JoinMessage = msg };

    public static RapidRequest ToRapidRequest(BatchedAlertMessage msg) =>
        new() { BatchedAlertMessage = msg };

    public static RapidRequest ToRapidRequest(ProbeMessage msg) =>
        new() { ProbeMessage = msg };

    public static RapidRequest ToRapidRequest(FastRoundPhase2bMessage msg) =>
        new() { FastRoundPhase2BMessage = msg };

    public static RapidRequest ToRapidRequest(Phase1aMessage msg) =>
        new() { Phase1AMessage = msg };

    public static RapidRequest ToRapidRequest(Phase1bMessage msg) =>
        new() { Phase1BMessage = msg };

    public static RapidRequest ToRapidRequest(Phase2aMessage msg) =>
        new() { Phase2AMessage = msg };

    public static RapidRequest ToRapidRequest(Phase2bMessage msg) =>
        new() { Phase2BMessage = msg };

    public static RapidRequest ToRapidRequest(LeaveMessage msg) =>
        new() { LeaveMessage = msg };

    public static RapidResponse ToRapidResponse(JoinResponse msg) =>
        new() { JoinResponse = msg };

    public static RapidResponse ToRapidResponse(ConsensusResponse msg) =>
        new() { ConsensusResponse = msg };

    public static RapidResponse ToRapidResponse(ProbeResponse msg) =>
        new() { ProbeResponse = msg };
}
