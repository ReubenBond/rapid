/*
 * Copyright © 2016 - 2025 VMware, Inc. All Rights Reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file
 * except in compliance with the License. You may obtain a copy of the License at
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software distributed under the
 * License is distributed on an "AS IS" BASIS, without warranties or conditions of any kind,
 * EITHER EXPRESS OR IMPLIED. See the License for the specific language governing
 * permissions and limitations under the License.
 */

namespace Rapid;

/// <summary>
/// Holds configuration parameters for different components of a Rapid instance.
/// All timeout values are in milliseconds.
/// </summary>
public sealed class Settings
{
    public const int DefaultGrpcTimeoutMs = 1000;
    public const int DefaultGrpcDefaultRetries = 5;
    public const int DefaultGrpcJoinTimeoutMs = 5000;
    public const int DefaultGrpcProbeTimeoutMs = 500;
    public const int DefaultFailureDetectorIntervalMs = 1000;
    public const int DefaultBatchingWindowMs = 100;
    public const long DefaultConsensusFallbackTimeoutBaseDelayMs = 500;
    public const int DefaultLeaveMessageTimeoutMs = 1500;

    /// <summary>
    /// Whether to use in-process transport for testing. Default: false
    /// </summary>
    public bool UseInProcessTransport { get; set; } = false;

    /// <summary>
    /// gRPC request timeout in milliseconds. Default: 1000ms
    /// </summary>
    public int GrpcTimeoutMs { get; set; } = DefaultGrpcTimeoutMs;

    /// <summary>
    /// Number of retries for failed gRPC requests. Default: 5
    /// </summary>
    public int GrpcDefaultRetries { get; set; } = DefaultGrpcDefaultRetries;

    /// <summary>
    /// Timeout for join operations in milliseconds. Default: 5000ms
    /// </summary>
    public int GrpcJoinTimeoutMs { get; set; } = DefaultGrpcJoinTimeoutMs;

    /// <summary>
    /// Timeout for failure detector probe messages in milliseconds. Default: 500ms
    /// </summary>
    public int GrpcProbeTimeoutMs { get; set; } = DefaultGrpcProbeTimeoutMs;

    /// <summary>
    /// Interval between failure detector probes in milliseconds. Default: 1000ms
    /// </summary>
    public int FailureDetectorIntervalMs { get; set; } = DefaultFailureDetectorIntervalMs;

    /// <summary>
    /// Window for batching alert messages before broadcasting in milliseconds. Default: 100ms
    /// </summary>
    public int BatchingWindowMs { get; set; } = DefaultBatchingWindowMs;

    /// <summary>
    /// Base delay for consensus fallback timeout in milliseconds. Default: 500ms
    /// </summary>
    public long ConsensusFallbackTimeoutBaseDelayMs { get; set; } = DefaultConsensusFallbackTimeoutBaseDelayMs;

    /// <summary>
    /// Timeout for leave messages in milliseconds. Default: 1500ms
    /// </summary>
    public int LeaveMessageTimeoutMs { get; set; } = DefaultLeaveMessageTimeoutMs;
}
