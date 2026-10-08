using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AgentMeter.Core;

public sealed class ProviderQueryException(FailureKind failure, TimeSpan? retryAfter = null) : Exception("Provider query failed")
{
    public FailureKind Failure { get; } = failure;
    // No server duration is different from an explicit duration. The scheduler
    // owns the escalating fallback, including when authentication changes later.
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public interface IProviderRpcProcess : IAsyncDisposable
{
    Task SendAsync(object message, CancellationToken cancellationToken);
    Task<JsonElement> ReadRpcResultAsync(int id, CancellationToken cancellationToken);
}

public sealed class ProviderProcess : IProviderRpcProcess
{
    private const int MaximumLineCharacters = 128 * 1024;
    private const int MaximumOutputCharacters = 1024 * 1024;
    private readonly Process _process;
    private readonly WindowsProcessJob? _job;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _stderrDrain;
    private readonly char[] _buffer = new char[4096];
    private int _position, _length, _outputCharacters;
    private bool _disposed;
    private readonly ProviderWorkspace _workspace;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> Owned = new();
    public static bool IsOwned(int processId) => Owned.ContainsKey(processId);

    private ProviderProcess(Process process, WindowsProcessJob? job, ProviderWorkspace workspace)
    {
        _process = process;
        _job = job;
        _workspace = workspace;
        Owned.TryAdd(process.Id, 0);
        _stderrDrain = DiscardStderrAsync();
    }

    public int ProcessId => _process.Id;

    public static ProviderProcess Start(string executable, IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environmentOverrides = null)
    {
        var workspace = new ProviderWorkspace();
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (environmentOverrides is not null)
            foreach (var (name, value) in environmentOverrides) info.Environment[name] = value;
        workspace.Configure(info);
        var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new ProviderQueryException(FailureKind.ProcessExited);
            return new ProviderProcess(process, WindowsProcessJob.Attach(process), workspace);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            process.Dispose();
            workspace.Dispose();
            throw;
        }
    }

    public async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> ReadControlResultAsync(string id, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) throw new ProviderQueryException(FailureKind.ProcessExited);
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            var message = document.RootElement;
            ClaudeControlTransport.RequireUnique(message);
            var type = ClaudeControlTransport.Text(message, "type");
            // Usage-only sessions must never return conversational messages.
            if (type is "assistant" or "user" or "result") throw new ProviderQueryException(FailureKind.Malformed);
            if (type != "control_response") continue;
            var response = message.GetProperty("response");
            if (ClaudeControlTransport.Text(response, "request_id") != id) continue;
            if (ClaudeControlTransport.Text(response, "subtype") != "success")
            {
                var error = ClaudeControlTransport.Get(response, "error");
                var category = error.ValueKind == JsonValueKind.String ? error.GetString() : ClaudeControlTransport.Text(error, "type");
                var rateLimited = category is "rate_limit_error" or "rate_limited" or "rate_limit_exceeded" ||
                    ClaudeControlTransport.Get(error, "code").TryGetInt32Safe(out var errorCode) && errorCode == 429;
                throw new ProviderQueryException(rateLimited ? FailureKind.RateLimited : FailureKind.Unsupported,
                    rateLimited ? RetryDelay(error, response) : null);
            }
            var payload = response.GetProperty("response");
            if (payload.ValueKind != JsonValueKind.Object) throw new ProviderQueryException(FailureKind.Malformed);
            return payload.Clone();
        }
    }

    public async Task<JsonElement> ReadRpcResultAsync(int id, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) throw new ProviderQueryException(FailureKind.ProcessExited);
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            var message = document.RootElement;
            if (message.ValueKind != JsonValueKind.Object || message.EnumerateObject().Select(p => p.Name)
                .Distinct(StringComparer.Ordinal).Count() != message.EnumerateObject().Count())
                throw new ProviderQueryException(FailureKind.Malformed);
            ClaudeControlTransport.RequireUnique(message);
            if (!message.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32Safe(out var value) || value != id)
                continue; // Notifications and unrelated responses are not usage.
            if (message.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            {
                var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var property) && property.TryGetInt32Safe(out var number) ? number : 0;
                var rateLimited = code is 429 or -32005 or -32001;
                throw new ProviderQueryException(rateLimited ? FailureKind.RateLimited :
                    code is -32601 or -32602 ? FailureKind.Unsupported : FailureKind.Network,
                    rateLimited ? RetryDelay(error, ClaudeControlTransport.Get(error, "data")) : null);
            }
            if (!message.TryGetProperty("result", out var result)) throw new ProviderQueryException(FailureKind.Malformed);
            return result.Clone();
        }
    }

    // Read fixed numeric fields only; provider error messages never enter diagnostics.
    private static TimeSpan? RetryDelay(JsonElement error, JsonElement metadata)
    {
        foreach (var source in new[] { error, metadata })
            foreach (var name in new[] { "retryAfterSeconds", "retry_after_seconds" })
            {
                var field = ClaudeControlTransport.Get(source, name);
                if (field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out var seconds) &&
                    double.IsFinite(seconds) && seconds > 0 && seconds <= 86_400) return TimeSpan.FromSeconds(seconds);
            }
        return null;
    }

    /// <summary>Reads one finite command's bounded JSON output and exit code.</summary>
    public async Task<(JsonElement Output, int ExitCode)> ReadJsonOutputAsync(CancellationToken cancellationToken)
    {
        // Status commands must complete without interactive input. RPC callers do not use this path.
        _process.StandardInput.Close();
        var output = new StringBuilder();
        while (await ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            output.AppendLine(line);
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output.ToString())) throw new ProviderQueryException(FailureKind.ProcessExited);
        using var document = JsonDocument.Parse(output.ToString(), new JsonDocumentOptions { MaxDepth = 32 });
        return (document.RootElement.Clone(), _process.ExitCode);
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (_position == _length)
            {
                _length = await _process.StandardOutput.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _position = 0;
                if (_length == 0) return line.Length == 0 ? null : line.ToString();
                _outputCharacters += _length;
                if (_outputCharacters > MaximumOutputCharacters) throw new ProviderQueryException(FailureKind.Malformed);
            }
            var newline = Array.IndexOf(_buffer, '\n', _position, _length - _position);
            var end = newline >= 0 ? newline : _length;
            if (line.Length + end - _position > MaximumLineCharacters) throw new ProviderQueryException(FailureKind.Malformed);
            line.Append(_buffer, _position, end - _position);
            _position = newline >= 0 ? end + 1 : end;
            if (newline >= 0) return line.ToString();
        }
    }

    private async Task DiscardStderrAsync()
    {
        var discard = new char[4096];
        try
        {
            while (await _process.StandardError.ReadAsync(discard.AsMemory(), _lifetime.Token).ConfigureAwait(false) > 0) { }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (IOException) { /* A killed child can close its pipe during the drain. */ }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        try { _process.StandardInput.Close(); }
        catch (IOException) { }
        catch (InvalidOperationException) { }
        _job?.Dispose();
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await _process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
        try { await _stderrDrain.WaitAsync(cleanup.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        Owned.TryRemove(_process.Id, out _);
        _process.Dispose();
        _workspace.Dispose();
        _lifetime.Dispose();
    }
}

internal static class JsonNumberExtensions
{
    public static bool TryGetInt32Safe(this JsonElement element, out int result)
    {
        result = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out result);
    }
}
