using Rapid.Pb;

namespace Rapid.Tests;

/// <summary>
/// Tests for Paxos and FastPaxos protocols
/// </summary>
public class PaxosTests
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

    #region Phase1aMessage Tests

    [Fact]
    public void Phase1aMessageCreationSetsAllFields()
    {
        var sender = Utils.HostFromParts("127.0.0.1", 1234);
        var rank = new Rank { Round = 5, NodeIndex = 10 };

        var msg = new Phase1aMessage
        {
            Sender = sender,
            ConfigurationId = 1000,
            Rank = rank
        };

        Assert.Equal(sender, msg.Sender);
        Assert.Equal(1000, msg.ConfigurationId);
        Assert.Equal(5, msg.Rank.Round);
        Assert.Equal(10, msg.Rank.NodeIndex);
    }

    [Fact]
    public void Phase1aMessageDefaultRankIsNull()
    {
        var msg = new Phase1aMessage
        {
            Sender = Utils.HostFromParts("127.0.0.1", 1234),
            ConfigurationId = 100
        };

        Assert.Null(msg.Rank);
    }

    [Fact]
    public void Phase1aMessageCloneCreatesIndependentCopy()
    {
        var original = new Phase1aMessage
        {
            Sender = Utils.HostFromParts("127.0.0.1", 1234),
            ConfigurationId = 100,
            Rank = new Rank { Round = 5, NodeIndex = 10 }
        };

        var clone = original.Clone();

        Assert.Equal(original.ConfigurationId, clone.ConfigurationId);
        Assert.Equal(original.Rank.Round, clone.Rank.Round);

        clone.ConfigurationId = 200;
        Assert.Equal(100, original.ConfigurationId);
    }

    #endregion

    #region Phase1bMessage Tests

    [Fact]
    public void Phase1bMessageCreationSetsAllFields()
    {
        var sender = Utils.HostFromParts("127.0.0.1", 1234);
        var rnd = new Rank { Round = 5, NodeIndex = 1 };
        var vrnd = new Rank { Round = 3, NodeIndex = 0 };
        var vval = Utils.HostFromParts("127.0.0.1", 9999);

        var msg = new Phase1bMessage
        {
            Sender = sender,
            ConfigurationId = 100,
            Rnd = rnd,
            Vrnd = vrnd
        };
        msg.Vval.Add(vval);

        Assert.Equal(sender, msg.Sender);
        Assert.Equal(100, msg.ConfigurationId);
        Assert.Equal(5, msg.Rnd.Round);
        Assert.Equal(3, msg.Vrnd.Round);
        Assert.Single(msg.Vval);
        Assert.Equal(vval, msg.Vval[0]);
    }

    [Fact]
    public void Phase1bMessageEmptyVvalIsValid()
    {
        var msg = new Phase1bMessage
        {
            Sender = Utils.HostFromParts("127.0.0.1", 1234),
            ConfigurationId = 100,
            Rnd = new Rank { Round = 5, NodeIndex = 1 },
            Vrnd = new Rank { Round = 0, NodeIndex = 0 }
        };

        Assert.Empty(msg.Vval);
    }

    [Fact]
    public void Phase1bMessageMultipleVvalAllStored()
    {
        var msg = new Phase1bMessage
        {
            Sender = Utils.HostFromParts("127.0.0.1", 1234),
            ConfigurationId = 100,
            Rnd = new Rank { Round = 5, NodeIndex = 1 },
            Vrnd = new Rank { Round = 1, NodeIndex = 0 }
        };
        msg.Vval.Add(Utils.HostFromParts("127.0.0.1", 1001));
        msg.Vval.Add(Utils.HostFromParts("127.0.0.1", 1002));
        msg.Vval.Add(Utils.HostFromParts("127.0.0.1", 1003));

        Assert.Equal(3, msg.Vval.Count);
    }

    #endregion

    #region Phase2aMessage Tests

    [Fact]
    public void Phase2aMessageCreationSetsAllFields()
    {
        var sender = Utils.HostFromParts("127.0.0.1", 1234);
        var rnd = new Rank { Round = 5, NodeIndex = 1 };

        var msg = new Phase2aMessage
        {
            Sender = sender,
            ConfigurationId = 100,
            Rnd = rnd
        };
        msg.Vval.Add(Utils.HostFromParts("127.0.0.1", 5678));

        Assert.Equal(sender, msg.Sender);
        Assert.Equal(100, msg.ConfigurationId);
        Assert.Equal(5, msg.Rnd.Round);
        Assert.Single(msg.Vval);
    }

    [Fact]
    public void Phase2aMessageAddRangeWorksCorrectly()
    {
        var msg = new Phase2aMessage
        {
            Sender = Utils.HostFromParts("127.0.0.1", 1234),
            ConfigurationId = 100,
            Rnd = new Rank { Round = 2, NodeIndex = 1 }
        };

        var endpoints = new List<Endpoint>
        {
            Utils.HostFromParts("10.0.0.1", 5001),
            Utils.HostFromParts("10.0.0.2", 5002),
            Utils.HostFromParts("10.0.0.3", 5003)
        };
        msg.Vval.AddRange(endpoints);

        Assert.Equal(3, msg.Vval.Count);
    }

    #endregion

    #region Phase2bMessage Tests

    [Fact]
    public void Phase2bMessageCreationSetsAllFields()
    {
        var sender = Utils.HostFromParts("127.0.0.1", 1234);
        var rnd = new Rank { Round = 5, NodeIndex = 1 };

        var msg = new Phase2bMessage
        {
            Sender = sender,
            ConfigurationId = 100,
            Rnd = rnd
        };
        msg.Endpoints.Add(Utils.HostFromParts("127.0.0.1", 5678));

        Assert.Equal(sender, msg.Sender);
        Assert.Equal(100, msg.ConfigurationId);
        Assert.Equal(5, msg.Rnd.Round);
        Assert.Single(msg.Endpoints);
    }

    [Fact]
    public void Phase2bMessageMultipleEndpointsAllStored()
    {
        var msg = new Phase2bMessage
        {
            Sender = Utils.HostFromParts("127.0.0.1", 1234),
            ConfigurationId = 100,
            Rnd = new Rank { Round = 2, NodeIndex = 1 }
        };

        for (var i = 0; i < 10; i++)
        {
            msg.Endpoints.Add(Utils.HostFromParts("192.168.1." + i, 5000 + i));
        }

        Assert.Equal(10, msg.Endpoints.Count);
    }

    #endregion

    #region FastRoundPhase2bMessage Tests

    [Fact]
    public void FastRoundPhase2bMessageCreationSetsAllFields()
    {
        var sender = Utils.HostFromParts("127.0.0.1", 1234);

        var msg = new FastRoundPhase2bMessage
        {
            Sender = sender,
            ConfigurationId = 100
        };
        msg.Endpoints.Add(Utils.HostFromParts("127.0.0.1", 5678));

        Assert.Equal(sender, msg.Sender);
        Assert.Equal(100, msg.ConfigurationId);
        Assert.Single(msg.Endpoints);
    }

    [Fact]
    public void FastRoundPhase2bMessageEmptyEndpointsIsValid()
    {
        var msg = new FastRoundPhase2bMessage
        {
            Sender = Utils.HostFromParts("127.0.0.1", 1234),
            ConfigurationId = 100
        };

        Assert.Empty(msg.Endpoints);
    }

    [Fact]
    public void FastRoundPhase2bMessageLargeProposalHandledCorrectly()
    {
        var msg = new FastRoundPhase2bMessage
        {
            Sender = Utils.HostFromParts("127.0.0.1", 1234),
            ConfigurationId = 100
        };

        for (var i = 0; i < 100; i++)
        {
            msg.Endpoints.Add(Utils.HostFromParts("10.0.0." + i, 5000));
        }

        Assert.Equal(100, msg.Endpoints.Count);
    }

    #endregion

    #region Rank Comparison in Paxos Context

    [Fact]
    public void RankFastRoundIsRound1()
    {
        var fastRound = new Rank { Round = 1, NodeIndex = 0 };
        var classicRound = new Rank { Round = 2, NodeIndex = 100 };

        Assert.True(classicRound.CompareTo(fastRound) > 0);
    }

    [Fact]
    public void RankClassicRoundsStartAtRound2()
    {
        var fastRound = new Rank { Round = 1, NodeIndex = 0 };
        var classicRound2 = new Rank { Round = 2, NodeIndex = 0 };

        Assert.True(classicRound2 > fastRound);
    }

    [Fact]
    public void RankNodeIndexBreaksTies()
    {
        var rank1 = new Rank { Round = 5, NodeIndex = 100 };
        var rank2 = new Rank { Round = 5, NodeIndex = 200 };

        Assert.True(rank2 > rank1);
    }

    [Fact]
    public void RankFResiliencyCalculation()
    {
        // f = floor((n-1)/4) for Fast Paxos
        Assert.Equal(1, (int)Math.Floor((5 - 1) / 4.0));
        Assert.Equal(2, (int)Math.Floor((10 - 1) / 4.0));
        Assert.Equal(4, (int)Math.Floor((20 - 1) / 4.0));
    }

    [Fact]
    public void RankQuorumThresholdCalculation()
    {
        // Threshold = n - f for Fast Paxos
        int n = 5;
        int f = (int)Math.Floor((n - 1) / 4.0);
        Assert.Equal(4, n - f);

        n = 10;
        f = (int)Math.Floor((n - 1) / 4.0);
        Assert.Equal(8, n - f);

        n = 20;
        f = (int)Math.Floor((n - 1) / 4.0);
        Assert.Equal(16, n - f);
    }

    #endregion

    #region ConsensusResponse Tests

    [Fact]
    public void ConsensusResponseDefaultFields()
    {
        var response = new ConsensusResponse();

        Assert.NotNull(response);
    }

    #endregion

    #region Message Serialization Tests

    [Fact]
    public void Phase1aMessageSerializeDeserializeRoundtrips()
    {
        var original = new Phase1aMessage
        {
            Sender = Utils.HostFromParts("127.0.0.1", 1234),
            ConfigurationId = 999,
            Rank = new Rank { Round = 10, NodeIndex = 20 }
        };

        var bytes = Google.Protobuf.MessageExtensions.ToByteArray(original);
        var deserialized = Phase1aMessage.Parser.ParseFrom(bytes);

        Assert.Equal(original.ConfigurationId, deserialized.ConfigurationId);
        Assert.Equal(original.Rank.Round, deserialized.Rank.Round);
        Assert.Equal(original.Rank.NodeIndex, deserialized.Rank.NodeIndex);
    }

    [Fact]
    public void Phase2aMessageSerializeDeserializePreservesEndpoints()
    {
        var original = new Phase2aMessage
        {
            Sender = Utils.HostFromParts("127.0.0.1", 1234),
            ConfigurationId = 100,
            Rnd = new Rank { Round = 5, NodeIndex = 10 }
        };
        original.Vval.Add(Utils.HostFromParts("10.0.0.1", 5001));
        original.Vval.Add(Utils.HostFromParts("10.0.0.2", 5002));

        var bytes = Google.Protobuf.MessageExtensions.ToByteArray(original);
        var deserialized = Phase2aMessage.Parser.ParseFrom(bytes);

        Assert.Equal(2, deserialized.Vval.Count);
        Assert.Equal("10.0.0.1", deserialized.Vval[0].Hostname.ToStringUtf8());
        Assert.Equal(5001, deserialized.Vval[0].Port);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void RankZeroValuesCompareCorrectly()
    {
        var zeroRank = new Rank { Round = 0, NodeIndex = 0 };
        var nonZeroRank = new Rank { Round = 0, NodeIndex = 1 };

        Assert.True(nonZeroRank > zeroRank);
    }

    [Fact]
    public void Phase1bMessageSameVrndAsRndIsValid()
    {
        var msg = new Phase1bMessage
        {
            Sender = Utils.HostFromParts("127.0.0.1", 1234),
            ConfigurationId = 100,
            Rnd = new Rank { Round = 5, NodeIndex = 10 },
            Vrnd = new Rank { Round = 5, NodeIndex = 10 }
        };

        Assert.Equal(msg.Rnd.Round, msg.Vrnd.Round);
    }

    [Fact]
    public void ConfigurationIdCanBeNegative()
    {
        var msg = new Phase1aMessage
        {
            Sender = Utils.HostFromParts("127.0.0.1", 1234),
            ConfigurationId = -1,
            Rank = new Rank { Round = 1, NodeIndex = 1 }
        };

        Assert.Equal(-1, msg.ConfigurationId);
    }

    [Fact]
    public void ConfigurationIdCanBeLargeValue()
    {
        var msg = new Phase1aMessage
        {
            Sender = Utils.HostFromParts("127.0.0.1", 1234),
            ConfigurationId = long.MaxValue,
            Rank = new Rank { Round = 1, NodeIndex = 1 }
        };

        Assert.Equal(long.MaxValue, msg.ConfigurationId);
    }

    #endregion
}
