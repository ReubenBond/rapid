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

namespace Rapid.Tests;

/// <summary>
/// Tests for Paxos and FastPaxos protocols
/// </summary>
internal class PaxosTests
{
    /// <summary>
    /// Test rank comparison - higher round wins
    /// </summary>
    [Fact]
    public void RankComparisonHigherRoundWins()
    {
        var rank1 = new Rank { Round = 1, NodeIndex = 0 };
        var rank2 = new Rank { Round = 2, NodeIndex = 0 };

        // rank2 should be greater
        Assert.True(CompareRanks(rank2, rank1) > 0);
        Assert.True(CompareRanks(rank1, rank2) < 0);
    }

    /// <summary>
    /// Test rank comparison - same round, higher node index wins
    /// </summary>
    [Fact]
    public void RankComparisonSameRoundHigherNodeIndexWins()
    {
        var rank1 = new Rank { Round = 1, NodeIndex = 0 };
        var rank2 = new Rank { Round = 1, NodeIndex = 1 };

        // rank2 should be greater
        Assert.True(CompareRanks(rank2, rank1) > 0);
        Assert.True(CompareRanks(rank1, rank2) < 0);
    }

    /// <summary>
    /// Test rank comparison - equal ranks
    /// </summary>
    [Fact]
    public void RankComparisonEqualRanks()
    {
        var rank1 = new Rank { Round = 1, NodeIndex = 5 };
        var rank2 = new Rank { Round = 1, NodeIndex = 5 };

        Assert.Equal(0, CompareRanks(rank1, rank2));
    }

    /// <summary>
    /// Test that Phase1bMessage can be created correctly
    /// </summary>
    [Fact]
    public void Phase1bMessageCreation()
    {
        var sender = Utils.HostFromParts("127.0.0.1", 1234);
        var rnd = new Rank { Round = 5, NodeIndex = 1 };
        var vrnd = new Rank { Round = 3, NodeIndex = 0 };

        var msg = new Phase1bMessage
        {
            Sender = sender,
            ConfigurationId = 100,
            Rnd = rnd,
            Vrnd = vrnd
        };

        Assert.Equal(sender, msg.Sender);
        Assert.Equal(100, msg.ConfigurationId);
        Assert.Equal(5, msg.Rnd.Round);
        Assert.Equal(3, msg.Vrnd.Round);
    }

    /// <summary>
    /// Test that Phase2aMessage can be created correctly
    /// </summary>
    [Fact]
    public void Phase2aMessageCreation()
    {
        var sender = Utils.HostFromParts("127.0.0.1", 1234);
        var value = Utils.HostFromParts("127.0.0.1", 5678);
        var rnd = new Rank { Round = 5, NodeIndex = 1 };

        var msg = new Phase2aMessage
        {
            Sender = sender,
            ConfigurationId = 100,
            Rnd = rnd
        };
        msg.Vval.Add(value);

        Assert.Equal(sender, msg.Sender);
        Assert.Equal(100, msg.ConfigurationId);
        Assert.Equal(5, msg.Rnd.Round);
        Assert.Single(msg.Vval);
    }

    private static int CompareRanks(Rank r1, Rank r2)
    {
        if (r1.Round != r2.Round)
            return r1.Round.CompareTo(r2.Round);
        return r1.NodeIndex.CompareTo(r2.NodeIndex);
    }
}
