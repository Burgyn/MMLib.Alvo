using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using System.Buffers;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// The single owner of the <see cref="AlvoManagementOptions.SectionName"/> configuration section: it binds
/// the prefix and refuses, at startup, every value that cannot become a route pattern — or that would sit
/// inside the Data API's own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every refusal is reported at once</b>, not the first one found: an operator repairing a prefix that is
/// wrong in three ways should not have to restart three times to learn that.
/// </para>
/// <para>
/// <see cref="IConfiguration"/> is resolved <b>optionally</b>, on
/// <c>AlvoSchemaOptionsConfiguration</c>'s precedent: a plain console host embedding Alvo need not have
/// registered configuration at all, and its absence means "no section", not a failure.
/// </para>
/// </remarks>
/// <param name="configuration">The ambient configuration, or <see langword="null"/> when the host registered none.</param>
/// <param name="api">The Data API's own options, read only to keep the two surfaces off one prefix.</param>
internal sealed class AlvoManagementOptionsConfiguration(IConfiguration? configuration, IOptions<AlvoApiOptions> api)
    : IConfigureOptions<AlvoManagementOptions>, IValidateOptions<AlvoManagementOptions>
{
    /// <summary>The configuration key every refusal about the prefix quotes verbatim.</summary>
    internal const string RoutePrefixKey =
        $"{AlvoManagementOptions.SectionName}:{nameof(AlvoManagementOptions.RoutePrefix)}";

    /// <summary>Characters a route prefix may not contain, because a route pattern gives each of them a meaning.</summary>
    private const string ReservedInRoutePattern = "{}*?#:";

    private static readonly SearchValues<char> _reserved = SearchValues.Create(ReservedInRoutePattern);

    /// <inheritdoc/>
    public void Configure(AlvoManagementOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        configuration?.GetSection(AlvoManagementOptions.SectionName).Bind(options);
    }

    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, AlvoManagementOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> refusals = [.. Refusals(options)];

        return refusals.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(refusals);
    }

    /// <summary>Everything wrong with the configured prefix, in one pass.</summary>
    /// <param name="options">The bound options.</param>
    private IEnumerable<string> Refusals(AlvoManagementOptions options)
    {
        var configured = options.RoutePrefix ?? string.Empty;
        var normalized = RoutePrefix.Normalize(configured);
        if (normalized.Length == 0)
        {
            yield return $"{RoutePrefixKey} reduces to nothing. The management surface owns literal segments "
                + "that would shadow an entity route at the root; set it to a path such as \"/management\".";
            yield break;
        }

        foreach (var refusal in ShapeRefusals(configured, normalized[1..]))
        {
            yield return refusal;
        }

        if (SitsUnderTheDataApi(normalized) is { } data)
        {
            yield return $"{RoutePrefixKey} '{configured}' would sit under the Data API's own prefix "
                + $"'{data}'. Mount the two surfaces apart, or move the Data API.";
        }
    }

    /// <summary>The ways a prefix can fail to be literal path text.</summary>
    /// <param name="configured">The prefix as the host wrote it, for the message.</param>
    /// <param name="trimmed">The normalized prefix without its leading slash.</param>
    private static IEnumerable<string> ShapeRefusals(string configured, string trimmed)
    {
        if (trimmed.Split('/').Any(string.IsNullOrWhiteSpace))
        {
            yield return $"{RoutePrefixKey} '{configured}' has an empty path segment, which is not a legal "
                + "route pattern. Use a single slash between segments, e.g. \"/management/v1\".";
        }

        if (trimmed.AsSpan().ContainsAny(_reserved))
        {
            yield return $"{RoutePrefixKey} '{configured}' contains a character a route pattern reserves (one "
                + $"of {ReservedInRoutePattern}). A prefix is literal path text.";
        }
    }

    /// <summary>
    /// The Data API's prefix when <paramref name="normalized"/> is it or sits inside it, otherwise
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <b>A Data API prefix that cannot itself mount is not reported here.</b> Reading
    /// <see cref="IOptions{TOptions}.Value"/> runs that type's own validator, and letting its refusal
    /// propagate out of <em>this</em> one would name the wrong option and the wrong fix in an exception
    /// whose type says it is about <see cref="AlvoManagementOptions"/>. <c>AlvoApiOptionsValidator</c> fails
    /// the same start, from its own options, with the message an operator can act on.
    /// </remarks>
    /// <param name="normalized">The normalized management prefix.</param>
    private string? SitsUnderTheDataApi(string normalized)
    {
        var data = DataApiPrefix();

        return data is { Length: > 0 }
            && (normalized.Equals(data, StringComparison.Ordinal)
                || normalized.StartsWith(data + "/", StringComparison.Ordinal))
            ? data
            : null;
    }

    /// <summary>The Data API's normalized prefix, or <see langword="null"/> when it does not mount at all.</summary>
    private string? DataApiPrefix()
    {
        try
        {
            return RoutePrefix.Normalize(api.Value.RoutePrefix ?? string.Empty);
        }
        catch (OptionsValidationException)
        {
            return null;
        }
    }
}
