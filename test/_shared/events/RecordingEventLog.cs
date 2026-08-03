using Microsoft.Extensions.Logging;

using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Testing.Events;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// Keeps the name and the rendered message of every line the run logged, and exposes the execution-log half of
/// it: the entries <see cref="EventLog.ActionExecuted"/> wrote.
/// </summary>
/// <remarks>
/// <para>
/// <b>The filter is <c>nameof(EventLog.ActionExecuted)</c>, not a string.</b> A source-generated
/// <c>LoggerMessage</c> names its <see cref="EventId"/> after the method that declares it, so the criterion's
/// "one entry per executed action" is anchored to the method the executor calls rather than to a spelling that
/// could drift away from it.
/// </para>
/// <para>
/// <b>It records every level, and the world sets the minimum to <c>Trace</c>.</b> A level filter that dropped
/// the entry would make "no execution-log entry" true for the wrong reason — the loudest possible way for an
/// absence criterion to pass vacuously.
/// </para>
/// </remarks>
internal sealed class RecordingEventLog : ILogger, ILoggerProvider
{
    /// <summary>The execution log: one entry per action that ran, and nothing else the run logged.</summary>
    internal IReadOnlyList<AlvoEventLogEntry> ActionLogEntries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Where(entry => string.Equals(entry.Name, _actionExecuted, StringComparison.Ordinal))];
            }
        }
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => this;

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc/>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        lock (_gate)
        {
            _entries.Add(new AlvoEventLogEntry(eventId.Name ?? string.Empty, formatter(state, exception)));
        }
    }

    /// <summary>
    /// Releases nothing: the captured entries are the point and have to outlive the logger factory that
    /// disposes its providers, because every assertion reads them after the run.
    /// </summary>
    public void Dispose()
    {
        // Intentionally empty — see the summary.
    }

    private static readonly string _actionExecuted = nameof(EventLog.ActionExecuted);

    private readonly List<AlvoEventLogEntry> _entries = [];

    private readonly object _gate = new();
}
