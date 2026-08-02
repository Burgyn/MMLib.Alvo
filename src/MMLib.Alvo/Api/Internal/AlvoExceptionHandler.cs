using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Answers an unhandled failure <b>from one of Alvo's own endpoints</b> with Alvo's problem document, and
/// declines everything else.
/// </summary>
/// <remarks>
/// <para>
/// Two halves matter for the failure it does claim and neither is sufficient. The <b>log</b> is why the
/// endpoint layer deliberately does not catch <c>IAlvoData</c>'s fifth family — a hand-made problem document
/// built at the call site would lose the stack trace. The <b>document</b> is #119: with only
/// <c>AddProblemDetails()</c> the framework writes an RFC 9110 status-code URI into <c>type</c>, so the one
/// member an agent branches on stops being an Alvo classification.
/// </para>
/// <para>
/// <b>What it declines is now as much of the contract as what it answers.</b> An earlier version returned
/// <see langword="true"/> for every exception in the pipeline, which cost two things at once. An
/// <see cref="IExceptionHandler"/> an embedded host registered <em>after</em> <c>AddAlvoProblemDetails()</c>
/// never ran — the framework stops at the first handler that claims a failure — so the host's own error
/// contract silently disappeared from production 500s, <em>including on its own non-Alvo endpoints</em>, with
/// no build error and no failing test. And a caller's own mistake came back as
/// <c>alvo.dev/errors/internal</c>: a body over the server's limit, or an upload the client truncated, raises
/// <see cref="BadHttpRequestException"/> — not one of <c>IAlvoData</c>'s five families — so the caller was
/// told "an invariant Alvo itself relies on is broken" and invited to retry something that can never succeed,
/// while an operator was paged with a stack trace for a client-side error.
/// </para>
/// <para>
/// <b>The seam is <see cref="DataApiOperationMetadata"/>, read off
/// <see cref="IExceptionHandlerFeature.Endpoint"/>.</b> Every generated route carries that marker (the marker
/// and the authorization filter are attached in the same call, so one without the other is unrepresentable),
/// and the exception-handler middleware captures the matched endpoint into its feature <em>before</em> it
/// clears the <see cref="HttpContext"/> — which is why the endpoint is read from the feature and never from
/// <c>httpContext.GetEndpoint()</c>, which is <see langword="null"/> by the time a handler runs. A failure
/// with no endpoint at all — one raised before routing matched — is not Alvo's either, and is declined by
/// the same rule.
/// </para>
/// </remarks>
internal sealed partial class AlvoExceptionHandler(ILogger<AlvoExceptionHandler> logger) : IExceptionHandler
{
    /// <inheritdoc/>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (!FailedInsideAlvo(httpContext))
        {
            return false;
        }

        await AnswerAsync(httpContext, exception).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Whether the endpoint this request failed on is one Alvo generated.
    /// </summary>
    /// <remarks>
    /// Read off <see cref="IExceptionHandlerFeature"/> rather than off the context: the middleware records
    /// the matched endpoint there and then clears the context's own, so this is the only place the answer
    /// still exists when a handler runs. No feature means this handler was invoked by something other than
    /// the exception-handler middleware, which cannot tell us whose endpoint failed — so Alvo declines.
    /// </remarks>
    /// <param name="httpContext">The failed request's context.</param>
    private static bool FailedInsideAlvo(HttpContext httpContext) =>
        httpContext.Features.Get<IExceptionHandlerFeature>()?.Endpoint?.Metadata
            .GetMetadata<DataApiOperationMetadata>() is not null;

