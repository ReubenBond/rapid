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

using System.Collections.Concurrent;
using System.IO.Hashing;
using Google.Protobuf;
using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Hosts K permutations of the memberlist that represent the monitoring relationship between nodes;
/// every node (an observer) observers its successor (a subject) on each ring.
/// </summary>
internal sealed class MembershipView
{
    private readonly int _k;
    private readonly ReaderWriterLockSlim _rwLock = new();
    private readonly List<AddressComparator> _addressComparators;
    private readonly List<SortedSet<Endpoint>> _rings;
    private readonly SortedSet<NodeId> _identifiersSeen;
    private readonly Dictionary<Endpoint, List<Endpoint>> _cachedObservers = new();
    private readonly HashSet<Endpoint> _allNodes = new();
    private long _currentConfigurationId = -1;
    private Configuration _currentConfiguration;
    private bool _shouldUpdateConfigurationId = true;

    public MembershipView(int k)
    {
        if (k <= 0) throw new ArgumentException("K must be positive", nameof(k));
        
        _k = k;
        _rings = new List<SortedSet<Endpoint>>(k);
        _addressComparators = new List<AddressComparator>(k);
        _identifiersSeen = new SortedSet<NodeId>(NodeIdComparer.Instance);
        
        for (int i = 0; i < k; i++)
        {
            var comparator = new AddressComparator(i);
            _addressComparators.Add(comparator);
            _rings.Add(new SortedSet<Endpoint>(comparator));
        }
        
        _currentConfiguration = new Configuration(_identifiersSeen, _rings[0]);
    }

    public MembershipView(int k, ICollection<NodeId> nodeIds, ICollection<Endpoint> endpoints)
    {
        if (k <= 0) throw new ArgumentException("K must be positive", nameof(k));
        
        _k = k;
        _rings = new List<SortedSet<Endpoint>>(k);
        _addressComparators = new List<AddressComparator>(k);
        _identifiersSeen = new SortedSet<NodeId>(NodeIdComparer.Instance);
        
        for (int i = 0; i < k; i++)
        {
            var comparator = new AddressComparator(i);
            _addressComparators.Add(comparator);
            var set = new SortedSet<Endpoint>(comparator);
            foreach (var endpoint in endpoints)
            {
                set.Add(endpoint);
                _allNodes.Add(endpoint);
            }
            _rings.Add(set);
        }
        
        foreach (var nodeId in nodeIds)
        {
            _identifiersSeen.Add(nodeId);
        }
        
        _currentConfiguration = new Configuration(_identifiersSeen, _rings[0]);
    }

