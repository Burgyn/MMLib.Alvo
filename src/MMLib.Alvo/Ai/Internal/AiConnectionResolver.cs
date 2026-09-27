using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Secrets;

using System.Security.Cryptography;
using System.Text.Json;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>
/// The one <see cref="IAiConnectionResolver"/>: configuration first, then the record somebody saved.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same precedence the secret store itself uses</b>, so there is one rule to learn rather than two: a
/// value the deployment pinned beats a value a screen wrote. The dashboard reports which one answered, which
/// is what turns "why is it still using the old model" into one glance.
/// </para>
/// <para>
/// <b>Resolved on every call, never cached.</b> Both layers change without a restart, and a cache would make
/// an operator's save look lost until the next deploy.
/// </para>
/// <para>
/// <b>An unreadable stored record is no connection, not a failure.</b> The screen that asks this question is
/// drawing a status; a throw would make a bad row take down the settings page that exists to fix it. It is
/// logged at warning — <b>without the payload</b>, because the payload is the credential.
/// </para>
/// </remarks>
/// <param name="options">The deployment's own configured connection, if it pinned one.</param>
/// <param name="secrets">Where the stored record and any referenced key are read from.</param>
/// <param name="logger">Where an unreadable stored record is reported.</param>
internal sealed partial class AiConnectionResolver(
    IOptionsMonitor<AlvoAiOptions> options,
    ISecretStore secrets,
    ILogger<AiConnectionResolver> logger) : IAiConnectionResolver
{
    /// <summary>The name the saved connection is held under.</summary>
    internal static SecretName StoredName { get; } = SecretName.Parse(StoredAiConnection.SecretName);

    /// <inheritdoc/>
    public async ValueTask<AiConnectionResolution> ResolveAsync(CancellationToken ct = default)
    {
        if (FromConfiguration() is { } configured)
        {
            return await WithKeyAsync(configured, ct).ConfigureAwait(false);
        }

        var stored = await FromStoreAsync(ct).ConfigureAwait(false);

        return stored is null
            ? new AiConnectionResolution(null, AiConnectionSource.None, AiKeyState.None)
            : new AiConnectionResolution(stored, AiConnectionSource.Store, KeyStateOf(stored));
    }

    /// <summary>
    /// Whether <paramref name="connection"/> carries the key its endpoint needs, judged from the connection alone.
    /// </summary>
    /// <remarks>
    /// A connection with no key is <see cref="AiKeyState.Missing"/> only on a host this build knows always refuses
    /// an unauthenticated call; anywhere else it is keyless on purpose until something says otherwise. What says
    /// otherwise is a reference that went unanswered, which only <see cref="WithKeyAsync"/> knows.
    /// </remarks>
    private static AiKeyState KeyStateOf(AlvoAiConnection connection)
    {
        if (connection.ApiKey is not null)
        {
            return AiKeyState.Present;
        }

        return AlwaysNeedsAKey(connection.Endpoint.Host) ? AiKeyState.Missing : AiKeyState.NotNeeded;
    }

    /// <summary>The connection this deployment pinned, or <see langword="null"/> when it pinned none.</summary>
    /// <remarks>
    /// A partially filled section is <see langword="null"/> rather than a refusal: half a connection cannot
    /// be dialled, and the screen's job is to say "not configured" and show the operator what is missing.
    /// </remarks>
    private AlvoAiConnection? FromConfiguration()
    {
        var configured = options.CurrentValue;

        return Build(configured.Kind, configured.Endpoint, configured.Model, apiKey: null);
    }

    /// <summary>
    /// The configured connection with the key its configuration referenced, resolved through the store, and whether
    /// that key is there.
    /// </summary>
    /// <remarks>
    /// <b>A reference that resolves nothing is a missing key, whatever the endpoint</b> — including a local one that
    /// would otherwise read as keyless: the operator who named a secret has said a key is needed. A reference that is
    /// not a secret name at all resolves nothing either, so it is the same state rather than a silent "no key".
    /// </remarks>
    private async ValueTask<AiConnectionResolution> WithKeyAsync(AlvoAiConnection connection, CancellationToken ct)
    {
        var named = options.CurrentValue.ApiKeySecretRef;
        if (string.IsNullOrWhiteSpace(named))
        {
            WarnIfEndpointAlwaysNeedsAKey(connection);
            return new AiConnectionResolution(connection, AiConnectionSource.Configuration, KeyStateOf(connection));
        }

        var key = SecretName.TryParse(named, out var reference)
            ? await ReferencedKeyAsync(reference!, ct).ConfigureAwait(false)
            : Unresolvable(named);
        key = string.IsNullOrWhiteSpace(key) ? null : key;

        return new AiConnectionResolution(
            connection with { ApiKey = key },
            AiConnectionSource.Configuration,
            key is null ? AiKeyState.Missing : AiKeyState.Present);
    }

    /// <summary>A reference that is not a secret name: reported by its length only, and no key.</summary>
    /// <remarks>
    /// <b>Never its value.</b> The likeliest way to write something here that is not a secret name is to paste the
    /// key itself into a setting called <c>ApiKey…</c> — and a key never matches the name pattern, so logging the text
    /// would put the credential in the host log on every request that resolves the connection.
    /// </remarks>
    private string? Unresolvable(string named)
    {
        ReferenceIsNotASecretName(logger, named.Length);
        return null;
    }

    /// <summary>
    /// The key <see cref="AlvoAiOptions.ApiKeySecretRef"/> names, or <see langword="null"/> when this build
    /// has nothing under that name.
    /// </summary>
    /// <remarks>
    /// <b>Logged by name either way this comes back empty</b> — the live defect this closes (reported 24 Sep
    /// 2026): <c>ApiKeySecretRef</c> named a secret nobody had written, Settings still reported the connection
    /// as configured, and the OpenAI client silently sent its own placeholder credential instead. A missing
    /// name and an unreadable row are told apart, because they call for different fixes — save the secret
    /// under that name, versus save the connection again — and only the second was logged before this.
    /// </remarks>
    private async ValueTask<string?> ReferencedKeyAsync(SecretName reference, CancellationToken ct)
    {
        string? value;
        try
        {
            value = await secrets.GetAsync(reference, ct).ConfigureAwait(false);
        }
        catch (CryptographicException)
        {
            StoredConnectionUnreadable(logger, reference.Value);
            return null;
        }

        if (value is null)
        {
            ReferencedSecretMissing(logger, reference.Value);
        }

        return value;
    }

    /// <summary>
    /// Warns when this connection dials an endpoint that never accepts an unauthenticated call, and
    /// <see cref="AlvoAiOptions.ApiKeySecretRef"/> names none at all.
    /// </summary>
    /// <remarks>
    /// Named hosts only, not "every endpoint": a local Ollama or vLLM is routinely run without a key on
    /// purpose, and warning on every keyless connection would bury the one case that is never intentional —
    /// OpenAI and Azure OpenAI both refuse an unauthenticated request outright.
    /// </remarks>
    private void WarnIfEndpointAlwaysNeedsAKey(AlvoAiConnection connection)
    {
        if (AlwaysNeedsAKey(connection.Endpoint.Host))
        {
            EndpointAlwaysNeedsAKey(logger, connection.Endpoint.Host);
        }
    }

    /// <summary>Whether <paramref name="host"/> is one this build knows always refuses an unauthenticated call.</summary>
    /// <remarks>
    /// <b>The known key-only hosts, not every host that needs a key.</b> OpenAI itself, and the three Azure host
    /// families this build dials with a key and nothing else (the adapter has no Entra path): Azure OpenAI, the
    /// Cognitive Services endpoint and Azure AI Foundry's. A host not listed with no key reads as
    /// <see cref="AiKeyState.NotNeeded"/> — the lenient side, since a local model or a proxy is routinely keyless.
    /// </remarks>
    private static bool AlwaysNeedsAKey(string host) =>
        string.Equals(host, "api.openai.com", StringComparison.OrdinalIgnoreCase)
        || _keyOnlyHostSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The Azure host families <see cref="AlwaysNeedsAKey"/> matches by suffix.</summary>
    private static readonly string[] _keyOnlyHostSuffixes =
        [".openai.azure.com", ".cognitiveservices.azure.com", ".services.ai.azure.com"];

    /// <summary>The saved connection, or <see langword="null"/> when there is none this build can read.</summary>
    private async ValueTask<AlvoAiConnection?> FromStoreAsync(CancellationToken ct)
    {
        var stored = await ReadAsync(StoredName, ct).ConfigureAwait(false);

        return stored is { Length: > 0 } ? Parse(stored) : null;
    }

    /// <summary>
    /// One secret, or <see langword="null"/> when this build cannot read it.
    /// </summary>
    /// <remarks>
    /// <b>A row the cipher refuses is no connection, not a failed page.</b> The store authenticates as it
    /// decrypts, so a tampered value — or one written under a key that has since been rotated away —
    /// arrives here as a <see cref="CryptographicException"/>. The screen asking this question is drawing a
    /// status, and letting the throw through would replace the settings page that exists to fix the row
    /// with a circuit error. Logged at warning by <em>name</em>; the value is exactly what must not reach a
    /// log.
    /// </remarks>
    private async ValueTask<string?> ReadAsync(SecretName name, CancellationToken ct)
    {
        try
        {
            return await secrets.GetAsync(name, ct).ConfigureAwait(false);
        }
        catch (CryptographicException)
        {
            StoredConnectionUnreadable(logger, name.Value);

            return null;
        }
    }

    /// <summary>The stored JSON as a connection, or <see langword="null"/> when it is not one.</summary>
    private AlvoAiConnection? Parse(string stored)
    {
        StoredAiConnection? record;
        try
        {
            record = JsonSerializer.Deserialize(stored, StoredAiConnectionJsonContext.Default.StoredAiConnection);
        }
        catch (JsonException)
        {
            StoredConnectionUnreadable(logger, StoredName.Value);
            return null;
        }

        return record is null ? null : Build(record.Kind, record.Endpoint, record.Model, record.ApiKey);
    }

    /// <summary>
    /// A connection from four loose values, or <see langword="null"/> when they do not make one.
    /// </summary>
    /// <remarks>
    /// One builder for both layers, so a configured connection and a saved one are validated identically —
    /// two builders is how the store comes to accept an endpoint configuration refuses.
    /// </remarks>
    private static AlvoAiConnection? Build(string? kind, string? endpoint, string? model, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(model) || !KindOf(kind, out var parsed))
        {
            return null;
        }

        return Uri.TryCreate(endpoint, UriKind.Absolute, out var address)
            ? new AlvoAiConnection(parsed, address, model, string.IsNullOrWhiteSpace(apiKey) ? null : apiKey)
            : null;
    }

    /// <summary>
    /// The enum the wire spelling denotes.
    /// </summary>
    /// <remarks>
    /// The hyphenated spellings are what an operator types, and they are matched exactly rather than through
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> — which would also accept <c>1</c>, and an
    /// out-of-range number would bind to a kind no adapter can dial.
    /// </remarks>
    private static bool KindOf(string? kind, out AiConnectionKind parsed)
    {
        parsed = AiConnectionKind.OpenAiCompatible;

        switch (kind)
        {
            case StoredAiConnection.OpenAiCompatibleKind:
                return true;
            case StoredAiConnection.AzureOpenAiKind:
                parsed = AiConnectionKind.AzureOpenAi;
                return true;
            default:
                return false;
        }
    }

    [LoggerMessage(
        EventId = 6101,
        Level = LogLevel.Warning,
        Message = "The secret '{SecretName}' could not be read, so this instance reports no AI connection. "
            + "Either it was written under a key this deployment no longer mounts, or the stored value was "
            + "changed. Save the connection again from the dashboard.")]
    private static partial void StoredConnectionUnreadable(ILogger logger, string secretName);

    [LoggerMessage(
        EventId = 6102,
        Level = LogLevel.Warning,
        Message = "Alvo:Ai:ApiKeySecretRef names secret '{SecretName}', which this instance does not have; "
            + "the connection is used without a key.")]
    private static partial void ReferencedSecretMissing(ILogger logger, string secretName);

    [LoggerMessage(
        EventId = 6103,
        Level = LogLevel.Warning,
        Message = "This connection dials '{Host}', which always needs a key, and Alvo:Ai:ApiKeySecretRef names "
            + "none — every call to it is sent without one.")]
    private static partial void EndpointAlwaysNeedsAKey(ILogger logger, string host);

    [LoggerMessage(
        EventId = 6104,
        Level = LogLevel.Warning,
        Message = "Alvo:Ai:ApiKeySecretRef is not a secret name — it must name a secret, never hold the key itself; "
            + "the configured value ({Length} characters) is not logged. Set it to a name matching "
            + "^[a-z][a-z0-9._-]{{0,63}}$ and save the key under that name. The connection is used without a key.")]
    private static partial void ReferenceIsNotASecretName(ILogger logger, int length);
}
