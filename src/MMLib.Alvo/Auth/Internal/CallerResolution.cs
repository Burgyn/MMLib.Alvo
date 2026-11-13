using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Auth.Internal;

/// <summary>What resolving a request's credential decided: the caller, or the refusal to answer with.</summary>
/// <param name="Principal">The resolved caller, or <see langword="null"/> for an anonymous one.</param>
/// <param name="Refusal">The 401 or 403 to answer instead, when the credential cannot be used.</param>
internal readonly record struct CallerAdmission(AlvoPrincipal? Principal, IResult? Refusal)
{
    /// <summary>Gets the caller to act as.</summary>
    internal AlvoContext Context => Principal?.Context ?? AlvoContext.Anonymous;
}

/// <summary>
/// The one reading of a caller's credential off an HTTP request, the one admission of the caller it resolves to,
/// and the one publication of that caller — shared by every endpoint filter that authenticates: the Data API's
/// <see cref="AlvoContextFilter"/>, the command and <c>.WithAlvoCaller()</c> gate <see cref="CallerGate"/>, and the
/// Management API's <c>ManagementCallerFilter</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is one type because it was two copies, and one of them carried a security rule.</b> The Data API's
/// filter and the Management API's each had their own <c>Presented</c>, and its "join repeated headers
/// rather than take the first" arm is not a parsing convenience: taking the first copy admits whoever sends
/// their header first, which is an authentication bypass one edit away. A rule with two homes is a rule that
/// gets fixed in one of them. The person channel (spec §5.7) made that a third site and a second header, which
/// is why the reading itself lives here too.
/// </para>
/// <para>
/// <b>The reading — spec §5.7's table, with the plan's D7 filling what it leaves open:</b>
/// </para>
/// <list type="table">
///   <listheader><term>Presented</term><description>Data API, commands, <c>.WithAlvoCaller()</c> / Management API</description></listheader>
///   <item><term>nothing</term><description>anonymous / anonymous</description></item>
///   <item><term>the API-key header</term><description>the API-key resolver / the API-key resolver</description></item>
///   <item>
///   <term><c>Authorization: Bearer</c>, a person resolver registered</term>
///   <description>the person resolver / ignored — Management never reads <c>Authorization</c></description>
///   </item>
///   <item>
///   <term><c>Authorization</c> with another scheme, or no person resolver</term>
///   <description>ignored, exactly as before — a host's own JwtBearer or Basic-auth proxy keeps working / ignored</description>
///   </item>
///   <item><term>an API key and an Alvo-owned Bearer</term><description>401, resolving neither / the API key</description></item>
///   <item>
///   <term>an Alvo-owned Bearer that is empty, carries whitespace, or shares the request with another <c>Authorization</c> line</term>
///   <description>401 <c>invalid_request</c>, resolving nothing — never "take the first" / ignored</description>
///   </item>
/// </list>
/// <para>
/// <b><c>Authorization</c> is never the person channel while it is the API-key header.</b> A host may name
/// <c>Authorization</c> as <see cref="AlvoAuthOptions.HeaderName"/>; that header then stays the API key's exactly
/// as before, and the person resolver is never consulted. The scheme is compared case-insensitively (RFC 7235
/// §2.1). Every presented-but-unusable credential is 401 — RFC 6750 §3.1 would answer <c>invalid_request</c>
/// with 400, and Alvo deliberately does not: one status for "your credential cannot be used" is what the three
/// sites already answer and what an agent branches on.
/// </para>
/// <para>
/// <b>Static, with the collaborators passed in.</b> The sites differ in what they do <em>after</em>
/// admission — two consult <see cref="ScopeGate"/>, the Management filter the management gate — and in how they
/// are constructed, so sharing a base class would couple those too. What is shared is three mechanics — reading,
/// admitting, publishing — and mechanics are what a static helper is for.
/// </para>
/// </remarks>
internal static class CallerResolution
{
    /// <summary>
    /// The key a host registers its person <see cref="IAlvoContextResolver"/> under; registering one is what opens
    /// the Bearer channel.
    /// </summary>
    internal const string PersonResolverKey = "alvo-person";

    private const string BearerScheme = "Bearer";

    private const string InvalidToken = "invalid_token";

    private const string InvalidRequest = "invalid_request";

    /// <summary>The one credential <paramref name="request"/> presents, read by spec §5.7's table.</summary>
    /// <remarks>
    /// Pure: it reads headers and decides, and resolves nothing — so the table can be pinned row by row without a
    /// resolver, and <see cref="AdmitAsync"/> can refuse an ambiguous or malformed request without calling one.
    /// </remarks>
    /// <param name="request">The request to read.</param>
    /// <param name="options">The API-key header name.</param>
    /// <param name="surface">Which surface is reading; Management never reads <c>Authorization</c>.</param>
    /// <param name="personRegistered">Whether a person resolver is registered.</param>
    /// <returns>The channel and its value.</returns>
    internal static PresentedCredential Read(
        HttpRequest request, AlvoAuthOptions options, CredentialSurface surface, bool personRegistered)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        var key = Presented(request, options.HeaderName);
        var bearer = ReadsAuthorization(options, surface, personRegistered)
            ? Bearer(request.Headers.Authorization)
            : PresentedCredential.Nothing;

