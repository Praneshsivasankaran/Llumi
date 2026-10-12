using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMeter.Core;

namespace AgentMeter;

internal enum AlertLedgerStoreStatus { NotLoaded, Created, Ready, Missing, Invalid, ProtectionFailed, ReadFailed, WriteFailed, Disposed }

internal interface IAlertStateProtector
{
    byte[] Protect(byte[] bytes);
    byte[] Unprotect(byte[] bytes);
}

internal interface IAlertLedgerStore : IDisposable
{
    AlertLedgerStoreStatus Status { get; }
    bool TryLoad(out AlertLedger ledger);
    bool TrySave(AlertLedger ledger);
    string Pseudonymize(string value);
}

// This is the sole on-disk exception for alert continuity. Both the random HMAC key
// and bounded pseudonymous ledger are encrypted/authenticated for the current Windows
// user. No provider response, usage amount, account identity or raw binding is written.
internal sealed class AlertLedgerStore(string directory, IAlertStateProtector? protector = null) : IAlertLedgerStore
{
    internal const int MaximumBytes = 524288;
    internal const string FileName = "usage-alerts.dat";
    private readonly object _sync = new();
    private readonly string _path = Path.Combine(directory, FileName);
    private readonly IAlertStateProtector _protector = protector ?? new WindowsAlertStateProtector();
    private byte[]? _key;
    private byte[]? _diskHash;
    private bool _loaded;
    private bool _existingObserved;
    private bool _disposed;
    public AlertLedgerStoreStatus Status { get; private set; } = AlertLedgerStoreStatus.NotLoaded;
    internal bool Available => !_disposed && _loaded && _key is not null && Status is AlertLedgerStoreStatus.Ready or AlertLedgerStoreStatus.Created;

