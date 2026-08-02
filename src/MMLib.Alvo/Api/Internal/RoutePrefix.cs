namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// <b>The one reduction of a configured <see cref="AlvoApiOptions.RoutePrefix"/> to the form a route pattern
/// is built from</b> — read by the startup validator and by the mapper, so "what was validated is what
/// mounts" holds by construction rather than by two authors agreeing.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is one type because the two halves were two copies, and the copies had already diverged once.</b>
/// <see cref="AlvoApiOptionsValidator"/> validated <c>prefix.Trim().Trim('/')</c> and
/// <c>MapAlvoDataApi</c> independently re-derived it — and the mapper's copy returned <c>"/"</c> for a prefix
/// of nothing but slashes, so <c>Map</c> built <c>"//owners"</c> and <c>RoutePatternFactory.Parse</c> threw on
/// the empty segment while the validator had already reported the value as valid. A validator returning
/// success is only evidence about the value it actually reduced.
/// </para>
/// <para>
/// <b>There is one method, not a normalizing one beside a trimming one.</b> The validator needs the path
/// segments and the mapper needs the mountable prefix, and both are read off this single output — its
/// contract is "empty, or one leading slash and then the path text", so the segments are what follows that
/// slash. A second entry point returning the untrimmed halfway form would be the two copies again, one
/// refactor later.
/// </para>
/// <para>
/// <b><see cref="Normalize"/> takes a non-nullable <see cref="string"/>, and the absence of a null arm is
/// deliberate.</b> <see cref="AlvoApiOptions.RoutePrefix"/> is non-nullable, and a host that assigns
/// <see langword="null"/> anyway is refused by <see cref="AlvoApiOptionsValidator"/> — which reports it as a
/// named failure rather than reducing it, and <c>OptionsFactory</c> runs every <c>IValidateOptions</c> on
/// create, so <c>.Value</c> throws <c>OptionsValidationException</c> before the mapper reaches this method. A
/// <c>prefix?.Trim()</c> here would be a branch neither caller can reach, and an unreachable guard reads as a
/// possibility the caller has to consider.
/// </para>
/// </remarks>
internal static class RoutePrefix
{
    /// <summary>
    /// Reduces a configured prefix to the one form a route pattern can be built from: a single leading slash
    /// and no trailing one, so <c>"api"</c>, <c>"/api"</c> and <c>"/api/"</c> mount in the same place instead
    /// of producing three different route tables.
    /// </summary>
    /// <remarks>
    /// A prefix that is <em>only</em> slashes or whitespace reduces to the empty string, which mounts the
    /// entities at the root (<c>/owners</c>). An <em>interior</em> empty segment cannot be repaired here and is
    /// refused at startup by <see cref="AlvoApiOptionsValidator"/> instead — which is why this returns a value
    /// rather than a result: by the time the mapper calls it, everything it cannot reduce has been rejected.
    /// </remarks>
    /// <param name="prefix">The configured prefix, as the host wrote it.</param>
    /// <returns>
    /// The empty string, or a single leading slash followed by the prefix's path text. The path segments are
    /// therefore everything after index 0, which is how <see cref="AlvoApiOptionsValidator"/> reads them.
    /// </returns>
    internal static string Normalize(string prefix)
    {
        var trimmed = prefix.Trim().Trim('/');
        return trimmed.Length == 0 ? string.Empty : $"/{trimmed}";
    }
}
