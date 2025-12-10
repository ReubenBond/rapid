namespace Rapid;

/// <summary>
/// Configuration options for the Rapid protocol.
/// </summary>
public sealed class RapidProtocolOptions
{
    /// <summary>
    /// Whether to use in-process transport for testing. Default: false
    /// </summary>
    public bool UseInProcessTransport { get; set; }

    /// <summary>
    /// gRPC request timeout. Default: 10 seconds
    /// </summary>
    public TimeSpan GrpcTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Number of retries for failed gRPC requests. Default: 5
    /// </summary>
    public int GrpcDefaultRetries { get; set; } = 5;

    /// <summary>
    /// Timeout for join operations. Default: 5 seconds
    /// </summary>
    public TimeSpan GrpcJoinTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Timeout for failure detector probe messages. Default: 500 milliseconds
    /// </summary>
    public TimeSpan GrpcProbeTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Interval between failure detector probes. Default: 1 second
    /// </summary>
    public TimeSpan FailureDetectorInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Window for batching alert messages before broadcasting. Default: 100 milliseconds
    /// </summary>
    public TimeSpan BatchingWindow { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Base delay for consensus fallback timeout. Default: 500 milliseconds.
    /// This is the minimum time to wait before falling back to Classic Paxos
    /// if Fast Paxos hasn't completed. Actual delay includes random jitter.
    /// </summary>
    public TimeSpan ConsensusFallbackTimeoutBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Timeout for leave messages. Default: 1.5 seconds
    /// </summary>
    public TimeSpan LeaveMessageTimeout { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Number of virtual rings for consistent hashing. Default: 10
    /// </summary>
    public int RingCount { get; set; } = 10;

    /// <summary>
    /// High watermark threshold for multi-node cut detection. Default: 9
    /// </summary>
    public int HighWaterMark { get; set; } = 9;

    /// <summary>
    /// Low watermark threshold for multi-node cut detection. Default: 4
    /// </summary>
    public int LowWaterMark { get; set; } = 4;

    /// <summary>
    /// Number of consecutive probe failures required before declaring a node down. Default: 3
    /// </summary>
    public int FailureDetectorConsecutiveFailures { get; set; } = 3;
}
