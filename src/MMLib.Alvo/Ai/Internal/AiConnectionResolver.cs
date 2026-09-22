using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Secrets;

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
    public async ValueTask<AlvoAiConnection?> ResolveAsync(CancellationToken ct = default) =>
        FromConfiguration() is { } configured
            ? await WithKeyAsync(configured, ct).ConfigureAwait(false)
            : await FromStoreAsync(ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public async ValueTask<AiConnectionSource> DescribeSourceAsync(CancellationToken ct = default)
    {
        if (FromConfiguration() is not null)
        {
            return AiConnectionSource.Configuration;
        }

        return await FromStoreAsync(ct).ConfigureAwait(false) is null
            ? AiConnectionSource.None
            : AiConnectionSource.Store;
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

        return connection with { ApiKey = await secrets.GetAsync(reference!, ct).ConfigureAwait(false) };
    }

    /// <summary>The saved connection, or <see langword="null"/> when there is none this build can read.</summary>
    private async ValueTask<AlvoAiConnection?> FromStoreAsync(CancellationToken ct)
    {
        var stored = await secrets.GetAsync(StoredName, ct).ConfigureAwait(false);

        return stored is { Length: > 0 } ? Parse(stored) : null;
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
        Message = "The stored AI connection under '{SecretName}' could not be read, so this instance reports "
            + "no AI connection. Save it again from the dashboard.")]
    private static partial void StoredConnectionUnreadable(ILogger logger, string secretName);
}
