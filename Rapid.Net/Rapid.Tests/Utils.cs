using Google.Protobuf;
using Rapid.Pb;

namespace Rapid.Tests;

/// <summary>
/// Test utility methods.
/// </summary>
internal static class Utils
{
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
    /// Converts a UUID to a NodeId.
    /// </summary>
    public static NodeId NodeIdFromUuid(Guid uuid)
    {
        var bytes = uuid.ToByteArray();
        var high = BitConverter.ToInt64(bytes, 0);
        var low = BitConverter.ToInt64(bytes, 8);
        return new NodeId { High = high, Low = low };
    }
}
