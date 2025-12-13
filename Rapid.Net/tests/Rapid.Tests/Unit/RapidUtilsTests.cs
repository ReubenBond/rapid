using Rapid.Pb;

namespace Rapid.Tests.Unit;

/// <summary>
/// Tests for RapidUtils utility methods.
/// </summary>
public class RapidUtilsTests
{

    [Fact]
    public void NodeIdFromUuidConvertsGuidCorrectly()
    {
        var guid = Guid.NewGuid();
        var nodeId = RapidUtils.NodeIdFromUuid(guid);

        Assert.NotEqual(0, nodeId.High);
        Assert.NotEqual(0, nodeId.Low);
    }

    [Fact]
    public void NodeIdFromUuidProducesDeterministicResults()
    {
        var guid = new Guid("12345678-1234-1234-1234-123456789abc");
        var nodeId1 = RapidUtils.NodeIdFromUuid(guid);
        var nodeId2 = RapidUtils.NodeIdFromUuid(guid);

        Assert.Equal(nodeId1.High, nodeId2.High);
        Assert.Equal(nodeId1.Low, nodeId2.Low);
    }

    [Fact]
    public void NodeIdFromUuidDifferentGuidsProduceDifferentNodeIds()
    {
        var guid1 = Guid.NewGuid();
        var guid2 = Guid.NewGuid();

        var nodeId1 = RapidUtils.NodeIdFromUuid(guid1);
        var nodeId2 = RapidUtils.NodeIdFromUuid(guid2);

        Assert.False(nodeId1.High == nodeId2.High && nodeId1.Low == nodeId2.Low);
    }

    [Fact]
    public void NodeIdFromUuidEmptyGuidWorks()
    {
        var nodeId = RapidUtils.NodeIdFromUuid(Guid.Empty);

        Assert.Equal(0, nodeId.High);
        Assert.Equal(0, nodeId.Low);
    }



    [Fact]
    public void HostFromStringValidInputParsesCorrectly()
    {
        var endpoint = RapidUtils.HostFromString("127.0.0.1:8080");

        Assert.Equal("127.0.0.1", endpoint.Hostname.ToStringUtf8());
        Assert.Equal(8080, endpoint.Port);
    }

    [Fact]
    public void HostFromStringLocalhostWithPortParsesCorrectly()
    {
        var endpoint = RapidUtils.HostFromString("localhost:9000");

        Assert.Equal("localhost", endpoint.Hostname.ToStringUtf8());
        Assert.Equal(9000, endpoint.Port);
    }

    [Fact]
    public void HostFromStringHighPortParsesCorrectly()
    {
        var endpoint = RapidUtils.HostFromString("10.0.0.1:65535");

        Assert.Equal("10.0.0.1", endpoint.Hostname.ToStringUtf8());
        Assert.Equal(65535, endpoint.Port);
    }

    [Fact]
    public void HostFromStringNullInputThrowsArgumentNullException() => Assert.Throws<ArgumentNullException>(() => RapidUtils.HostFromString(null!));

    [Fact]
    public void HostFromStringInvalidFormatNoColonThrows() => Assert.Throws<ArgumentException>(() => RapidUtils.HostFromString("127.0.0.1"));

    [Fact]
    public void HostFromStringInvalidFormatNonNumericPortThrows() => Assert.Throws<ArgumentException>(() => RapidUtils.HostFromString("127.0.0.1:abc"));

    [Fact]
    public void HostFromStringInvalidFormatEmptyPortThrows() => Assert.Throws<ArgumentException>(() => RapidUtils.HostFromString("127.0.0.1:"));

    [Fact]
    public void HostFromStringInvalidFormatMultipleColonsThrows() => Assert.Throws<ArgumentException>(() => RapidUtils.HostFromString("127.0.0.1:8080:extra"));



    [Fact]
    public void HostFromPartsValidInputCreatesEndpoint()
    {
        var endpoint = RapidUtils.HostFromParts("192.168.1.1", 5000);

        Assert.Equal("192.168.1.1", endpoint.Hostname.ToStringUtf8());
        Assert.Equal(5000, endpoint.Port);
    }

    [Fact]
    public void HostFromPartsZeroPortWorks()
    {
        var endpoint = RapidUtils.HostFromParts("localhost", 0);

        Assert.Equal("localhost", endpoint.Hostname.ToStringUtf8());
        Assert.Equal(0, endpoint.Port);
    }

