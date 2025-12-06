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


    public bool UseInProcessTransport { get; set; } = false;
    public int GrpcTimeoutMs { get; set; } = DefaultGrpcTimeoutMs;
    public int GrpcDefaultRetries { get; set; } = DefaultGrpcDefaultRetries;
    public int GrpcJoinTimeoutMs { get; set; } = DefaultGrpcJoinTimeoutMs;
    public int GrpcProbeTimeoutMs { get; set; } = DefaultGrpcProbeTimeoutMs;
    public int FailureDetectorIntervalMs { get; set; } = DefaultFailureDetectorIntervalMs;
    public int BatchingWindowMs { get; set; } = DefaultBatchingWindowMs;
    public long ConsensusFallbackTimeoutBaseDelayMs { get; set; } = DefaultConsensusFallbackTimeoutBaseDelayMs;
    public int LeaveMessageTimeoutMs { get; set; } = DefaultLeaveMessageTimeoutMs;
}