    public JoinStatusCode IsSafeToJoin(Endpoint node, NodeId uuid)
    {
        _rwLock.EnterReadLock();
        try
        {
            if (_allNodes.Contains(node))
            {
                return JoinStatusCode.HostnameAlreadyInRing;
            }

            if (_identifiersSeen.Contains(uuid))
            {
                return JoinStatusCode.UuidAlreadyInRing;
            }

            return JoinStatusCode.SafeToJoin;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public void RingAdd(Endpoint node, NodeId nodeId)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(nodeId);

        if (IsIdentifierPresent(nodeId))
        {
            throw new UuidAlreadySeenException(node, nodeId);
        }

        _rwLock.EnterWriteLock();
        try
        {
            if (_rings[0].Contains(node))
            {
                throw new NodeAlreadyInRingException(node);
            }

            var affectedSubjects = new HashSet<Endpoint>();

            for (int k = 0; k < _k; k++)
            {
                var endpoints = _rings[k];
                endpoints.Add(node);

                var subject = GetLower(endpoints, node);
                if (subject != null)
                {
                    affectedSubjects.Add(subject);
                }
            }
            _allNodes.Add(node);

            foreach (var subject in affectedSubjects)
            {
                _cachedObservers.Remove(subject);
            }

            _identifiersSeen.Add(nodeId);
            _shouldUpdateConfigurationId = true;
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    public void RingDelete(Endpoint node)
    {
        ArgumentNullException.ThrowIfNull(node);
        
        _rwLock.EnterWriteLock();
        try
        {
            if (!_rings[0].Contains(node))
            {
                throw new NodeNotInRingException(node);
            }

            var affectedSubjects = new HashSet<Endpoint>();

            for (int k = 0; k < _k; k++)
            {
                var endpoints = _rings[k];

                var oldSubject = GetLower(endpoints, node);
                if (oldSubject != null)
                {
                    affectedSubjects.Add(oldSubject);
                }

                endpoints.Remove(node);
                _addressComparators[k].RemoveEndpoint(node);
                _cachedObservers.Remove(node);
            }
            _allNodes.Remove(node);

            foreach (var subject in affectedSubjects)
            {
                _cachedObservers.Remove(subject);
            }

            _shouldUpdateConfigurationId = true;
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    public List<Endpoint> GetObserversOf(Endpoint node)
    {
        ArgumentNullException.ThrowIfNull(node);
        
        _rwLock.EnterReadLock();
        try
        {
            if (!_allNodes.Contains(node))
            {
                throw new NodeNotInRingException(node);
            }
            
            if (!_cachedObservers.TryGetValue(node, out var observers))
            {
                observers = ComputeObserversOf(node);
                _cachedObservers[node] = observers;
            }
            return observers;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    private List<Endpoint> ComputeObserversOf(Endpoint node)
    {
        ArgumentNullException.ThrowIfNull(node);
        
        if (!_rings[0].Contains(node))
        {
            throw new NodeNotInRingException(node);
        }

        if (_rings[0].Count <= 1)
        {
            return new List<Endpoint>();
        }

        var observers = new List<Endpoint>();

        for (int k = 0; k < _k; k++)
        {
            var list = _rings[k];
            var successor = GetHigher(list, node);
            if (successor == null)
            {
                observers.Add(list.Min!);
            }
            else
            {
                observers.Add(successor);
            }
        }
        return observers;
    }

    public List<Endpoint> GetSubjectsOf(Endpoint node)
    {
        ArgumentNullException.ThrowIfNull(node);
        
        _rwLock.EnterReadLock();
        try
        {
            if (!_allNodes.Contains(node))
            {
                throw new NodeNotInRingException(node);
            }

            if (_rings[0].Count <= 1)
            {
                return new List<Endpoint>();
            }
            
            return GetPredecessorsOf(node);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public List<Endpoint> GetExpectedObserversOf(Endpoint node)
    {
        ArgumentNullException.ThrowIfNull(node);
        
        _rwLock.EnterReadLock();
        try
        {
            if (_rings[0].Count == 0)
            {
                return new List<Endpoint>();
            }
            return GetPredecessorsOf(node);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    private List<Endpoint> GetPredecessorsOf(Endpoint node)
    {
        var subjects = new List<Endpoint>();

        for (int k = 0; k < _k; k++)
        {
            var list = _rings[k];
            var predecessor = GetLower(list, node);
            if (predecessor == null)
            {
                subjects.Add(list.Max!);
            }
            else
            {
                subjects.Add(predecessor);
            }
        }
        return subjects;
    }

    public bool IsHostPresent(Endpoint address)
    {
        _rwLock.EnterReadLock();
        try
        {
            return _allNodes.Contains(address);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public bool IsIdentifierPresent(NodeId identifier)
    {
        _rwLock.EnterReadLock();
        try
        {
            return _identifiersSeen.Contains(identifier);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public long GetCurrentConfigurationId()
    {
        _rwLock.EnterReadLock();
        try
        {
            if (_shouldUpdateConfigurationId)
            {
                UpdateCurrentConfigurationId();
                _shouldUpdateConfigurationId = false;
            }
            return _currentConfigurationId;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public List<Endpoint> GetRing(int k)
    {
        _rwLock.EnterReadLock();
        try
        {
            if (k < 0) throw new ArgumentOutOfRangeException(nameof(k));
            return new List<Endpoint>(_rings[k]);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public List<int> GetRingNumbers(Endpoint observer, Endpoint subject)
    {
        _rwLock.EnterReadLock();
        try
        {
            var subjects = GetSubjectsOf(observer);
            if (subjects.Count == 0)
            {
                return new List<int>();
            }

            var ringIndexes = new List<int>();
            int ringNumber = 0;
            foreach (var node in subjects)
            {
                if (node.Equals(subject))
                {
                    ringIndexes.Add(ringNumber);
                }
                ringNumber++;
            }
            return ringIndexes;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public int GetMembershipSize()
    {
        _rwLock.EnterReadLock();
        try
        {
            return _rings[0].Count;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    private void UpdateCurrentConfigurationId()
    {
        _currentConfiguration = new Configuration(_identifiersSeen, _rings[0]);
        _currentConfigurationId = _currentConfiguration.GetConfigurationId();
    }

    public Configuration GetConfiguration()
    {
        _rwLock.EnterReadLock();
        try
        {
            if (_shouldUpdateConfigurationId)
            {
                UpdateCurrentConfigurationId();
                _shouldUpdateConfigurationId = false;
            }
            return _currentConfiguration;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public AddressComparator GetRingZeroComparator()
    {
        return _addressComparators[0];
    }

    private static Endpoint? GetLower(SortedSet<Endpoint> set, Endpoint value)
    {
        return set.GetViewBetween(set.Min!, value).Where(e => !e.Equals(value)).LastOrDefault();
    }

    private static Endpoint? GetHigher(SortedSet<Endpoint> set, Endpoint value)
    {
        var max = set.Max!;
        if (value.Equals(max)) return null;
        return set.GetViewBetween(value, max).Where(e => !e.Equals(value)).FirstOrDefault();
    }

    public sealed class NodeAlreadyInRingException : Exception
    {
        public NodeAlreadyInRingException(Endpoint node) : base(node.ToString()) { }
    }

    public sealed class NodeNotInRingException : Exception
    {
        public NodeNotInRingException(Endpoint node) : base(node.ToString()) { }
    }

    public sealed class UuidAlreadySeenException : Exception
    {
        public UuidAlreadySeenException(Endpoint node, NodeId nodeId)
            : base($"Endpoint add attempt with identifier already seen: {{host: {node}, identifier: {nodeId}}}") { }
    }

    public sealed class Configuration
    {
        public List<NodeId> NodeIds { get; }
        public List<Endpoint> Endpoints { get; }

        public Configuration(IEnumerable<NodeId> nodeIds, IEnumerable<Endpoint> endpoints)
        {
            NodeIds = new List<NodeId>(nodeIds);
            Endpoints = new List<Endpoint>(endpoints);
        }

        public long GetConfigurationId()
        {
            return GetConfigurationId(NodeIds, Endpoints);
        }

        public static long GetConfigurationId(IEnumerable<NodeId> identifiers, IEnumerable<Endpoint> endpoints)
        {
            long hash = 1;
            foreach (var id in identifiers)
            {
                hash = hash * 37 + (long)XxHash64.HashToUInt64(BitConverter.GetBytes(id.High));
                hash = hash * 37 + (long)XxHash64.HashToUInt64(BitConverter.GetBytes(id.Low));
            }
            foreach (var endpoint in endpoints)
            {
                hash = hash * 37 + (long)XxHash64.HashToUInt64(endpoint.Hostname.Span);
                hash = hash * 37 + (long)XxHash64.HashToUInt64(BitConverter.GetBytes(endpoint.Port));
            }
            return hash;
        }
    }

    public sealed class AddressComparator : IComparer<Endpoint>
    {
        private readonly int _seed;
        private readonly ConcurrentDictionary<Endpoint, long> _hashCache = new();

        public AddressComparator(int seed)
        {
            _seed = seed;
        }

        public int Compare(Endpoint? x, Endpoint? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            var hash1 = _hashCache.GetOrAdd(x, ComputeHash);
            var hash2 = _hashCache.GetOrAdd(y, ComputeHash);
            return hash1.CompareTo(hash2);
        }

        private long ComputeHash(Endpoint endpoint)
        {
            var hostnameHash = (long)XxHash64.HashToUInt64(endpoint.Hostname.Span, _seed);
            var portHash = (long)XxHash64.HashToUInt64(BitConverter.GetBytes(endpoint.Port), _seed);
            return hostnameHash * 31 + portHash;
        }

        public void RemoveEndpoint(Endpoint endpoint)
        {
            _hashCache.TryRemove(endpoint, out _);
        }
    }

    private sealed class NodeIdComparer : IComparer<NodeId>
    {
        public static readonly NodeIdComparer Instance = new();

        private NodeIdComparer() { }

        public int Compare(NodeId? x, NodeId? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            // First, compare high bits
            if (x.High < y.High) return -1;
            if (x.High > y.High) return 1;

            // High bits are equal, so compare low bits
            if (x.Low < y.Low) return -1;
            if (x.Low > y.Low) return 1;

            return 0;
        }
    }
}
