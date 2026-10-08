using System.Text;
using System.Text.Json;
using AgentMeter.Core;

if (args.Length == 1 && args[0] == "--acceptance-activity") { await AcceptanceChecks.Run(includeUsage: false); return; }

if (args.Length == 1 && args[0] == "--acceptance") { await AcceptanceChecks.Run(); return; }

// Private validation harness; never packaged with the application.
if (args.Length == 2 && args[0] == "--activity-observe" && int.TryParse(args[1], out var seconds))
{
    var scanner = new WindowsActivitySource();
    var timer = System.Diagnostics.Stopwatch.StartNew();
    using var self = System.Diagnostics.Process.GetCurrentProcess();
    var cpuBefore = self.TotalProcessorTime.TotalSeconds;
    ActivitySnapshot? previous = null;
    while (timer.Elapsed.TotalSeconds < Math.Clamp(seconds, 1, 180))
    {
        var current = scanner.Capture();
        if (current != previous) Console.WriteLine(JsonSerializer.Serialize(new { seconds = Math.Round(timer.Elapsed.TotalSeconds, 2), activity = current }));
        previous = current;
        await Task.Delay(500);
    }
    Console.WriteLine(JsonSerializer.Serialize(new { elapsedSeconds = timer.Elapsed.TotalSeconds, cpuSeconds = self.TotalProcessorTime.TotalSeconds - cpuBefore }));
    return;
}

// A finite native fixture for environment inheritance. Keep this independent of
// PowerShell startup and ConvertTo-Json module autoload on a fresh Windows runner.
if (args.Length == 2 && args[0] == "--environment")
{
    Console.WriteLine(JsonSerializer.Serialize(new { value = Environment.GetEnvironmentVariable(args[1]) }));
    return;
}

// Run the production control adapter against this synthetic executable. Privacy
// preferences live only in this owned fixture process and its owned children.
if (args.SequenceEqual(new[] { "--claude-control-query" }))
{
    var names = new[] { "DISABLE_TELEMETRY", "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC", "DO_NOT_TRACK" };
    var before = names.Select(Environment.GetEnvironmentVariable).ToArray();
    Environment.SetEnvironmentVariable("LLUMI_SYNTHETIC_CONTROL_PROTOCOL", "1");
    var source = new ClaudeControlTransport(() => Environment.ProcessPath);
    var result = await source.QueryAsync(default);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        success = result.Usage.Failure == FailureKind.None && result.Binding is not null,
        remaining = result.Usage.Snapshot?.Windows.FirstOrDefault()?.RemainingPercent,
        parentEnvironmentUnchanged = before.SequenceEqual(names.Select(Environment.GetEnvironmentVariable))
    }));
    return;
}

if (Environment.GetEnvironmentVariable("LLUMI_SYNTHETIC_CONTROL_PROTOCOL") == "1")
{
    if (Environment.GetEnvironmentVariable("DISABLE_TELEMETRY") != "1") { Environment.ExitCode = 2; return; }
    if (args.SequenceEqual(new[] { "auth", "status" }))
    {
        Console.WriteLine("""{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"fixture@example.invalid","orgId":"00000000-0000-0000-0000-000000000042","orgName":"Fixture","subscriptionType":"pro","analyticsDisabled":true}""");
        return;
    }
    if (!args.SequenceEqual(ClaudeControlTransport.Arguments)) { Environment.ExitCode = 3; return; }
    using var init = JsonDocument.Parse((await Console.In.ReadLineAsync())!);
    var initialize = init.RootElement;
    if (initialize.GetProperty("type").GetString() != "control_request" ||
        initialize.GetProperty("request").GetProperty("subtype").GetString() != "initialize" ||
        initialize.GetProperty("request").GetProperty("hooks").EnumerateObject().Any()) { Environment.ExitCode = 4; return; }
    Console.WriteLine("""{"type":"control_response","response":{"subtype":"success","request_id":"init","response":{"account":{"email":"fixture@example.invalid","organization":"Fixture","apiProvider":"firstParty","apiKeySource":"none","tokenSource":"oauth"}}}}""");
    using var request = JsonDocument.Parse((await Console.In.ReadLineAsync())!);
    var controlQuery = request.RootElement;
    if (controlQuery.GetProperty("type").GetString() != "control_request" ||
        controlQuery.GetProperty("request").GetProperty("subtype").GetString() != "get_usage" ||
        !controlQuery.GetProperty("request").GetProperty("skip_behaviors").GetBoolean()) { Environment.ExitCode = 5; return; }
    Console.WriteLine("""{"type":"control_response","response":{"subtype":"success","request_id":"usage","response":{"rate_limits_available":true,"behaviors":null,"subscription_type":"pro","session":{"total_cost_usd":0,"total_api_duration_ms":0,"model_usage":{}},"rate_limits":{"five_hour":{"utilization":12}}}}}""");
    return;
}

// Test-only auth-status fixture verifies that the real source passes its child privacy flag.
if (args.SequenceEqual(new[] { "auth", "status" }))
{
    Console.WriteLine(JsonSerializer.Serialize(new { loggedIn = false }));
    Environment.ExitCode = Environment.GetEnvironmentVariable("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC") == "1" ? 1 : 2;
    return;
}

// Test-only sacrificial parent: killing this host must close its Windows job.
var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
var grandchildScript = Convert.ToBase64String(Encoding.Unicode.GetBytes("[System.Threading.Tasks.Task]::Delay(60000).GetAwaiter().GetResult()"));
var script = $"$child = Start-Process -FilePath '{shell.Replace("'", "''")}' -ArgumentList '-NoProfile','-NonInteractive','-EncodedCommand','{grandchildScript}' -PassThru -WindowStyle Hidden; [Console]::Out.WriteLine(('{{\"id\":1,\"result\":{{\"child\":' + $child.Id + '}}}}')); [System.Threading.Tasks.Task]::Delay(60000).GetAwaiter().GetResult()";
await using var query = ProviderProcess.Start(shell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]);
using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var ready = await query.ReadRpcResultAsync(1, startup.Token);
Console.WriteLine(JsonSerializer.Serialize(new { child = query.ProcessId, grandchild = ready.GetProperty("child").GetInt32() }));
await Console.Out.FlushAsync(startup.Token);
await Task.Delay(Timeout.Infinite, startup.Token);
