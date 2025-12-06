/*
 * Copyright © 2016 - 2025 VMware, Inc. All Rights Reserved.
 */

using Rapid.Pb;

namespace Rapid.Messaging;

/// <summary>
/// Interface for handling incoming membership protocol messages.
/// </summary>
public interface IMembershipServiceHandler
{
    /// <summary>
    /// Handles an incoming Rapid protocol message and returns a response.
    /// </summary>
    /// <param name="request">The incoming request message.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The response message.</returns>
    Task<RapidResponse> HandleMessageAsync(RapidRequest request, CancellationToken cancellationToken = default);
}
