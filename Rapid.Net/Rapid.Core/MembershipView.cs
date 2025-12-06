using System.IO.Hashing;
using System.Runtime.InteropServices;
using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Hosts K permutations of the memberlist that represent the monitoring relationship between nodes;
/// every node (an observer) observers its successor (a subject) on each ring.
/// </summary>
internal sealed class MembershipView : IDisposable
{
    private readonly int _k;
    private readonly ReaderWriterLockSlim _rwLock = new();
    private readonly List<AddressComparator> _addressComparators;
    private readonly List<SortedSet<Endpoint>> _rings;
    private readonly SortedSet<NodeId> _identifiersSeen;
    private readonly Dictionary<Endpoint, List<Endpoint>> _cachedObservers = [];
    private readonly HashSet<Endpoint> _allNodes = [];
    private long _currentConfigurationId = -1;
    private Configuration _currentConfiguration;
    private bool _shouldUpdateConfigurationId = true;

    /// <summary>
    /// Initializes a new instance of the MembershipView class with the specified number of rings.
    /// </summary>
    /// <param name="k">Number of monitoring rings to maintain. Must be positive.</param>
    /// <exception cref="ArgumentException">Thrown when k is not positive.</exception>
    public MembershipView(int k)
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

        _currentConfiguration = new Configuration(_identifiersSeen, _rings[0]);
    }

    /// <summary>
    /// Initializes a new instance of the MembershipView class with the specified number of rings
    /// and pre-populated with the given nodes.
    /// </summary>
    /// <param name="k">Number of monitoring rings to maintain.</param>
    /// <param name="nodeIds">Collection of node identifiers to add.</param>
    /// <param name="endpoints">Collection of endpoints corresponding to the node IDs.</param>
    /// <exception cref="ArgumentException">Thrown when nodeIds and endpoints counts don't match.</exception>
    public MembershipView(int k, ICollection<NodeId> nodeIds, ICollection<Endpoint> endpoints)
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

        _currentConfiguration = new Configuration(_identifiersSeen, _rings[0]);
    }

    /// <summary>
    /// Checks whether it is safe for a node to join the membership view.
    /// </summary>
    /// <param name="node">The endpoint of the node attempting to join.</param>
    /// <param name="uuid">The unique identifier of the node attempting to join.</param>
    /// <returns>
    /// A JoinStatusCode indicating whether the join is safe:
    /// SAFE_TO_JOIN, HOSTNAME_ALREADY_IN_RING, UUID_ALREADY_IN_RING, or CONFIG_CHANGED.
    /// </returns>
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

    /// <summary>
    /// Adds a node to all K rings in the membership view.
    /// </summary>
    /// <param name="node">The endpoint of the node to add.</param>
    /// <param name="nodeId">The unique identifier for the node.</param>
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

        _rwLock.EnterWriteLock();
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
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Removes a node from all K rings in the membership view.
    /// </summary>
    /// <param name="node">The endpoint of the node to remove.</param>
    /// <exception cref="NodeNotInRingException">Thrown if the node is not in the ring.</exception>
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
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Gets the list of observers monitoring the given node across all K rings.
    /// An observer is a node that monitors its successor on a ring.
    /// </summary>
    /// <param name="node">The node being monitored.</param>
    /// <returns>A list of endpoints that are observers of the given node.</returns>
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
    /// Gets the list of subjects that the given node is monitoring across all K rings.
    /// A subject is a node being monitored by its predecessor on a ring.
    /// </summary>
    /// <param name="node">The observing node.</param>
    /// <returns>A list of endpoints that the given node is monitoring.</returns>
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
                return [];
            }

            return GetPredecessorsOf(node);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets the expected list of observers for a node that hasn't been added to the rings yet.
    /// This is used during the join protocol to determine which nodes should monitor the joining node.
    /// </summary>
    /// <param name="node">The node to calculate expected observers for.</param>
    /// <returns>A list of endpoints that would observe this node if it were added.</returns>
    public List<Endpoint> GetExpectedObserversOf(Endpoint node)
    {
        ArgumentNullException.ThrowIfNull(node);

        _rwLock.EnterReadLock();
        try
        {
            if (_rings[0].Count == 0)
            {
                return [];
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
    /// Checks if a host endpoint is present in the membership view.
    /// </summary>
    /// <param name="address">The endpoint to check.</param>
    /// <returns>True if the endpoint is present; otherwise, false.</returns>
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

    /// <summary>
    /// Checks if a node identifier has been seen before in the membership view.
    /// </summary>
    /// <param name="identifier">The node identifier to check.</param>
    /// <returns>True if the identifier has been seen; otherwise, false.</returns>
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

    /// <summary>
    /// Gets the current configuration identifier for the membership view.
    /// The configuration ID is a hash of all node identifiers and endpoints in the view.
    /// </summary>
    /// <returns>The current configuration ID.</returns>
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

    /// <summary>
    /// Gets all endpoints in a specific ring.
    /// </summary>
    /// <param name="k">The ring number (0-based index).</param>
    /// <returns>A list of endpoints in the specified ring.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if k is out of range.</exception>
    public List<Endpoint> GetRing(int k)
    {
        _rwLock.EnterReadLock();
        try
        {
            if (k < 0) throw new ArgumentOutOfRangeException(nameof(k));
            return [.. _rings[k]];
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets the ring numbers where an observer is monitoring a subject.
    /// </summary>
    /// <param name="observer">The observing node.</param>
    /// <param name="subject">The subject being monitored.</param>
    /// <returns>A list of ring numbers where the observer monitors the subject.</returns>
    public List<int> GetRingNumbers(Endpoint observer, Endpoint subject)
    {
        _rwLock.EnterReadLock();
        try
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
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets the current number of nodes in the membership view.
    /// </summary>
    /// <returns>The number of nodes in the view.</returns>
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

    /// <summary>
    /// Gets the current configuration containing all node identifiers and endpoints.
    /// </summary>
    /// <returns>The current configuration object.</returns>
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

    public sealed class Configuration(IEnumerable<NodeId> nodeIds, IEnumerable<Endpoint> endpoints)
    {
        public List<NodeId> NodeIds { get; } = [.. nodeIds];
        public List<Endpoint> Endpoints { get; } = [.. endpoints];

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

    public void Dispose()
    {
        _rwLock.Dispose();
    }
}

