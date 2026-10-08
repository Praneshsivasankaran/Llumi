using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class ClaudeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1789478000000);
    private const string Good = """{"version":2,"samples":[{"t":1789477765995,"org":"fixture-account","u":{"fh":0,"sd":8}}]}""";

    [Fact]
    public void RealShapePreservesZeroOriginalTimestampAndUnknownReset()
    {
        var result = ClaudeParser.Parse(Good, Now);
        Assert.Equal(FailureKind.None, result.Failure);
        Assert.NotNull(result.Snapshot);
        Assert.True(result.Snapshot.IsCached);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789477765995), result.Snapshot.ObservedAt);
        Assert.Equal(new double?[] { 100, 92 }, result.Snapshot.Windows.Select(w => w.RemainingPercent));
        Assert.All(result.Snapshot.Windows, window => Assert.Null(window.ResetsAt));
        Assert.DoesNotContain("fixture-account", result.Snapshot.Source);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"version\":2}")]
    [InlineData("{\"version\":2,\"version\":2,\"samples\":[]}")]
    [InlineData("{\"version\":2,\"samples\":[{}]}")]
    [InlineData("{\"version\":2,\"samples\":[{\"t\":\"1789477765995\",\"org\":\"a\",\"u\":{}}]}")]
    public void MalformedDocumentsFailWithoutExceptions(string json) =>
        Assert.Equal(FailureKind.Malformed, ClaudeParser.Parse(json, Now).Failure);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void UnknownAndUnidentifiedLegacyVersionsAreUnsupported(int version) =>
        Assert.Equal(FailureKind.Unsupported, ClaudeParser.Parse(Good.Replace("\"version\":2", $"\"version\":{version}"), Now).Failure);

    [Theory]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("1e999")]
    [InlineData("\"25%\"")]
    [InlineData("true")]
    public void InvalidPercentageRemainsUnknown(string percentage)
    {
        var result = ClaudeParser.Parse(Good.Replace("\"fh\":0", $"\"fh\":{percentage}"), Now);
        Assert.NotNull(result.Snapshot);
        Assert.Null(result.Snapshot.Windows[0].UsedPercent);
        Assert.Null(result.Snapshot.Windows[0].RemainingPercent);
        Assert.Equal(92, result.Snapshot.Windows[1].RemainingPercent);
    }

    [Fact]
    public void MissingAndNullFieldsAreNotInvented()
    {
        var result = ClaudeParser.Parse(Good.Replace("\"fh\":0,\"sd\":8", "\"sd\":null,\"so\":35"), Now);
        var window = Assert.Single(result.Snapshot!.Windows);
        Assert.Equal("so", window.Id);
        Assert.Equal(65, window.RemainingPercent);
    }

    [Fact]
    public void NoRecognizedWindowsAreUnavailable() => Assert.Equal(FailureKind.Unsupported,
        ClaudeParser.Parse(Good.Replace("\"fh\":0,\"sd\":8", "\"xu\":42,\"future\":19"), Now).Failure);

    [Fact]
    public void MultipleAccountsFailClosed()
    {
        var json = Good.Replace("}]}", ",\"ignored\":true},{\"t\":1789477865995,\"org\":\"second-account\",\"u\":{\"fh\":99}}]}");
        var result = ClaudeParser.Parse(json, Now);
        Assert.Null(result.Snapshot);
        Assert.Equal(FailureKind.Unsupported, result.Failure);
        Assert.Equal(ClaudeParser.AmbiguousAccountMessage, result.Detail);
        Assert.DoesNotContain("second-account", result.Detail);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    public void MissingAccountNeverDisplaysUsage(string account)
    {
        var result = ClaudeParser.Parse(Good.Replace("\"fixture-account\"", account), Now);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void SelectsNewestWholeSampleWithoutFillingFromOlderWindows()
    {
        var json = Good.Replace("}]}", "},{\"t\":1789477965995,\"org\":\"fixture-account\",\"u\":{\"sd\":12}}]}");
        var result = ClaudeParser.Parse(json, Now);
        var window = Assert.Single(result.Snapshot!.Windows);
        Assert.Equal("sd", window.Id);
        Assert.Equal(88, window.RemainingPercent);
    }

    [Fact]
    public void OutOfOrderSampleStillSelectsNewest()
    {
        var json = Good.Replace("}]}", "},{\"t\":1789477700000,\"org\":\"fixture-account\",\"u\":{\"sd\":99}}]}");
        Assert.Equal(92, ClaudeParser.Parse(json, Now).Snapshot!.Windows[1].RemainingPercent);
    }

    [Theory]
    [InlineData("999999999999999999")]
    [InlineData("1789479999999")]
    [InlineData("-999999999999999999")]
    public void InvalidOrFutureTimestampFails(string timestamp) => Assert.Equal(FailureKind.Malformed,
        ClaudeParser.Parse(Good.Replace("1789477765995", timestamp), Now).Failure);

    [Fact]
    public void DuplicatePercentageKeyFails() => Assert.Equal(FailureKind.Malformed,
        ClaudeParser.Parse(Good.Replace("\"fh\":0", "\"fh\":0,\"fh\":100"), Now).Failure);

    [Fact]
    public void ReReadingDoesNotFreshenObservedAt()
    {
        var before = ClaudeParser.Parse(Good, Now).Snapshot!;
        var after = ClaudeParser.Parse(Good, Now.AddHours(1)).Snapshot!;
        Assert.Equal(before.ObservedAt, after.ObservedAt);
        Assert.True(after.IsCached);
        Assert.True(Now.AddHours(1) - after.ObservedAt > TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task MissingCacheIsUnavailable() => Assert.Equal(FailureKind.Unsupported,
        (await new ClaudeProvider(() => []).QueryAsync(CancellationToken.None)).Failure);

    [Fact]
    public async Task MultipleInstallationsFailClosed() => Assert.Equal(FailureKind.Unsupported,
        (await new ClaudeProvider(() => ["one", "two"]).QueryAsync(CancellationToken.None)).Failure);

    [Fact]
    public async Task MissingFileDuringReadIsHandled() => Assert.Equal(FailureKind.Unsupported,
        (await new ClaudeProvider(() => [Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())]).QueryAsync(CancellationToken.None)).Failure);

    [Fact]
    public async Task CancellationIsRespected()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ClaudeProvider(() => []).QueryAsync(cancellation.Token));
    }

    [Fact]
    public async Task FileReadAndSubsequentCorruptionAreHandled()
    {
        var file = Path.GetTempFileName();
        try
        {
            var provider = new ClaudeProvider(() => [file], () => Now);
            await File.WriteAllTextAsync(file, Good);
            var unverified = await provider.QueryAsync(CancellationToken.None);
            Assert.Null(unverified.Snapshot);
            Assert.Equal(FailureKind.Unsupported, unverified.Failure);
            Assert.Equal(ClaudeProvider.UnverifiedAccountMessage, unverified.Detail);
            await File.WriteAllTextAsync(file, "{broken");
            Assert.Equal(FailureKind.Malformed, (await provider.QueryAsync(CancellationToken.None)).Failure);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData("fixture-account")]
    [InlineData("a-different-account")]
    public async Task AValidSingleAccountCacheNeverEstablishesCurrentCliIdentity(string account)
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, Good.Replace("fixture-account", account));
            var provider = new ClaudeProvider(() => [file], () => Now);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var result = await provider.QueryAsync(CancellationToken.None);
                Assert.Null(result.Snapshot);
                Assert.Equal(FailureKind.Unsupported, result.Failure);
                Assert.Equal(ClaudeProvider.UnverifiedAccountMessage, result.Detail);
                Assert.DoesNotContain(account, result.Detail);
            }
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task ProviderNeverFallsBackToNewestOfMultipleAccounts()
    {
        var file = Path.GetTempFileName();
        try
        {
            var json = Good.Replace("}]}", "},{\"t\":1789477965995,\"org\":\"other-account\",\"u\":{\"sd\":99}}]}");
            await File.WriteAllTextAsync(file, json);
            var result = await new ClaudeProvider(() => [file], () => Now).QueryAsync(CancellationToken.None);
            Assert.Null(result.Snapshot);
            Assert.Equal(ClaudeParser.AmbiguousAccountMessage, result.Detail);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task InvalidUtf8AndOversizedFilesAreHandled()
    {
        var file = Path.GetTempFileName();
        try
        {
            var provider = new ClaudeProvider(() => [file], () => Now);
            await File.WriteAllBytesAsync(file, [0xff, 0xff]);
            Assert.Equal(FailureKind.Malformed, (await provider.QueryAsync(CancellationToken.None)).Failure);
            await File.WriteAllBytesAsync(file, new byte[4 * 1024 * 1024 + 1]);
            Assert.Equal(FailureKind.Malformed, (await provider.QueryAsync(CancellationToken.None)).Failure);
        }
        finally { File.Delete(file); }
    }
}
