using System.IO.Hashing;
using System.Runtime.InteropServices;
using Rapid.Exceptions;
using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Hosts K permutations of the memberlist that represent the monitoring relationship between nodes;
/// every node (an observer) observers its successor (a subject) on each ring.
/// This is the internal mutable version used by MembershipService for ring management.
/// </summary>
internal sealed class MutableMembershipView
{
    private readonly int _k;
    private readonly Lock _lock = new();
    private readonly List<AddressComparator> _addressComparators;
    private readonly List<SortedSet<Endpoint>> _rings;
    private readonly SortedSet<NodeId> _identifiersSeen;
    private readonly Dictionary<Endpoint, List<Endpoint>> _cachedObservers = [];
    private readonly HashSet<Endpoint> _allNodes = [];
    private long _currentConfigurationId = -1;
    private bool _shouldUpdateConfigurationId = true;

    /// <summary>
    /// Initializes a new instance of the MutableMembershipView class with the specified number of rings.
    /// </summary>
    /// <param name="k">Number of monitoring rings to maintain. Must be positive.</param>
    /// <exception cref="ArgumentException">Thrown when k is not positive.</exception>
    public MutableMembershipView(int k)
    {
        if (k <= 0) throw new ArgumentException("K must be positive", nameof(k));

        _k = k;
        _rings = new List<SortedSet<Endpoint>>(k);
        _addressComparators = new List<AddressComparator>(k);
        _identifiersSeen = new SortedSet<NodeId>(NodeIdComparer.Instance);

        for (var i = 0; i < k; i++)
        {
            var comparator = new AddressComparator(i);
            _addressComparators.Add(comparator);
            _rings.Add(new SortedSet<Endpoint>(comparator));
        }
    }

    /// <summary>
    /// Used to bootstrap a membership view from the fields of a MembershipView.Configuration object.
    /// </summary>
    /// <param name="k">Number of monitoring rings to maintain.</param>
    /// <param name="nodeIds">Collection of node identifiers to add.</param>
    /// <param name="endpoints">Collection of endpoints corresponding to the node IDs.</param>
    /// <exception cref="ArgumentException">Thrown when nodeIds and endpoints counts don't match.</exception>
    public MutableMembershipView(int k, ICollection<NodeId> nodeIds, ICollection<Endpoint> endpoints)
    {
        if (k <= 0) throw new ArgumentException("K must be positive", nameof(k));

        _k = k;
        _rings = new List<SortedSet<Endpoint>>(k);
        _addressComparators = new List<AddressComparator>(k);
        _identifiersSeen = new SortedSet<NodeId>(NodeIdComparer.Instance);

        for (var i = 0; i < k; i++)
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
    }

    /// <summary>
    /// Gets the number of rings (K value).
    /// </summary>
    public int K => _k;

    /// <summary>
    /// Queries if a host with a logical identifier <paramref name="uuid"/> is safe to add to the network.
    /// </summary>
    /// <param name="node">The joining node.</param>
    /// <param name="uuid">The joining node's identifier.</param>
    /// <returns>
    /// HOSTNAME_ALREADY_IN_RING if the <paramref name="node"/> is already in the ring.
    /// UUID_ALREADY_IN_RING if the <paramref name="uuid"/> is already seen before.
    /// SAFE_TO_JOIN otherwise.
    /// </returns>
    public JoinStatusCode IsSafeToJoin(Endpoint node, NodeId uuid)
    {
        lock (_lock)
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
    }

    /// <summary>
    /// Add a node to all K rings and records its unique identifier.
    /// </summary>
    /// <param name="node">The node to be added.</param>
    /// <param name="nodeId">The logical identifier of the node being added.</param>
    /// <exception cref="NodeAlreadyInRingException">Thrown if the node is already in the ring.</exception>
    /// <exception cref="UuidAlreadySeenException">Thrown if the node ID has been seen before.</exception>
    public void RingAdd(Endpoint node, NodeId nodeId)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(nodeId);

        if (IsIdentifierPresent(nodeId))
        {
            throw new UuidAlreadySeenException(node, nodeId);
        }

