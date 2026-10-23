using Microsoft.Extensions.Logging;

using System.Collections.Concurrent;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>Keeps every exception a host logged, so a fact can read why an attempt failed.</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<Exception> _exceptions = new();

    /// <summary>Gets every exception logged, in order.</summary>
    internal IReadOnlyList<Exception> Exceptions => [.. _exceptions];

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new Recorder(_exceptions);

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    private sealed class Recorder(ConcurrentQueue<Exception> exceptions) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                exceptions.Enqueue(exception);
            }
        }
    }
}
