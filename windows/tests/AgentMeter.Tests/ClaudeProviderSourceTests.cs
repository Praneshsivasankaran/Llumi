using System.Collections.Concurrent;
using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class ClaudeProviderSourceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-15T15:00:00Z");
    private static readonly ClaudeAccountBinding FirstAccount = new("fixture-first-account", "fixture-first-organization");
    private static readonly ClaudeAccountBinding SecondAccount = new("fixture-second-account", "fixture-second-organization");

    [Fact]
    public void ProductionV2ProviderOnlyCreatesDirectControlSource()
    {
        // Inspect the default composition without querying the developer's authenticated account.
        // Explicit source injection below intentionally retains historical Desktop regressions.
        var provider = new ClaudeProvider();
        var field = typeof(ClaudeProvider).GetField("sources", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var sources = Assert.IsType<IClaudeUsageSource[]>(field!.GetValue(provider));
        Assert.IsType<ClaudeControlTransport>(Assert.Single(sources));
    }

    [Theory]
    [InlineData(ClaudeClient.Desktop, ClaudeAuthentication.SignedOut)]
    [InlineData(ClaudeClient.Code, ClaudeAuthentication.Missing)]
    public async Task OneAuthenticatedClientWorksWithoutTheOther(ClaudeClient client, ClaudeAuthentication otherAuthentication)
    {
        var expected = Good(client);
        var good = Constant(expected);
        var absent = Constant(Absent(Other(client), otherAuthentication));
        var result = await Provider(good, absent).QueryAsync(CancellationToken.None);
        Assert.Same(expected.Usage.Snapshot, result.Snapshot);
        Assert.Equal(FailureKind.None, result.Failure);
        Assert.Equal(1, good.Calls);
        Assert.Equal(1, absent.Calls);
    }

    [Fact]
    public async Task AccountSwitchFollowedByMalformedUsageClearsOldCoordinatorSnapshot()
    {
        var current = Good(ClaudeClient.Desktop);
        var source = Dynamic(ClaudeClient.Desktop, () => current);
        var coordinator = Coordinator(Provider(source));
        await coordinator.RefreshAsync();
        Assert.Same(current.Usage.Snapshot!.Windows, Assert.Single(coordinator.States).Snapshot!.Windows);

        current = Failed(ClaudeClient.Desktop, FailureKind.Malformed, SecondAccount);
        await coordinator.RefreshAsync();
        var failed = Assert.Single(coordinator.States);
        Assert.Null(failed.Snapshot);
        Assert.Equal(FailureKind.Malformed, failed.Failure);
        Assert.Equal(ProviderStatus.Error, failed.Status);

        // A later failure on that same new account must not resurrect the first one.
        current = Failed(ClaudeClient.Desktop, FailureKind.Network, SecondAccount);
        await coordinator.RefreshAsync();
        Assert.Null(Assert.Single(coordinator.States).Snapshot);
    }

    [Fact]
    public async Task LostIdentityAndMalformedStateClearsOldCoordinatorSnapshot()
    {
        var current = Good(ClaudeClient.Desktop);
        var coordinator = Coordinator(Provider(Dynamic(ClaudeClient.Desktop, () => current)));
        await coordinator.RefreshAsync();
        Assert.NotNull(Assert.Single(coordinator.States).Snapshot);

        current = new(ClaudeClient.Desktop, ClaudeAuthentication.Unknown, null, ProviderResult.Fail(FailureKind.Malformed));
        await coordinator.RefreshAsync();
        Assert.Equal(FailureKind.Malformed, Assert.Single(coordinator.States).Failure);
        Assert.Null(Assert.Single(coordinator.States).Snapshot);
        await coordinator.RefreshAsync();
        Assert.Equal(FailureKind.Malformed, Assert.Single(coordinator.States).Failure);
        Assert.Null(Assert.Single(coordinator.States).Snapshot);
    }

    [Fact]
    public async Task SameVerifiedAccountNetworkFailureRetainsVisiblyStaleSnapshot()
    {
        var original = Good(ClaudeClient.Desktop);
        var current = original;
        var coordinator = Coordinator(Provider(Dynamic(ClaudeClient.Desktop, () => current)));
        await coordinator.RefreshAsync();
        current = Failed(ClaudeClient.Desktop, FailureKind.Network, FirstAccount);
        await coordinator.RefreshAsync();
        var state = Assert.Single(coordinator.States);
        Assert.Same(original.Usage.Snapshot!.Windows, state.Snapshot!.Windows);
        Assert.Equal(FailureKind.Network, state.Failure);
        Assert.True(state.IsStale(Now));
        Assert.Equal("Stale", PopupText.Status(state, Now));
    }

    [Fact]
    public async Task NewVerifiedAccountSuccessReplacesSnapshotAndResetsContinuity()
    {
        var current = Good(ClaudeClient.Desktop);
        var coordinator = Coordinator(Provider(Dynamic(ClaudeClient.Desktop, () => current)));
        await coordinator.RefreshAsync();
        var replacement = Good(ClaudeClient.Desktop, SecondAccount, 78);
        current = replacement;
        await coordinator.RefreshAsync();
        Assert.Same(replacement.Usage.Snapshot!.Windows, Assert.Single(coordinator.States).Snapshot!.Windows);

        current = Failed(ClaudeClient.Desktop, FailureKind.Network, SecondAccount);
        await coordinator.RefreshAsync();
        Assert.Equal(FailureKind.Network, Assert.Single(coordinator.States).Failure);
        Assert.Same(replacement.Usage.Snapshot!.Windows, Assert.Single(coordinator.States).Snapshot!.Windows);
    }

    [Fact]
    public async Task SigningOutClearsPreviousUsage()
    {
        var current = Good(ClaudeClient.Desktop);
        var coordinator = Coordinator(Provider(Dynamic(ClaudeClient.Desktop, () => current)));
        await coordinator.RefreshAsync();
        current = Absent(ClaudeClient.Desktop, ClaudeAuthentication.SignedOut);
        await coordinator.RefreshAsync();
        Assert.Null(Assert.Single(coordinator.States).Snapshot);
        Assert.Equal(ProviderStatus.Unavailable, Assert.Single(coordinator.States).Status);
    }

    [Fact]
    public async Task SameAccountSourceFailureDoesNotBlockHealthyPeer()
    {
        var desktop = Good(ClaudeClient.Desktop);
        var provider = Provider(Constant(desktop), Constant(Failed(ClaudeClient.Code, FailureKind.Timeout, FirstAccount)));
        var result = await provider.QueryAsync(CancellationToken.None);
        Assert.Equal(FailureKind.None, result.Failure);
        Assert.Same(desktop.Usage.Snapshot, result.Snapshot);
    }

    [Fact]
    public async Task VerifiedCliWorksWithAmbiguousDesktopHistoryAndKeepsItsOwnStaleData()
    {
        var cliUsage = Good(ClaudeClient.Code);
        var currentCli = cliUsage;
        var desktop = new ClaudeSourceResult(ClaudeClient.Desktop, ClaudeAuthentication.Unknown, null,
            ProviderResult.Fail(FailureKind.Unsupported, "Fixture historical accounts are ambiguous"));
        var coordinator = Coordinator(Provider(Constant(desktop), Dynamic(ClaudeClient.Code, () => currentCli)));
        await coordinator.RefreshAsync();
        Assert.Same(cliUsage.Usage.Snapshot!.Windows, Assert.Single(coordinator.States).Snapshot!.Windows);

        currentCli = Failed(ClaudeClient.Code, FailureKind.Network, FirstAccount);
        await coordinator.RefreshAsync();
        var stale = Assert.Single(coordinator.States);
        Assert.Equal(FailureKind.Network, stale.Failure);
        Assert.Same(cliUsage.Usage.Snapshot!.Windows, stale.Snapshot!.Windows);
        Assert.True(stale.IsStale(Now));

        currentCli = new(ClaudeClient.Code, ClaudeAuthentication.Unknown, null, ProviderResult.Fail(FailureKind.Malformed));
        await coordinator.RefreshAsync();
        Assert.Null(Assert.Single(coordinator.States).Snapshot);
        Assert.Equal(FailureKind.Malformed, Assert.Single(coordinator.States).Failure);
    }

    [Fact]
    public async Task NewlyVerifiedDifferentDesktopAccountClearsPreviousCliUsage()
    {
        var currentDesktop = new ClaudeSourceResult(ClaudeClient.Desktop, ClaudeAuthentication.Unknown, null,
            ProviderResult.Fail(FailureKind.Unsupported));
        var coordinator = Coordinator(Provider(Dynamic(ClaudeClient.Desktop, () => currentDesktop), Constant(Good(ClaudeClient.Code))));
        await coordinator.RefreshAsync();
        Assert.NotNull(Assert.Single(coordinator.States).Snapshot);

        currentDesktop = Failed(ClaudeClient.Desktop, FailureKind.Network, SecondAccount);
        await coordinator.RefreshAsync();
        Assert.Equal(FailureKind.Unsupported, Assert.Single(coordinator.States).Failure);
        Assert.Null(Assert.Single(coordinator.States).Snapshot);
    }

    [Fact]
    public async Task VerifiedCliCanReplaceLostDesktopIdentityWithoutReusingHistoricalNumbers()
    {
        var originalDesktop = Good(ClaudeClient.Desktop);
        var currentDesktop = originalDesktop;
        var currentCli = Absent(ClaudeClient.Code);
        var coordinator = Coordinator(Provider(Dynamic(ClaudeClient.Desktop, () => currentDesktop), Dynamic(ClaudeClient.Code, () => currentCli)));
        await coordinator.RefreshAsync();
        Assert.Same(originalDesktop.Usage.Snapshot!.Windows, Assert.Single(coordinator.States).Snapshot!.Windows);

        currentDesktop = originalDesktop with { Authentication = ClaudeAuthentication.Unknown };
        var replacement = Good(ClaudeClient.Code, SecondAccount, 73);
        currentCli = replacement;
        await coordinator.RefreshAsync();
        Assert.Same(replacement.Usage.Snapshot!.Windows, Assert.Single(coordinator.States).Snapshot!.Windows);

        currentCli = Failed(ClaudeClient.Code, FailureKind.Network, SecondAccount);
        await coordinator.RefreshAsync();
        Assert.Same(replacement.Usage.Snapshot!.Windows, Assert.Single(coordinator.States).Snapshot!.Windows);
        Assert.Equal(FailureKind.Network, Assert.Single(coordinator.States).Failure);
    }

    [Fact]
    public async Task ThrownIdentityQueryDoesNotBlockVerifiedPeerOrLeakException()
    {
        var logs = new ConcurrentQueue<string>();
        var failed = new Source(ClaudeClient.Code, _ => throw new IOException("fixture-sensitive-exception-value"));
        var healthy = Good(ClaudeClient.Desktop);
        var provider = new ClaudeProvider([Constant(healthy), failed], () => Now, log: logs.Enqueue);
        var result = await provider.QueryAsync(CancellationToken.None);
        Assert.Equal(FailureKind.None, result.Failure);
        Assert.Same(healthy.Usage.Snapshot, result.Snapshot);
        Assert.DoesNotContain("fixture-sensitive-exception-value", result.Detail ?? "");
        Assert.DoesNotContain(logs, entry => entry.Contains("fixture-sensitive-exception-value", StringComparison.Ordinal));
        Assert.Contains("claude.Code.Unknown.Unexpected", logs);
    }

    [Fact]
    public async Task SourceFailureDoesNotPreventAnotherProviderRefreshing()
    {
        var claude = Provider(new Source(ClaudeClient.Desktop, _ => throw new IOException("fixture failure")));
        var healthy = new OtherProvider();
        var coordinator = new RefreshCoordinator([claude, healthy], minimumInterval: TimeSpan.Zero);
        await coordinator.RefreshAsync();
        Assert.Equal(FailureKind.Unexpected, coordinator.States.Single(state => state.Name == "Claude Code").Failure);
        var ready = coordinator.States.Single(state => state.Name == healthy.Name);
        Assert.Equal(ProviderStatus.Ready, ready.Status);
        Assert.NotNull(ready.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncooperativeTimedOutSourceIsNotQueriedAgainWhilePending(bool verifiedPeerAvailable)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ClaudeSourceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source(ClaudeClient.Desktop, async _ =>
        {
            started.TrySetResult();
            try { return await release.Task; }
            finally { finished.TrySetResult(); }
        });
        var cli = Good(ClaudeClient.Code);
        IClaudeUsageSource[] sources = verifiedPeerAvailable ? [source, Constant(cli)] : [source];
        var provider = new ClaudeProvider(sources, () => Now, TimeSpan.FromMilliseconds(250));
        try
        {
            var first = provider.QueryAsync(CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(verifiedPeerAvailable ? FailureKind.None : FailureKind.Timeout, firstResult.Failure);
            Assert.Same(verifiedPeerAvailable ? cli.Usage.Snapshot : null, firstResult.Snapshot);
            var second = await provider.QueryAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(verifiedPeerAvailable ? FailureKind.None : FailureKind.Timeout, second.Failure);
            Assert.Same(verifiedPeerAvailable ? cli.Usage.Snapshot : null, second.Snapshot);
            Assert.Equal(1, source.Calls);
        }
        finally
        {
            release.TrySetResult(Good(ClaudeClient.Desktop));
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task SimultaneousProviderRefreshCannotStartDuplicateSourceQueries()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ClaudeSourceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source(ClaudeClient.Desktop, _ =>
        {
            started.TrySetResult();
            return release.Task;
        });
        var provider = Provider(source);
        var first = provider.QueryAsync(CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var overlapping = await provider.QueryAsync(CancellationToken.None);
            Assert.Equal(FailureKind.Timeout, overlapping.Failure);
            Assert.Equal(1, source.Calls);
        }
        finally { release.TrySetResult(Good(ClaudeClient.Desktop)); }
        Assert.Equal(FailureKind.None, (await first.WaitAsync(TimeSpan.FromSeconds(5))).Failure);
    }

    [Fact]
    public async Task AlreadyCancelledRefreshDoesNotInvokeSource()
    {
        var source = Constant(Good(ClaudeClient.Desktop));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(source).QueryAsync(cancellation.Token));
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task CancellationReachesSourceWithoutStartingAnotherQuery()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source(ClaudeClient.Desktop, async token =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); }
            return Good(ClaudeClient.Desktop);
        });
        var provider = Provider(source);
        using var cancellation = new CancellationTokenSource();
        var pending = provider.QueryAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task CompletedSourceCancellationDoesNotPoisonLaterRefresh()
    {
        var firstCall = true;
        var source = new Source(ClaudeClient.Desktop, _ =>
        {
            if (!firstCall) return Task.FromResult(Good(ClaudeClient.Desktop));
            firstCall = false;
            return Task.FromException<ClaudeSourceResult>(new OperationCanceledException());
        });
        var provider = Provider(source);
        Assert.Equal(FailureKind.Timeout, (await provider.QueryAsync(CancellationToken.None)).Failure);
        var result = await provider.QueryAsync(CancellationToken.None);
        Assert.Equal(FailureKind.None, result.Failure);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task LoggingContainsClientHealthWithoutAccountOrOrganizationIdentifiers()
    {
        var logs = new ConcurrentQueue<string>();
        var provider = new ClaudeProvider([Constant(Good(ClaudeClient.Desktop)), Constant(Absent(ClaudeClient.Code))],
            () => Now, log: logs.Enqueue);
        Assert.NotNull((await provider.QueryAsync(CancellationToken.None)).Snapshot);
        var text = string.Join("\n", logs);
        Assert.DoesNotContain(FirstAccount.AccountId!, text);
        Assert.DoesNotContain(FirstAccount.OrganizationId!, text);
        Assert.Contains("claude.Desktop.Authenticated.None", text);
        Assert.Contains("claude.selected.Desktop", text);
    }

    [Fact]
    public async Task SourceCannotImpersonateDifferentClient()
    {
        var incorrect = new Source(ClaudeClient.Code, _ => Task.FromResult(Good(ClaudeClient.Desktop)));
        var result = await Provider(incorrect).QueryAsync(CancellationToken.None);
        Assert.Equal(FailureKind.Malformed, result.Failure);
        Assert.Null(result.Snapshot);
        var verifiedDesktop = Good(ClaudeClient.Desktop);
        var healthyPeer = await Provider(Constant(verifiedDesktop), incorrect).QueryAsync(CancellationToken.None);
        Assert.Equal(FailureKind.None, healthyPeer.Failure);
        Assert.Same(verifiedDesktop.Usage.Snapshot, healthyPeer.Snapshot);
    }

    [Fact]
    public void DuplicateClientSourcesAreRejectedAtConstruction() => Assert.Throws<ArgumentException>(() =>
        Provider(Constant(Good(ClaudeClient.Desktop)), Constant(Good(ClaudeClient.Desktop))));

    private static ClaudeProvider Provider(params IClaudeUsageSource[] sources) => new(sources, () => Now);
    private static RefreshCoordinator Coordinator(ClaudeProvider provider) => new([provider], minimumInterval: TimeSpan.Zero);
    private static Source Constant(ClaudeSourceResult result) => new(result.Client, _ => Task.FromResult(result));
    private static Source Dynamic(ClaudeClient client, Func<ClaudeSourceResult> result) => new(client, _ => Task.FromResult(result()));
    private static ClaudeClient Other(ClaudeClient client) => client == ClaudeClient.Desktop ? ClaudeClient.Code : ClaudeClient.Desktop;
    private static ClaudeSourceResult Good(ClaudeClient client, ClaudeAccountBinding? binding = null, double used = 20) =>
        new(client, ClaudeAuthentication.Authenticated, binding ?? FirstAccount,
            new(new UsageSnapshot([new("five_hour", "5 hours", used, Now.AddHours(1))], Now, $"Fixture {client}")));
    private static ClaudeSourceResult Failed(ClaudeClient client, FailureKind failure, ClaudeAccountBinding binding) =>
        new(client, ClaudeAuthentication.Authenticated, binding, ProviderResult.Fail(failure));
    private static ClaudeSourceResult Absent(ClaudeClient client, ClaudeAuthentication authentication = ClaudeAuthentication.Missing) =>
        new(client, authentication, null, ProviderResult.Fail(authentication == ClaudeAuthentication.SignedOut
            ? FailureKind.LoggedOut : FailureKind.NotInstalled));

    private sealed class Source(ClaudeClient client, Func<CancellationToken, Task<ClaudeSourceResult>> query) : IClaudeUsageSource
    {
        private int calls;
        public ClaudeClient Client => client;
        public int Calls => Volatile.Read(ref calls);
        public Task<ClaudeSourceResult> QueryAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return query(cancellationToken);
        }
    }

    private sealed class OtherProvider : IUsageProvider
    {
        public string Name => "Healthy fixture provider";
        public Task<ProviderResult> QueryAsync(CancellationToken cancellationToken) => Task.FromResult(Good(ClaudeClient.Code).Usage);
    }
}
