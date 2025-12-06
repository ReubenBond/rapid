using Microsoft.Extensions.Options;

namespace Rapid;

/// <summary>
/// Validates RapidProtocolOptions configuration at startup.
/// </summary>
internal sealed class RapidProtocolOptionsValidator : IValidateOptions<RapidProtocolOptions>
{
    public ValidateOptionsResult Validate(string? name, RapidProtocolOptions options)
    {
        if (options.GrpcTimeout <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail("GrpcTimeout must be positive");
        }

        if (options.GrpcDefaultRetries < 0)
        {
            return ValidateOptionsResult.Fail("GrpcDefaultRetries must be non-negative");
        }

        if (options.GrpcJoinTimeout <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail("GrpcJoinTimeout must be positive");
        }

        if (options.GrpcProbeTimeout <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail("GrpcProbeTimeout must be positive");
        }

        if (options.FailureDetectorInterval <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail("FailureDetectorInterval must be positive");
        }

        if (options.BatchingWindow <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail("BatchingWindow must be positive");
        }

        if (options.ConsensusFallbackTimeoutBaseDelay <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail("ConsensusFallbackTimeoutBaseDelay must be positive");
        }

        if (options.LeaveMessageTimeout <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail("LeaveMessageTimeout must be positive");
        }

        return ValidateOptionsResult.Success;
    }
}
