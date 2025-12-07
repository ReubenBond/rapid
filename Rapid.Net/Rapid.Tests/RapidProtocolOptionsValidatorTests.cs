namespace Rapid.Tests;

/// <summary>
/// Tests for RapidProtocolOptionsValidator validation logic.
/// </summary>
public class RapidProtocolOptionsValidatorTests
{
    private readonly RapidProtocolOptionsValidator _validator = new();

    #region GrpcTimeout Validation

    [Fact]
    public void ValidateGrpcTimeoutZeroFails()
    {
        var options = CreateValidOptions();
        options.GrpcTimeout = TimeSpan.Zero;

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("GrpcTimeout", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateGrpcTimeoutNegativeFails()
    {
        var options = CreateValidOptions();
        options.GrpcTimeout = TimeSpan.FromSeconds(-1);

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void ValidateGrpcTimeoutPositiveSucceeds()
    {
        var options = CreateValidOptions();
        options.GrpcTimeout = TimeSpan.FromSeconds(10);

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    #endregion

    #region GrpcDefaultRetries Validation

    [Fact]
    public void ValidateGrpcDefaultRetriesNegativeFails()
    {
        var options = CreateValidOptions();
        options.GrpcDefaultRetries = -1;

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("GrpcDefaultRetries", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateGrpcDefaultRetriesZeroSucceeds()
    {
        var options = CreateValidOptions();
        options.GrpcDefaultRetries = 0;

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ValidateGrpcDefaultRetriesPositiveSucceeds()
    {
        var options = CreateValidOptions();
        options.GrpcDefaultRetries = 5;

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    #endregion

    #region GrpcJoinTimeout Validation

    [Fact]
    public void ValidateGrpcJoinTimeoutZeroFails()
    {
        var options = CreateValidOptions();
        options.GrpcJoinTimeout = TimeSpan.Zero;

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("GrpcJoinTimeout", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateGrpcJoinTimeoutPositiveSucceeds()
    {
        var options = CreateValidOptions();
        options.GrpcJoinTimeout = TimeSpan.FromSeconds(5);

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    #endregion

    #region GrpcProbeTimeout Validation

    [Fact]
    public void ValidateGrpcProbeTimeoutZeroFails()
    {
        var options = CreateValidOptions();
        options.GrpcProbeTimeout = TimeSpan.Zero;

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("GrpcProbeTimeout", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateGrpcProbeTimeoutPositiveSucceeds()
    {
        var options = CreateValidOptions();
        options.GrpcProbeTimeout = TimeSpan.FromMilliseconds(500);

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    #endregion

    #region FailureDetectorInterval Validation

    [Fact]
    public void ValidateFailureDetectorIntervalZeroFails()
    {
        var options = CreateValidOptions();
        options.FailureDetectorInterval = TimeSpan.Zero;

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("FailureDetectorInterval", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateFailureDetectorIntervalPositiveSucceeds()
    {
        var options = CreateValidOptions();
        options.FailureDetectorInterval = TimeSpan.FromSeconds(1);

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    #endregion

    #region BatchingWindow Validation

    [Fact]
    public void ValidateBatchingWindowZeroFails()
    {
        var options = CreateValidOptions();
        options.BatchingWindow = TimeSpan.Zero;

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("BatchingWindow", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateBatchingWindowPositiveSucceeds()
    {
        var options = CreateValidOptions();
        options.BatchingWindow = TimeSpan.FromMilliseconds(100);

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    #endregion

    #region ConsensusFallbackTimeoutBaseDelay Validation

    [Fact]
    public void ValidateConsensusFallbackTimeoutBaseDelayZeroFails()
    {
        var options = CreateValidOptions();
        options.ConsensusFallbackTimeoutBaseDelay = TimeSpan.Zero;

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("ConsensusFallbackTimeoutBaseDelay", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateConsensusFallbackTimeoutBaseDelayPositiveSucceeds()
    {
        var options = CreateValidOptions();
        options.ConsensusFallbackTimeoutBaseDelay = TimeSpan.FromMilliseconds(500);

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    #endregion

    #region LeaveMessageTimeout Validation

    [Fact]
    public void ValidateLeaveMessageTimeoutZeroFails()
    {
        var options = CreateValidOptions();
        options.LeaveMessageTimeout = TimeSpan.Zero;

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("LeaveMessageTimeout", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateLeaveMessageTimeoutPositiveSucceeds()
    {
        var options = CreateValidOptions();
        options.LeaveMessageTimeout = TimeSpan.FromMilliseconds(1500);

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    #endregion

    #region Combined Validation

    [Fact]
    public void ValidateAllDefaultValuesSucceeds()
    {
        var options = new RapidProtocolOptions();

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ValidateAllValidCustomValuesSucceeds()
    {
        var options = new RapidProtocolOptions
        {
            GrpcTimeout = TimeSpan.FromSeconds(5),
            GrpcDefaultRetries = 3,
            GrpcJoinTimeout = TimeSpan.FromSeconds(3),
            GrpcProbeTimeout = TimeSpan.FromMilliseconds(250),
            FailureDetectorInterval = TimeSpan.FromMilliseconds(500),
            BatchingWindow = TimeSpan.FromMilliseconds(50),
            ConsensusFallbackTimeoutBaseDelay = TimeSpan.FromMilliseconds(250),
            LeaveMessageTimeout = TimeSpan.FromMilliseconds(1000)
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ValidateWithNameWorks()
    {
        var options = CreateValidOptions();

        var result = _validator.Validate("TestOptions", options);

        Assert.True(result.Succeeded);
    }

    #endregion

    private static RapidProtocolOptions CreateValidOptions() => new();
}
