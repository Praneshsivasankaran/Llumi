using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class CliFallbackTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Llumi-cli-fallback-tests", Guid.NewGuid().ToString("N"));
    private const string Auth = """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"fixture@example.invalid","orgId":"00000000-0000-0000-0000-000000000042","orgName":"Fixture","subscriptionType":"pro","analyticsDisabled":true}""";
    private const string ClaudeUsage = """{"rate_limits_available":true,"behaviors":null,"subscription_type":"pro","session":{"total_cost_usd":0,"total_api_duration_ms":0,"model_usage":{}},"rate_limits":{"five_hour":{"utilization":12}}}""";
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(193)] [InlineData(216)]
    public async Task CodexUnlaunchableFirstInstallationDoesNotMaskHealthyLaterInstallation(int error)
    {
        var first = FileAt("first", "codex.exe");
        var second = FileAt("second", "codex.exe");
        var environment = new CliSearchEnvironment(PathValue(first, second), Path.Combine(root, "local"),
            Path.Combine(root, "roaming"), Architecture.X64);
        var attempted = new List<string>();
        var provider = new CodexProvider(executableCandidates: () => CliLocator.CodexCandidates(environment), startProcess: executable =>
        {
            attempted.Add(executable);
            if (executable == first) throw new Win32Exception(error); // Launch failed before any provider response.
            Assert.Equal(second, executable);
            return new Session();
        });

        var result = await provider.QueryAsync(default);

        Assert.Equal(FailureKind.None, result.Failure);
        Assert.Equal(AuthenticationStatus.Verified, result.Authentication);
        Assert.NotNull(result.VerifiedBinding);
        Assert.Equal(new[] { first, second }, attempted);
        Assert.Equal(88, Assert.Single(result.Snapshot!.Windows).RemainingPercent);
    }

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(193)] [InlineData(216)]
    public async Task ClaudeUnlaunchableFirstStandaloneInstallationDoesNotMaskHealthyLaterInstallation(int error)
    {
        var first = FileAt("first", "claude.exe");
        var second = FileAt("second", "claude.exe");
        var environment = new ClaudeCliSearchEnvironment(PathValue(first, second), Path.Combine(root, "profile"),
            Path.Combine(root, "local"), Path.Combine(root, "roaming"));
        var attempted = new List<string>();
        var source = new ClaudeControlTransport(locateCandidates: () => ClaudeCliLocator.StandaloneCandidates(environment), auth: (executable, _) =>
        {
            attempted.Add(executable);
            if (executable == first) throw new Win32Exception(error);
            Assert.Equal(second, executable);
            return Task.FromResult((Json(Auth), 0));
        }, usage: (executable, _, _) =>
        {
            Assert.Equal(second, executable);
            return Task.FromResult(Json(ClaudeUsage));
        });

        var result = await source.QueryAsync(default);

        Assert.Equal(FailureKind.None, result.Usage.Failure);
        Assert.Equal(ClaudeAuthentication.Authenticated, result.Authentication);
        Assert.NotNull(result.Binding);
        Assert.Equal(new[] { first, second, second }, attempted); // The selected healthy executable verifies identity twice.
        Assert.Equal(88, Assert.Single(result.Usage.Snapshot!.Windows).RemainingPercent);
    }

    [Theory]
    [InlineData("signed-out", FailureKind.LoggedOut)]
    [InlineData("billing", FailureKind.UnsupportedBilling)]
    [InlineData("rate-limit", FailureKind.RateLimited)]
    [InlineData("empty", FailureKind.None)]
    [InlineData("malformed", FailureKind.Malformed)]
    [InlineData("unsupported", FailureKind.Unsupported)]
    [InlineData("late-launch-error", FailureKind.AccessDenied)]
    public async Task CodexRespondingInstallationNeverFallsBack(string outcome, FailureKind expected)
    {
        var attempted = new List<string>();
        var provider = new CodexProvider(executableCandidates: () => ["first.exe", "second.exe"], startProcess: executable =>
        {
            attempted.Add(executable);
            return new Session(id =>
            {
                if (id == 1 && outcome == "unsupported") throw new ProviderQueryException(FailureKind.Unsupported);
                if (id == 2 && outcome == "signed-out") return Json("""{"account":null}""");
                if (id == 2 && outcome == "billing") return Json("""{"account":{"type":"apiKey"}}""");
                if (id == 2 && outcome == "malformed") return Json("{}");
                if (id == 3 && outcome == "late-launch-error") throw new Win32Exception(193);
                if (id == 3 && outcome == "rate-limit") throw new ProviderQueryException(FailureKind.RateLimited);
                if (id == 3 && outcome == "empty") return Json("{}");
                return Session.DefaultResult(id);
            });
        });
        var result = await provider.QueryAsync(default);
        Assert.Equal(expected, result.Failure);
        Assert.Equal(new[] { "first.exe" }, attempted);
        if (outcome == "empty") Assert.Empty(result.Snapshot!.Windows);
    }

    [Theory]
    [InlineData("signed-out", FailureKind.LoggedOut)]
    [InlineData("billing", FailureKind.UnsupportedBilling)]
    [InlineData("rate-limit", FailureKind.RateLimited)]
    [InlineData("empty", FailureKind.None)]
    [InlineData("malformed", FailureKind.Malformed)]
    [InlineData("unsupported", FailureKind.Unsupported)]
    [InlineData("late-launch-error", FailureKind.AccessDenied)]
    [InlineData("post-auth-launch-error", FailureKind.AccessDenied)]
    public async Task ClaudeRespondingInstallationNeverFallsBack(string outcome, FailureKind expected)
    {
        var attempted = new List<string>();
        var source = new ClaudeControlTransport(locateCandidates: () => ["first.exe", "second.exe"], auth: (executable, _) =>
        {
            attempted.Add(executable);
            if (outcome == "post-auth-launch-error" && attempted.Count > 1) throw new Win32Exception(193);
            return Task.FromResult((Json(outcome switch
            {
                "signed-out" => """{"loggedIn":false}""",
                "billing" => Auth.Replace("\"claude.ai\"", "\"apiKey\""),
                "malformed" => "{}",
                _ => Auth
            }), 0));
        }, usage: (_, _, _) =>
        {
            if (outcome == "rate-limit") throw new ProviderQueryException(FailureKind.RateLimited);
            if (outcome == "unsupported") throw new ProviderQueryException(FailureKind.Unsupported);
            if (outcome == "late-launch-error") throw new Win32Exception(193);
            return Task.FromResult(Json(outcome == "empty" ? ClaudeUsage.Replace("{\"five_hour\":{\"utilization\":12}}", "{}") : ClaudeUsage));
        });
        var result = await source.QueryAsync(default);
        Assert.Equal(expected, result.Usage.Failure);
        Assert.All(attempted, executable => Assert.Equal("first.exe", executable));
        Assert.InRange(attempted.Count, 1, 2);
        if (outcome == "empty") Assert.Empty(result.Usage.Snapshot!.Windows);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AccessDeniedDoesNotTryAnotherInstallation(bool claude)
    {
        var attempted = 0;
        ProviderResult result;
        if (claude)
        {
            var source = new ClaudeControlTransport(locateCandidates: () => ["first.exe", "second.exe"], auth: (_, _) =>
            { attempted++; throw new Win32Exception(5); });
            result = (await source.QueryAsync(default)).Usage;
        }
        else
        {
            var provider = new CodexProvider(executableCandidates: () => ["first.exe", "second.exe"], startProcess: _ =>
            { attempted++; throw new Win32Exception(5); });
            result = await provider.QueryAsync(default);
        }
        Assert.Equal(FailureKind.AccessDenied, result.Failure); Assert.Equal(1, attempted);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FallbackHonorsCancellationAndCandidateLimit(bool claude)
    {
        foreach (var cancel in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var attempted = 0;
            void FailedLaunch() { attempted++; if (cancel) cancellation.Cancel(); throw new Win32Exception(2); }
            IReadOnlyList<string> Candidates() => Enumerable.Range(0, 20).Select(index => $"candidate-{index}.exe").ToArray();
            async Task Query()
            {
                if (claude)
                    await new ClaudeControlTransport(locateCandidates: Candidates, auth: (_, _) =>
                    { FailedLaunch(); throw new InvalidOperationException(); }).QueryAsync(cancellation.Token);
                else
                    await new CodexProvider(executableCandidates: Candidates, startProcess: _ =>
                    { FailedLaunch(); throw new InvalidOperationException(); }).QueryAsync(cancellation.Token);
            }
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Query); else await Query();
            Assert.Equal(cancel ? 1 : 8, attempted);
        }
    }

    [Fact]
    public void ExplicitOverridesRemainSoleCandidatesAndDiscoveryListsAreBoundedAndDeduplicated()
    {
        var codex = FileAt("chosen", "codex.exe");
        var claude = FileAt("chosen", "claude.exe");
        var codexPaths = Enumerable.Range(0, 10).Select(index => FileAt($"extra-{index}", "codex.exe")).ToArray();
        var claudePaths = Enumerable.Range(0, 10).Select(index => FileAt($"extra-{index}", "claude.exe")).ToArray();
        var codexEnvironment = new CliSearchEnvironment(PathValue([codex, codex, .. codexPaths]), "", "", Architecture.X64, codex);
        var claudeEnvironment = new ClaudeCliSearchEnvironment(PathValue([claude, claude, .. claudePaths]), "", "", "", claude);
        Assert.Equal(new[] { codex }, CliLocator.CodexCandidates(codexEnvironment));
        Assert.Equal(new[] { claude }, ClaudeCliLocator.StandaloneCandidates(claudeEnvironment));
        Assert.Empty(CliLocator.CodexCandidates(codexEnvironment with { CodexOverride = Path.Combine(root, "missing.exe") }));
        Assert.Empty(ClaudeCliLocator.StandaloneCandidates(claudeEnvironment with { ClaudeOverride = Path.Combine(root, "missing.exe") }));
        Assert.Equal(8, CliLocator.CodexCandidates(codexEnvironment with { CodexOverride = null }).Count);
        Assert.Equal(8, ClaudeCliLocator.StandaloneCandidates(claudeEnvironment with { ClaudeOverride = null }).Count);
        Assert.Equal(8, CliLocator.CodexCandidates(codexEnvironment with { CodexOverride = null }).Distinct().Count());
        Assert.Equal(8, ClaudeCliLocator.StandaloneCandidates(claudeEnvironment with { ClaudeOverride = null }).Distinct().Count());
    }

    [Fact]
    public void ActivityRecognizesLaterCandidatesWithoutLaunchingAndHonorsEnablement()
    {
        var codex = new[] { @"C:\fixture\broken\codex.exe", @"C:\fixture\healthy\codex.exe" };
        var claude = new[] { @"C:\fixture\broken\claude.exe", @"C:\fixture\healthy\claude.exe" };
        var codexDiscovery = 0; var claudeDiscovery = 0; var captures = 0;
        var source = new WindowsActivitySource(() => { codexDiscovery++; return codex; }, () => { claudeDiscovery++; return claude; },
            (codexEnabled, claudeEnabled, codexCandidates, claudeCandidates) =>
            {
                captures++;
                return new(new(WindowsActivitySource.CandidateProvider(codex[1], codexEnabled, claudeEnabled, codexCandidates, claudeCandidates) == "Codex"),
                    new(WindowsActivitySource.CandidateProvider(claude[1], codexEnabled, claudeEnabled, codexCandidates, claudeCandidates) == "Claude Code"));
            });
        Assert.Equal(new[] { "Codex", "Claude Code" }, source.Capture().Providers);
        Assert.Equal(1, codexDiscovery); Assert.Equal(1, claudeDiscovery);
        source.SetEnabled(false, true);
        Assert.Equal(new[] { "Claude Code" }, source.Capture().Providers);
        Assert.Equal(1, codexDiscovery); Assert.Equal(2, claudeDiscovery);
        source.SetEnabled(false, false);
        Assert.Empty(source.Capture().Providers); Assert.Equal(2, captures);
        Assert.Null(WindowsActivitySource.CandidateProvider(@"C:\unrelated\codex.exe", true, true, codex, claude));
        Assert.Null(WindowsActivitySource.CandidateProvider(codex[1], false, true, codex, claude));
    }

    [Fact]
    public async Task CodexTriesLaterNativePackageWithinSameNpmPrefix()
    {
        var first = FileAt("npm", "node_modules", "@openai", "codex", "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe");
        var second = FileAt("npm", "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe");
        var environment = new CliSearchEnvironment(Path.Combine(root, "npm"), "", "", Architecture.X64);
        Assert.Equal(new[] { first, second }, CliLocator.CodexCandidates(environment));
        var attempts = 0;
        var result = await new CodexProvider(executableCandidates: () => CliLocator.CodexCandidates(environment), startProcess: executable =>
        {
            attempts++;
            if (executable == first) throw new Win32Exception(193);
            Assert.Equal(second, executable); return new Session();
        }).QueryAsync(default);
        Assert.Equal(FailureKind.None, result.Failure); Assert.Equal(2, attempts);
    }

    private static string PathValue(params string[] paths) => string.Join(Path.PathSeparator, paths.Select(Path.GetDirectoryName));
    private string FileAt(params string[] parts)
    {
        var file = Path.Combine([root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "Synthetic locator fixture; never executed.");
        return file;
    }
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
    private sealed class Session(Func<int, JsonElement>? read = null) : IProviderRpcProcess
    {
        public Task SendAsync(object message, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<JsonElement> ReadRpcResultAsync(int id, CancellationToken cancellationToken) => Task.FromResult(read?.Invoke(id) ?? DefaultResult(id));
        public static JsonElement DefaultResult(int id) => id switch
        {
            2 or 4 => Json("""{"account":{"type":"chatgpt","email":"fixture@example.invalid","planType":"pro"}}"""),
            3 => Json("""{"rateLimits":{"primary":{"usedPercent":12,"windowDurationMins":300}}}"""),
            _ => Json("{}")
        };
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