        _lock.Enter();
        try
        {
            if (_rings[0].Contains(node))
            {
                throw new NodeAlreadyInRingException(node);
            }

            var affectedSubjects = new HashSet<Endpoint>();

            for (var k = 0; k < _k; k++)
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
            _lock.Exit();
        }
    }

    /// <summary>
    /// Delete a host from all K rings.
    /// </summary>
    /// <param name="node">The host to be removed.</param>
    /// <exception cref="NodeNotInRingException">Thrown if the node is not in the ring.</exception>
    public void RingDelete(Endpoint node)
    {
        ArgumentNullException.ThrowIfNull(node);

        _lock.Enter();
        try
        {
            if (!_rings[0].Contains(node))
            {
                throw new NodeNotInRingException(node);
            }

            var affectedSubjects = new HashSet<Endpoint>();

            for (var k = 0; k < _k; k++)
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
            _lock.Exit();
        }
    }

    /// <summary>
    /// Returns the set of observers for <paramref name="node"/>.
    /// </summary>
    /// <param name="node">Input node.</param>
    /// <returns>The set of observers for <paramref name="node"/>.</returns>
    /// <exception cref="NodeNotInRingException">Thrown if <paramref name="node"/> is not in the ring.</exception>
    public List<Endpoint> GetObserversOf(Endpoint node)
    {
        ArgumentNullException.ThrowIfNull(node);

        lock (_lock)
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
    }

    /// <summary>
    /// Computes the set of observers for <paramref name="node"/>.
    /// Only call this from a (thread-)safe place!
    /// </summary>
    /// <param name="node">Input node.</param>
    /// <returns>The set of observers for <paramref name="node"/>.</returns>
    /// <exception cref="NodeNotInRingException">Thrown if <paramref name="node"/> is not in the ring.</exception>
    private List<Endpoint> ComputeObserversOf(Endpoint node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!_rings[0].Contains(node))
        {
            throw new NodeNotInRingException(node);
        }

        if (_rings[0].Count <= 1)
        {
            return [];
        }

        var observers = new List<Endpoint>();

        for (var k = 0; k < _k; k++)
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

    /// <summary>
    /// Returns the set of nodes monitored by <paramref name="node"/>.
    /// </summary>
    /// <param name="node">Input node.</param>
    /// <returns>The set of nodes monitored by <paramref name="node"/>.</returns>
    /// <exception cref="NodeNotInRingException">Thrown if <paramref name="node"/> is not in the ring.</exception>
    public List<Endpoint> GetSubjectsOf(Endpoint node)
    {
        ArgumentNullException.ThrowIfNull(node);

        lock (_lock)
        {
            if (!_allNodes.Contains(node))
            {
                throw new NodeNotInRingException(node);
            }

            if (_rings[0].Count <= 1)
            {
                return [];
            }

            return GetPredecessorsOf(node);
        }
    }

    /// <summary>
    /// Returns the expected observers of <paramref name="node"/>, even before it is
    /// added to the ring. Used during the bootstrap protocol to identify
    /// the nodes responsible for gatekeeping a joining peer.
    /// </summary>
    /// <param name="node">Input node.</param>
    /// <returns>The list of nodes monitored by <paramref name="node"/>. Empty list if the membership is empty.</returns>
    public List<Endpoint> GetExpectedObserversOf(Endpoint node)
    {
        ArgumentNullException.ThrowIfNull(node);

        lock (_lock)
        {
            if (_rings[0].Count == 0)
            {
                return [];
            }
            return GetPredecessorsOf(node);
        }
    }

    /// <summary>
    /// Used by GetExpectedObserversOf() and GetSubjectsOf().
    /// </summary>
    private List<Endpoint> GetPredecessorsOf(Endpoint node)
    {
        var subjects = new List<Endpoint>();

        for (var k = 0; k < _k; k++)
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

    /// <summary>
    /// Query if a host is part of the current membership set.
    /// </summary>
    /// <param name="address">The host.</param>
    /// <returns>True if the node is present in the membership view and false otherwise.</returns>
    public bool IsHostPresent(Endpoint address)
    {
        lock (_lock)
        {
            return _allNodes.Contains(address);
        }
    }

    /// <summary>
    /// Query if an identifier has been used by a node already.
    /// </summary>
    /// <param name="identifier">The identifier to query for.</param>
    /// <returns>True if the identifier has been seen before and false otherwise.</returns>
    public bool IsIdentifierPresent(NodeId identifier)
    {
        lock (_lock)
        {
            return _identifiersSeen.Contains(identifier);
        }
    }

    /// <summary>
    /// Get the current identifier of the configuration. Computed based on the
    /// set of nodes in the view as well as the identifiers seen so far.
    /// </summary>
    /// <returns>The current configuration identifier.</returns>
    public long GetCurrentConfigurationId()
    {
        lock (_lock)
        {
            if (_shouldUpdateConfigurationId)
            {
                UpdateCurrentConfigurationId();
                _shouldUpdateConfigurationId = false;
            }
            return _currentConfigurationId;
        }
    }

    /// <summary>
    /// Get the list of endpoints in the k'th ring.
    /// </summary>
    /// <param name="k">The index of the ring to query.</param>
    /// <returns>The list of endpoints in the k'th ring.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if k is out of range.</exception>
    public List<Endpoint> GetRing(int k)
    {
        lock (_lock)
        {
            if (k < 0) throw new ArgumentOutOfRangeException(nameof(k));
            return [.. _rings[k]];
        }
    }

    /// <summary>
    /// Get the ring number of an observer for a given subject.
    /// </summary>
    /// <param name="observer">The observer node.</param>
    /// <param name="subject">The subject node.</param>
    /// <returns>The indexes k such that <paramref name="observer"/> is a successor of <paramref name="subject"/> on ring[k].</returns>
    public List<int> GetRingNumbers(Endpoint observer, Endpoint subject)
    {
        lock (_lock)
        {
            var subjects = GetSubjectsOf(observer);
            if (subjects.Count == 0)
            {
                return [];
            }

            var ringIndexes = new List<int>();
            var ringNumber = 0;
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
    }

    /// <summary>
    /// Get the number of nodes currently in the membership.
    /// </summary>
    /// <returns>The number of nodes in the membership.</returns>
    public int GetMembershipSize()
    {
        lock (_lock)
        {
            return _rings[0].Count;
        }
    }

    /// <summary>
    /// XXX: May not be stable across processes. Verify.
    /// </summary>
    private void UpdateCurrentConfigurationId()
    {
        _currentConfigurationId = MembershipViewConfiguration.GetConfigurationId(_identifiersSeen, _rings[0]);
    }

    /// <summary>
    /// Creates an immutable MembershipView snapshot of the current state.
    /// </summary>
    /// <returns>An immutable MembershipView instance.</returns>
    public MembershipView ToImmutableView()
    {
        lock (_lock)
        {
            if (_shouldUpdateConfigurationId)
            {
                UpdateCurrentConfigurationId();
                _shouldUpdateConfigurationId = false;
            }
            return new MembershipView(_k, _currentConfigurationId, [.. _rings[0]], [.. _identifiersSeen]);
        }
    }

    private static Endpoint? GetLower(SortedSet<Endpoint> set, Endpoint value)
    {
        if (set.Count == 0) return null;

        var min = set.Min!;
        // If value is less than or equal to min, there is no lower element
        if (set.Comparer.Compare(value, min) <= 0) return null;

        return set.GetViewBetween(min, value).Where(e => !e.Equals(value)).LastOrDefault();
    }

    private static Endpoint? GetHigher(SortedSet<Endpoint> set, Endpoint value)
    {
        var max = set.Max!;
        if (value.Equals(max)) return null;
        return set.GetViewBetween(value, max).Where(e => !e.Equals(value)).FirstOrDefault();
    }

    /// <summary>
    /// Used to order endpoints in the different rings.
    /// </summary>
    private sealed class AddressComparator(int seed) : IComparer<Endpoint>
    {
        private readonly int _seed = seed;
        private readonly Dictionary<Endpoint, long> _hashCache = [];
        private readonly object _hashCacheLock = new();

        public int Compare(Endpoint? x, Endpoint? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            var hash1 = GetCachedHash(x);
            var hash2 = GetCachedHash(y);
            return hash1.CompareTo(hash2);
        }

        private static long ComputeHash(int seed, Endpoint endpoint)
        {
            var hostnameHash = (long)XxHash64.HashToUInt64(endpoint.Hostname.Span, seed);
            var portHash = (long)XxHash64.HashToUInt64(BitConverter.GetBytes(endpoint.Port), seed);
            return hostnameHash * 31 + portHash;
        }

        public void RemoveEndpoint(Endpoint endpoint)
        {
            lock (_hashCacheLock)
            {
                _hashCache.Remove(endpoint, out _);
            }
        }

        private long GetCachedHash(Endpoint endpoint)
        {
            lock (_hashCacheLock)
            {
                ref var hash = ref CollectionsMarshal.GetValueRefOrAddDefault(_hashCache, endpoint, out var exists);
                if (!exists)
                {
                    hash = ComputeHash(_seed, endpoint);
                }
                return hash;
            }
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
