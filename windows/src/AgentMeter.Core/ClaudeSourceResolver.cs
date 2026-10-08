using System.Text.Json.Serialization;

namespace AgentMeter.Core;

public enum ClaudeClient { Desktop, Code }
public enum ClaudeAuthentication { Missing, SignedOut, Authenticated, Unknown }

// Sources create bindings only after verifying current authentication. Account and
// organization identifiers belong to different namespaces and must never substitute.
public sealed record ClaudeAccountBinding([property: JsonIgnore] string? AccountId, [property: JsonIgnore] string? OrganizationId = null)
{
    public bool IsComplete => IsIdentifier(AccountId) &&
        (OrganizationId is null || IsIdentifier(OrganizationId));

    private static bool IsIdentifier(string? value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim();

    // Keep accidental diagnostic interpolation from disclosing account identifiers.
    public override string ToString() => "Claude account binding (identifiers omitted)";
}

public sealed record ClaudeSourceResult(ClaudeClient Client, ClaudeAuthentication Authentication,
    [property: JsonIgnore] ClaudeAccountBinding? Binding, ProviderResult Usage);

public interface IClaudeUsageSource
{
    ClaudeClient Client { get; }
    Task<ClaudeSourceResult> QueryAsync(CancellationToken cancellationToken);
}

// Binding remains available for a failed usage query when current identity is known.
// The provider can then invalidate an older snapshot after an account switch.
public sealed record ClaudeResolution(ProviderResult Result, [property: JsonIgnore] ClaudeAccountBinding? Binding = null,
    ClaudeClient? Client = null);

public static class ClaudeSourceResolver
{
    public const string AmbiguousIdentityMessage = "Claude account identity could not be verified.";
    public const string MultipleAccountsMessage = "Multiple Claude accounts or allowance scopes are active; usage is unavailable.";

    public static ClaudeResolution Resolve(IEnumerable<ClaudeSourceResult> sources, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var results = sources.ToArray();
        if (results.Length == 0) return new(ProviderResult.Fail(FailureKind.NotInstalled));
        if (results.Any(source => !Enum.IsDefined(source.Client) || !Enum.IsDefined(source.Authentication)) ||
            results.Select(source => source.Client).Distinct().Count() != results.Length)
            return Ambiguous();

        // Each client establishes its own eligibility. Unverified Desktop history
        // cannot supply numbers or veto an independently verified CLI account.
        var authenticated = results.Where(source => source.Authentication == ClaudeAuthentication.Authenticated &&
            source.Binding?.IsComplete == true).ToArray();
        if (authenticated.Length == 0)
        {
            if (results.Any(source => source.Authentication is ClaudeAuthentication.Authenticated or ClaudeAuthentication.Unknown ||
                source.Binding is not null || source.Usage.Snapshot is not null))
                return SelectFailure(results.Select(source => source with { Usage = UnverifiedFailure(source) }), null);
            return new(ProviderResult.Fail(results.Any(source => source.Authentication == ClaudeAuthentication.SignedOut)
                ? FailureKind.LoggedOut : FailureKind.NotInstalled));
        }

        var binding = authenticated[0].Binding!;
        foreach (var source in authenticated.Skip(1))
        {
            var other = source.Binding!;
            if (binding.AccountId != other.AccountId ||
                binding.OrganizationId is not null && other.OrganizationId is not null &&
                    binding.OrganizationId != other.OrganizationId)
                return new(ProviderResult.Fail(FailureKind.Unsupported, MultipleAccountsMessage));
            if (binding.OrganizationId != other.OrganizationId)
                return Ambiguous();
        }

        var evaluated = authenticated.Select(source => source with { Usage = ValidateUsage(source.Usage, now) }).ToArray();
        var selected = evaluated.Where(source => source.Usage.Failure == FailureKind.None)
            .OrderByDescending(source => source.Usage.Snapshot!.ObservedAt)
            .ThenBy(source => source.Usage.Snapshot!.IsCached)
            .ThenByDescending(source => source.Usage.Snapshot!.Windows.Any(window => window.UsedPercent is not null))
            .ThenBy(source => source.Client)
            .FirstOrDefault();
        if (selected is not null)
            return new(selected.Usage, binding, selected.Client);

        return SelectFailure(evaluated, binding);
    }

    private static ProviderResult ValidateUsage(ProviderResult result, DateTimeOffset now)
    {
        if (result.Failure != FailureKind.None)
            return result with { Snapshot = null };
        if (result.Snapshot is not { } snapshot || snapshot.ObservedAt > now.AddMinutes(1))
            return ProviderResult.Fail(FailureKind.Malformed);
        return result;
    }

    private static ProviderResult UnverifiedFailure(ClaudeSourceResult source)
    {
        if (source.Usage.Failure == FailureKind.None ||
            source.Authentication is ClaudeAuthentication.Missing or ClaudeAuthentication.SignedOut &&
                (source.Binding is not null || source.Usage.Snapshot is not null))
            return ProviderResult.Fail(FailureKind.Unsupported, AmbiguousIdentityMessage);
        // With no verified candidate, preserve useful diagnostics but no numbers or
        // identity. The provider then invalidates any previously selected account.
        return source.Usage with { Snapshot = null };
    }

    private static ClaudeResolution Ambiguous() => new(ProviderResult.Fail(FailureKind.Unsupported, AmbiguousIdentityMessage));

    private static ClaudeResolution SelectFailure(IEnumerable<ClaudeSourceResult> sources, ClaudeAccountBinding? binding)
    {
        // Source details are already safe diagnostics under the provider contract.
        // Prefer concrete failures to unavailable secondary interfaces, deterministically.
        var source = sources.OrderBy(source => FailurePriority(source.Usage.Failure))
            .ThenBy(source => source.Client).First();
        return new(source.Usage, binding, source.Client);
    }

    private static int FailurePriority(FailureKind failure) => failure switch
    {
        FailureKind.Malformed => 0,
        FailureKind.AccountChanged => 0,
        FailureKind.RateLimited => 1,
        FailureKind.AccessDenied => 1,
        FailureKind.Timeout => 2,
        FailureKind.Network => 3,
        FailureKind.ProcessExited => 4,
        FailureKind.Unexpected => 5,
        FailureKind.Unsupported => 6,
        FailureKind.UnsupportedBilling => 6,
        FailureKind.LoggedOut => 7,
        FailureKind.NotInstalled => 8,
        _ => 9
    };
}
