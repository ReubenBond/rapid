using CsCheck;
using Google.Protobuf;
using Rapid.Pb;

namespace Rapid.Tests;

/// <summary>
/// Property-based tests using CsCheck for core Rapid types.
/// These tests verify invariants and properties that should hold for all inputs.
/// </summary>
public class PropertyBasedTests
{
    #region Generators

    /// <summary>
    /// Generator for valid endpoints.
    /// </summary>
    private static readonly Gen<Endpoint> GenEndpoint =
        Gen.Select(
            Gen.Int[1, 255],  // IP last octet
            Gen.Int[1000, 65535]  // Port
        ).Select((octet, port) => new Endpoint
        {
            Hostname = ByteString.CopyFromUtf8($"127.0.0.{octet}"),
            Port = port
        });

    /// <summary>
    /// Generator for NodeIds (UUIDs).
    /// </summary>
    private static readonly Gen<NodeId> GenNodeId =
        Gen.Select(Gen.Long, Gen.Long)
            .Select((high, low) => new NodeId { High = high, Low = low });

    /// <summary>
    /// Generator for a list of unique endpoints with their NodeIds.
    /// </summary>
    private static Gen<List<(Endpoint Endpoint, NodeId NodeId)>> GenUniqueNodes(int minCount, int maxCount)
    {
        return Gen.Int[minCount, maxCount].SelectMany(count =>
            Gen.Select(
                Gen.Int[1, 255].Array[count].Where(a => a.Distinct().Count() == count),
                Gen.Int[1000, 65535].Array[count],
                GenNodeId.Array[count]
            ).Select((octets, ports, nodeIds) =>
            {
                var result = new List<(Endpoint, NodeId)>(count);
                for (var i = 0; i < count; i++)
                {
                    var endpoint = new Endpoint
                    {
                        Hostname = ByteString.CopyFromUtf8($"127.0.0.{octets[i]}"),
                        Port = ports[i]
                    };
                    result.Add((endpoint, nodeIds[i]));
                }
                return result;
            }));
    }

    /// <summary>
    /// Generator for K values (number of rings).
    /// </summary>
    private static readonly Gen<int> GenK = Gen.Int[1, 10];

    /// <summary>
    /// Generator for H values (high watermark for reports).
    /// </summary>
    private static readonly Gen<int> GenH = Gen.Int[1, 10];

    /// <summary>
    /// Generator for L values (low watermark for reports).
    /// </summary>
    private static readonly Gen<int> GenL = Gen.Int[1, 10];

    #endregion

    #region MembershipView Properties

    [Fact]
    public void MembershipView_RingCount_Equals_K()
    {
        Gen.Select(GenK, GenUniqueNodes(1, 20))
            .Sample((k, nodes) =>
            {
                var builder = new MembershipViewBuilder(k);
                foreach (var (endpoint, nodeId) in nodes)
                {
                    builder.RingAdd(endpoint, nodeId);
                }
                var view = builder.Build();

                return view.RingCount == k;
            });
    }

    [Fact]
    public void MembershipView_Size_Equals_AddedNodes()
    {
        Gen.Select(GenK, GenUniqueNodes(1, 20))
            .Sample((k, nodes) =>
            {
                var builder = new MembershipViewBuilder(k);
                foreach (var (endpoint, nodeId) in nodes)
                {
                    builder.RingAdd(endpoint, nodeId);
                }
                var view = builder.Build();

                return view.Size == nodes.Count;
            });
    }

    [Fact]
    public void MembershipView_GetRing_Returns_Correct_Size()
    {
        Gen.Select(GenK, GenUniqueNodes(1, 20))
            .Sample((k, nodes) =>
            {
                var builder = new MembershipViewBuilder(k);
                foreach (var (endpoint, nodeId) in nodes)
                {
                    builder.RingAdd(endpoint, nodeId);
                }
                var view = builder.Build();

                // Each ring should have the same number of elements as there are nodes
                for (var ringNumber = 0; ringNumber < k; ringNumber++)
                {
                    var ring = view.GetRing(ringNumber);
                    if (ring.Length != nodes.Count) return false;
                }
                return true;
            });
    }

    [Fact]
    public void MembershipView_IsHostPresent_Returns_True_For_Added_Nodes()
    {
        Gen.Select(GenK, GenUniqueNodes(1, 20))
            .Sample((k, nodes) =>
            {
                var builder = new MembershipViewBuilder(k);
                foreach (var (endpoint, nodeId) in nodes)
                {
                    builder.RingAdd(endpoint, nodeId);
                }
                var view = builder.Build();

                // All added nodes should be present
                return nodes.All(n => view.IsHostPresent(n.Endpoint));
            });
    }

