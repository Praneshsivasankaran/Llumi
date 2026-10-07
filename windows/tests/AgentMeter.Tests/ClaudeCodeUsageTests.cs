using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class ClaudeCodeUsageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-15T15:00:00Z");
    private const string Organization = "00000000-0000-0000-0000-000000000042";
    private const string Auth = """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":" Fixture@Example.Invalid ","orgId":"00000000-0000-0000-0000-000000000042","subscriptionType":"pro","orgName":"Fixture organization","analyticsDisabled":false}""";
    private static readonly string Account = "claude-email-sha256:" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes("fixture@example.invalid"))).ToLowerInvariant();
    private static readonly string Binding = JsonSerializer.Serialize(new { accountId = Account, organizationId = Organization });

    [Fact]
    public async Task AuthenticatedCliQueriesBridgeAndReturnsVerifiedUsage()
    {
        var calls = 0;
        var source = Source((executable, _) =>
        {
            Assert.Equal("fixture-cli.exe", executable);
            calls++;
            return Task.FromResult(Usage());
        });
        var result = await source.QueryAsync(CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal(ClaudeAuthentication.Authenticated, result.Authentication);
        Assert.Equal(new ClaudeAccountBinding(Account, Organization), result.Binding);
        Assert.Equal(new double?[] { 100, 92 }, result.Usage.Snapshot!.Windows.Select(window => window.RemainingPercent));
        Assert.Equal(Now, result.Usage.Snapshot.ObservedAt);
        Assert.DoesNotContain("fixture@example.invalid", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("orgId")]
    [InlineData("email")]
    [InlineData("authMethod")]
    [InlineData("apiProvider")]
    public async Task IncompleteAuthMetadataDoesNotStartBridge(string missing)
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Auth)!;
        data.Remove(missing);
        var calls = 0;
        var source = new ClaudeCodeSource(() => "fixture-cli.exe", (_, _) => Task.FromResult(Response(JsonSerializer.Serialize(data))),
            usageQuery: (_, _) => { calls++; throw new InvalidOperationException("must not query"); });
        var result = await source.QueryAsync(CancellationToken.None);
        Assert.Equal(0, calls);
        Assert.Equal(ClaudeAuthentication.Authenticated, result.Authentication);
        Assert.Equal(FailureKind.Unsupported, result.Usage.Failure);
        Assert.Null(result.Binding);
    }

    [Fact]
    public async Task MissingOrSignedOutCliNeverCallsBridge()
    {
        var missing = new ClaudeCodeSource(() => null, usageQuery: (_, _) => throw new InvalidOperationException("must not query"));
        Assert.Equal(ClaudeAuthentication.Missing, (await missing.QueryAsync(CancellationToken.None)).Authentication);
        var signedOut = new ClaudeCodeSource(() => "fixture-cli.exe", (_, _) => Task.FromResult(Response("{\"loggedIn\":false}", 1)),
            usageQuery: (_, _) => throw new InvalidOperationException("must not query"));
        Assert.Equal(ClaudeAuthentication.SignedOut, (await signedOut.QueryAsync(CancellationToken.None)).Authentication);
    }

    [Fact]
    public async Task AccountChangeBetweenStatusAndBridgeDiscardsUsage()
    {
        var different = JsonSerializer.Serialize(new { accountId = "claude-email-sha256:" + new string('b', 64), organizationId = Organization });
        var source = Source((_, _) => Task.FromResult(Usage(binding: different)));
        var result = await source.QueryAsync(CancellationToken.None);
        Assert.Equal(ClaudeAuthentication.Unknown, result.Authentication);
        Assert.Equal(FailureKind.AccountChanged, result.Usage.Failure);
        Assert.Null(result.Binding);
        Assert.Null(result.Usage.Snapshot);
    }

    [Fact]
    public async Task BridgeSignedOutAfterInitialStatusClearsIdentity()
    {
        var response = ClaudeUsageParserTests.Envelope("null", "signedOut", "loggedOut", "identityUnavailable", "null");
        var result = await Source((_, _) => Task.FromResult(Response(response, 1))).QueryAsync(CancellationToken.None);
        Assert.Equal(ClaudeAuthentication.SignedOut, result.Authentication);
        Assert.Equal(FailureKind.LoggedOut, result.Usage.Failure);
        Assert.Null(result.Binding);
    }

    [Fact]
    public async Task VerifiedBridgeQuotaFailurePreservesIdentityForStaleHandling()
    {
        var response = ClaudeUsageParserTests.Envelope("null", failure: "timeout", detail: "queryTimedOut", binding: Binding);
        var result = await Source((_, _) => Task.FromResult(Response(response, 1))).QueryAsync(CancellationToken.None);
        Assert.Equal(ClaudeAuthentication.Authenticated, result.Authentication);
        Assert.Equal(new ClaudeAccountBinding(Account, Organization), result.Binding);
        Assert.Equal(FailureKind.Timeout, result.Usage.Failure);
        Assert.Null(result.Usage.Snapshot);
    }

    [Fact]
    public async Task PrivacyOptOutIsRespectedWithoutRetainingUnverifiedIdentity()
    {
        var response = ClaudeUsageParserTests.Envelope("null", failure: "unsupported", detail: "privacyOptOut", binding: "null");
        var result = await Source((_, _) => Task.FromResult(Response(response, 1))).QueryAsync(CancellationToken.None);
        Assert.Equal(FailureKind.Unsupported, result.Usage.Failure);
        Assert.Contains("privacy", result.Usage.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Binding);
        Assert.Null(result.Usage.Snapshot);
    }

    [Fact]
    public async Task MissingPackagedBridgeIsSafeAndDoesNotRequireUserCredentials()
    {
        var missing = Path.Combine(Path.GetTempPath(), "AgentMeter-missing-bridge-" + Guid.NewGuid().ToString("N"));
        var source = new ClaudeCodeSource(() => "fixture-cli.exe", (_, _) => Task.FromResult(Response(Auth)), bridgeDirectory: missing);
        var result = await source.QueryAsync(CancellationToken.None);
        Assert.Equal(ClaudeAuthentication.Authenticated, result.Authentication);
        Assert.Equal(FailureKind.Unsupported, result.Usage.Failure);
        Assert.Contains("helper", result.Usage.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Binding);
    }

    [Fact]
    public async Task BridgeTimeoutDoesNotRetainUnrevalidatedIdentity()
    {
        var source = Source(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Usage();
        }, TimeSpan.FromMilliseconds(40));
        var result = await source.QueryAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(FailureKind.Timeout, result.Usage.Failure);
        Assert.Equal(ClaudeAuthentication.Unknown, result.Authentication);
        Assert.Null(result.Binding);
    }

    [Fact]
    public async Task CallerCancellationDuringBridgePropagates()
    {
        using var cancellation = new CancellationTokenSource();
        var source = Source(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Usage();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.QueryAsync(cancellation.Token));
    }

    [Fact]
    public async Task UnexpectedBridgeOutputAndExceptionTextNeverReachUser()
    {
        var malformed = await Source((_, _) => Task.FromResult(Response("{\"private\":\"fixture-secret\"}"))).QueryAsync(CancellationToken.None);
        Assert.Equal(FailureKind.Malformed, malformed.Usage.Failure);
        Assert.DoesNotContain("fixture-secret", malformed.ToString());
        var failed = await Source((_, _) => throw new IOException("fixture-secret")).QueryAsync(CancellationToken.None);
        Assert.Equal(FailureKind.ProcessExited, failed.Usage.Failure);
        Assert.DoesNotContain("fixture-secret", failed.ToString());
        Assert.Null(failed.Binding);
    }

    [Fact]
    public async Task RealProviderProcessIsDisposedWhenUsageQueryTimesOut()
    {
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = Source(async (_, token) =>
        {
            await using var process = ProviderProcess.Start(Path.Combine(AppContext.BaseDirectory, "AgentMeter.ProcessHost.exe"), []);
            started.TrySetResult(process.ProcessId);
            return await process.ReadJsonOutputAsync(token);
        }, TimeSpan.FromMilliseconds(500));
        var pending = source.QueryAsync(CancellationToken.None);
        var pid = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(FailureKind.Timeout, result.Usage.Failure);
        Assert.False(IsRunning(pid));
    }

    [Fact]
    public void SourceDeadlineAcceptsTwelveSecondsAndRejectsLonger()
    {
        _ = new ClaudeCodeSource(timeout: TimeSpan.FromSeconds(12));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClaudeCodeSource(timeout: TimeSpan.FromSeconds(13)));
    }

    private static ClaudeCodeSource Source(Func<string, CancellationToken, Task<(JsonElement Output, int ExitCode)>> usage,
        TimeSpan? timeout = null) => new(() => "fixture-cli.exe", (_, _) => Task.FromResult(Response(Auth)), timeout, usage, () => Now);
    private static (JsonElement, int) Usage(string? binding = null) => Response(ClaudeUsageParserTests.Envelope(
        "{\"five_hour\":{\"utilization\":0,\"resets_at\":\"2026-09-15T20:00:00Z\"},\"seven_day\":{\"utilization\":8}}", binding: binding ?? Binding));
    private static (JsonElement, int) Response(string json, int exitCode = 0)
    {
        using var document = JsonDocument.Parse(json);
        return (document.RootElement.Clone(), exitCode);
    }
    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
