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
/// Represents a single node status change event.
/// </summary>
public sealed class NodeStatusChange
{
    public Endpoint Endpoint { get; }
    public EdgeStatus Status { get; }
    public Metadata Metadata { get; }

    internal NodeStatusChange(Endpoint endpoint, EdgeStatus status, Metadata metadata)
    {
        Endpoint = endpoint;
        Status = status;
        Metadata = metadata;
    }

    public override string ToString()
    {
        return $"{Endpoint.Hostname.ToStringUtf8()}:{Endpoint.Port}:{Status}:{Metadata}";
    }
}