    [Fact]
    public void MembershipView_GetObserversOf_Returns_Up_To_K_Observers()
    {
        Gen.Select(GenK, GenUniqueNodes(3, 20))
            .Sample((k, nodes) =>
            {
                var builder = new MembershipViewBuilder(k);
                foreach (var (endpoint, nodeId) in nodes)
                {
                    builder.RingAdd(endpoint, nodeId);
                }
                var view = builder.Build();

                var testNode = nodes[0].Endpoint;
                var observers = view.GetObserversOf(testNode);

                // Observers count should be exactly K (one per ring)
                return observers.Length == k;
            });
    }

    [Fact]
    public void MembershipView_GetExpectedObserversOf_Does_Not_Include_Self()
    {
        Gen.Select(GenK, GenUniqueNodes(2, 20))
            .Sample((k, nodes) =>
            {
                var builder = new MembershipViewBuilder(k);
                foreach (var (endpoint, nodeId) in nodes)
                {
                    builder.RingAdd(endpoint, nodeId);
                }
                var view = builder.Build();

                var testNode = nodes[0].Endpoint;
                var expectedObservers = view.GetExpectedObserversOf(testNode);

                // Self should never be an observer of itself
                return !expectedObservers.Contains(testNode);
            });
    }

    #endregion

    #region SimpleCutDetector Properties

    [Fact]
    public void SimpleCutDetector_Single_Report_With_Required_Votes_Triggers_Cut()
    {
        Gen.Select(Gen.Int[1, 2], GenUniqueNodes(2, 10))
            .Sample((observersPerSubject, nodes) =>
            {
                var builder = new MembershipViewBuilder(observersPerSubject);
                foreach (var (endpoint, nodeId) in nodes)
                {
                    builder.RingAdd(endpoint, nodeId);
                }
                var view = builder.Build();

                var detector = new SimpleCutDetector(view);

                var subject = nodes[0].Endpoint;

                // Report observersPerSubject times (simulating different observers on different rings)
                var proposals = new List<List<Endpoint>>();
                for (var i = 0; i < observersPerSubject; i++)
                {
                    var result = detector.AggregateForProposal(
                        new AlertMessage
                        {
                            EdgeSrc = nodes[(i + 1) % nodes.Count].Endpoint,
                            EdgeDst = subject,
                            EdgeStatus = EdgeStatus.Up,
                            RingNumber = { i }
                        });
                    proposals.Add(result);
                }

                // After required reports, there should be a proposal
                var lastProposalHasSubject = proposals.Last().Contains(subject);
                return lastProposalHasSubject;
            });
    }

    [Fact]
    public void SimpleCutDetector_Empty_For_Unknown_Subject()
    {
        Gen.Select(Gen.Int[1, 2], GenUniqueNodes(2, 10))
            .Sample((observersPerSubject, nodes) =>
            {
                var builder = new MembershipViewBuilder(observersPerSubject);
                foreach (var (endpoint, nodeId) in nodes)
                {
                    builder.RingAdd(endpoint, nodeId);
                }
                var view = builder.Build();

                var detector = new SimpleCutDetector(view);

                // Create an unknown endpoint
                var unknownEndpoint = new Endpoint
                {
                    Hostname = ByteString.CopyFromUtf8("192.168.1.1"),
                    Port = 9999
                };

                // Report once for unknown subject - should not trigger until threshold
                var result = detector.AggregateForProposal(
                    new AlertMessage
                    {
                        EdgeSrc = nodes[0].Endpoint,
                        EdgeDst = unknownEndpoint,
                        EdgeStatus = EdgeStatus.Up,
                        RingNumber = { 0 }
                    });

                // With observersPerSubject > 1, first report returns empty
                // With observersPerSubject == 1, it triggers immediately
                return observersPerSubject == 1 ? result.Contains(unknownEndpoint) : result.Count == 0;
            });
    }

    #endregion

    #region MultiNodeCutDetector Properties

