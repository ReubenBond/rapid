using Rapid.Pb;

namespace Rapid.Messaging;

/// <summary>
/// Interface for sending messages to remote nodes in the cluster.
/// </summary>
public interface IMessagingClient : IDisposable
{
    /// <summary>
    /// Sends a message to a remote node without waiting for a response.
    /// May retry on failures based on implementation.
    /// </summary>
    /// <param name="remote">The remote endpoint to send to.</param>
    /// <param name="request">The request message.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    void SendOneWayMessage(Endpoint remote, RapidRequest request, CancellationToken cancellationToken);

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
        CancellationToken cancellationToken);

    /// <summary>
    /// Sends a message to a remote node with best-effort delivery.
    /// Does not retry on failures.
    /// </summary>
    /// <param name="remote">The remote endpoint to send to.</param>
    /// <param name="request">The request message.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The response from the remote node, or an error response.</returns>
    Task<RapidResponse> SendMessageBestEffortAsync(Endpoint remote, RapidRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Shuts down the messaging client and releases resources.
    /// </summary>
    void Shutdown();
}
