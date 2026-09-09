using Microsoft.Extensions.Options;

namespace MMLib.Alvo.Auth.Internal;

/// <summary>
/// Fail-fast startup check (spec §0 principle 5, secure-by-default) for
/// <see cref="AlvoAuthOptions.DevKeys"/> and <see cref="AlvoAuthOptions.HeaderName"/>. A misconfigured
/// dev key — an unparseable scope, a missing <c>KeyId</c>/<c>Secret</c>, a duplicate <c>KeyId</c>, or a
/// <c>KeyId</c> containing the <c>.</c> separator — is otherwise dropped silently by
/// <see cref="InMemoryApiKeyStore"/> (or throws lazily from a <c>Dictionary</c> key collision on first
/// use), leaving an operator staring at an indistinguishable 401 with no clue which key or value is wrong.
/// </summary>
internal sealed class AlvoAuthOptionsValidator : IValidateOptions<AlvoAuthOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, AlvoAuthOptions options)
    {
        var failures = new List<string>();
        var seenKeyIds = new HashSet<string>(StringComparer.Ordinal);

        ValidateHeaderName(nameof(AlvoAuthOptions.HeaderName), options.HeaderName, failures);
        ValidateHeaderName(nameof(AlvoAuthOptions.TenantHeaderName), options.TenantHeaderName, failures);

        foreach (var key in options.DevKeys)
        {
            ValidateKey(key, seenKeyIds, failures);
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>Headers a browser attaches by itself, which a credential must never be read from.</summary>
    /// <remarks>
    /// <para>
    /// <c>Cookie</c> is the whole list, and one entry is enough: it is the request header a browser attaches
    /// to a cross-origin request <em>without the page asking</em>, for every visitor who has one. Every other
    /// header a forged request could carry has to be set by script, which is what a preflight then governs.
    /// </para>
    /// <para>
    /// <b><c>Authorization</c> is deliberately not on the list, and the exception is stated so a later reader
    /// does not re-derive it and doubt the list.</b> A browser does re-send it by itself, but only to an
    /// origin where the user has already completed an HTTP authentication challenge — and it is not
    /// CORS-safelisted, so script cannot set it cross-origin without a preflight. It is also a legitimate
    /// place for an API key, which <c>Cookie</c> never is.
    /// </para>
    /// </remarks>
    private static readonly string[] _browserAttachedHeaders = ["Cookie"];

    /// <summary>
    /// Refuses a credential header a browser attaches by itself — reading a credential from
    /// <c>Cookie</c> turns every Alvo route into a cross-site-request-forgery target.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Configuration-reachable, which is why it is checked here rather than trusted to a code review.</b>
    /// <see cref="AlvoAuthOptions.HeaderName"/> is bound from configuration by any host that binds the
    /// <c>Alvo:Auth</c> section — the standalone host does, and so does the embedded sample — so
    /// <c>Alvo__Auth__HeaderName=Cookie</c> is an environment variable away, with no code change and no
    /// review to catch it. The combination it enables is exactly #191's threat model: a browser-authenticated
    /// caller reaching a body-taking route, at which point the JSON <c>Content-Type</c> requirement
    /// (<c>Api.Internal.JsonContentType</c>) is the only thing left standing between a cross-site form and a
    /// write.
    /// </para>
    /// <para>
    /// <b>Refused rather than warned about, and refused at startup.</b> A warning in a log an operator is
    /// not reading is not a control, and the misconfiguration is not one a request can reveal: every request
    /// afterwards looks like it worked. §0 principle 5 says the insecure combination must be unreachable, not
    /// merely discouraged — and the message names the fix, because a refusal an operator cannot act on is
    /// its own defect (§0 principle 4).
    /// </para>
    /// <para>
    /// <b>It does not close the general problem, and does not pretend to.</b> A host that maps its own
    /// session cookie onto a header in its own middleware reaches the same place, deliberately and visibly.
    /// What this refuses is the version nobody decided: a one-line environment override.
    /// </para>
    /// </remarks>
    /// <param name="option">The option's name, so the message names the key an operator has to change.</param>
    /// <param name="headerName">The configured header.</param>
    /// <param name="failures">The failure list to add to.</param>
    private static void ValidateHeaderName(string option, string headerName, List<string> failures)
    {
        // Trimmed before the comparison, and the reason is the message rather than the security: a
        // configured "Cookie " reads no header at all, so the host is broken rather than exposed — and an
        // operator facing a blanket 403 with no explanation is exactly who this validator exists for.
        if (!_browserAttachedHeaders.Contains(headerName.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        failures.Add(
            $"Alvo:Auth:{option} is '{headerName}', a header the browser attaches to cross-origin "
            + "requests by itself — reading a credential from it makes every Alvo route a "
            + "cross-site-request-forgery target. Read it from a header only script can set "
            + "(the default is 'X-Alvo-Api-Key'). To let your host's own users reach Alvo, resolve them in "
            + "your own endpoints and pass the AlvoContext to IAlvoData instead; see "
            + "samples/MMLib.Alvo.Samples.EmbeddedHost.");
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
