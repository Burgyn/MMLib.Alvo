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
            return new AiConnectionResolution(
                await WithKeyAsync(configured, ct).ConfigureAwait(false), AiConnectionSource.Configuration);
        }

        var stored = await FromStoreAsync(ct).ConfigureAwait(false);

        return new AiConnectionResolution(
            stored, stored is null ? AiConnectionSource.None : AiConnectionSource.Store);
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

    /// <summary>The same connection with the key its configuration referenced, resolved through the store.</summary>
    private async ValueTask<AlvoAiConnection?> WithKeyAsync(AlvoAiConnection connection, CancellationToken ct)
    {
        if (!SecretName.TryParse(options.CurrentValue.ApiKeySecretRef, out var reference))
        {
            return connection;
        }

        return connection with { ApiKey = await ReadAsync(reference!, ct).ConfigureAwait(false) };
    }

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
            ? new AlvoAiConnection(parsed, address, model, string.IsNullOrEmpty(apiKey) ? null : apiKey)
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
}
