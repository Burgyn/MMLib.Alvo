using Microsoft.AspNetCore.Http;
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
    /// The 401 for a credential that was presented and cannot be used — unknown, revoked, expired,
    /// malformed, or naming a tenant it was not issued for. One wording for all of them, because
    /// telling them apart would let a caller enumerate key ids one request at a time.
    /// </summary>
    internal static IResult Unauthenticated() => Problem(
        StatusCodes.Status401Unauthorized,
        "The presented API key could not be used. Check the key, whether it has been revoked or has expired, "
        + "and whether it was issued for the tenant you requested.");

    /// <summary>
    /// The 403 for a resolved key whose scopes do not cover this operation. It names neither the entity
    /// nor the operation: the refusal happens before any row is consulted, and a message naming the
    /// entity would answer "does this entity exist" for a caller whose scopes keep them out of it.
    /// </summary>
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
}
