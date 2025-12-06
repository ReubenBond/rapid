/*
 * Copyright © 2016 - 2025 VMware, Inc. All Rights Reserved.
 */

using Grpc.Core;
using Rapid.Pb;

namespace Rapid.Messaging;

/// <summary>
/// gRPC service implementation for Rapid membership protocol.
/// </summary>
internal sealed class MembershipServiceImpl(IMembershipServiceHandler handler) : Pb.MembershipService.MembershipServiceBase
{
    public override async Task<RapidResponse> sendRequest(RapidRequest request, ServerCallContext context)
    {
        return await handler.HandleMessageAsync(request, context.CancellationToken).ConfigureAwait(false);
    }
}
