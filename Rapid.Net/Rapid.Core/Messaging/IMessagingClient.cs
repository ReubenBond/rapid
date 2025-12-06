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

using Rapid.Pb;

namespace Rapid.Messaging;

/// <summary>
/// Interface for sending messages to remote nodes in the cluster.
/// </summary>
public interface IMessagingClient : IDisposable
{
    /// <summary>
    /// Sends a message to a remote node and waits for a response.
    /// May retry on failures based on implementation.
    /// </summary>
    /// <param name="remote">The remote endpoint to send to.</param>
    /// <param name="request">The request message.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The response from the remote node.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    Task<RapidResponse> SendMessageAsync(Endpoint remote, RapidRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message to a remote node with best-effort delivery.
    /// Does not retry on failures.
    /// </summary>
    /// <param name="remote">The remote endpoint to send to.</param>
    /// <param name="request">The request message.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The response from the remote node, or an error response.</returns>
    Task<RapidResponse> SendMessageBestEffortAsync(Endpoint remote, RapidRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Shuts down the messaging client and releases resources.
    /// </summary>
    void Shutdown();
}
