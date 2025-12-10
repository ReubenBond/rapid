using Rapid.Pb;

namespace Rapid.Messaging;

/// <summary>
/// Delegate invoked when a one-way message fails to be delivered.
/// </summary>
/// <param name="remote">The endpoint that failed to receive the message.</param>
public delegate void DeliveryFailureCallback(Endpoint remote);

/// <summary>
/// Interface for sending messages to remote nodes in the cluster.
/// </summary>
public interface IMessagingClient : IDisposable, IAsyncDisposable
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
    /// Sends a message to a remote node without waiting for a response, with failure notification.
    /// </summary>
    /// <param name="remote">The remote endpoint to send to.</param>
    /// <param name="request">The request message.</param>
    /// <param name="onDeliveryFailure">Callback invoked if delivery fails. May be called from any thread.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    void SendOneWayMessage(Endpoint remote, RapidRequest request, DeliveryFailureCallback? onDeliveryFailure, CancellationToken cancellationToken);

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
