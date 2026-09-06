using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Which failures the #119 handler claims, and which of the claimed ones it records at
/// <see cref="LogLevel.Error"/> — the level an on-call rotation is paged by, so what it does <em>not</em>
/// record is as much of a contract as what it does.
/// </summary>
/// <remarks>
/// <para>
/// A client that hangs up mid-request is not a defect in Alvo. <c>ExceptionHandlerMiddleware</c> already
/// suppresses its own diagnostics for an aborted request, but it still invokes registered handlers, so
/// behind an ingress every closed tab and every proxy timeout would otherwise arrive as an Alvo error with a
/// stack trace. Three of the facts here are that predicate's three corners; a handler that skipped on either
/// half alone would fail one of the two controls.
/// </para>
/// <para>
/// The rest are the ownership rule, at the seam itself: an endpoint carrying
/// <see cref="DataApiOperationMetadata"/> is Alvo's, anything else is the host's, and a client's own mistake
/// is neither party's invariant. <see cref="AlvoExceptionHandlerScopeTests"/> measures the same rule over a
/// running pipeline, where the consequence — whether the <em>host's</em> handler gets to run — is visible.
/// </para>
/// </remarks>
public class AlvoExceptionHandlerTests
{
    [Fact]
    public async Task A_cancellation_from_a_caller_that_hung_up_is_not_logged_as_an_error()
    {
        var logs = new RecordingLoggerProvider();
        var context = Context(aborted: true, _alvoEndpoint);

        var handled = await Handle(logs, context, new OperationCanceledException());

        handled.ShouldBeTrue("the caller is gone, but the pipeline still needs the failure marked handled");
        logs.Records.ShouldBeEmpty(
            "a disconnected client is not a broken invariant, and paging someone for one trains them to "
            + "ignore the level that means Alvo really failed");
    }

    /// <summary>
    /// The first control: a cancellation whose request was <em>not</em> aborted is Alvo cancelling its own
    /// work, which is a real bug and stays logged.
    /// </summary>
    [Fact]
    public async Task A_cancellation_on_a_live_request_is_still_logged_as_an_error()
    {
        var logs = new RecordingLoggerProvider();
        var context = Context(aborted: false, _alvoEndpoint);

        await Handle(logs, context, new OperationCanceledException());

        logs.Records.ShouldContain(LogLevel.Error);
    }

    /// <summary>
    /// The second control: an abort that surfaced as something other than a cancellation is a real failure
    /// that merely raced the disconnect, and losing it would be the worse trade.
    /// </summary>
    [Fact]
    public async Task Another_failure_on_an_aborted_request_is_still_logged_as_an_error()
    {
        var logs = new RecordingLoggerProvider();
        var context = Context(aborted: true, _alvoEndpoint);

        await Handle(logs, context, new InvalidOperationException("a broken invariant"));

        logs.Records.ShouldContain(LogLevel.Error);
    }

    /// <summary>
    /// A failure on an endpoint Alvo did not generate is declined, and Alvo says nothing about it.
    /// </summary>
    /// <remarks>
    /// Both halves are the fix for one defect. Declining is what lets the host's own
    /// <see cref="IExceptionHandler"/> run; staying silent is the other half, because a framework writing an
    /// <see cref="LogLevel.Error"/> record with a stack trace about someone else's endpoint is noise that
    /// looks exactly like its own failure.
    /// </remarks>
    [Fact]
    public async Task A_failure_on_an_endpoint_alvo_did_not_generate_is_declined()
    {
        var logs = new RecordingLoggerProvider();
        var context = Context(aborted: false, endpoint: _hostEndpoint);

        var handled = await Handle(logs, context, new InvalidOperationException("the host's own bug"));

        handled.ShouldBeFalse(
            "Alvo must decline a host endpoint's failure, or an IExceptionHandler the host registered after "
            + "AddAlvoProblemDetails() never runs — for its own endpoints either");
        logs.Records.ShouldBeEmpty();
        context.Response.StatusCode.ShouldBe(
            StatusCodes.Status200OK, "declining means writing nothing at all, not writing a 500 and saying no");
    }

    /// <summary>
    /// A failure with no endpoint at all — raised before routing matched — is nobody's endpoint, so it is
    /// declined by the same rule rather than by a second one.
    /// </summary>
    [Fact]
    public async Task A_failure_before_routing_matched_anything_is_declined()
    {
        var logs = new RecordingLoggerProvider();
        var context = Context(aborted: false, endpoint: null);

        var handled = await Handle(logs, context, new InvalidOperationException("a middleware's own bug"));

        handled.ShouldBeFalse();
        logs.Records.ShouldBeEmpty();
    }

