using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AgentMeter.Core;

namespace AgentMeter.Tests;

public sealed class ProcessTests
{
    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    private static string Encoded(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    private static ProviderProcess Start(string script) => ProviderProcess.Start(PowerShell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Encoded(script)]);

    [Fact]
    public async Task ParsesRpcWhileIgnoringNotificationsAndDrainingStderr()
    {
        await using var process = Start("[Console]::Error.Write(('x' * 100000)); [Console]::Out.WriteLine('{\"method\":\"updated\"}'); [Console]::Out.WriteLine('{\"id\":4,\"result\":{\"ok\":true}}')");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await process.ReadRpcResultAsync(4, timeout.Token);
        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task TimeoutCancelsReadAndDisposalKillsOwnedProcess()
    {
        await using var process = Start("[Console]::Out.WriteLine('{\"id\":1,\"result\":{}}'); [System.Threading.Tasks.Task]::Delay(60000).GetAwaiter().GetResult()");
        var id = process.ProcessId;
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.ReadRpcResultAsync(1, startup.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.ReadRpcResultAsync(2, timeout.Token));
        await process.DisposeAsync();
        Assert.False(IsRunning(id));
    }

    [Fact]
    public async Task UnexpectedExitIsAControlledFailure()
    {
        await using var process = Start("exit 0");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<ProviderQueryException>(() => process.ReadRpcResultAsync(1, timeout.Token));
        Assert.Equal(FailureKind.ProcessExited, error.Failure);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"id\":1,\"result\":")]
    public async Task MalformedOrPartialRpcAtEndOfStreamCannotProduceUsage(string response)
    {
        await using var process = Start($"[Console]::Out.Write('{response.Replace("'", "''")}')");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<JsonException>(() => process.ReadRpcResultAsync(1, timeout.Token));
    }

    [Theory]
    [InlineData(-32601, FailureKind.Unsupported)]
    [InlineData(-32602, FailureKind.Unsupported)]
    [InlineData(-32000, FailureKind.Network)]
    [InlineData(429, FailureKind.RateLimited)]
    [InlineData(-32005, FailureKind.RateLimited)]
    public async Task RpcErrorsAreClassifiedWithoutExposingRawMessage(int code, FailureKind failure)
    {
        await using var process = Start($"[Console]::Out.WriteLine('{{\"id\":1,\"error\":{{\"code\":{code},\"message\":\"secret raw response\"}}}}')");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<ProviderQueryException>(() => process.ReadRpcResultAsync(1, timeout.Token));
        Assert.Equal(failure, error.Failure);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Fact]
    public async Task StructuredRpcRateLimitPreservesNumericRetryWithoutRawText()
    {
        await using var process = Start("[Console]::Out.WriteLine('{\"id\":1,\"error\":{\"code\":429,\"message\":\"secret raw response\",\"data\":{\"retryAfterSeconds\":180}}}')");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<ProviderQueryException>(() => process.ReadRpcResultAsync(1, timeout.Token));
        Assert.Equal(FailureKind.RateLimited, error.Failure);
        Assert.Equal(TimeSpan.FromMinutes(3), error.RetryAfter);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Theory]
    [InlineData("rate_limit_error", FailureKind.RateLimited)]
    [InlineData("rate_limited", FailureKind.RateLimited)]
    [InlineData("secret raw response 429", FailureKind.Unsupported)]
    public async Task ControlErrorsUseOnlyFixedCategoriesAndPreserveRetryDelay(string category, FailureKind expected)
    {
        var message = JsonSerializer.Serialize(new { type = "control_response", response = new
        { request_id = "usage", subtype = "error", error = new { type = category, retry_after_seconds = 90 } } });
        await using var process = Start("[Console]::Out.WriteLine('" + message + "')");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<ProviderQueryException>(() => process.ReadControlResultAsync("usage", timeout.Token));
        Assert.Equal(expected, error.Failure);
        Assert.Equal(expected == FailureKind.RateLimited ? TimeSpan.FromSeconds(90) : (TimeSpan?)null, error.RetryAfter);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Fact]
    public async Task ExcessiveUnterminatedOutputIsBounded()
    {
        await using var process = Start("[Console]::Out.Write(('x' * 200000)); [Console]::Out.Flush(); [System.Threading.Tasks.Task]::Delay(60000).GetAwaiter().GetResult()");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<ProviderQueryException>(() => process.ReadRpcResultAsync(1, timeout.Token));
        Assert.Equal(FailureKind.Malformed, error.Failure);
    }

