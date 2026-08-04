using Microsoft.Extensions.Logging;

namespace MMLib.Alvo.Tests;

/// <summary>
/// The narrowest <see cref="ILogger"/> that can answer <b>what a log line said</b> — it keeps the formatted
/// message and level of every entry, and <see cref="Warnings"/> is the warning-shaped view of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hand-written rather than substituted.</b> <see cref="ILogger.Log{TState}"/> is generic over the state
/// and takes the formatter as a delegate, so a mocking framework can record the call but not the rendered
/// message without re-implementing exactly this — and the rendered message is the whole assertion. "A warning
/// was logged" passes on any wording, including one that named the wrong blocks or none.
/// </para>
/// <para>
/// <b>It is its own <see cref="ILoggerProvider"/>, which is what lets a fact reach a warning written through
/// an injected <c>ILogger&lt;T&gt;</c>.</b> A type under test that takes <c>ILogger&lt;SomeService&gt;</c>
/// cannot be handed this directly, and <c>NullLogger&lt;T&gt;.Instance</c> — what
/// <c>SchemaMigrationRunnerTests</c> passes everywhere else — cannot observe a warning by construction. Adding
/// this as a provider to a real <see cref="LoggerFactory"/> and asking it for
/// <c>CreateLogger&lt;SomeService&gt;()</c> exercises the pipeline the host actually uses, including the
/// <c>LoggerMessage</c> source-generated delegates, rather than the <see cref="ILogger"/> seam alone.
/// </para>
/// <para>
/// Every category writes into one list, deliberately: a fact that has to say which category a line came from
/// is asserting on the framework's own plumbing rather than on the message, and the runs that use this
/// resolve exactly one logger.
/// </para>
/// </remarks>
internal sealed class CapturingLogger : ILogger, ILoggerProvider
{
    private readonly List<CapturedLogEntry> _entries = [];

    /// <summary>Every entry written through this logger, in order.</summary>
    internal IReadOnlyList<CapturedLogEntry> Entries => _entries;

    /// <summary>The formatted message of every warning written through this logger, in order.</summary>
    internal IReadOnlyList<string> Warnings =>
        [.. _entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message)];

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
        _entries.Add(new CapturedLogEntry(logLevel, formatter(state, exception), exception));
    }

    /// <summary>
    /// Releases nothing. The captured messages are the point of this double and have to outlive the
    /// <see cref="LoggerFactory"/> that disposes its providers, because the assertions read them after the run.
    /// </summary>
    public void Dispose()
    {
        // Intentionally empty — see the summary.
    }
}

/// <summary>One captured log entry: the level, the rendered message, and the exception it carried.</summary>
/// <param name="Level">The level the entry was written at.</param>
/// <param name="Message">The formatted message, exactly as the pipeline rendered it.</param>
/// <param name="Exception">The exception the entry carried, when it carried one.</param>
internal sealed record CapturedLogEntry(LogLevel Level, string Message, Exception? Exception);
