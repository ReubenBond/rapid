/*
 * Copyright © 2016 - 2025 VMware, Inc. All Rights Reserved.
 */

using Rapid.Pb;

namespace Rapid.Messaging;

/// <summary>
/// Interface for receiving messages from remote nodes in the cluster.
/// </summary>
public interface IMessagingServer : IDisposable
{
    /// <summary>
    /// Sets the membership service handler that will process incoming messages.
    /// </summary>
    /// <param name="service">The membership service handler.</param>
    void SetMembershipService(IMembershipServiceHandler service);

    /// <summary>
    /// Starts the messaging server and begins listening for incoming messages.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that completes when the server has started.</returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Shuts down the messaging server and releases resources.
    /// </summary>
    void Shutdown();
}

/// <summary>
/// Interface for handling incoming membership protocol messages.
/// </summary>
public interface IMembershipServiceHandler
{
    /// <summary>
    /// Handles an incoming Rapid protocol message and returns a response.
    /// </summary>
    /// <param name="request">The incoming request message.</param>
    /// <returns>The response message.</returns>
    Task<RapidResponse> HandleMessageAsync(RapidRequest request);
}
