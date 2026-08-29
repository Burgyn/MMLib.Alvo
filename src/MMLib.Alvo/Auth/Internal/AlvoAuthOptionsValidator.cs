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

        ValidateSecret(key, failures);

        ValidateScopes(key, failures);
    }

    /// <summary>
    /// The length floor under a dev key's secret — the only thing standing between
    /// <see cref="ApiKeyHash"/>'s single unsalted SHA-256 pass and a rainbow table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One SHA-256 pass is correct for a high-entropy random key, and nothing made the key
    /// high-entropy.</b> The comparison is constant-time, the unknown-key path runs a dummy comparison, and
    /// <c>DevKeys</c> defaults to empty — so there is no default credential and no enumeration oracle. What
    /// was missing is the assumption the hash rests on: <c>Secret = "password"</c> was accepted, and its
    /// digest is one lookup away the moment it reaches a store, a log or a support bundle (#125).
    /// </para>
    /// <para>
    /// <b>32 characters, because that is what this repository's own recipe produces.</b>
    /// <c>openssl rand -hex 16</c> — the line <c>scripts/test-e2e</c>, <c>playground/run</c> and the
    /// examples' READMEs all publish — is 128 bits written as exactly 32 hex characters, so the floor is set
    /// <em>at</em> the recipe rather than above it: every secret the docs tell an operator to generate
    /// passes, and no hand-typed word does. It is deliberately not higher, because a floor that refuses the
    /// project's own documented recipe is a floor nobody can satisfy by following the instructions.
    /// </para>
    /// <para>
    /// <b>What the number cannot do.</b> Length is a
    /// <em>proxy</em> for entropy and not a measure of it: a 32-character secret that is guessable is still
    /// guessable, and nothing here can tell. That is not a hole to plug at this line — it is why the
    /// mechanism is documented as dev-only, and why <b>#36</b>, the real issuance path, must not inherit
    /// this hash: a user-chosen secret needs a password KDF with a per-key salt, not one SHA-256 pass with
    /// a length check in front of it.
    /// </para>
    /// </remarks>
    internal const int MinimumSecretLength = 32;

    /// <summary>Reports an empty or too-short dev-key secret.</summary>
    /// <remarks>
    /// The empty case is reported separately from the short case on purpose: an empty <c>Secret</c> is
    /// almost always an unset environment variable, and telling that operator to lengthen a secret they
    /// never set sends them to the wrong file.
    /// </remarks>
    /// <param name="key">The dev key being validated.</param>
    /// <param name="failures">The failure list this pass appends to.</param>
    private static void ValidateSecret(AlvoDevApiKey key, List<string> failures)
    {
        if (string.IsNullOrEmpty(key.Secret))
        {
            failures.Add($"Dev API key '{key.KeyId}' has an empty Secret.");
        }
        else if (key.Secret.Length < MinimumSecretLength)
        {
            failures.Add(
                $"Dev API key '{key.KeyId}' has a Secret of {key.Secret.Length} characters; at least "
                + $"{MinimumSecretLength} are required. The secret is hashed with a single SHA-256 pass, "
                + "which is only as strong as the assumption that the secret is random — generate one "
                + "('openssl rand -hex 16' produces exactly this length) rather than choosing it.");
        }
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
