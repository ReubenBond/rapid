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
/// Tests for multi node cut detection
/// </summary>
public class MultiNodeCutDetectorTests
{
    private const int K = 10;
    private const int H = 8;
    private const int L = 2;
    private const long ConfigurationId = -1; // Should not affect the following tests

    private static AlertMessage CreateAlertMessage(Endpoint src, Endpoint dst, EdgeStatus status,
        long configurationId, int ringNumber)
    {
        var msg = new AlertMessage
        {
            EdgeSrc = src,
            EdgeDst = dst,
            EdgeStatus = status,
            ConfigurationId = configurationId
        };
        msg.RingNumber.Add(ringNumber);
        return msg;
    }

    /// <summary>
    /// A series of updates with the right ring indexes
    /// </summary>
    [Fact]
    public void CutDetectionTest()
    {
        var detector = new MultiNodeCutDetector(K, H, L);
        var dst = Utils.HostFromParts("127.0.0.2", 2);
        List<Endpoint> ret;

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Single(ret);
        Assert.Equal(1, detector.GetNumProposals());
    }

    [Fact]
    public void CutDetectionTestBlockingOneBlocker()
    {
        var detector = new MultiNodeCutDetector(K, H, L);
        var dst1 = Utils.HostFromParts("127.0.0.2", 2);
        var dst2 = Utils.HostFromParts("127.0.0.3", 2);
        List<Endpoint> ret;

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst1, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst2, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst1, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Empty(ret);
        Assert.Equal(0, detector.GetNumProposals());

        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst2, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Equal(2, ret.Count);
        Assert.Equal(1, detector.GetNumProposals());
    }

    [Fact]
    public void CutDetectionTestBlockingThreeBlockers()
    {
        var detector = new MultiNodeCutDetector(K, H, L);
        var dst1 = Utils.HostFromParts("127.0.0.2", 2);
        var dst2 = Utils.HostFromParts("127.0.0.3", 2);
        var dst3 = Utils.HostFromParts("127.0.0.4", 2);
        List<Endpoint> ret;

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst1, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst2, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst3, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst1, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Empty(ret);
        Assert.Equal(0, detector.GetNumProposals());

        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst3, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Empty(ret);
        Assert.Equal(0, detector.GetNumProposals());

        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst2, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Equal(3, ret.Count);
        Assert.Equal(1, detector.GetNumProposals());
    }

    [Fact]
    public void CutDetectionTestBlockingMultipleBlockersPastH()
    {
        var detector = new MultiNodeCutDetector(K, H, L);
        var dst1 = Utils.HostFromParts("127.0.0.2", 2);
        var dst2 = Utils.HostFromParts("127.0.0.3", 2);
        var dst3 = Utils.HostFromParts("127.0.0.4", 2);
        List<Endpoint> ret;

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst1, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst2, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst3, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        // Unlike the previous test, add more reports for dst1 and dst3 past the H boundary.
        detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst1, EdgeStatus.Up, ConfigurationId, H - 1));
        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H + 1), dst1, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Empty(ret);
        Assert.Equal(0, detector.GetNumProposals());

        detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst3, EdgeStatus.Up, ConfigurationId, H - 1));
        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H + 1), dst3, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Empty(ret);
        Assert.Equal(0, detector.GetNumProposals());

        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst2, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Equal(3, ret.Count);
        Assert.Equal(1, detector.GetNumProposals());
    }

    [Fact]
    public void CutDetectionTestBelowL()
    {
        var detector = new MultiNodeCutDetector(K, H, L);
        var dst1 = Utils.HostFromParts("127.0.0.2", 2);
        var dst2 = Utils.HostFromParts("127.0.0.3", 2);
        var dst3 = Utils.HostFromParts("127.0.0.4", 2);
        List<Endpoint> ret;

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst1, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        // Unlike the previous test, dst2 has < L updates
        for (var i = 0; i < L - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst2, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst3, EdgeStatus.Up, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst1, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Empty(ret);
        Assert.Equal(0, detector.GetNumProposals());

        ret = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", H), dst3, EdgeStatus.Up, ConfigurationId, H - 1));
        Assert.Equal(2, ret.Count);
        Assert.Equal(1, detector.GetNumProposals());
    }

    [Fact]
    public void CutDetectionTestBatch()
    {
        var detector = new MultiNodeCutDetector(K, H, L);
        const int numNodes = 3;
        var endpoints = new List<Endpoint>();

        for (var i = 0; i < numNodes; i++)
        {
            endpoints.Add(Utils.HostFromParts("127.0.0.2", 2 + i));
        }

        var proposal = new List<Endpoint>();
        foreach (var endpoint in endpoints)
        {
            for (var ringNumber = 0; ringNumber < K; ringNumber++)
            {
                proposal.AddRange(detector.AggregateForProposal(CreateAlertMessage(
                    Utils.HostFromParts("127.0.0.1", 1), endpoint, EdgeStatus.Up,
                    ConfigurationId, ringNumber)));
            }
        }

        Assert.Equal(numNodes, proposal.Count);
    }

    [Fact]
    public void CutDetectionTestLinkInvalidation()
    {
        using var mView = new MembershipView(K);
        var detector = new MultiNodeCutDetector(K, H, L);
        const int numNodes = 30;
        var endpoints = new List<Endpoint>();

        for (var i = 0; i < numNodes; i++)
        {
            var node = Utils.HostFromParts("127.0.0.2", 2 + i);
            endpoints.Add(node);
            mView.RingAdd(node, Utils.NodeIdFromUuid(Guid.NewGuid()));
        }

        var dst = endpoints[0];
        var observers = mView.GetObserversOf(dst);
        Assert.Equal(K, observers.Count);

        List<Endpoint> ret;

        // This adds alerts from the observers[0, H - 1) of node dst.
        for (var i = 0; i < H - 1; i++)
        {
            ret = detector.AggregateForProposal(CreateAlertMessage(observers[i], dst,
                EdgeStatus.Down, ConfigurationId, i));
            Assert.Empty(ret);
            Assert.Equal(0, detector.GetNumProposals());
        }

        // Next, we add alerts *about* observers[H, K) of node dst.
        var failedObservers = new HashSet<Endpoint>();
        for (var i = H - 1; i < K; i++)
        {
            var observersOfObserver = mView.GetObserversOf(observers[i]);
            failedObservers.Add(observers[i]);
            for (var j = 0; j < K; j++)
            {
                ret = detector.AggregateForProposal(CreateAlertMessage(observersOfObserver[j], observers[i],
                    EdgeStatus.Down, ConfigurationId, j));
                Assert.Empty(ret);
                Assert.Equal(0, detector.GetNumProposals());
            }
        }

        // At this point, (K - H - 1) observers of dst will be past H, and dst will be in H - 1. 
        // Link invalidation should bring the failed observers and dst to the stable region.
        ret = detector.InvalidateFailingEdges(mView);
        Assert.Equal(4, ret.Count);
        Assert.Equal(1, detector.GetNumProposals());

        foreach (var node in ret)
        {
            Assert.True(failedObservers.Contains(node) || node.Equals(dst));
        }
    }
}
