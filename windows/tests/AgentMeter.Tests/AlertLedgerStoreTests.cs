using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class AlertLedgerStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Llumi.AlertStore.Tests", Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_directory, AlertLedgerStore.FileName);
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
    private readonly PlainTestProtector _protector = new();

    [Fact]
    public void FreshStorePersistsKeyBeforeUseAndRetainsPseudonymsAndReservationsAcrossRestart()
    {
        string key;
        using (var first = new AlertLedgerStore(_directory, _protector))
        {
            Assert.Throws<InvalidOperationException>(() => first.Pseudonymize("synthetic-account"));
            Assert.True(first.TryLoad(out var empty));
            Assert.Empty(empty.EffectiveEntries);
            Assert.Equal(AlertLedgerStoreStatus.Created, first.Status);
            key = first.Pseudonymize("synthetic-account|five-hour");
            Assert.Equal(64, key.Length);
            Assert.True(first.TrySave(new(Entries: [new(key, Now.AddHours(5), Now.AddHours(5), Now, [75, 90], Now.AddHours(-1))])));
        }
        using var restarted = new AlertLedgerStore(_directory, _protector);
        Assert.True(restarted.TryLoad(out var ledger));
        Assert.Equal(AlertLedgerStoreStatus.Ready, restarted.Status);
        Assert.Equal(key, restarted.Pseudonymize("synthetic-account|five-hour"));
        var entry = Assert.Single(ledger.EffectiveEntries);
        Assert.Equal(new[] { 75, 90 }, entry.ConsumedThresholds);
        Assert.Equal(Now.AddHours(-1), entry.AppliedSnoozeUntil);
        Assert.DoesNotContain("synthetic-account", File.ReadAllText(StatePath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void SeparateInstallationsHaveUnrelatedFingerprints()
    {
        using var first = new AlertLedgerStore(_directory, _protector);
        using var other = new AlertLedgerStore(Path.Combine(_directory, "other"), _protector);
        Assert.True(first.TryLoad(out _));
        Assert.True(other.TryLoad(out _));
        Assert.NotEqual(first.Pseudonymize("synthetic-binding"), other.Pseudonymize("synthetic-binding"));
    }

    [Fact]
    public void ActualCurrentUserProtectionRoundTripsWithoutPlaintextMetadata()
    {
        string key;
        using (var first = new AlertLedgerStore(_directory))
        {
            Assert.True(first.TryLoad(out _));
            key = first.Pseudonymize("synthetic-sensitive-binding");
            Assert.True(first.TrySave(new(Entries: [new(key, Now.AddHours(5), Now.AddHours(5), Now, [95])])));
        }
        var bytes = File.ReadAllBytes(StatePath);
        Assert.DoesNotContain("ConsumedThresholds", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain(key, Encoding.UTF8.GetString(bytes));
        using var restarted = new AlertLedgerStore(_directory);
        Assert.True(restarted.TryLoad(out var state));
        Assert.Equal(key, Assert.Single(state.EffectiveEntries).Key);
        Assert.Equal(key, restarted.Pseudonymize("synthetic-sensitive-binding"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"Version\":1,\"Key\":\"bad\",\"Ledger\":{}}")]
    [InlineData("{\"Version\":1,\"Version\":1,\"Key\":\"bad\",\"Ledger\":{}}")]
    public void CorruptStateIsPreservedAndCannotBecomeEmpty(string bytes)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StatePath, bytes);
        using var store = new AlertLedgerStore(_directory, _protector);
        Assert.False(store.TryLoad(out _));
        Assert.Equal(AlertLedgerStoreStatus.Invalid, store.Status);
        Assert.False(store.TrySave(new()));
        Assert.Throws<InvalidOperationException>(() => store.Pseudonymize("synthetic"));
        Assert.Equal(bytes, File.ReadAllText(StatePath));
        File.Delete(StatePath);
        Assert.False(store.TryLoad(out _));
        Assert.Equal(AlertLedgerStoreStatus.Missing, store.Status);
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public void FailedUnprotectionNeverReplacesExistingBytes()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StatePath, "synthetic-protected-data");
        using var store = new AlertLedgerStore(_directory, new ThrowingProtector());
        Assert.False(store.TryLoad(out _));
        Assert.Equal(AlertLedgerStoreStatus.ProtectionFailed, store.Status);
        Assert.Equal("synthetic-protected-data", File.ReadAllText(StatePath));
    }

    [Fact]
    public void FailedInitialProtectionCannotCreateAnAvailableUnpersistedKey()
    {
        using var store = new AlertLedgerStore(_directory, new ThrowingProtector());
        Assert.False(store.TryLoad(out _));
        Assert.False(store.Available);
        Assert.False(File.Exists(StatePath));
        Assert.Throws<InvalidOperationException>(() => store.Pseudonymize("synthetic"));
    }

    [Fact]
    public void InitialProtectionFailureCanRecoverBeforeAnyLedgerWasCommitted()
    {
        var protector = new RecoverableProtector();
        using var store = new AlertLedgerStore(_directory, protector);
        Assert.False(store.TryLoad(out _));
        Assert.False(store.Available);
        protector.Reject = false;
        Assert.True(store.TryLoad(out var ledger));
        Assert.True(store.Available);
        Assert.Empty(ledger.EffectiveEntries);
        Assert.Equal(AlertLedgerStoreStatus.Created, store.Status);
        Assert.True(File.Exists(StatePath));
    }

    [Fact]
    public void OversizeInputIsRejectedBeforeUnprotection()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(StatePath, new byte[AlertLedgerStore.MaximumBytes + 1]);
        using var store = new AlertLedgerStore(_directory, new ThrowingProtector());
        Assert.False(store.TryLoad(out _));
        Assert.Equal(AlertLedgerStoreStatus.Invalid, store.Status);
    }

    [Fact]
    public void DeletedOrExternallyModifiedStateSuspendsSaving()
    {
        using var store = new AlertLedgerStore(_directory, _protector);
        Assert.True(store.TryLoad(out _));
        File.WriteAllText(StatePath, "external-change");
        Assert.False(store.TrySave(new()));
        Assert.Equal(AlertLedgerStoreStatus.Invalid, store.Status);
        Assert.Equal("external-change", File.ReadAllText(StatePath));
    }

    [Fact]
    public void MissingStateAfterLoadDoesNotRegenerateKeyOrLedger()
    {
        using var store = new AlertLedgerStore(_directory, _protector);
        Assert.True(store.TryLoad(out _));
        File.Delete(StatePath);
        Assert.False(store.TrySave(new()));
        Assert.Equal(AlertLedgerStoreStatus.Missing, store.Status);
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public void FailedAtomicReplaceKeepsPreviousStateAndCleansTemporaryFile()
    {
        using var store = new AlertLedgerStore(_directory, _protector);
        Assert.True(store.TryLoad(out _));
        var before = File.ReadAllBytes(StatePath);
        using (var fileLock = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(store.TrySave(new(Entries: [new(store.Pseudonymize("synthetic"), Now.AddHours(1), Now.AddHours(1), Now, [75])])));
            Assert.Equal(AlertLedgerStoreStatus.WriteFailed, store.Status);
        }
        Assert.Equal(before, File.ReadAllBytes(StatePath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Theory]
    [InlineData("RawAccountBinding")]
    [InlineData("ConsumedThresholds")]
    public void UnknownOrDuplicateEntryFieldsFailClosed(string injected)
    {
        using (var store = new AlertLedgerStore(_directory, _protector))
        {
            Assert.True(store.TryLoad(out _));
            Assert.True(store.TrySave(new(Entries: [new(store.Pseudonymize("synthetic"), Now.AddHours(1), Now.AddHours(1), Now, [75])])));
        }
        var json = File.ReadAllText(StatePath);
        json = json.Replace("\"ConsumedThresholds\":[75]", $"\"ConsumedThresholds\":[75],\"{injected}\":[]", StringComparison.Ordinal);
        File.WriteAllText(StatePath, json);
        using var reloaded = new AlertLedgerStore(_directory, _protector);
        Assert.False(reloaded.TryLoad(out _));
        Assert.Equal(AlertLedgerStoreStatus.Invalid, reloaded.Status);
    }

    [Fact]
    public void InvalidLedgerCannotReplaceGoodState()
    {
        using var store = new AlertLedgerStore(_directory, _protector);
        Assert.True(store.TryLoad(out _));
        var before = File.ReadAllBytes(StatePath);
        Assert.False(store.TrySave(new(Version: 99)));
        Assert.False(store.TrySave(new(Entries: [new("raw-synthetic-binding", Now.AddHours(1), Now.AddHours(1), Now, [75])])));
        Assert.Equal(before, File.ReadAllBytes(StatePath));
    }

    [Fact]
    public void DisposedStoreCannotUseKeyOrChangeFiles()
    {
        var store = new AlertLedgerStore(_directory, _protector);
        Assert.True(store.TryLoad(out _));
        store.Dispose();
        Assert.False(store.TryLoad(out _));
        Assert.False(store.TrySave(new()));
        Assert.Equal(AlertLedgerStoreStatus.Disposed, store.Status);
        Assert.Throws<InvalidOperationException>(() => store.Pseudonymize("synthetic"));
    }

    private sealed class PlainTestProtector : IAlertStateProtector
    {
        public byte[] Protect(byte[] bytes) => bytes.ToArray();
        public byte[] Unprotect(byte[] bytes) => bytes.ToArray();
    }
    private sealed class ThrowingProtector : IAlertStateProtector
    {
        public byte[] Protect(byte[] bytes) => throw new CryptographicException("synthetic");
        public byte[] Unprotect(byte[] bytes) => throw new CryptographicException("synthetic");
    }
    private sealed class RecoverableProtector : IAlertStateProtector
    {
        internal bool Reject = true;
        public byte[] Protect(byte[] bytes) => Reject ? throw new CryptographicException("synthetic") : bytes.ToArray();
        public byte[] Unprotect(byte[] bytes) => bytes.ToArray();
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
