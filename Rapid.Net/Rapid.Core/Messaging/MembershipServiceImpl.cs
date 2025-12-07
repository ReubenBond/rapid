using Grpc.Core;
using Rapid.Pb;

namespace Rapid.Messaging;

/// <summary>
/// gRPC service implementation for Rapid membership protocol.
/// </summary>
internal sealed class MembershipServiceImpl(IMembershipServiceHandler handler) : Pb.MembershipService.MembershipServiceBase
{
    public override async Task<RapidResponse> sendRequest(RapidRequest request, ServerCallContext context) => await handler.HandleMessageAsync(request, context.CancellationToken).ConfigureAwait(false);
}
