using System.IO.Hashing;
using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Represents a configuration identifier that combines a monotonic version counter
/// with a membership hash for uniqueness. This ensures configuration IDs are both
/// strictly ordered (via version) and unique per membership (via hash).
/// </summary>
/// <remarks>
/// The struct is designed to be wire-compatible with the existing int64 configurationId
/// in protobuf messages. Use <see cref="ToInt64"/> and <see cref="FromInt64"/> for conversion.
/// </remarks>
public readonly struct ConfigurationId : IEquatable<ConfigurationId>, IComparable<ConfigurationId>
{
    /// <summary>
    /// The default/empty configuration ID with version 0 and hash 0.
    /// </summary>
    public static readonly ConfigurationId Empty = new(0, 0);

    /// <summary>
    /// Gets the monotonic version counter that increments with each configuration change.
    /// </summary>
    public long Version { get; }

    /// <summary>
    /// Gets the hash of the membership for uniqueness checking.
    /// </summary>
    public long Hash { get; }

    /// <summary>
    /// Initializes a new ConfigurationId with the specified version and hash.
    /// </summary>
    /// <param name="version">The monotonic version counter.</param>
    /// <param name="hash">The membership hash.</param>
    public ConfigurationId(long version, long hash)
    {
        Version = version;
        Hash = hash;
    }

    /// <summary>
    /// Creates a new ConfigurationId with an incremented version and a new hash
    /// computed from the given membership.
    /// </summary>
    /// <param name="identifiers">The node identifiers in the membership.</param>
    /// <param name="endpoints">The endpoints in the membership.</param>
    /// <returns>A new ConfigurationId with version incremented by 1.</returns>
    public ConfigurationId Next(IEnumerable<NodeId> identifiers, IEnumerable<Endpoint> endpoints)
    {
        var newHash = ComputeMembershipHash(identifiers, endpoints);
        return new ConfigurationId(Version + 1, newHash);
    }

    /// <summary>
    /// Creates a ConfigurationId from a membership without incrementing version.
    /// Used when initializing from existing membership data.
    /// </summary>
    /// <param name="version">The version to use.</param>
    /// <param name="identifiers">The node identifiers in the membership.</param>
    /// <param name="endpoints">The endpoints in the membership.</param>
    /// <returns>A new ConfigurationId.</returns>
    public static ConfigurationId Create(long version, IEnumerable<NodeId> identifiers, IEnumerable<Endpoint> endpoints)
    {
        var hash = ComputeMembershipHash(identifiers, endpoints);
        return new ConfigurationId(version, hash);
    }

    /// <summary>
    /// Converts the ConfigurationId to a 64-bit integer for wire transmission.
    /// The version is stored in the upper 32 bits and the hash in the lower 32 bits.
    /// </summary>
    /// <returns>A 64-bit integer representation.</returns>
    public long ToInt64() => (Version << 32) | (Hash & 0xFFFFFFFFL);

    /// <summary>
    /// Creates a ConfigurationId from a 64-bit integer (wire format).
    /// </summary>
    /// <param name="value">The 64-bit integer representation.</param>
    /// <returns>A ConfigurationId.</returns>
    public static ConfigurationId FromInt64(long value) => new(value >> 32, value & 0xFFFFFFFFL);

    /// <summary>
    /// Compares this ConfigurationId to another for ordering.
    /// Comparison is primarily by version, then by hash for equal versions.
    /// </summary>
    public int CompareTo(ConfigurationId other)
    {
        var versionComparison = Version.CompareTo(other.Version);
        return versionComparison != 0 ? versionComparison : Hash.CompareTo(other.Hash);
    }

    /// <summary>
    /// Checks equality with another ConfigurationId.
    /// Two ConfigurationIds are equal if both version and hash match.
    /// </summary>
    public bool Equals(ConfigurationId other) => Version == other.Version && Hash == other.Hash;

    public override bool Equals(object? obj) => obj is ConfigurationId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Version, Hash);

    public override string ToString() => $"ConfigurationId(v{Version}, h{Hash:X8})";

    public static bool operator ==(ConfigurationId left, ConfigurationId right) => left.Equals(right);
    public static bool operator !=(ConfigurationId left, ConfigurationId right) => !left.Equals(right);
    public static bool operator <(ConfigurationId left, ConfigurationId right) => left.CompareTo(right) < 0;
    public static bool operator <=(ConfigurationId left, ConfigurationId right) => left.CompareTo(right) <= 0;
    public static bool operator >(ConfigurationId left, ConfigurationId right) => left.CompareTo(right) > 0;
    public static bool operator >=(ConfigurationId left, ConfigurationId right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// Implicit conversion to long for backward compatibility.
    /// </summary>
    public static implicit operator long(ConfigurationId configId) => configId.ToInt64();

    /// <summary>
    /// Explicit conversion from long.
    /// </summary>
    public static explicit operator ConfigurationId(long value) => FromInt64(value);

    /// <summary>
    /// Computes a hash of the membership for uniqueness checking.
    /// </summary>
    private static long ComputeMembershipHash(IEnumerable<NodeId> identifiers, IEnumerable<Endpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        ArgumentNullException.ThrowIfNull(endpoints);

        long hash = 1;
        foreach (var id in identifiers)
        {
            hash = hash * 37 + (long)XxHash64.HashToUInt64(BitConverter.GetBytes(id.High));
            hash = hash * 37 + (long)XxHash64.HashToUInt64(BitConverter.GetBytes(id.Low));
        }
        foreach (var endpoint in endpoints)
        {
            hash = hash * 37 + (long)XxHash64.HashToUInt64(endpoint.Hostname.Span);
            hash = hash * 37 + (long)XxHash64.HashToUInt64(BitConverter.GetBytes(endpoint.Port));
        }
        return hash;
    }
}