    [Fact]
    public void MultiNodeCutDetector_Respects_H_Threshold()
    {
        Gen.Select(GenK.Where(k => k >= 4), GenH.Where(h => h >= 2), GenL.Where(l => l >= 1), GenUniqueNodes(5, 15))
            .Where(t => t.Item1 > t.Item2 && t.Item2 >= t.Item3) // K > H >= L
            .Sample((k, h, l, nodes) =>
            {
                var builder = new MembershipViewBuilder(k);
                foreach (var (endpoint, nodeId) in nodes)
                {
                    builder.RingAdd(endpoint, nodeId);
                }
                var view = builder.Build();

                var detector = new MultiNodeCutDetector(h, l, view);

                var subject = nodes[0].Endpoint;

                // Report h-1 times - should not trigger
                var proposals = new List<List<Endpoint>>();
                for (var i = 0; i < h - 1 && i < nodes.Count - 1; i++)
                {
                    var result = detector.AggregateForProposal(
                        new AlertMessage
                        {
                            EdgeSrc = nodes[i + 1].Endpoint,
                            EdgeDst = subject,
                            EdgeStatus = EdgeStatus.Up,
                            RingNumber = { i }
                        });
                    proposals.Add(result);
                }

                // Should not have proposals yet (H threshold not reached)
                return proposals.All(p => !p.Contains(subject));
            });
    }

    [Fact]
    public void MultiNodeCutDetector_Does_Not_Duplicate_Reports_From_Same_Ring()
    {
        Gen.Select(GenK.Where(k => k >= 4), GenUniqueNodes(3, 10))
            .Sample((k, nodes) =>
            {
                var builder = new MembershipViewBuilder(k);
                foreach (var (endpoint, nodeId) in nodes)
                {
                    builder.RingAdd(endpoint, nodeId);
                }
                var view = builder.Build();

                // Use K > H >= L (e.g., K=k, H=k-1, L=1)
                var detector = new MultiNodeCutDetector(k - 1, 1, view);

                var observer = nodes[1].Endpoint;
                var subject = nodes[0].Endpoint;

                // Report multiple times from the same observer on the same ring
                var results = new List<List<Endpoint>>();
                for (var i = 0; i < 5; i++)
                {
                    var result = detector.AggregateForProposal(
                        new AlertMessage
                        {
                            EdgeSrc = observer,
                            EdgeDst = subject,
                            EdgeStatus = EdgeStatus.Up,
                            RingNumber = { 0 }  // Same ring each time
                        });
                    results.Add(result);
                }

                // Multiple reports from same observer/ring should not accumulate
                // (only the first one counts)
                // All results should be empty since we only have 1 unique ring report
                return results.All(r => r.Count == 0);
            });
    }

    #endregion

    #region Rank Properties (using protobuf Rank)

    [Fact]
    public void Rank_CompareTo_Is_Transitive()
    {
        Gen.Select(Gen.Int[0, 100], Gen.Int[0, 100], Gen.Int[0, 100],
                   Gen.Int[0, 100], Gen.Int[0, 100], Gen.Int[0, 100])
            .Sample((r1, n1, r2, n2, r3, n3) =>
            {
                var rank1 = new Rank { Round = r1, NodeIndex = n1 };
                var rank2 = new Rank { Round = r2, NodeIndex = n2 };
                var rank3 = new Rank { Round = r3, NodeIndex = n3 };

                // If rank1 <= rank2 and rank2 <= rank3, then rank1 <= rank3
                if (rank1.CompareTo(rank2) <= 0 && rank2.CompareTo(rank3) <= 0)
                {
                    return rank1.CompareTo(rank3) <= 0;
                }
                return true;
            });
    }

    [Fact]
    public void Rank_CompareTo_Is_Antisymmetric()
    {
        Gen.Select(Gen.Int[0, 100], Gen.Int[0, 100], Gen.Int[0, 100], Gen.Int[0, 100])
            .Sample((r1, n1, r2, n2) =>
            {
                var rank1 = new Rank { Round = r1, NodeIndex = n1 };
                var rank2 = new Rank { Round = r2, NodeIndex = n2 };

                // If rank1 <= rank2 and rank2 <= rank1, then rank1 == rank2
                if (rank1.CompareTo(rank2) <= 0 && rank2.CompareTo(rank1) <= 0)
                {
                    return rank1.CompareTo(rank2) == 0;
                }
                return true;
            });
    }

    [Fact]
    public void Rank_Higher_Round_Means_Higher_Rank()
    {
        Gen.Select(Gen.Int[0, 100], Gen.Int[0, 100], Gen.Int[0, 100])
            .Where(t => t.Item1 != t.Item2)
            .Sample((round1, round2, nodeIndex) =>
            {
                var rank1 = new Rank { Round = round1, NodeIndex = nodeIndex };
                var rank2 = new Rank { Round = round2, NodeIndex = nodeIndex };

                // Higher round number should mean higher rank
                if (round1 > round2)
                {
                    return rank1.CompareTo(rank2) > 0;
                }
                else
                {
                    return rank1.CompareTo(rank2) < 0;
                }
            });
    }

    #endregion

    #region ListEndpointComparer Properties

