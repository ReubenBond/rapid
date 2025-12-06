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

namespace Rapid.Pb;

public partial class Rank : IComparable<Rank>
{
    public int CompareTo(Rank? other)
    {
        if (other == null) return 1;

        var roundCmp = Round.CompareTo(other.Round);
        if (roundCmp != 0) return roundCmp;
        return NodeIndex.CompareTo(other.NodeIndex);
    }

    public static bool operator ==(Rank? left, Rank? right)
    {
        if (left is null) return right is null;
        return left.Equals(right);
    }

    public static bool operator !=(Rank? left, Rank? right) => !(left == right);

    public static bool operator <(Rank? left, Rank? right)
    {
        if (left is null) return right is not null;
        return left.CompareTo(right) < 0;
    }

    public static bool operator <=(Rank? left, Rank? right)
    {
        if (left is null) return true;
        return left.CompareTo(right) <= 0;
    }

    public static bool operator >(Rank? left, Rank? right)
    {
        if (left is null) return false;
        return left.CompareTo(right) > 0;
    }

    public static bool operator >=(Rank? left, Rank? right)
    {
        if (left is null) return right is null;
        return left.CompareTo(right) >= 0;
    }
}
