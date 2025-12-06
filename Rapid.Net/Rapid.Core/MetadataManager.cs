/*
 * Copyright © 2016 - 2025 VMware, Inc. All Rights Reserved.
 */

using Rapid.Pb;

namespace Rapid;

internal sealed class MetadataManager
{
    private readonly Dictionary<Endpoint, Metadata> _metadata = new();
    private readonly object _lock = new();

    public void Add(Endpoint endpoint, Metadata metadata)
    {
        lock (_lock)
        {
            _metadata[endpoint] = metadata;
        }
    }

    public void AddMetadata(IReadOnlyDictionary<Endpoint, Metadata> metadata)
    {
        lock (_lock)
        {
            foreach (var kvp in metadata)
            {
                _metadata[kvp.Key] = kvp.Value;
            }
        }
    }

    public Metadata? Get(Endpoint endpoint)
    {
        lock (_lock)
        {
            return _metadata.TryGetValue(endpoint, out var metadata) ? metadata : null;
        }
    }

    public void RemoveNode(Endpoint endpoint)
    {
        lock (_lock)
        {
            _metadata.Remove(endpoint);
        }
    }

    public IReadOnlyDictionary<Endpoint, Metadata> GetAllMetadata()
    {
        lock (_lock)
        {
            return new Dictionary<Endpoint, Metadata>(_metadata);
        }
    }
}
