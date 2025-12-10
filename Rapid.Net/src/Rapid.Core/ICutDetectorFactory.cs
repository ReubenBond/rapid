using Microsoft.Extensions.Options;

namespace Rapid;

/// <summary>
/// Factory for creating cut detectors based on cluster size.
/// This exists because the appropriate cut detector varies based on effective cluster parameters.
/// </summary>
internal interface ICutDetectorFactory
{
    /// <summary>
    /// Creates the appropriate cut detector for the given membership view.
    /// </summary>
    /// <param name="membershipView">The current membership view.</param>
    /// <returns>
    /// A cut detector appropriate for the cluster size:
    /// - For single-node clusters (ObserversPerSubject = 0): SimpleCutDetector with threshold 1
    /// - For small clusters where MultiNodeCutDetector constraints cannot be satisfied: SimpleCutDetector
    /// - For larger clusters (ObserversPerSubject >= 3 and K > H): MultiNodeCutDetector with H/L watermarks
    /// </returns>
    ICutDetector Create(MembershipView membershipView);
}

/// <summary>
/// Default implementation of ICutDetectorFactory.
/// Uses RapidProtocolOptions to compute effective parameters based on cluster size.
/// </summary>
internal sealed class CutDetectorFactory(IOptions<RapidProtocolOptions> protocolOptions) : ICutDetectorFactory
{
    private readonly RapidProtocolOptions _options = protocolOptions.Value;

    /// <inheritdoc/>
    public ICutDetector Create(MembershipView membershipView)
    {
        ArgumentNullException.ThrowIfNull(membershipView);
        
        var (observersPerSubject, highWatermark, lowWatermark) = _options.GetEffectiveParameters(membershipView.Size);
        
        // For single-node cluster, no cut detection needed
        if (observersPerSubject == 0)
        {
            // Use SimpleCutDetector with K=1 - it will never trigger since there are no observers
            // but it provides a valid implementation that won't crash
            return new SimpleCutDetector(1, membershipView);
        }
        
        // MultiNodeCutDetector requires K >= 3 and K > H >= L >= 1
        // If these constraints cannot be satisfied, use SimpleCutDetector
        if (observersPerSubject < 3 || observersPerSubject <= highWatermark)
        {
            return new SimpleCutDetector(observersPerSubject, membershipView);
        }
        
        // For larger clusters with valid parameters, use the full multi-node cut detection
        return new MultiNodeCutDetector(observersPerSubject, highWatermark, lowWatermark, membershipView);
    }
}
