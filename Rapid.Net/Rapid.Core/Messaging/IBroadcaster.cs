/*
 * Copyright © 2016 - 2025 VMware, Inc. All Rights Reserved.
 */

using Rapid.Pb;

namespace Rapid.Messaging;

/// <summary>
/// Interface for broadcasting messages to multiple nodes in the cluster.
/// </summary>
public interface IBroadcaster
{
    /// <summary>
    /// Updates the membership list for broadcast operations.
    /// </summary>
    /// <param name="membership">The current cluster membership.</param>
    void SetMembership(IReadOnlyList<Endpoint> membership);

    /// <summary>
    /// Broadcasts a message to all nodes in the membership.
    /// </summary>
    /// <param name="request">The request message to broadcast.</param>
    /// <returns>A task that completes when all broadcasts are sent.</returns>
    Task BroadcastAsync(RapidRequest request);
}
