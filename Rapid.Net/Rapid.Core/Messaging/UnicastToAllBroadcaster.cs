/*
 * Copyright © 2016 - 2025 VMware, Inc. All Rights Reserved.
 */

using Rapid.Pb;

namespace Rapid.Messaging;

public sealed class UnicastToAllBroadcaster(IMessagingClient client) : IBroadcaster
{
    private readonly IMessagingClient _client = client;
    private IReadOnlyList<Endpoint> _membership = Array.Empty<Endpoint>();

    public void SetMembership(IReadOnlyList<Endpoint> membership)
    {
        _membership = membership;
    }

    public async Task BroadcastAsync(RapidRequest request)
    {
        var tasks = _membership.Select(endpoint => 
            _client.SendMessageBestEffortAsync(endpoint, request));
        await Task.WhenAll(tasks);
    }
}
