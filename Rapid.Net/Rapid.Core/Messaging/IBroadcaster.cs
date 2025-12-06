/*
 * Copyright © 2016 - 2025 VMware, Inc. All Rights Reserved.
 */

using Rapid.Pb;

namespace Rapid.Messaging;

public interface IBroadcaster
{
    void SetMembership(IReadOnlyList<Endpoint> membership);
    Task BroadcastAsync(RapidRequest request);
}
