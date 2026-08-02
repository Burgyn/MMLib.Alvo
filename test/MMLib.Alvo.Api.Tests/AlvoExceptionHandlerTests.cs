using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Which failures the #119 handler records at <see cref="LogLevel.Error"/> — the level an on-call rotation
/// is paged by, so what it does <em>not</em> record is as much of a contract as what it does.
/// </summary>
/// <remarks>
/// A client that hangs up mid-request is not a defect in Alvo. <c>ExceptionHandlerMiddleware</c> already
/// suppresses its own diagnostics for an aborted request, but it still invokes registered handlers, so
/// behind an ingress every closed tab and every proxy timeout would otherwise arrive as an Alvo error with a
/// stack trace. The three facts here are the predicate's three corners; a handler that skipped on either
/// half alone would fail one of the two controls.
/// </remarks>
public class AlvoExceptionHandlerTests
{
    [Fact]
    public async Task A_cancellation_from_a_caller_that_hung_up_is_not_logged_as_an_error()
    {
        var logs = new RecordingLoggerProvider();
        var context = Context(aborted: true);

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
        var context = Context(aborted: false);

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
        var context = Context(aborted: true);

        await Handle(logs, context, new InvalidOperationException("a broken invariant"));

        logs.Records.ShouldContain(LogLevel.Error);
    }

    private static ValueTask<bool> Handle(RecordingLoggerProvider logs, HttpContext context, Exception exception)
    {
        using var factory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(logs);
        });

        var handler = new AlvoExceptionHandler(factory.CreateLogger<AlvoExceptionHandler>());

        return handler.TryHandleAsync(context, exception, CancellationToken.None);
    }

    /// <summary>A context whose response can be written and whose connection is, or is not, still there.</summary>
    /// <param name="aborted">Whether the caller has hung up.</param>
    private static DefaultHttpContext Context(bool aborted)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };

        context.Request.Method = HttpMethod.Get.Method;
        context.Request.Path = "/api/owners";
        context.Response.Body = new MemoryStream();

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
