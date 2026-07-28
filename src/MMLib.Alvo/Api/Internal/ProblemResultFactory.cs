using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The one place a refusal becomes a status code, a problem <c>type</c> and a body. <c>IAlvoData</c>'s
/// remarks settle the mapping — a request layer has nothing but the exception <em>type</em> to map from —
/// and this is the single authority that applies it, so no endpoint decides a status, a type URI or a
/// media type of its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>One factory rather than <c>Results.Problem</c> at each call site.</b> The framework's own helper
/// stamps an RFC 9110 status-code URI as <c>type</c>, which classifies the refusal as "422" — a fact the
/// status line already carried — instead of as the kind of refusal it is. Routing every refusal through
/// here is what makes <see cref="AlvoProblemTypes"/> the classification an agent branches on, and what
/// keeps the <c>violations</c> array on the one shape <see cref="AlvoViolation"/> publishes.
/// </para>
/// <para>
/// <b><see cref="InvalidOperationException"/> is deliberately not caught, and neither is
/// <see cref="ArgumentNullException"/>.</b> Both mean "an invariant the implementation itself relies on is
/// broken" — <c>IAlvoData</c>'s fifth family — which is never a well-formed request from an authorized
/// caller; swallowing either into a hand-made 500 would lose the stack trace the host's own logging is there
/// to record. They propagate, and the host answers 500 — see <see cref="AlvoProblemTypes"/> for why the
/// catalogue therefore has no slug for it.
/// </para>
/// </remarks>
internal static class ProblemResultFactory
{
    /// <summary>
    /// The authentication scheme a 401 advertises. Not one of the IANA-registered schemes, because Alvo's
    /// dev credential is not one: it is a header-carried API key, and RFC 7235's <c>auth-param</c> syntax
    /// is what lets the challenge say <em>which</em> header without inventing a scheme's semantics.
    /// </summary>
    /// <remarks>
    /// The name is <see cref="Auth.AlvoAuthOptions.HeaderName"/>'s value at runtime, so a host that moved
    /// the header is advertising the header it actually reads. #36's real identity providers will add
    /// their own challenge beside this one, which is why the header is appended rather than assigned.
    /// </remarks>
    private const string ApiKeyScheme = "AlvoApiKey";

    /// <summary>
    /// The 401 for a credential that was presented and cannot be used — unknown, revoked, expired,
    /// malformed, or naming a tenant it was not issued for. One wording for all of them, because
    /// telling them apart would let a caller enumerate key ids one request at a time.
    /// </summary>
    /// <remarks>
    /// RFC 7235 §3.1 makes <c>WWW-Authenticate</c> a <b>MUST</b> on a 401, and it is the only thing that
    /// makes the status actionable without documentation: it names the scheme and the header the caller
    /// should have used, so an agent can discover how to authenticate instead of guessing.
    /// </remarks>
    /// <param name="headerName">The header a credential is read from, named in the challenge.</param>
    internal static IResult Unauthenticated(string headerName) => new UnauthenticatedResult(
        Problem(
            StatusCodes.Status401Unauthorized,
            AlvoProblemTypes.Unauthenticated,
            "The presented API key could not be used. Check the key, whether it has been revoked or has expired, "
            + "and whether it was issued for the tenant you requested."),
        $"{ApiKeyScheme} header=\"{headerName}\"");

    /// <summary>
    /// The 403 for a resolved key whose scopes do not cover this operation — a different slug from
    /// <see cref="AlvoProblemTypes.Forbidden"/> because it is a different fix.
    /// </summary>
    /// <remarks>
    /// The wording names neither the entity nor the operation, and what that protects is <b>the shape of
    /// the key's own grant</b> — not the entity's existence, which the 403-vs-404 split already discloses
    /// to anyone who can compare two requests. A message naming the missing scope would let a caller map
    /// out which entities their key does and does not cover, one request at a time, which is a fingerprint
    /// of the credential rather than of the data. The scope a key holds is knowable to whoever issued it;
    /// it should not be re-derivable by probing.
    /// </remarks>
    internal static IResult ScopeRefused() => Problem(
        StatusCodes.Status403Forbidden,
        AlvoProblemTypes.OutOfScope,
        "The presented API key's scopes do not permit this operation. Grant the key the scope it needs.");

    /// <summary>
    /// The 404 for a row that does not exist <em>or</em> that the caller's policy excludes — one
    /// wording and one type, because <c>IAlvoData</c>'s contract makes the two indistinguishable and the
    /// HTTP layer must not undo that.
    /// </summary>
    internal static IResult NotFound() => Problem(
        StatusCodes.Status404NotFound, AlvoProblemTypes.NotFound, new AlvoRecordNotFoundException().Message);

    /// <summary>
    /// The 422 for a request whose shape is wrong, carrying every reason rather than the first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>violations</c> extension is what §0 principle 4 asks for — a machine-readable code and a fix
    /// suggestion per problem, so an agent can repair its request without guessing.
    /// </para>
    /// <para>
    /// The <c>detail</c> is the violations' own messages, which are built from constants and server-owned
    /// values only (see <see cref="AlvoViolation"/>) — so no caller-supplied text is reflected into the
    /// response, and this is not the place a NUL, an RTL override or a quote comes back out.
    /// </para>
    /// </remarks>
    /// <param name="violations">Every reason the query string was refused.</param>
    internal static IResult MalformedQuery(IReadOnlyList<AlvoViolation> violations) =>
        Problem(StatusCodes.Status422UnprocessableEntity, AlvoProblemTypes.MalformedQuery, violations);

