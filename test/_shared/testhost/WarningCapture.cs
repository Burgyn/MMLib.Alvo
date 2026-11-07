using Microsoft.Extensions.Logging;

using System.Collections.Concurrent;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>Keeps every line a host logs at Warning under one event name, and nothing else.</summary>
internal sealed class WarningCapture(string eventName) : ILoggerProvider, ILogger
{
    private readonly ConcurrentQueue<string> _lines = new();

    internal IReadOnlyList<string> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (eventId.Name == eventName && logLevel == LogLevel.Warning)
        {
            _lines.Enqueue(formatter(state, exception));
        }
    }

    public void Dispose()
    {
    }
}