    [Fact]
    public void HostFromPartsNegativePortWorks()
    {
        // Note: The method doesn't validate port range
        var endpoint = RapidUtils.HostFromParts("localhost", -1);

        Assert.Equal(-1, endpoint.Port);
    }



    [Fact]
    public void LoggableSingleEndpointFormatsCorrectly()
    {
        var endpoint = RapidUtils.HostFromParts("10.0.0.1", 9000);

        var result = RapidUtils.Loggable(endpoint);

        Assert.Equal("10.0.0.1:9000", result);
    }

    [Fact]
    public void LoggableNullEndpointThrowsArgumentNullException() => Assert.Throws<ArgumentNullException>(() => RapidUtils.Loggable((Endpoint)null!));

    [Fact]
    public void LoggableMultipleEndpointsFormatsCorrectly()
    {
        var endpoints = new List<Endpoint>
        {
            RapidUtils.HostFromParts("10.0.0.1", 9000),
            RapidUtils.HostFromParts("10.0.0.2", 9001),
            RapidUtils.HostFromParts("10.0.0.3", 9002)
        };

        var result = RapidUtils.Loggable(endpoints);

        Assert.Equal("[10.0.0.1:9000, 10.0.0.2:9001, 10.0.0.3:9002]", result);
    }

    [Fact]
    public void LoggableEmptyEndpointListReturnsEmptyBrackets()
    {
        var endpoints = new List<Endpoint>();

        var result = RapidUtils.Loggable(endpoints);

        Assert.Equal("[]", result);
    }

    [Fact]
    public void LoggableSingleElementListFormatsCorrectly()
    {
        var endpoints = new List<Endpoint>
        {
            RapidUtils.HostFromParts("127.0.0.1", 1234)
        };

        var result = RapidUtils.Loggable(endpoints);

        Assert.Equal("[127.0.0.1:1234]", result);
    }



    [Fact]
    public void ToRapidRequestPreJoinMessageWrapsCorrectly()
    {
        var msg = new PreJoinMessage
        {
            Sender = RapidUtils.HostFromParts("127.0.0.1", 1234),
            NodeId = RapidUtils.NodeIdFromUuid(Guid.NewGuid())
        };

        var request = msg.ToRapidRequest();

        Assert.NotNull(request.PreJoinMessage);
        Assert.Equal(msg.Sender, request.PreJoinMessage.Sender);
    }

    [Fact]
    public void ToRapidRequestJoinMessageWrapsCorrectly()
    {
        var msg = new JoinMessage
        {
            Sender = RapidUtils.HostFromParts("127.0.0.1", 1234),
            NodeId = RapidUtils.NodeIdFromUuid(Guid.NewGuid()),
            ConfigurationId = 100
        };

        var request = msg.ToRapidRequest();

        Assert.NotNull(request.JoinMessage);
        Assert.Equal(msg.Sender, request.JoinMessage.Sender);
        Assert.Equal(100, request.JoinMessage.ConfigurationId);
    }

    [Fact]
    public void ToRapidRequestBatchedAlertMessageWrapsCorrectly()
    {
        var msg = new BatchedAlertMessage();
        msg.Messages.Add(new AlertMessage
        {
            EdgeSrc = RapidUtils.HostFromParts("127.0.0.1", 1234),
            EdgeDst = RapidUtils.HostFromParts("127.0.0.1", 1235),
            EdgeStatus = EdgeStatus.Down,
            ConfigurationId = 100
        });

        var request = msg.ToRapidRequest();

        Assert.NotNull(request.BatchedAlertMessage);
        Assert.Single(request.BatchedAlertMessage.Messages);
    }

    [Fact]
    public void ToRapidRequestProbeMessageWrapsCorrectly()
    {
        var msg = new ProbeMessage
        {
            Sender = RapidUtils.HostFromParts("127.0.0.1", 1234)
        };

        var request = msg.ToRapidRequest();

        Assert.NotNull(request.ProbeMessage);
        Assert.Equal(msg.Sender, request.ProbeMessage.Sender);
    }

    [Fact]
    public void ToRapidRequestFastRoundPhase2bMessageWrapsCorrectly()
    {
        var proposal = new MembershipProposal { ConfigurationId = 100 };
        proposal.Members.Add(new MemberInfo
        {
            Endpoint = RapidUtils.HostFromParts("127.0.0.1", 1235),
            NodeId = new NodeId { High = 1, Low = 2 }
        });

        var msg = new FastRoundPhase2bMessage
        {
            ConfigurationId = 100,
            Sender = RapidUtils.HostFromParts("127.0.0.1", 1234),
            Proposal = proposal
        };

        var request = msg.ToRapidRequest();

        Assert.NotNull(request.FastRoundPhase2BMessage);
        Assert.Equal(100, request.FastRoundPhase2BMessage.ConfigurationId);
    }

