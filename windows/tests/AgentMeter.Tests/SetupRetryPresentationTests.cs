using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class SetupRetryPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AutomaticBackoffDoesNotDisablePermittedManualRetry()
    {
        var state = new ProviderState("Codex", ProviderStatus.Error, Failure: FailureKind.Network,
            RetryAt: Now.AddSeconds(10), AutomaticRetryAt: Now.AddSeconds(60));
        Assert.False(SetupRetryPresentation.CanRetry(state, Now));
        Assert.Equal("Automatic retry available in 1m. Retry available in 10s.", SetupRetryPresentation.Message(state, Now));
        Assert.True(SetupRetryPresentation.CanRetry(state, Now.AddSeconds(10)));
        Assert.Equal("Automatic retry available in 50s. You can retry now.", SetupRetryPresentation.Message(state, Now.AddSeconds(10)));
        Assert.Null(SetupRetryPresentation.Message(state, Now.AddSeconds(60)));
    }

    [Fact]
    public void RateEmbargoPrecedesGenericBackoffAndNeverInvitesEarlyRetry()
    {
        var state = new ProviderState("Claude Code", ProviderStatus.Error, Failure: FailureKind.RateLimited,
            RetryAt: Now.AddMinutes(2), AutomaticRetryAt: Now.AddMinutes(5));
        Assert.False(SetupRetryPresentation.CanRetry(state, Now.AddMinutes(1)));
        Assert.Equal("Rate limited. Retry in 1m.", SetupRetryPresentation.Message(state, Now.AddMinutes(1)));
        Assert.True(SetupRetryPresentation.CanRetry(state, Now.AddMinutes(2)));
        Assert.Equal("Automatic retry available in 3m. You can retry now.", SetupRetryPresentation.Message(state, Now.AddMinutes(2)));
    }

    [Theory]
    [InlineData(false, ProviderStatus.Error, null)]
    [InlineData(true, ProviderStatus.Loading, "Checking…")]
    public void DisabledOrCheckingNeverShowsOldFailureCountdown(bool enabled, ProviderStatus status, string? expected)
    {
        var state = new ProviderState("Codex", status, Enabled: enabled, Failure: FailureKind.Network,
            RetryAt: Now.AddSeconds(10), AutomaticRetryAt: Now.AddSeconds(30));
        Assert.False(SetupRetryPresentation.CanRetry(state, Now));
        Assert.Equal(expected, SetupRetryPresentation.Message(state, Now));
    }

    [Fact]
    public void DiagnosticExportKeepsBothDeadlineValuesPrivate()
    {
        var state = new ProviderState("Codex", ProviderStatus.Error, Failure: FailureKind.Network,
            RetryAt: Now.AddSeconds(10), AutomaticRetryAt: Now.AddSeconds(30));
        Assert.Equal(SetupDiagnostics.Report([state], "2.1.0", "2.1.0.0"),
            SetupDiagnostics.Report([state with { RetryAt = null, AutomaticRetryAt = null }], "2.1.0", "2.1.0.0"));
    }
}