    [Theory]
    [InlineData("{\"id\":9,\"id\":1,\"result\":{}}")]
    [InlineData("{\"id\":1,\"result\":{},\"result\":{\"usage\":100}}")]
    [InlineData("{\"id\":1,\"error\":{},\"error\":null,\"result\":{}}")]
    public async Task DuplicateRpcEnvelopeFieldsCannotDisguiseDifferentResponse(string response)
    {
        await using var process = Start($"[Console]::Out.WriteLine('{response}')");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<ProviderQueryException>(() => process.ReadRpcResultAsync(1, timeout.Token));
        Assert.Equal(FailureKind.Malformed, error.Failure);
    }

    [Fact]
    public async Task DisposalKillsDescendantProcesses()
    {
        var childCommand = Encoded("[System.Threading.Tasks.Task]::Delay(60000).GetAwaiter().GetResult()");
        await using var process = Start($"$child = Start-Process -FilePath '{PowerShell.Replace("'", "''")}' -ArgumentList '-NoProfile','-NonInteractive','-EncodedCommand','{childCommand}' -PassThru -WindowStyle Hidden; [Console]::Out.WriteLine(('{{\"id\":1,\"result\":{{\"child\":' + $child.Id + '}}}}')); [System.Threading.Tasks.Task]::Delay(60000).GetAwaiter().GetResult()");
        // Allow cold PowerShell/Start-Process setup separately from the asserted cleanup.
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await process.ReadRpcResultAsync(1, startup.Token);
        var childId = result.GetProperty("child").GetInt32();
        Assert.True(IsRunning(childId));
        using var exited = Process.GetProcessById(childId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.DisposeAsync();
        await exited.WaitForExitAsync(timeout.Token);
        Assert.False(IsRunning(childId));
    }

    [Fact]
    public async Task AbruptParentTerminationKillsItsQueryAndGrandchild()
    {
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var dotnet = Path.Combine(runtime.Parent!.Parent!.Parent!.FullName, "dotnet.exe");
        var info = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "AgentMeter.ProcessHost.dll"));
        using var host = Process.Start(info)!;
        Process? child = null;
        Process? grandchild = null;
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var line = await host.StandardOutput.ReadLineAsync(startup.Token);
            Assert.False(string.IsNullOrWhiteSpace(line));
            using var ready = JsonDocument.Parse(line!);
            child = Process.GetProcessById(ready.RootElement.GetProperty("child").GetInt32());
            grandchild = Process.GetProcessById(ready.RootElement.GetProperty("grandchild").GetInt32());
            Assert.False(child.HasExited);
            Assert.False(grandchild.HasExited);

            // Deliberately kill only the parent. Its managed finally/dispose code cannot run.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            host.Kill(entireProcessTree: false);
            await host.WaitForExitAsync(timeout.Token);
            await child.WaitForExitAsync(timeout.Token);
            await grandchild.WaitForExitAsync(timeout.Token);
            Assert.True(child.HasExited);
            Assert.True(grandchild.HasExited);
        }
        finally
        {
            // Clean up owned fixture processes even when an assertion fails.
            foreach (var process in new[] { host, child, grandchild })
            {
                if (process is null) continue;
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            child?.Dispose();
            grandchild?.Dispose();
        }
    }

    private static bool IsRunning(int id)
    {
        try { using var process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