    [Fact]
    public void ToRapidRequestPhase1aMessageWrapsCorrectly()
    {
        var msg = new Phase1aMessage
        {
            ConfigurationId = 100,
            Sender = RapidUtils.HostFromParts("127.0.0.1", 1234),
            Rank = new Rank { Round = 2, NodeIndex = 5 }
        };

        var request = msg.ToRapidRequest();

        Assert.NotNull(request.Phase1AMessage);
        Assert.Equal(2, request.Phase1AMessage.Rank.Round);
    }

    [Fact]
    public void ToRapidRequestPhase1bMessageWrapsCorrectly()
    {
        var msg = new Phase1bMessage
        {
            ConfigurationId = 100,
            Sender = RapidUtils.HostFromParts("127.0.0.1", 1234),
            Rnd = new Rank { Round = 2, NodeIndex = 5 },
            Vrnd = new Rank { Round = 1, NodeIndex = 3 }
        };

        var request = msg.ToRapidRequest();

        Assert.NotNull(request.Phase1BMessage);
        Assert.Equal(2, request.Phase1BMessage.Rnd.Round);
    }

    [Fact]
    public void ToRapidRequestPhase2aMessageWrapsCorrectly()
    {
        var proposal = new MembershipProposal { ConfigurationId = 100 };
        proposal.Members.Add(new MemberInfo
        {
            Endpoint = RapidUtils.HostFromParts("127.0.0.1", 1235),
            NodeId = new NodeId { High = 1, Low = 2 }
        });

        var msg = new Phase2aMessage
        {
            ConfigurationId = 100,
            Sender = RapidUtils.HostFromParts("127.0.0.1", 1234),
            Rnd = new Rank { Round = 2, NodeIndex = 5 },
            Proposal = proposal
        };

        var request = msg.ToRapidRequest();

        Assert.NotNull(request.Phase2AMessage);
        Assert.Single(request.Phase2AMessage.Proposal.Members);
    }

    [Fact]
    public void ToRapidRequestPhase2bMessageWrapsCorrectly()
    {
        var proposal = new MembershipProposal { ConfigurationId = 100 };
        proposal.Members.Add(new MemberInfo
        {
            Endpoint = RapidUtils.HostFromParts("127.0.0.1", 1235),
            NodeId = new NodeId { High = 1, Low = 2 }
        });

        var msg = new Phase2bMessage
        {
            ConfigurationId = 100,
            Sender = RapidUtils.HostFromParts("127.0.0.1", 1234),
            Rnd = new Rank { Round = 2, NodeIndex = 5 },
            Proposal = proposal
        };

        var request = msg.ToRapidRequest();

        Assert.NotNull(request.Phase2BMessage);
        Assert.Single(request.Phase2BMessage.Proposal.Members);
    }

    [Fact]
    public void ToRapidRequestLeaveMessageWrapsCorrectly()
    {
        var msg = new LeaveMessage
        {
            Sender = RapidUtils.HostFromParts("127.0.0.1", 1234)
        };

        var request = msg.ToRapidRequest();

        Assert.NotNull(request.LeaveMessage);
        Assert.Equal(msg.Sender, request.LeaveMessage.Sender);
    }



    [Fact]
    public void ToRapidResponseJoinResponseWrapsCorrectly()
    {
        var msg = new JoinResponse
        {
            StatusCode = JoinStatusCode.SafeToJoin,
            ConfigurationId = 100
        };
        msg.Endpoints.Add(RapidUtils.HostFromParts("127.0.0.1", 1234));

        var response = msg.ToRapidResponse();

        Assert.NotNull(response.JoinResponse);
        Assert.Equal(JoinStatusCode.SafeToJoin, response.JoinResponse.StatusCode);
    }

    [Fact]
    public void ToRapidResponseConsensusResponseWrapsCorrectly()
    {
        var msg = new ConsensusResponse();

        var response = msg.ToRapidResponse();

        Assert.NotNull(response.ConsensusResponse);
    }

    [Fact]
    public void ToRapidResponseProbeResponseWrapsCorrectly()
    {
        var msg = new ProbeResponse { Status = NodeStatus.Ok };

        var response = msg.ToRapidResponse();

        Assert.NotNull(response.ProbeResponse);
        Assert.Equal(NodeStatus.Ok, response.ProbeResponse.Status);
    }

}