    /// <summary>Logs the failure at the level it deserves and writes the document it maps to.</summary>
    /// <param name="httpContext">The failed request's context.</param>
    /// <param name="exception">The failure.</param>
    private async Task AnswerAsync(HttpContext httpContext, Exception exception)
    {
        if (exception is BadHttpRequestException client)
        {
            await RefuseAsync(httpContext, client).ConfigureAwait(false);
            return;
        }

        if (!ClientDisconnected(httpContext, exception))
        {
            Failed(logger, exception, httpContext.Request.Method, httpContext.Request.Path.Value);
        }

        await ProblemResultFactory.Internal().ExecuteAsync(httpContext).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers a request the server refused before Alvo could read it, at the status the server chose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The status is the exception's, never 500.</b> <see cref="BadHttpRequestException.StatusCode"/> is
    /// the server's own decision about a request it could not read — 413 for a body over the configured
    /// limit, 400 for framing the connection broke, 408 for a body arriving too slowly — and flattening it
    /// tells an agent to retry a request whose size or framing is the thing that has to change.
    /// </para>
    /// <para>
    /// <b>Logged at <see cref="LogLevel.Warning"/>, and without the exception.</b> A client-side mistake is
    /// not a defect in Alvo, and <see cref="LogLevel.Error"/> with a stack trace is what an on-call rotation
    /// is paged by; a truncated upload behind a flaky mobile connection would page someone once per retry.
    /// The record is kept, at the level an operator reads rather than the one that wakes them, because a
    /// server limit being hit repeatedly <em>is</em> worth noticing.
    /// </para>
    /// </remarks>
    /// <param name="httpContext">The failed request's context.</param>
    /// <param name="exception">The server's refusal.</param>
    private async Task RefuseAsync(HttpContext httpContext, BadHttpRequestException exception)
    {
        Unreadable(logger, httpContext.Request.Method, httpContext.Request.Path.Value, exception.StatusCode);

        await ProblemResultFactory.Unreadable(exception.StatusCode).ExecuteAsync(httpContext)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Whether this failure is the caller hanging up rather than Alvo breaking.
    /// </summary>
    /// <remarks>
    /// <c>ExceptionHandlerMiddleware</c> suppresses its <em>own</em> diagnostics for an aborted request but
    /// still invokes registered handlers, so without this every closed browser tab and every ingress timeout
    /// behind a proxy becomes an <c>Error</c> record with a stack trace — the level operators page on. Both
    /// halves are required: an <see cref="OperationCanceledException"/> whose request was <em>not</em>
    /// aborted is a real cancellation bug inside Alvo and stays logged, and an abort that surfaced as some
    /// other exception is a real failure that happened to race the disconnect.
    /// </remarks>
    /// <param name="httpContext">The failed request's context.</param>
    /// <param name="exception">The failure.</param>
    private static bool ClientDisconnected(HttpContext httpContext, Exception exception) =>
        exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested;

    /// <summary>
    /// The one log record, as a compile-time-generated <c>LoggerMessage</c> delegate.
    /// </summary>
    /// <remarks>
    /// Source-generated rather than a <c>LogError</c> call because <c>CA1848</c> is an error in this repository.
    /// The <paramref name="exception"/> is passed as the record's exception rather than formatted into the
    /// message, which is what keeps the stack trace — the half of #119 that a tidy problem document must not
    /// cost.
    /// </remarks>
    /// <param name="logger">The logger this handler writes through.</param>
    /// <param name="exception">The failure, logged with its stack trace.</param>
    /// <param name="method">The failed request's HTTP method.</param>
    /// <param name="path">The failed request's path.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "Alvo failed to handle {Method} {Path}.")]
    private static partial void Failed(ILogger logger, Exception exception, string method, string? path);

    /// <summary>The client-fault record: no exception, and a level nobody is paged by.</summary>
    /// <param name="logger">The logger this handler writes through.</param>
    /// <param name="method">The refused request's HTTP method.</param>
    /// <param name="path">The refused request's path.</param>
    /// <param name="statusCode">The status the server refused it with.</param>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Alvo could not read {Method} {Path}; the server refused it with {StatusCode}.")]
    private static partial void Unreadable(ILogger logger, string method, string? path, int statusCode);
}
