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

using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Represents a cluster membership status change.
/// </summary>
public sealed class ClusterStatusChange
{
    public long ConfigurationId { get; }
    public IReadOnlyList<Endpoint> Membership { get; }
    public IReadOnlyList<NodeStatusChange> Delta { get; }

    public ClusterStatusChange(long configurationId, IReadOnlyList<Endpoint> membership, 
        IReadOnlyList<NodeStatusChange> delta)
    {
        ConfigurationId = configurationId;
        Membership = membership;
        Delta = delta;
    }

    public override string ToString()
    {
        return $"ClusterStatusChange{{configurationId={ConfigurationId}, " +
               $"membership={string.Join(",", Membership)}, delta={string.Join(",", Delta)}}}";
    }
}