    [Fact]
    public void ListEndpointComparer_Equal_Lists_Have_Same_HashCode()
    {
        GenUniqueNodes(1, 10)
            .Sample(nodes =>
            {
                var list1 = nodes.Select(n => n.Endpoint).ToList();
                var list2 = nodes.Select(n => n.Endpoint).ToList();

                var comparer = ListEndpointComparer.Instance;

                return comparer.Equals(list1, list2) &&
                       comparer.GetHashCode(list1) == comparer.GetHashCode(list2);
            });
    }

    [Fact]
    public void ListEndpointComparer_Order_Matters()
    {
        GenUniqueNodes(2, 10)
            .Sample(nodes =>
            {
                var list1 = nodes.Select(n => n.Endpoint).ToList();
                var list2 = nodes.Select(n => n.Endpoint).Reverse().ToList();

                var comparer = ListEndpointComparer.Instance;

                // Lists with same elements in different order should NOT be equal
                // (SequenceEqual is order-sensitive)
                return !comparer.Equals(list1, list2) || list1.SequenceEqual(list2);
            });
    }

    [Fact]
    public void ListEndpointComparer_Different_Elements_Are_Not_Equal()
    {
        Gen.Select(GenUniqueNodes(2, 10), GenEndpoint)
            .Sample((nodes, extra) =>
            {
                var list1 = nodes.Select(n => n.Endpoint).ToList();
                var list2 = nodes.Select(n => n.Endpoint).ToList();
                list2.Add(extra);

                var comparer = ListEndpointComparer.Instance;

                // Lists with different elements should not be equal
                return !comparer.Equals(list1, list2);
            });
    }

    #endregion

    #region ConfigurationId Properties

    [Fact]
    public void ConfigurationId_Next_Increases_Version()
    {
        Gen.Long[1, 1000]
            .Sample(version =>
            {
                var config = new ConfigurationId(version);
                var next = config.Next();

                return next.Version == version + 1;
            });
    }

    [Fact]
    public void ConfigurationId_Equality_Works_Correctly()
    {
        Gen.Long[1, 1000]
            .Sample(version =>
            {
                var config1 = new ConfigurationId(version);
                var config2 = new ConfigurationId(version);
                var config3 = new ConfigurationId(version + 1);

                return config1 == config2 && config1 != config3;
            });
    }

    [Fact]
    public void ConfigurationId_Comparison_Works_Correctly()
    {
        Gen.Select(Gen.Long[1, 1000], Gen.Long[1, 1000])
            .Sample((v1, v2) =>
            {
                var config1 = new ConfigurationId(v1);
                var config2 = new ConfigurationId(v2);

                if (v1 < v2) return config1 < config2;
                if (v1 > v2) return config1 > config2;
                return config1 == config2;
            });
    }

    #endregion

    #region MetadataManager Properties

    [Fact]
    public void MetadataManager_Get_Returns_Set_Value()
    {
        Gen.Select(GenEndpoint, Gen.String[1, 100])
            .Sample((endpoint, metadataValue) =>
            {
                var manager = new MetadataManager();
                var metadata = new Metadata();
                metadata.Metadata_.Add("key", ByteString.CopyFromUtf8(metadataValue));

                manager.Add(endpoint, metadata);
                var retrieved = manager.Get(endpoint);

                return retrieved != null &&
                       retrieved.Metadata_.ContainsKey("key") &&
                       retrieved.Metadata_["key"].ToStringUtf8() == metadataValue;
            });
    }

    [Fact]
    public void MetadataManager_Get_Returns_Null_For_Unknown_Endpoint()
    {
        Gen.Select(GenEndpoint, GenEndpoint)
            .Where(t => !t.Item1.Equals(t.Item2))
            .Sample((stored, queried) =>
            {
                var manager = new MetadataManager();
                var metadata = new Metadata();
                metadata.Metadata_.Add("key", ByteString.CopyFromUtf8("value"));

                manager.Add(stored, metadata);
                var retrieved = manager.Get(queried);

                return retrieved == null;
            });
    }

    [Fact]
    public void MetadataManager_RemoveNode_Removes_Metadata()
    {
        Gen.Select(GenEndpoint, Gen.String[1, 100])
            .Sample((endpoint, metadataValue) =>
            {
                var manager = new MetadataManager();
                var metadata = new Metadata();
                metadata.Metadata_.Add("key", ByteString.CopyFromUtf8(metadataValue));

                manager.Add(endpoint, metadata);
                manager.RemoveNode(endpoint);
                var retrieved = manager.Get(endpoint);

                return retrieved == null;
            });
    }

    #endregion
}
