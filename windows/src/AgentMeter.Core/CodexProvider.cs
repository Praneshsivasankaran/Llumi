using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentMeter.Core;

public sealed class CodexProvider : IUsageProvider
{
    private readonly TimeSpan timeout;
    private readonly Func<string?> executableLocator;
    private readonly Func<string, IProviderRpcProcess> startProcess;
    private readonly string clientVersion;
    public string Name => "Codex";

    public CodexProvider(TimeSpan? timeout = null, Func<string?>? executableLocator = null,
        Func<string, IProviderRpcProcess>? startProcess = null, string? clientVersion = null)
    {
        this.timeout = timeout ?? TimeSpan.FromSeconds(20);
        if (this.timeout <= TimeSpan.Zero || this.timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        this.executableLocator = executableLocator ?? CliLocator.FindCodex;
        this.clientVersion = clientVersion ?? "2.1.3";
        if (this.clientVersion.Length > 32 || this.clientVersion.Any(c => !char.IsAsciiDigit(c) && c != '.') ||
            !Version.TryParse(this.clientVersion, out var version) || version.Build < 0)
            throw new ArgumentException("The client version must be a numeric product version.", nameof(clientVersion));
        this.startProcess = startProcess ?? (executable => ProviderProcess.Start(executable,
            ["app-server", "--listen", "stdio://", "-c", "analytics.enabled=false"]));
    }

    public async Task<ProviderResult> QueryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        string? verifiedBinding = null;
        TimeSpan? rateEmbargo = null;
        ProviderResult result;
        try
        {
            var executable = executableLocator();
            if (executable is null) return Complete(ProviderResult.Fail(FailureKind.NotInstalled), null);
            await using var process = startProcess(executable);
            var token = deadline.Token;
            await process.SendAsync(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "llumi", version = clientVersion } } }, token).ConfigureAwait(false);
            await process.ReadRpcResultAsync(1, token).ConfigureAwait(false);
            await process.SendAsync(new { method = "initialized", @params = new { } }, token).ConfigureAwait(false);
            var before = await ReadAccountAsync(process, 2, token).ConfigureAwait(false);
            if (before.Failure != FailureKind.None) return Complete(ProviderResult.Fail(before.Failure), null);
            await process.SendAsync(new { id = 3, method = "account/rateLimits/read", @params = new { excludeResetCreditDetails = true } }, token).ConfigureAwait(false);
            ProviderResult usage;
            try
            {
                var response = await process.ReadRpcResultAsync(3, token).ConfigureAwait(false);
                usage = CodexParser.Parse(response, DateTimeOffset.UtcNow);
            }
            catch (ProviderQueryException error)
            {
                if (error.Failure == FailureKind.RateLimited) rateEmbargo = error.RetryAfter;
                usage = ProviderResult.Fail(error.Failure) with { RetryAfter = error.RetryAfter };
            }
            // Revalidate within this same session, including after a rate-limit RPC error.
            // An account switch must never attach another account's quota to this observation.
            var after = await ReadAccountAsync(process, 4, token).ConfigureAwait(false);
            if (after.Failure != FailureKind.None) return Complete(ProviderResult.Fail(after.Failure) with { RetryAfter = rateEmbargo }, null);
            if (before.Binding != after.Binding)
                return Complete(ProviderResult.Fail(FailureKind.AccountChanged) with { RetryAfter = rateEmbargo }, null);
            verifiedBinding = after.Binding;
            result = usage;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { result = ProviderResult.Fail(FailureKind.Timeout); }
        catch (ProviderQueryException ex) { result = ProviderResult.Fail(ex.Failure) with { RetryAfter = ex.RetryAfter }; }
        catch (JsonException) { result = ProviderResult.Fail(FailureKind.Malformed); }
        catch (UnauthorizedAccessException) { result = ProviderResult.Fail(FailureKind.AccessDenied); }
        catch (Win32Exception ex) { result = ProviderResult.Fail(ex.NativeErrorCode is 2 or 3 ? FailureKind.NotInstalled : FailureKind.AccessDenied); }
        catch (IOException) { result = ProviderResult.Fail(FailureKind.ProcessExited); }
        catch (InvalidOperationException) { result = ProviderResult.Fail(FailureKind.ProcessExited); }
        catch (Exception) { result = ProviderResult.Fail(FailureKind.Unexpected); }
        return Complete(result with { RetryAfter = result.RetryAfter ?? rateEmbargo }, verifiedBinding);
    }

    private ProviderResult Complete(ProviderResult result, string? binding)
    {
        return result with
        {
            VerifiedBinding = binding is null ? null : new AccountBinding(binding),
            Authentication = binding is not null ? AuthenticationStatus.Verified : result.Failure switch
            {
                FailureKind.LoggedOut => AuthenticationStatus.SignedOut,
                FailureKind.NotInstalled => AuthenticationStatus.Missing,
                FailureKind.UnsupportedBilling => AuthenticationStatus.UnsupportedBilling,
                _ => AuthenticationStatus.Unknown
            }
        };
    }

    private static async Task<(string? Binding, FailureKind Failure)> ReadAccountAsync(
        IProviderRpcProcess process, int id, CancellationToken token)
    {
        await process.SendAsync(new { id, method = "account/read", @params = new { refreshToken = false } }, token).ConfigureAwait(false);
        var result = await process.ReadRpcResultAsync(id, token).ConfigureAwait(false);
        if (!UniqueObject(result) || !result.TryGetProperty("account", out var account)) return (null, FailureKind.Malformed);
        if (account.ValueKind == JsonValueKind.Null) return (null, FailureKind.LoggedOut);
        if (!UniqueObject(account) || !Text(account, "type", out var type)) return (null, FailureKind.Malformed);
        if (type == "apiKey") return (null, FailureKind.UnsupportedBilling);
        if (type is not ("chatgpt" or "chatgptAuthTokens")) return (null, FailureKind.Unsupported);
        if (!Text(account, "email", out var email) || !Text(account, "planType", out var plan) ||
            email.Length > 320 || !email.Contains('@') || email.Any(char.IsWhiteSpace) || email.Any(char.IsControl) ||
            plan.Length > 80 || plan.Any(char.IsControl)) return (null, FailureKind.Unsupported);
        // Only an in-memory digest survives parsing. Raw identity is never included in state or logs.
        var binding = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(type + "\n" + email.ToLowerInvariant() + "\n" + plan)));
        return (binding, FailureKind.None);
    }

    private static bool UniqueObject(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count();
    private static bool Text(JsonElement value, string name, out string text)
    {
        text = "";
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        text = property.GetString()!;
        return !string.IsNullOrWhiteSpace(text) && text == text.Trim();
    }
}
