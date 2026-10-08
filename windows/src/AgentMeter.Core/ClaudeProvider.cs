using System.Security.Cryptography;
using System.Text;

namespace AgentMeter.Core;

// V1 uses standalone Claude Code only. The injectable legacy source seam is kept
// for historical regression coverage and is never selected by production startup.
public sealed class ClaudeProvider : IUsageProvider
{
    public const string UnverifiedAccountMessage = ClaudeDesktopSource.UnverifiedAccountMessage;
    private readonly IClaudeUsageSource[] sources;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Action<string> log;
    private readonly TimeSpan sourceTimeout;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<ClaudeClient, Task<ClaudeSourceResult>> pending = new();
    public string Name => "Claude Code";

    public ClaudeProvider(Action<string>? log = null)
        : this([new ClaudeControlTransport()], log: log) { }

    // Retains the existing cache-injection seam for isolated provider regressions.
    public ClaudeProvider(Func<IReadOnlyList<string>> findCaches, Func<DateTimeOffset>? utcNow = null)
        : this([new ClaudeDesktopSource(findCaches, utcNow)], utcNow: utcNow) { }

    public ClaudeProvider(IEnumerable<IClaudeUsageSource> sources, Func<DateTimeOffset>? utcNow = null,
        TimeSpan? sourceTimeout = null, Action<string>? log = null)
    {
        this.sources = sources.ToArray();
        if (this.sources.Select(source => source.Client).Distinct().Count() != this.sources.Length)
            throw new ArgumentException("A Claude client source must be unique.", nameof(sources));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.sourceTimeout = sourceTimeout ?? TimeSpan.FromSeconds(15);
        if (this.sourceTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sourceTimeout));
        this.log = log ?? (_ => { });
    }

    public async Task<ProviderResult> QueryAsync(CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return ProviderResult.Fail(FailureKind.Timeout);
        try
        {
            var results = await Task.WhenAll(sources.Select(source => QuerySourceAsync(source, cancellationToken))).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var resolution = ClaudeSourceResolver.Resolve(results, utcNow());
            if (resolution.Result.Snapshot is not null && resolution.Result.Failure == FailureKind.None)
            {
                log($"claude.selected.{resolution.Client}");
            }
            // Current query proof is explicit. The coordinator owns old-observation continuity.
            return resolution.Result with
            {
                RetryAfter = results.Select(r => r.Usage.RetryAfter).Where(d => d is not null).DefaultIfEmpty(resolution.Result.RetryAfter).Max(),
                RateLimitObserved = results.Any(r => r.Usage.RateLimitObserved || r.Usage.Failure == FailureKind.RateLimited),
                VerifiedBinding = resolution.Binding is { IsComplete: true } binding ? new AccountBinding(Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(binding.AccountId + "\n" + binding.OrganizationId)))) : null,
                Authentication = resolution.Binding is { IsComplete: true } ? AuthenticationStatus.Verified : resolution.Result.Failure switch
                {
                    FailureKind.LoggedOut => AuthenticationStatus.SignedOut,
                    FailureKind.NotInstalled => AuthenticationStatus.Missing,
                    FailureKind.UnsupportedBilling => AuthenticationStatus.UnsupportedBilling,
                    _ => AuthenticationStatus.Unknown
                }
            };
        }
        finally { gate.Release(); }
    }

    private async Task<ClaudeSourceResult> QuerySourceAsync(IClaudeUsageSource source, CancellationToken cancellationToken)
    {
        ClaudeSourceResult result;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(sourceTimeout);
        var token = deadline.Token;
        try
        {
            if (pending.TryGetValue(source.Client, out var earlier) && !earlier.IsCompleted)
                result = Unknown(source.Client, FailureKind.Timeout);
            else
            {
                var operation = Task.Run(() => source.QueryAsync(token), token);
                pending[source.Client] = operation;
                _ = operation.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                result = await operation.WaitAsync(token).ConfigureAwait(false);
                if (result.Client != source.Client) result = Unknown(source.Client, FailureKind.Malformed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { result = Unknown(source.Client, FailureKind.Timeout); }
        catch (UnauthorizedAccessException) { result = Unknown(source.Client, FailureKind.AccessDenied); }
        catch (Exception) { result = Unknown(source.Client, FailureKind.Unexpected); }
        log($"claude.{source.Client}.{result.Authentication}.{result.Usage.Failure}");
        return result;
    }

    private static ClaudeSourceResult Unknown(ClaudeClient client, FailureKind failure)
        => new(client, ClaudeAuthentication.Unknown, null, ProviderResult.Fail(failure));

    public static IReadOnlyList<string> FindUsageCaches() => ClaudeDesktopSource.FindUsageCaches();
}
