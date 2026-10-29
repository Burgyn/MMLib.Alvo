using Microsoft.Extensions.Logging;

using System.Collections.Concurrent;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>Keeps every <c>ManagementAudit</c> line a host logs at Warning (spec 2026-10-14 E11), and nothing else.</summary>
internal sealed class AuditCapture : ILoggerProvider, ILogger
{
    private readonly ConcurrentQueue<string> _lines = new();

    internal IReadOnlyList<string> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (eventId.Name == "ManagementAudit" && logLevel == LogLevel.Warning)
        {
            _lines.Enqueue(formatter(state, exception));
        }
    }

    public void Dispose()
    {
    }
}
