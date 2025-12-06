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
/// Event types to subscribe from the cluster.
/// </summary>
public enum ClusterEvents
{
    /// <summary>When a node announces a proposal using the multi node cut detector.</summary>
    ViewChangeProposal,

    /// <summary>When a fast-paxos quorum of identical proposals were received.</summary>
    ViewChange,

    /// <summary>When a fast-paxos quorum of identical proposals is unavailable.</summary>
    ViewChangeOneStepFailed,

    /// <summary>When a node detects that it has been removed from the network.</summary>
    Kicked
}
