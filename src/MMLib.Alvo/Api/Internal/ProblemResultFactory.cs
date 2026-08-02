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
/// to record. They propagate, and the host answers 500. A host that asked Alvo to answer for it
/// (<c>AddAlvoProblemDetails()</c>) renders that 500 through <see cref="Internal"/> from an exception
/// handler, <em>after</em> logging the exception — which is why the entry point below exists and why no
/// endpoint calls it.
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
    /// The 500 for a broken invariant, in a host that asked Alvo to answer for it.
    /// </summary>
    /// <remarks>
    /// The detail is a constant. Nothing about the failure is reflected — not the exception type, not its
    /// message, not a stack frame — because the caller cannot act on any of it and an attacker can. The
    /// exception itself is logged by <c>AlvoExceptionHandler</c>, which is the trade #119 describes: log
    /// everything, disclose the classification and nothing else.
    /// </remarks>
    internal static IResult Internal() => Problem(
        StatusCodes.Status500InternalServerError,
        AlvoProblemTypes.Internal,
        "The request could not be completed because of an internal error. It has been logged; retry, and if it "
        + "persists, report it to whoever operates this instance.");

    /// <summary>
    /// The refusal for a request the web server would not read — rendered at <em>its</em> status, not 500.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The status is a parameter because the server, not Alvo, decided it.</b>
    /// <c>BadHttpRequestException.StatusCode</c> is 413 for a body over the configured limit, 400 for framing
    /// that broke mid-upload, 408 for a body arriving too slowly; the caller can act on the difference, and
    /// collapsing all three into a 500 tells an agent to retry a request whose size or framing is the thing
    /// that has to change.
    /// </para>
    /// <para>
    /// The <c>detail</c> is a constant, for the same reason <see cref="Internal"/>'s is: the server's own
    /// message can carry the configured limit and, for some rejections, part of what the caller sent, and
    /// neither belongs in a body. What the caller needs — that this request cannot succeed unchanged — is
    /// server-owned prose.
    /// </para>
    /// </remarks>
    /// <param name="statusCode">The status the web server refused the request with.</param>
    internal static IResult Unreadable(int statusCode) => Problem(
        statusCode,
        AlvoProblemTypes.UnreadableRequest,
        "The server refused this request before Alvo could read it — the status says which limit it crossed: "
        + "a body too large, a body that arrived too slowly, or one whose framing broke. Send a smaller or "
        + "well-formed request; retrying this one unchanged cannot succeed.");

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
    /// The 409 for a request that collides with stored state a database constraint guards — the one refusal
    /// whose reason the framework could not check itself before the write reached the engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It carries a <c>violations</c> array like a 422 does, and for the same reason: §0 principle 4's reader
    /// is an agent deciding what to change, and "409" alone tells it only to stop. Each entry names a field
    /// the caller supplied, with a stable <c>code</c> and a fix suggestion — which is precisely what the
    /// <c>500 internal</c> this replaced could not carry.
    /// </para>
    /// <para>
    /// <b>The pointer names a field, never a value, and the field name comes from the schema.</b> The port
    /// resolves it from the violated index's own properties (never from the payload's keys, which are
    /// caller-supplied text), and framework-managed columns are already excluded there — so this cannot tell a
    /// caller to change <c>tenant_id</c>, which they may not write on an update at all.
    /// </para>
    /// <para>
    /// <b>A refusal that names no field still gets one entry, pointed at the document.</b>
    /// <see cref="AlvoConstraintKind.Referenced"/> deliberately names nothing (see
    /// <see cref="AlvoConstraintViolationException"/>), and SQLite reports no columns for a foreign-key
    /// failure at all — so an empty <c>violations</c> array would be the shape a caller reads as "no
    /// machine-readable reason", which is the defect being fixed. RFC 6901's empty pointer is the whole
    /// document, which is exactly what is in conflict when no single field is.
    /// </para>
    /// </remarks>
    /// <param name="exception">The port's refusal, carrying the kind and the fields it named.</param>
    private static IResult Conflict(AlvoConstraintViolationException exception) => Problem(
        StatusCodes.Status409Conflict, AlvoProblemTypes.Conflict, ConflictViolations(exception));

    /// <summary>One violation per named field, or one for the whole document when none was named.</summary>
    /// <param name="exception">The port's refusal.</param>
    private static IReadOnlyList<AlvoViolation> ConflictViolations(AlvoConstraintViolationException exception) =>
        exception.Kind == AlvoConstraintKind.Unique && exception.Fields.Count > 0
            ? [.. exception.Fields.Select(UniqueViolation)]
            : [new AlvoViolation(string.Empty, CodeFor(exception.Kind), exception.Message, FixFor(exception.Kind))];

    /// <summary>The entry for one field a unique constraint refused.</summary>
    /// <remarks>
    /// The message says nothing about <em>which</em> record holds the value, or whether the caller can see it:
    /// on a tenant-scoped entity the constraint spans the tenant (#137), so the colliding row is always one of
    /// the caller's own — but the wording must not start depending on that, because a non-scoped entity's
    /// collision can be with a row no rule of theirs admits.
    /// </remarks>
    /// <param name="field">The field name, from the entity's schema.</param>
    private static AlvoViolation UniqueViolation(string field) => new(
        "/" + field,
        "unique",
        "This field is declared unique and another record already holds the value sent for it.",
        "Send a value no other record holds, or change the record that holds it.");

    /// <summary>The stable code for a refusal that names no field.</summary>
    /// <param name="kind">Which constraint refused the request.</param>
    private static string CodeFor(AlvoConstraintKind kind) =>
        kind == AlvoConstraintKind.Referenced ? "referenced" : "unique";

    /// <summary>The fix for a refusal that names no field.</summary>
    /// <param name="kind">Which constraint refused the request.</param>
    private static string FixFor(AlvoConstraintKind kind) => kind == AlvoConstraintKind.Referenced
        ? "Delete the records that reference this one, or point them at something else, then retry."
        : "Send a value no other record holds on the field declared unique.";

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
        catch (AlvoConstraintViolationException exception)
        {
            return Conflict(exception);
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
            return Malformed(WithoutArgumentDetail(exception.Message));
        }
    }

    /// <summary>
    /// <see cref="ArgumentException.Message"/> with everything <see cref="ArgumentException"/> itself appends
    /// removed — the <c>(Parameter '…')</c> suffix and, for a range exception, the <c>Actual value was …</c>
    /// line after it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An internal argument name is an implementation detail of whichever guard raised it, not part of the
    /// contract an agent reads, and <c>AlvoApiWorld</c> screens every response in the suite for one.
    /// </para>
    /// <para>
    /// <b>It belongs on the arm above and not only in the query parser, which is where it started.</b> The
    /// parser strips the port's guards it calls itself; this arm renders every <em>other</em>
    /// <see cref="ArgumentException"/> the port can raise, and those carry a <c>paramName</c> too — the first
    /// one a caller can actually reach is <c>AlvoIdempotency.EnsureUsableKey</c>, whose refusal would
    /// otherwise have shipped <c>(Parameter 'key')</c> in a 422 body. The suffix is appended on the
    /// <b>same line</b>, separated by a space, so a version of this that cut only at the first newline
    /// stripped nothing at all.
    /// </para>
    /// <para>
    /// It takes a non-null <see cref="string"/> and guards nothing: both call sites pass
    /// <see cref="Exception.Message"/>, which is never <see langword="null"/>, so a
    /// <c>ThrowIfNull</c> here was a check no caller could reach — and an unreachable guard reads as a
    /// possibility the caller has to consider.
    /// </para>
    /// </remarks>
    /// <param name="message">The exception message to sanitize.</param>
    internal static string WithoutArgumentDetail(string message)
    {
        var appended = message.IndexOf(ArgumentNameSuffix, StringComparison.Ordinal);
        var text = appended < 0 ? message : message[..appended];
        var newline = text.IndexOf('\n');
        return (newline < 0 ? text : text[..newline]).TrimEnd();
    }

    /// <summary>How <see cref="ArgumentException"/> introduces the argument name it appends to a message.</summary>
    private const string ArgumentNameSuffix = " (Parameter '";

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
