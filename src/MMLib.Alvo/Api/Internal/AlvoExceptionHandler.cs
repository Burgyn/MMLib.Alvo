using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Logs an unhandled failure and answers it with Alvo's own problem document.
/// </summary>
/// <remarks>
/// Both halves matter and neither is sufficient. The <b>log</b> is why the endpoint layer deliberately does not
/// catch this family — a hand-made problem document built at the call site would lose the stack trace. The
/// <b>document</b> is #119: with only <c>AddProblemDetails()</c> the framework writes an RFC 9110 status-code
/// URI into <c>type</c>, so the one member an agent branches on stops being an Alvo classification.
/// </remarks>
internal sealed partial class AlvoExceptionHandler(ILogger<AlvoExceptionHandler> logger) : IExceptionHandler
{
    /// <inheritdoc/>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (!ClientDisconnected(httpContext, exception))
        {
            Failed(logger, exception, httpContext.Request.Method, httpContext.Request.Path.Value);
        }

        await ProblemResultFactory.Internal().ExecuteAsync(httpContext).ConfigureAwait(false);
        return true;
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
}
