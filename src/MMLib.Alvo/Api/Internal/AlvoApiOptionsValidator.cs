using Microsoft.Extensions.Options;
using System.Buffers;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Fail-fast startup check for <see cref="AlvoApiOptions"/>. Every failure names the option and the
/// fix, because a misconfiguration that surfaces at the first request surfaces as the wrong diagnosis:
/// <c>RoutePrefix = "/"</c> throws a <c>RoutePatternException</c> from inside routing while the host is
/// still starting, and a negative <see cref="AlvoApiOptions.DefaultPageSize"/> answers every list with
/// a 422 that blames the caller.
/// </summary>
internal sealed class AlvoApiOptionsValidator : IValidateOptions<AlvoApiOptions>
{
    /// <summary>Characters a route prefix may not contain, because a route pattern gives each of them a meaning.</summary>
    private const string ReservedInRoutePattern = "{}*?#:";

    private static readonly SearchValues<char> _reserved = SearchValues.Create(ReservedInRoutePattern);

    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, AlvoApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        ValidateRoutePrefix(options.RoutePrefix, failures);
        ValidatePaging(options, failures);
        ValidatePayloadBounds(options, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// The prefix must survive normalization into a legal route pattern. Normalization trims whitespace and
    /// every leading and trailing slash, so <c>"/"</c>, <c>"//"</c> and <c>" / "</c> all collapse to nothing
    /// — legal, and it mounts the entities at the root. An <em>interior</em> empty segment cannot be
    /// repaired and is what produces the opaque <c>RoutePatternException</c> this check exists to pre-empt.
    /// </summary>
    /// <remarks>
    /// Accepting a value here is a claim that it <em>mounts</em>, not merely that it parses as a string,
    /// which is why <c>DataApiRoutingTests.The_route_prefix_can_mount_at_the_root</c> serves a request over
    /// the normalized form instead of asserting that this method returned success.
    /// </remarks>
    private static void ValidateRoutePrefix(string prefix, List<string> failures)
    {
        if (prefix is null)
        {
            failures.Add($"{nameof(AlvoApiOptions.RoutePrefix)} is null; set it to a path such as \"/api\", or to \"\" to mount at the root.");
            return;
        }

        var trimmed = prefix.Trim().Trim('/');
        if (trimmed.Length == 0)
        {
            return;
        }

        if (trimmed.Split('/').Any(string.IsNullOrWhiteSpace))
        {
            failures.Add(
                $"{nameof(AlvoApiOptions.RoutePrefix)} '{prefix}' has an empty path segment, which is not a legal "
                + "route pattern. Use a single slash between segments, e.g. \"/api/v1\".");
        }

        if (trimmed.AsSpan().ContainsAny(_reserved))
        {
            failures.Add(
                $"{nameof(AlvoApiOptions.RoutePrefix)} '{prefix}' contains a character a route pattern reserves "
                + $"(one of {ReservedInRoutePattern}). A prefix is literal path text — it cannot carry a route "
                + "parameter, a wildcard or a query.");
        }
    }

    private static void ValidatePaging(AlvoApiOptions options, List<string> failures)
    {
        RequirePositive(options.MaxPageSize, nameof(AlvoApiOptions.MaxPageSize), failures);
        RequirePositive(options.DefaultPageSize, nameof(AlvoApiOptions.DefaultPageSize), failures);

        if (options.DefaultPageSize > options.MaxPageSize)
        {
            failures.Add(
                $"{nameof(AlvoApiOptions.DefaultPageSize)} ({options.DefaultPageSize}) is larger than "
                + $"{nameof(AlvoApiOptions.MaxPageSize)} ({options.MaxPageSize}), so the size a request gets when it "
                + "names none would exceed the size it is allowed to ask for. Lower the default, or raise the maximum.");
        }
    }

    private static void ValidatePayloadBounds(AlvoApiOptions options, List<string> failures)
    {
        RequirePositive(options.MaxRequestBodyBytes, nameof(AlvoApiOptions.MaxRequestBodyBytes), failures);
        RequirePositive(options.MaxPayloadDepth, nameof(AlvoApiOptions.MaxPayloadDepth), failures);
        RequirePositive(options.MaxPayloadKeys, nameof(AlvoApiOptions.MaxPayloadKeys), failures);
    }

    /// <summary>
    /// Zero is refused along with a negative value: a bound of zero rejects every request, which is a
    /// silently disabled endpoint rather than a configured limit.
    /// </summary>
    private static void RequirePositive(int value, string option, List<string> failures)
    {
        if (value <= 0)
        {
            failures.Add($"{option} is {value}; it must be greater than zero, or the endpoint refuses every request.");
        }
    }
}
