/*
 * Copyright © 2016 - 2025 VMware, Inc. All Rights Reserved.
 */

using Rapid.Pb;

namespace Rapid.Messaging;

public interface IMessagingServer : IDisposable
{
    void SetMembershipService(IMembershipServiceHandler service);
    Task StartAsync(CancellationToken cancellationToken = default);
    void Shutdown();
}

public interface IMembershipServiceHandler
{
    Task<RapidResponse> HandleMessageAsync(RapidRequest request);
}
