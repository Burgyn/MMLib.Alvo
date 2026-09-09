using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The JSON <c>Content-Type</c> requirement every body-taking route is guarded by (#191).
/// </summary>
/// <remarks>
/// <para>
/// <b>It is a CSRF guard whose mechanism lives in the browser.</b> WHATWG Fetch safelists
/// <c>application/x-www-form-urlencoded</c>, <c>multipart/form-data</c> and <c>text/plain</c>, and a
/// request carrying only safelisted headers crosses origins with cookies attached and no preflight — as
/// does one declaring no <c>Content-Type</c> at all. Requiring JSON is what forces the preflight, and
/// nothing here is a defence on its own. That is also why the refusal's <em>position</em> in the pipeline
/// is free to follow this layer's ordering rule rather than race it: by the time a request arrives, the
/// preflight has already been demanded or skipped.
/// </para>
/// <para>
/// <b>Called from the five body-taking delegates rather than from a filter or from the body reader.</b>
/// In each it is the <em>first</em> header guard: immediately after the operation's decision and before
/// <c>EnsureUnconditional</c>, <c>Precondition</c> or <c>IdempotencyKey</c> interpret anything. A request
/// that is not even in the form this endpoint reads should not be answered with advice about
/// <c>If-Match</c>. Two reasons put it after the decision, both of them
/// <c>DataApiEndpoints.EnsureOperationIsAllowed</c>'s own: precedence (an unauthorized caller hears about
/// the denial, not about their request's shape) and cost (nothing is read on behalf of a caller who cannot
/// succeed).
/// </para>
/// <para>
/// <b>Two other enforcement points were rejected, and the reasons are worth keeping.</b> A filter
/// registered in <c>DataApiEndpoints.Protect</c> would be the one registration point, but it would land
/// <em>between</em> the scope 403 and the policy decision — reversing that precedence — and would still
/// need a per-kind condition a future kind could miss. Inside <c>BoundedJsonBody.ReadAsync</c>, where all
/// three readers funnel, the refusal would have to travel as a <c>BodyRefusal</c>: a channel every caller
/// renders as a 422 with a <c>violations</c> array, on a code path that has already consumed the body.
/// </para>
/// <para>
/// <b>What "after the decision" does and does not buy, stated because the distinction is easy to
/// overread.</b> <c>PolicyEngine</c> denies at the decision layer for four reasons — no descriptor applied,
/// an unconfigured operation, a tenant-scoped entity with no tenant, and a predicate reading a caller value
/// the caller lacks. A <em>configured</em> rule always resolves to an allow carrying a <c>USING</c> /
/// <c>WITH CHECK</c> predicate the port enforces per row. So a caller whom <c>'admin' in @user.roles</c>
/// will ultimately refuse is not denied here, and for that caller the 415 comes first — exactly as
/// <c>EnsureUnconditional</c>'s 412 already does, and with the same justification: a 415 names nothing
/// about the entity, the row, or whether it exists, and its fix is knowable to the caller before they send
/// anything.
/// </para>
/// <para>
/// <c>DataApiContentTypeTests.Every_endpoint_kind_is_guarded_exactly_when_it_reads_a_body</c> is what makes
/// the five call sites exhaustive: it drives every <see cref="DataApiEndpointKind"/> rather than a list of
/// routes, so a new kind — or an existing one that acquires a body — fails there until its delegate calls
/// this.
/// </para>
/// </remarks>
internal static class JsonContentType
{
    /// <summary>The media type the generated document declares for a request body.</summary>
    private const string Json = "application/json";

    /// <summary>RFC 7396's spelling, accepted by the <c>+json</c> suffix rule and advertised on a PATCH.</summary>
    private const string MergePatchJson = "application/merge-patch+json";

    /// <summary>The <c>application</c> type, the only one a structured <c>+json</c> suffix is accepted under.</summary>
    private const string ApplicationType = "application";

    /// <summary>The structured syntax suffix (RFC 6839) that makes a subtype JSON.</summary>
    private const string JsonSuffix = "json";

    /// <summary>What a refusal advertises this API accepts, for a request that used this method.</summary>
    /// <remarks>
    /// A <c>PATCH</c> is offered RFC 7396's spelling beside the plain one, because a merge patch is what a
    /// partial update <em>is</em> and a client sending that media type is being more precise rather than
    /// wrong. Every other method is offered the one media type the document declares.
    /// </remarks>
    /// <param name="method">The request's method.</param>
    internal static string Advertised(string method) =>
        HttpMethods.IsPatch(method) ? $"{Json}, {MergePatchJson}" : Json;

    /// <summary>
    /// Refuses the request when it does not declare a JSON body, or <see langword="null"/> when it may
    /// proceed.
    /// </summary>
    /// <remarks>
    /// <b>Unconditional, and an option to turn it off was considered and dropped.</b> The case for one was
    /// "a host with its own CSRF defence should not be forced through Alvo's" — which does not hold: leaving
    /// the requirement on costs such a host nothing, because every legitimate client already sends
    /// <c>application/json</c>. The only real case is a legacy caller that sends no <c>Content-Type</c>, and
    /// Alvo has no released package and therefore no such caller. Adding an option later is not a breaking
    /// change; removing one is, so the asymmetry says not now — and an option that disables a security
    /// control is a liability of its own, being a thing a later host sets without understanding why it is
    /// there.
    /// </remarks>
    /// <param name="request">The request whose declaration to judge.</param>
    internal static IResult? Refuse(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return IsJson(request.ContentType) ? null : ProblemResultFactory.UnsupportedMediaType(request.Method);
    }

    /// <summary>
    /// Whether <paramref name="contentType"/> declares JSON: <c>application/json</c>, or any
    /// <c>application/*+json</c>, with any parameters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An absent or unparseable declaration is not JSON.</b> Absent is the case the guard exists for — a
    /// request declaring nothing is CORS-safelisted by omission — and an unparseable one cannot be read as
    /// anything, so reading it as JSON would accept exactly what a forger would send.
    /// </para>
    /// <para>
    /// <b>Parameters are ignored rather than validated.</b> The readers are UTF-8 by construction, so a body
    /// actually encoded otherwise earns the existing <c>malformed-json</c> 422 — a diagnosis about the bytes,
    /// which is the accurate one. A second refusal for the same underlying failure would only make a caller
    /// guess which of the two to repair.
    /// </para>
    /// <para>
    /// The suffix test goes through <see cref="MediaTypeHeaderValue"/>'s own <c>Type</c> and <c>Suffix</c>
    /// rather than through string surgery on the header, so <c>application/merge-patch+json</c> is decided by
    /// the framework's parser and a subtype that merely <em>ends</em> in the word cannot pass by accident.
    /// </para>
    /// </remarks>
    /// <param name="contentType">The request's declared media type, if any.</param>
    private static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && (parsed.MediaType.Equals(Json, StringComparison.OrdinalIgnoreCase)
            || (parsed.Type.Equals(ApplicationType, StringComparison.OrdinalIgnoreCase)
                && parsed.Suffix.Equals(JsonSuffix, StringComparison.OrdinalIgnoreCase)));
}
