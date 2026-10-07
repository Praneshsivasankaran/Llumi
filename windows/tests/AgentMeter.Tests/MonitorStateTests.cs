using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class MonitorStateTests
{
    [Theory]
    [InlineData("removed", "primary")]
    [InlineData(null, "primary")]
    [InlineData("SECONDARY", "secondary")]
    public void SavedDisplayWinsWhenPresentAndMissingDisplayFallsBackToPrimary(string? saved, string expected)
    {
        Assert.Equal(expected, MonitorPosition.SelectDisplay(saved, "primary", ["secondary", "primary"]));
        if (expected == "primary")
            Assert.Equal(new Point(20, 60), MonitorPosition.Restore(new(2, saved ?? "removed", .8, .5),
                expected, new(0, 40, 1600, 900), new(202, 34), 96));
    }

    private static UsageWindow W(string id, double? used = 30) => new(id, "Localized arbitrary label", used, null, id == "codex/secondary" ? 10080 : 300);
    private static ProviderState State(string name, params UsageWindow[] windows) => new(name, ProviderStatus.Ready,
        new UsageSnapshot(windows, DateTimeOffset.UtcNow, "test"));

    [Fact]
    public void CoreCodexAlwaysWinsOverReorderedSparkAndSecondary()
    {
        var main = W("codex/primary", 71);
        foreach (var windows in new[] {
            new[] { W("codex_bengalfox/primary", 0), W("codex/secondary", 5), main },
            new[] { main, W("codex_bengalfox/secondary", 0), W("codex/secondary", 5) } })
            Assert.Same(main, MonitorSelection.Select(State("Codex", windows)));
    }

    [Fact]
    public void SupportedLegacyCodexEnvelopeSelectsItsActualNormalizedCoreId()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""
            {"rateLimits":{"primary":{"usedPercent":71,"windowDurationMins":10080,"resetsAt":1789817100}}}
            """);
        var result = CodexParser.Parse(json.RootElement, DateTimeOffset.UtcNow);
        Assert.NotNull(result.Snapshot);
        var selected = MonitorSelection.Select(new ProviderState("Codex", ProviderStatus.Ready, result.Snapshot));
        Assert.NotNull(selected);
        Assert.Equal("codex/primary", selected.Id);
        Assert.Equal(29, selected.RemainingPercent);
    }

    [Fact]
    public void MainUnknownPercentageDoesNotBecomeBonusAllowance()
    {
        var main = W("codex/primary", null);
        Assert.Equal("codex/secondary", MonitorSelection.Select(State("Codex", main, W("codex/secondary", 0)))!.Id);
        Assert.Null(MonitorSelection.Select(State("Codex", W("codex_bengalfox/primary", 0))));
        Assert.Null(MonitorSelection.Select(State("Codex", W("invented/primary", 0))));
    }

    [Theory]
    [InlineData("Codex", "codex/secondary")]
    [InlineData("Claude", "five_hour")]
    [InlineData("Claude Code", "five_hour")]
    public void RecognizedFallbackRetainsOriginalWindow(string provider, string id)
    {
        var selected = W(id);
        Assert.Same(selected, MonitorSelection.Select(State(provider, selected)));
    }

    [Fact]
    public void ClaudeUsesFiveHourRegardlessOfLabelsOrderAndOtherModelLimits()
    {
        var main = W("five_hour", 8);
        Assert.Same(main, MonitorSelection.Select(State("Claude", W("seven_day", 0), W("seven_day_opus", 99), main)));
        Assert.Same(main, MonitorSelection.Select(State("Claude", main, W("seven_day", 0))));
        Assert.Null(MonitorSelection.Select(State("Claude", W("seven_day_sonnet"))));
    }

    [Fact]
    public void DuplicateAndUnknownSemanticsDoNotGuess()
    {
        Assert.Equal("seven_day", MonitorSelection.Select(State("Claude", W("seven_day"), W("seven_day")))!.Id);
        Assert.Null(MonitorSelection.Select(State("Claude", W("seven_day", 10), W("seven_day", 50))));
        Assert.Null(MonitorSelection.Select(State("Other", W("opaque"))));
        Assert.Null(MonitorSelection.Select(new ProviderState("Claude", ProviderStatus.Unavailable)));
    }

    [Fact]
    public void SelectionPreservesStaleSnapshotForPresentationToLabel()
    {
        var state = State("Claude", W("five_hour")) with { Status = ProviderStatus.Error, Failure = FailureKind.Network };
        Assert.Same(state.Snapshot!.Windows[0], MonitorSelection.Select(state));
    }

    [Theory]
    [InlineData(96)] [InlineData(120)] [InlineData(144)] [InlineData(168)] [InlineData(192)]
    public void LogicalPositionRestoresAcrossDpi(int dpi)
    {
        var work = new Rectangle(-2400, -100, 2400, 1500);
        var saved = new MonitorPosition(1, "second", 200, 200);
        var restored = MonitorPosition.Restore(saved, "second", work, new Size(350 * dpi / 96, 112 * dpi / 96), dpi);
        Assert.Equal(new Point(work.Left + 200 * dpi / 96, work.Top + 200 * dpi / 96), restored);
    }

    [Fact]
    public void MissingMonitorRestoresToCurrentWorkAreaWithScaledMargin()
    {
        var saved = new MonitorPosition(1, "removed", 800, 500);
        Assert.Equal(new Point(40, 120), MonitorPosition.Restore(saved, "primary", new Rectangle(0, 80, 2000, 1400), new Size(700, 224), 192));
    }

    [Fact]
    public void ChangedResolutionClampsAndOversizedWindowKeepsHeaderReachable()
    {
        var saved = new MonitorPosition(1, "screen", 4000, -500);
        Assert.Equal(new Point(450, 40), MonitorPosition.Restore(saved, "screen", new Rectangle(0, 40, 800, 600), new Size(350, 112), 96));
        Assert.Equal(new Point(-100, 20), MonitorPosition.Restore(saved, "screen", new Rectangle(-100, 20, 100, 80), new Size(350, 112), 96));
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(100001)] [InlineData(-100001)]
    public void InvalidCoordinatesUseSafeDefault(double x)
    {
        var saved = new MonitorPosition(1, "screen", x, 5);
        Assert.False(saved.IsValid);
        Assert.Equal(new Point(20, 20), MonitorPosition.Restore(saved, "screen", new Rectangle(0, 0, 800, 600), new Size(350, 112), 96));
    }

    [Fact]
    public void PositionRoundTripPersistsOnlyUiFieldsAndDoesNotRestorePinnedState()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "position.json");
        var store = new MonitorPositionStore(path);
        Assert.Null(store.Load());
        var saved = new MonitorPosition(1, "display", 50.5, 20.25);
        Assert.True(store.Save(saved));
        Assert.Equal(saved, new MonitorPositionStore(path).Load());
        Assert.True(store.Save(saved with { Left = 100 }));
        Assert.Equal(100, store.Load()!.Left);
        Assert.Single(Directory.GetFiles(directory.Path));
        Assert.DoesNotContain("pinned", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(96)] [InlineData(144)] [InlineData(192)]
    public void NormalizedAnchorPreservesRelativePositionAcrossResolutionAndDpi(int dpi)
    {
        var oldWork = new Rectangle(-2400, -100, 2400, 1500);
        var saved = MonitorPosition.Capture(new Point(-1900, 500), "second", oldWork, new Size(200, 34));
        Assert.Equal(2, saved.Version); Assert.Equal(.25, saved.Left); Assert.Equal(.4, saved.Top);
        var newWork = new Rectangle(-1600, 80, 1600, 1000);
        var size = MonitorForm.SizeForDpi(dpi);
        var restored = MonitorPosition.Restore(saved, "second", newWork, size, dpi);
        Assert.InRange(Math.Abs(restored.X + size.Width / 2d - (-1200)), 0, .5);
        Assert.Equal(480, restored.Y);
    }

    [Fact]
    public void NormalizedPositionRoundTripsWhileLegacySchemaStillLoads()
    {
        using var directory = new TempDirectory(); var path = Path.Combine(directory.Path, "position.json");
        var store = new MonitorPositionStore(path);
        var saved = MonitorPosition.Capture(new(100, 80), "screen", new(0, 0, 1000, 800), new(200, 34));
        Assert.True(store.Save(saved)); Assert.Equal(saved, store.Load());
        File.WriteAllText(path, """{"Version":1,"Display":"screen","Left":12.5,"Top":18}""");
        Assert.Equal(new MonitorPosition(1, "screen", 12.5, 18), store.Load());
    }

    [Theory]
    [InlineData("")] [InlineData("{")] [InlineData("[]")]
    [InlineData("{\"Version\":1,\"Display\":\"x\",\"Left\":1,\"Top\":2,\"Left\":3}")]
    [InlineData("{\"Version\":2,\"Display\":\"x\",\"Left\":1,\"Top\":2}")]
    [InlineData("{\"Version\":1,\"Display\":null,\"Left\":1,\"Top\":2}")]
    [InlineData("{\"Version\":1,\"Display\":\"x\",\"Left\":\"NaN\",\"Top\":2}")]
    public void CorruptPreferencesFailSafely(string content)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "position.json");
        File.WriteAllText(path, content);
        Assert.Null(new MonitorPositionStore(path).Load());
    }

    [Fact]
    public void OversizedAndUnwritablePreferencesDoNotCrashAndLogNoPaths()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "position.json");
        File.WriteAllText(path, new string(' ', 4097));
        var messages = new List<string>();
        Assert.Null(new MonitorPositionStore(path, messages.Add).Load());
        Assert.False(new MonitorPositionStore(directory.Path, messages.Add).Save(new(1, "x", 0, 0)));
        Assert.All(messages, message => Assert.StartsWith("monitor.position-", message));
        Assert.All(messages, message => Assert.DoesNotContain(directory.Path, message));
    }

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AgentMeter.MonitorTests." + Guid.NewGuid().ToString("N"));
        internal TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
