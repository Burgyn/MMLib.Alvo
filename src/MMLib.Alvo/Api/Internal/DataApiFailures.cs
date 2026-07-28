using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The one place a refusal becomes a status code. <c>IAlvoData</c>'s remarks settle the mapping —
/// a request layer has nothing but the exception <em>type</em> to map from — and this is the single
/// authority that applies it, so no endpoint decides a status of its own.
/// </summary>
/// <remarks>
/// <para>
/// Task 5 replaces the <em>rendering</em> here with <c>ProblemResultFactory</c>: the
/// <c>https://alvo.dev/errors/&lt;slug&gt;</c> type URIs, the <c>violations</c> array, and the fix
/// suggestions §0 principle 4 requires. The mapping itself — which exception is which status — is
/// already the contract and does not move.
/// </para>
/// <para>
/// <b><see cref="InvalidOperationException"/> is deliberately not caught.</b> Its contract is "an
/// invariant the implementation itself relies on is broken", which is never a well-formed request from
/// an authorized caller; swallowing it into a hand-made 500 would lose the stack trace the host's own
/// logging is there to record. It propagates, and the host answers 500.
/// </para>
/// </remarks>
internal static class DataApiFailures
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
            "The presented API key could not be used. Check the key, whether it has been revoked or has expired, "
            + "and whether it was issued for the tenant you requested."),
        $"{ApiKeyScheme} header=\"{headerName}\"");

    /// <summary>
    /// The 403 for a resolved key whose scopes do not cover this operation.
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
        "The presented API key's scopes do not permit this operation. Grant the key the scope it needs.");

    /// <summary>
    /// The 404 for a row that does not exist <em>or</em> that the caller's policy excludes — one
    /// wording, because <c>IAlvoData</c>'s contract makes the two indistinguishable and the HTTP layer
    /// must not undo that.
    /// </summary>
    internal static IResult NotFound() => Problem(
        StatusCodes.Status404NotFound, new AlvoRecordNotFoundException().Message);

    /// <summary>
    /// The 422 for a request whose shape is wrong — the same channel an <see cref="ArgumentException"/>
    /// out of the port lands on, because "the query or payload is malformed" is one diagnosis whether the
    /// API layer or the port noticed it.
    /// </summary>
    /// <param name="detail">What was wrong with the request.</param>
    internal static IResult Malformed(string detail) =>
        Problem(StatusCodes.Status422UnprocessableEntity, detail);

    /// <summary>
    /// The 422 for a query string that could not be parsed, carrying every reason rather than the first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>violations</c> extension is what §0 principle 4 asks for — a machine-readable code and a fix
    /// suggestion per problem, so an agent can repair its request without guessing. Task 5's
    /// <c>ProblemResultFactory</c> takes over the <em>rendering</em> (the <c>alvo.dev/errors</c> type URI,
    /// the same array for body validation); the 422 and the array's shape are already the contract.
    /// </para>
    /// <para>
    /// The <c>detail</c> is the violations' own messages, which are built from constants and server-owned
    /// values only (see <see cref="AlvoViolation"/>) — so no caller-supplied text is reflected into the
    /// response, and this is not the place a NUL, an RTL override or a quote comes back out.
    /// </para>
    /// </remarks>
    /// <param name="violations">Every reason the query string was refused.</param>
    internal static IResult MalformedQuery(IReadOnlyList<AlvoViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);
        return Results.Problem(
            detail: string.Join(" ", violations.Select(violation => violation.Message).Distinct(StringComparer.Ordinal)),
            statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?> { ["violations"] = violations });
    }

    /// <summary>
    /// Runs <paramref name="operation"/> and maps every refusal <c>IAlvoData</c> is allowed to raise
    /// onto its status code.
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
            return Problem(StatusCodes.Status403Forbidden, exception.Message);
        }
        catch (AlvoRecordNotFoundException)
        {
            return NotFound();
        }
        catch (AlvoPreconditionFailedException exception)
        {
            return Problem(StatusCodes.Status412PreconditionFailed, exception.Message);
        }
        catch (AlvoIdempotencyConflictException exception)
        {
            return Problem(StatusCodes.Status409Conflict, exception.Message);
        }
        catch (ArgumentException exception)
        {
            // Last, because it is the widest of the five: the malformed-query/payload channel.
            return Problem(StatusCodes.Status422UnprocessableEntity, exception.Message);
        }
    }

    // Task 5: ProblemResultFactory takes this over — the alvo.dev/errors type URI per failure class and
    // the violations array. The status mapping above is already settled and moves with it unchanged.
    private static IResult Problem(int statusCode, string detail) =>
        Results.Problem(detail: detail, statusCode: statusCode);

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