    /// <summary>
    /// A request the web server refused is answered at <em>its</em> status, not flattened to 500 — and it is
    /// not logged at the level an operator is paged by.
    /// </summary>
    /// <remarks>
    /// <c>BadHttpRequestException</c> is what a body over the server's limit, an upload the client truncated,
    /// or a body arriving too slowly raises. None of them is one of <c>IAlvoData</c>'s five families, so a
    /// handler that answered every exception with <c>alvo.dev/errors/internal</c> told the caller that "an
    /// invariant Alvo itself relies on is broken" — inviting a retry that can never succeed — while paging an
    /// operator with a stack trace for a client-side mistake. The record is kept at
    /// <see cref="LogLevel.Warning"/>: a limit being hit repeatedly is worth noticing, at the level an
    /// operator reads rather than the one that wakes them.
    /// </remarks>
    [Fact]
    public async Task A_request_the_server_would_not_read_answers_its_own_status()
    {
        var logs = new RecordingLoggerProvider();
        var context = Context(aborted: false, endpoint: _alvoEndpoint);

        var handled = await Handle(
            logs, context, new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge));

        handled.ShouldBeTrue("the request failed on Alvo's own endpoint, so Alvo answers it");
        context.Response.StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);
        logs.Records.ShouldNotContain(
            LogLevel.Error,
            "a caller's own mistake must not page whoever operates the instance, and a stack trace of Alvo's "
            + "internals says nothing about it anyway");
        logs.Records.ShouldContain(LogLevel.Warning);
    }

    /// <summary>
    /// Invokes the handler exactly the way <c>ExceptionHandlerMiddleware</c> does: the matched endpoint
    /// recorded onto <see cref="IExceptionHandlerFeature"/>, and then cleared off the context.
    /// </summary>
    /// <remarks>
    /// The clearing is not decoration. <c>ClearHttpContext</c> runs before any handler is invoked, so
    /// <c>httpContext.GetEndpoint()</c> is always <see langword="null"/> in production and a handler reading
    /// it would decline everything, forever, with every fact still green. Emulating the middleware here is
    /// what makes these facts measure the shape the handler is actually handed.
    /// </remarks>
    private static ValueTask<bool> Handle(RecordingLoggerProvider logs, HttpContext context, Exception exception)
    {
        context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature
        {
            Error = exception,
            Path = context.Request.Path.Value!,
            Endpoint = context.GetEndpoint(),
        });
        context.SetEndpoint(null);

        using var factory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(logs);
        });

        var handler = new AlvoExceptionHandler(factory.CreateLogger<AlvoExceptionHandler>());

        return handler.TryHandleAsync(context, exception, CancellationToken.None);
    }

    /// <summary>One of Alvo's generated routes, identified the way the handler identifies one.</summary>
    private static readonly Endpoint _alvoEndpoint = new(
        requestDelegate: null,
        new EndpointMetadataCollection(new DataApiOperationMetadata("owners", DataApiEndpointKind.List)),
        "GET /api/owners");

    /// <summary>An endpoint the host mapped itself, which carries no marker of Alvo's.</summary>
    private static readonly Endpoint _hostEndpoint = new(
        requestDelegate: null, EndpointMetadataCollection.Empty, "GET /the-hosts-own");

    /// <summary>A context whose response can be written and whose connection is, or is not, still there.</summary>
    /// <param name="aborted">Whether the caller has hung up.</param>
    /// <param name="endpoint">The endpoint the request matched, or <see langword="null"/> for none.</param>
    private static DefaultHttpContext Context(bool aborted, Endpoint? endpoint)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };

        context.Request.Method = HttpMethod.Get.Method;
        context.Request.Path = "/api/owners";
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(endpoint);

        if (aborted)
        {
            context.RequestAborted = new CancellationToken(canceled: true);
        }

        return context;
    }

    /// <summary>Every level the handler wrote at, which is the whole of what these facts read.</summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogLevel> _records = [];

        internal IReadOnlyList<LogLevel> Records
        {
            get
            {
                lock (_records)
                {
                    return [.. _records];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

        public void Dispose()
        {
        }

        private void Record(LogLevel level)
        {
            lock (_records)
            {
                _records.Add(level);
            }
        }

        private sealed class RecordingLogger(RecordingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) => owner.Record(logLevel);
        }
    }
}
