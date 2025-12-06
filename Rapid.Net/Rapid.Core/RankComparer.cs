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

internal sealed class RankComparer : IEqualityComparer<Rank>, IComparer<Rank>
{
    public static readonly RankComparer Instance = new();

    private RankComparer() { }

    public bool Equals(Rank? x, Rank? y)
    {
        if (x == null && y == null) return true;
        if (x == null || y == null) return false;
        return x.Equals(y);
    }

    public int GetHashCode(Rank obj)
    {
        return HashCode.Combine(obj.Round, obj.NodeIndex);
    }

    public int Compare(Rank? x, Rank? y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return -1;
        if (y == null) return 1;
        return x.CompareTo(y);
    }
}

