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

internal sealed class ListEndpointComparer : IEqualityComparer<List<Endpoint>>
{
    public static readonly ListEndpointComparer Instance = new();

    private ListEndpointComparer() { }

    public bool Equals(List<Endpoint>? x, List<Endpoint>? y)
    {
        if (x == null && y == null) return true;
        if (x == null || y == null) return false;
        return x.SequenceEqual(y);
    }

    public int GetHashCode(List<Endpoint> obj)
    {
        var hash = new HashCode();
        foreach (var endpoint in obj)
        {
            hash.Add(endpoint.GetHashCode());
        }
        return hash.ToHashCode();
    }
}