    public bool TryLoad(out AlertLedger ledger)
    {
        lock (_sync)
        {
            ledger = new();
            if (_disposed) return false;
            byte[]? plain = null;
            try
            {
                byte[] encrypted;
                try
                {
                    using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    _existingObserved = true;
                    if (input.Length is <= 0 or > MaximumBytes) return Fail(AlertLedgerStoreStatus.Invalid);
                    encrypted = new byte[(int)input.Length];
                    input.ReadExactly(encrypted);
                }
                catch (FileNotFoundException)
                {
                    // Missing after a successful load is a fault, never a fresh ledger.
                    if (_loaded || _existingObserved) return Fail(AlertLedgerStoreStatus.Missing);
                    if (_key is not null) CryptographicOperations.ZeroMemory(_key);
                    _key = RandomNumberGenerator.GetBytes(32);
                    if (!Write(ledger, create: true)) return false;
                    _loaded = true;
                    Status = AlertLedgerStoreStatus.Created;
                    return true;
                }
                catch (DirectoryNotFoundException)
                {
                    if (_loaded || _existingObserved) return Fail(AlertLedgerStoreStatus.Missing);
                    if (_key is not null) CryptographicOperations.ZeroMemory(_key);
                    _key = RandomNumberGenerator.GetBytes(32);
                    if (!Write(ledger, create: true)) return false;
                    _loaded = true;
                    Status = AlertLedgerStoreStatus.Created;
                    return true;
                }
                try { plain = _protector.Unprotect(encrypted); }
                catch (CryptographicException) { return Fail(AlertLedgerStoreStatus.ProtectionFailed); }
                if (plain.Length is <= 0 or > MaximumBytes) return Fail(AlertLedgerStoreStatus.Invalid);
                using var document = JsonDocument.Parse(plain, new JsonDocumentOptions { MaxDepth = 8 });
                var root = document.RootElement;
                if (!HasExactly(root, "Version", "Key", "Ledger") || root.GetProperty("Version").GetInt32() != 1)
                    return Fail(AlertLedgerStoreStatus.Invalid);
                var keyText = root.GetProperty("Key").GetString();
                if (keyText is null || keyText.Length != 44) return Fail(AlertLedgerStoreStatus.Invalid);
                var key = Convert.FromBase64String(keyText);
                if (key.Length != 32) { CryptographicOperations.ZeroMemory(key); return Fail(AlertLedgerStoreStatus.Invalid); }
                var ledgerJson = root.GetProperty("Ledger");
                if (!HasExactly(ledgerJson, "Version", "Entries") || ledgerJson.GetProperty("Entries").ValueKind != JsonValueKind.Array ||
                    ledgerJson.GetProperty("Entries").GetArrayLength() > 256 ||
                    ledgerJson.GetProperty("Entries").EnumerateArray().Any(entry => !HasExactly(entry,
                        "Key", "ResetAt", "RearmAfter", "ObservedAt", "ConsumedThresholds", "AppliedSnoozeUntil")))
                { CryptographicOperations.ZeroMemory(key); return Fail(AlertLedgerStoreStatus.Invalid); }
                var value = ledgerJson.Deserialize<AlertLedger>();
                if (value is not { IsValid: true })
                { CryptographicOperations.ZeroMemory(key); return Fail(AlertLedgerStoreStatus.Invalid); }
                if (_key is not null) CryptographicOperations.ZeroMemory(_key);
                _key = key;
                _loaded = true;
                _diskHash = SHA256.HashData(encrypted);
                ledger = value;
                Status = AlertLedgerStoreStatus.Ready;
                return true;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
            { return Fail(AlertLedgerStoreStatus.Invalid); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            { return Fail(AlertLedgerStoreStatus.ReadFailed); }
            finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
        }
    }

    public bool TrySave(AlertLedger ledger)
    {
        lock (_sync)
        {
            if (!Available || !ledger.IsValid) return false;
            // An unexpected external change suspends delivery, preserving the evidence.
            try
            {
                using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length is <= 0 or > MaximumBytes || _diskHash is null ||
                    !CryptographicOperations.FixedTimeEquals(SHA256.HashData(input), _diskHash))
                    return Fail(AlertLedgerStoreStatus.Invalid);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            { return Fail(AlertLedgerStoreStatus.Missing); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return Fail(AlertLedgerStoreStatus.ReadFailed); }
            return Write(ledger, create: false);
        }
    }

    public string Pseudonymize(string value)
    {
        lock (_sync)
        {
            if (!Available) throw new InvalidOperationException("Alert state is unavailable.");
            if (string.IsNullOrEmpty(value) || value.Length > 8192) throw new ArgumentException("Invalid alert key.", nameof(value));
            var input = Encoding.UTF8.GetBytes("Llumi.UsageAlerts.v1\0" + value);
            try { return Convert.ToHexString(HMACSHA256.HashData(_key!, input)); }
            finally { CryptographicOperations.ZeroMemory(input); }
        }
    }

    private bool Write(AlertLedger ledger, bool create)
    {
        byte[]? plain = null;
        string? temporary = null;
        try
        {
            var envelope = new
            {
                Version = 1, Key = Convert.ToBase64String(_key!),
                Ledger = new { ledger.Version, Entries = ledger.EffectiveEntries.Select(entry => new
                    { entry.Key, entry.ResetAt, entry.RearmAfter, entry.ObservedAt, entry.ConsumedThresholds, entry.AppliedSnoozeUntil }).ToArray() }
            };
            plain = JsonSerializer.SerializeToUtf8Bytes(envelope);
            if (plain.Length > MaximumBytes) return Fail(AlertLedgerStoreStatus.Invalid);
            byte[] encrypted;
            try { encrypted = _protector.Protect(plain); }
            catch (CryptographicException) { return Fail(AlertLedgerStoreStatus.ProtectionFailed); }
            if (encrypted.Length is <= 0 or > MaximumBytes) return Fail(AlertLedgerStoreStatus.Invalid);
            var fullPath = Path.GetFullPath(_path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(encrypted); output.Flush(true); }
            if (create) File.Move(temporary, fullPath, overwrite: false);
            else File.Replace(temporary, fullPath, destinationBackupFileName: null);
            temporary = null;
            _diskHash = SHA256.HashData(encrypted);
            Status = AlertLedgerStoreStatus.Ready;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Fail(AlertLedgerStoreStatus.WriteFailed); }
        finally
        {
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static bool HasExactly(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var expected = new HashSet<string>(names, StringComparer.Ordinal);
        return element.EnumerateObject().All(property => expected.Remove(property.Name)) && expected.Count == 0;
    }

    private bool Fail(AlertLedgerStoreStatus status) { Status = status; return false; }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            if (_key is not null) CryptographicOperations.ZeroMemory(_key);
            _key = null;
            Status = AlertLedgerStoreStatus.Disposed;
        }
    }
}

internal sealed class WindowsAlertStateProtector : IAlertStateProtector
{
    public byte[] Protect(byte[] bytes) => Transform(bytes, protect: true);
    public byte[] Unprotect(byte[] bytes) => Transform(bytes, protect: false);

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var source = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var result = new DataBlob();
        try
        {
            Marshal.Copy(bytes, 0, source.Data, bytes.Length);
            // No LOCAL_MACHINE flag: only the current user's DPAPI scope. UI is forbidden.
            var succeeded = protect
                ? CryptProtectData(ref source, null, 0, 0, 0, 1, out result)
                : CryptUnprotectData(ref source, 0, 0, 0, 0, 1, out result);
            if (!succeeded) throw new CryptographicException("Alert state protection failed.");
            if (result.Length is <= 0 or > AlertLedgerStore.MaximumBytes) throw new CryptographicException("Invalid protected state size.");
            var output = new byte[result.Length];
            Marshal.Copy(result.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            for (var offset = 0; offset < bytes.Length; offset++) Marshal.WriteByte(source.Data, offset, 0);
            Marshal.FreeHGlobal(source.Data);
            if (result.Data != 0)
            {
                for (var offset = 0; offset < result.Length; offset++) Marshal.WriteByte(result.Data, offset, 0);
                LocalFree(result.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { internal int Length; internal nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, nint entropy, nint reserved, nint prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, nint description, nint entropy, nint reserved, nint prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