    /// <summary>
    /// The 422 for a malformed request with one reason and no violation list — the channel an
    /// <see cref="ArgumentException"/> out of the port lands on, because "the query or payload is
    /// malformed" is one diagnosis whether this layer or the port noticed it.
    /// </summary>
    /// <param name="detail">What was wrong with the request, in the port's own wording.</param>
    internal static IResult Malformed(string detail) =>
        Problem(StatusCodes.Status422UnprocessableEntity, AlvoProblemTypes.MalformedQuery, detail);

    /// <summary>
    /// The 422 for a request body the entity's schema refuses, carrying <b>every</b> violation.
    /// </summary>
    /// <remarks>
    /// A separate slug from <see cref="MalformedQuery"/> because the two have different fixes and a caller
    /// can act on the difference: a malformed request could not be read at all, whereas a validation
    /// failure was read, understood, and measured against the entity's declared shape — so every violation
    /// here names a field the caller can correct.
    /// </remarks>
    /// <param name="violations">Every reason the body was refused.</param>
    internal static IResult Validation(IReadOnlyList<AlvoViolation> violations) =>
        Problem(StatusCodes.Status422UnprocessableEntity, AlvoProblemTypes.Validation, violations);

    /// <summary>
    /// Runs <paramref name="operation"/> and maps every refusal <c>IAlvoData</c> is allowed to raise
    /// onto its status code and problem type.
    /// </summary>
    /// <param name="operation">The port call, plus the rendering of its success.</param>
    internal static async Task<IResult> GuardAsync(Func<Task<IResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (AlvoAuthorizationException exception)
        {
            // The port's message is already designed never to name the entity, the row, or whether it
            // exists (the tenant guard's deliberate exception aside), so it reaches the caller verbatim.
            return Problem(StatusCodes.Status403Forbidden, AlvoProblemTypes.Forbidden, exception.Message);
        }
        catch (AlvoRecordNotFoundException)
        {
            return NotFound();
        }
        catch (AlvoPreconditionFailedException exception)
        {
            return Problem(
                StatusCodes.Status412PreconditionFailed, AlvoProblemTypes.PreconditionFailed, exception.Message);
        }
        catch (AlvoIdempotencyConflictException exception)
        {
            return Problem(StatusCodes.Status409Conflict, AlvoProblemTypes.IdempotencyConflict, exception.Message);
        }
        catch (ArgumentException exception) when (exception is not ArgumentNullException)
        {
            // Last, because it is the widest of the five: the malformed-query/payload channel.
            //
            // ArgumentNullException is excluded, and that exclusion is IAlvoData's own rule rather than this
            // layer's local opinion — see the port's family table, which states it there because a provider
            // author reads the port and not this file. In short: no request can express a null argument, so
            // one reaching here is a broken invariant (family 5, rendered 500 by the host with its stack
            // trace intact), and the region this guards grew several ThrowIfNull calls of its own when
            // validation and the format catalogue landed inside it.
            return Malformed(exception.Message);
        }
    }

    /// <summary>
    /// One problem document whose <c>detail</c> is the violations' own messages and whose
    /// <c>violations</c> extension carries them structured.
    /// </summary>
    /// <remarks>
    /// The messages are joined <em>distinctly</em>: several fields failing the same rule produce the same
    /// sentence, and repeating it once per field turns a readable <c>detail</c> into noise while the
    /// <c>violations</c> array still names every field separately.
    /// </remarks>
    private static IResult Problem(int statusCode, string type, IReadOnlyList<AlvoViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);
        return Problem(
            statusCode,
            type,
            string.Join(" ", violations.Select(violation => violation.Message).Distinct(StringComparer.Ordinal)),
            violations);
    }

    private static IResult Problem(int statusCode, string type, string detail) =>
        Problem(statusCode, type, detail, violations: null);

    /// <summary>
    /// The one call to <see cref="Results.Problem(string?, string?, int?, string?, string?, IDictionary{string, object?})"/>
    /// in the whole feature.
    /// </summary>
    /// <remarks>
    /// <b>The <c>type</c> is always Alvo's, never the framework's default.</b> Left unset,
    /// <c>Results.Problem</c> fills in an RFC 9110 status-code URI, which classifies a refusal by the
    /// status a client already read off the response line — so two refusals with different fixes become
    /// indistinguishable to anything branching on <c>type</c>. Passing it here, from the enumerated
    /// catalogue, is what makes that impossible to forget at a call site.
    /// </remarks>
    private static IResult Problem(
        int statusCode, string type, string detail, IReadOnlyList<AlvoViolation>? violations) =>
        Results.Problem(
            detail: detail,
            statusCode: statusCode,
            type: AlvoProblemTypes.UriOf(type),
            extensions: violations is null
                ? null
                : new Dictionary<string, object?>(StringComparer.Ordinal) { ["violations"] = violations });

    /// <summary>
    /// A problem response plus the <c>WWW-Authenticate</c> challenge RFC 7235 requires on a 401. A
    /// wrapper rather than a header written at the call site, so the challenge cannot be forgotten by a
    /// second path that also answers 401 — there is exactly one way to produce one.
    /// </summary>
    /// <param name="problem">The problem response to write.</param>
    /// <param name="challenge">The challenge value.</param>
    private sealed class UnauthenticatedResult(IResult problem, string challenge) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.Headers.Append(HeaderNames.WWWAuthenticate, challenge);
            return problem.ExecuteAsync(httpContext);
        }
    }
}
