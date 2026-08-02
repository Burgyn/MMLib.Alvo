using Microsoft.Extensions.Options;

namespace MMLib.Alvo.Auth.Internal;

/// <summary>
/// Fail-fast startup check (spec §0 principle 5, secure-by-default) for
/// <see cref="AlvoAuthOptions.DevKeys"/>. A misconfigured dev key — an unparseable scope, a
/// missing <c>KeyId</c>/<c>Secret</c>, a duplicate <c>KeyId</c>, or a <c>KeyId</c> containing the
/// <c>.</c> separator — is otherwise dropped silently by <see cref="InMemoryApiKeyStore"/> (or
/// throws lazily from a <c>Dictionary</c> key collision on first use), leaving an operator staring
/// at an indistinguishable 401 with no clue which key or value is wrong.
/// </summary>
internal sealed class AlvoAuthOptionsValidator : IValidateOptions<AlvoAuthOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, AlvoAuthOptions options)
    {
        var failures = new List<string>();
        var seenKeyIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var key in options.DevKeys)
        {
            ValidateKey(key, seenKeyIds, failures);
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateKey(AlvoDevApiKey key, HashSet<string> seenKeyIds, List<string> failures)
    {
        ValidateKeyId(key.KeyId, seenKeyIds, failures);

        if (string.IsNullOrEmpty(key.Secret))
        {
            failures.Add($"Dev API key '{key.KeyId}' has an empty Secret.");
        }

        ValidateScopes(key, failures);
    }

    private static void ValidateKeyId(string keyId, HashSet<string> seenKeyIds, List<string> failures)
    {
        if (string.IsNullOrEmpty(keyId))
        {
            failures.Add("A dev API key has an empty KeyId.");
        }
        else if (keyId.Contains('.', StringComparison.Ordinal))
        {
            failures.Add(
                $"Dev API key '{keyId}' has a KeyId containing '.', which is unreachable: a "
                + "presented key is split into keyId/secret on the first '.'.");
        }
        else if (!seenKeyIds.Add(keyId))
        {
            failures.Add($"Duplicate dev API key KeyId '{keyId}'.");
        }
    }

    private static void ValidateScopes(AlvoDevApiKey key, List<string> failures)
    {
        foreach (var scopeText in key.Scopes)
        {
            if (!ApiKeyScope.TryParse(scopeText, out _))
            {
                failures.Add(
                    $"Dev API key '{key.KeyId}' has an unparseable scope '{scopeText}'; "
                    + "expected '<entity|*>:<read|write>'.");
            }
        }
    }
}
