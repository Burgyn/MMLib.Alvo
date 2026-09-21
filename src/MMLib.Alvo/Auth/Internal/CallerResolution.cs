using Microsoft.AspNetCore.Http;

namespace MMLib.Alvo.Auth.Internal;

/// <summary>
/// The one reading of a caller's credential off an HTTP request, and the one publication of the caller it
/// resolves to — shared by every endpoint filter that authenticates.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is one type because it was two copies, and one of them carried a security rule.</b> The Data API's
/// filter and the Management API's each had their own <c>Presented</c>, and its "join repeated headers
/// rather than take the first" arm is not a parsing convenience: taking the first copy admits whoever sends
/// their header first, which is an authentication bypass one edit away. A rule with two homes is a rule that
/// gets fixed in one of them.
/// </para>
/// <para>
/// <b>Static, with the collaborators passed in.</b> The two filters differ in what they do <em>after</em>
/// authentication — one consults <see cref="ScopeGate"/>, the other the management gate — and in how they
/// are constructed, so sharing a base class would couple those too. What is shared is three mechanics, and
/// mechanics are what a static helper is for.
/// </para>
/// </remarks>
internal static class CallerResolution
{
    /// <summary>
    /// The value a caller presented in <paramref name="header"/>, or <see langword="null"/> when they
    /// presented none.
    /// </summary>
    /// <remarks>
    /// An absent header and one sent with an empty value are the same thing — no credential — so both yield
    /// <see langword="null"/> and the request is served as anonymous, which default-deny already handles.
    /// <b>Repeated headers are joined rather than resolved one at a time:</b> an ambiguous credential must
    /// not be answered by picking whichever copy came first, and the joined text cannot be a usable key, so
    /// it lands on the 401 path.
    /// </remarks>
    /// <param name="request">The request to read.</param>
    /// <param name="header">The header name to read.</param>
    /// <returns>The presented value, or <see langword="null"/>.</returns>
    internal static string? Presented(HttpRequest request, string header)
    {
        if (!request.Headers.TryGetValue(header, out var values))
        {
            return null;
        }

        var value = values.Count == 1 ? values[0] : string.Join(',', values.ToArray());

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Resolves <paramref name="presentedKey"/>, confirmed against the tenant the same request asked for.
    /// </summary>
    /// <remarks>
    /// The tenant header is read here rather than by the caller because it is only ever meaningful beside a
    /// key: a requested tenant is honoured as confirmation of the tenant the key was issued for, never on
    /// its own.
    /// </remarks>
    /// <param name="resolver">The port that turns a credential into a principal.</param>
    /// <param name="presentedKey">The credential as the caller presented it.</param>
    /// <param name="request">The request the tenant header and the cancellation token are read from.</param>
    /// <param name="options">The header names to read.</param>
    /// <returns>The resolved caller, or <see langword="null"/> — deny — for a credential that cannot be used.</returns>
    internal static ValueTask<AlvoPrincipal?> ResolveAsync(
        IAlvoContextResolver resolver, string presentedKey, HttpRequest request, AlvoAuthOptions options) =>
        resolver.ResolveAsync(
            presentedKey,
            Presented(request, options.TenantHeaderName),
            request.HttpContext.RequestAborted);

    /// <summary>
    /// Publishes <paramref name="principal"/> for the duration of the rest of the pipeline and takes it away
    /// again.
    /// </summary>
    /// <remarks>
    /// <paramref name="principal"/> is <see langword="null"/> for an anonymous caller and that is published
    /// as-is: an anonymous caller <em>has</em> no principal, so there is nothing to invent and no reader has
    /// to know a sentinel convention to spot one. The clear is a <c>finally</c> rather than a trailing
    /// statement so a throwing endpoint cannot leave a caller published on the ambient context this
    /// request's thread later reuses.
    /// </remarks>
    /// <param name="accessor">Where the caller is published.</param>
    /// <param name="principal">The resolved caller, or <see langword="null"/> for an anonymous one.</param>
    /// <param name="context">The invocation being filtered.</param>
    /// <param name="next">The rest of the pipeline.</param>
    /// <returns>Whatever the rest of the pipeline returned.</returns>
    internal static async ValueTask<object?> PublishingAsync(
        IAlvoContextAccessor accessor,
        AlvoPrincipal? principal,
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        accessor.Principal = principal;
        try
        {
            return await next(context).ConfigureAwait(false);
        }
        finally
        {
            accessor.Principal = null;
        }
    }
}