        return (key, bearer.Channel) switch
        {
            (null, _) => bearer,
            (_, CredentialChannel.None) => new PresentedCredential(CredentialChannel.ApiKey, key),
            _ => new PresentedCredential(CredentialChannel.Ambiguous, null),
        };
    }

    /// <summary>Reads, resolves and decides the caller of <paramref name="request"/>.</summary>
    /// <remarks>
    /// No credential is anonymous — <c>(null, null)</c>; a credential that cannot be used is 401 with the challenge
    /// of the channel it arrived on; an ambiguous or malformed one is 401 before any resolver is called. What a site
    /// does with an admitted caller — a scope gate, a management gate — stays the site's.
    /// </remarks>
    /// <param name="request">The request to admit.</param>
    /// <param name="resolvers">The API-key resolver and, when registered, the person resolver.</param>
    /// <param name="options">The header names to read.</param>
    /// <param name="surface">Which surface is admitting.</param>
    /// <returns>The admitted caller, or the refusal to answer with.</returns>
    internal static ValueTask<CallerAdmission> AdmitAsync(
        HttpRequest request, CallerResolvers resolvers, AlvoAuthOptions options, CredentialSurface surface)
    {
        ArgumentNullException.ThrowIfNull(resolvers);
        var credential = Read(request, options, surface, resolvers.HasPerson);

        return credential.Channel switch
        {
            CredentialChannel.None => ValueTask.FromResult(new CallerAdmission(null, null)),
            CredentialChannel.ApiKey => AdmittingAsync(resolvers.ApiKeys, credential, request, options),
            CredentialChannel.Bearer => AdmittingAsync(resolvers.Person(request.HttpContext), credential, request, options),
            _ => ValueTask.FromResult(new CallerAdmission(null, Refusal(credential.Channel, options))),
        };
    }

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
    private static ValueTask<AlvoPrincipal?> ResolveAsync(
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

    /// <summary>Resolves a credential that was read cleanly, and refuses it when its resolver does.</summary>
    /// <param name="resolver">The resolver the credential's channel belongs to.</param>
    /// <param name="credential">The credential, with its value.</param>
    /// <param name="request">The request the tenant header and the cancellation token are read from.</param>
    /// <param name="options">The header names to read.</param>
    private static async ValueTask<CallerAdmission> AdmittingAsync(
        IAlvoContextResolver resolver, PresentedCredential credential, HttpRequest request, AlvoAuthOptions options)
    {
        var principal = await ResolveAsync(resolver, credential.Value!, request, options).ConfigureAwait(false);
        return principal is null
            ? new CallerAdmission(null, Refusal(credential.Channel, options))
            : new CallerAdmission(principal, null);
    }

    /// <summary>The 401 a credential that cannot be used earns, with its own channel's challenge.</summary>
    /// <param name="channel">The channel the credential arrived on.</param>
    /// <param name="options">The API-key header name the challenge advertises.</param>
    private static IResult Refusal(CredentialChannel channel, AlvoAuthOptions options) => channel switch
    {
        CredentialChannel.ApiKey => ProblemResultFactory.Unauthenticated(options.HeaderName),
        CredentialChannel.Bearer => ProblemResultFactory.BearerUnauthenticated(InvalidToken),
        CredentialChannel.MalformedBearer => ProblemResultFactory.BearerUnauthenticated(InvalidRequest),
        _ => ProblemResultFactory.AmbiguousCredential(options.HeaderName),
    };

    /// <summary>Whether this surface, under these options, reads <c>Authorization</c> at all (D7).</summary>
    /// <param name="options">The API-key header name, which keeps <c>Authorization</c> when it names it.</param>
    /// <param name="surface">The surface reading.</param>
    /// <param name="personRegistered">Whether a person resolver is registered.</param>
    private static bool ReadsAuthorization(AlvoAuthOptions options, CredentialSurface surface, bool personRegistered) =>
        surface == CredentialSurface.Data
        && personRegistered
        && !string.Equals(options.HeaderName, HeaderNames.Authorization, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Bearer reading of the request's <c>Authorization</c> field lines.</summary>
    /// <remarks>
    /// No Bearer line at all is <see cref="CredentialChannel.None"/> — another scheme is the host's. A Bearer line
    /// beside any other line is malformed, by the same rule <see cref="Presented"/> applies to the API key: an
    /// ambiguous credential is refused, never disambiguated by taking the first.
    /// </remarks>
    /// <param name="lines">The <c>Authorization</c> field lines.</param>
    private static PresentedCredential Bearer(StringValues lines)
    {
        if (!lines.Any(IsBearer))
        {
            return PresentedCredential.Nothing;
        }

        var token = lines.Count == 1 ? TokenOf(lines[0]!) : string.Empty;
        return token.Length == 0 || token.Any(char.IsWhiteSpace)
            ? new PresentedCredential(CredentialChannel.MalformedBearer, null)
            : new PresentedCredential(CredentialChannel.Bearer, token);
    }

    /// <summary>Whether one field line's scheme — the text before its first space — is Bearer, in any case.</summary>
    /// <param name="line">The field line.</param>
    private static bool IsBearer(string? line) =>
        line is not null && SchemeOf(line).Equals(BearerScheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>The text before a field line's first space, or the whole line when it has none.</summary>
    /// <param name="line">The field line.</param>
    private static ReadOnlySpan<char> SchemeOf(string line)
    {
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? line : line.AsSpan(0, space);
    }

    /// <summary>The text after a field line's first space, or empty when it has none.</summary>
    /// <param name="line">The field line.</param>
    private static string TokenOf(string line)
    {
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? string.Empty : line[(space + 1)..];
    }
}
