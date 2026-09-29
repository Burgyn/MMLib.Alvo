using Microsoft.Extensions.Logging;

using System.ClientModel;
using System.Globalization;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>
/// The assistant's logger for one eval turn: it keeps the provider's status code from a failed turn, and nothing else.
/// </summary>
/// <remarks>
/// A failed turn's sentence ("The AI endpoint did not answer…") cannot tell a wrong model name from an expired key, and
/// a maintainer calibrating a new endpoint needs to. The exception's message is deliberately not kept: a provider's
/// error routinely echoes the request — a base address, a header, sometimes the key — and this output is published.
/// </remarks>
internal sealed class ProviderStatusLogger : ILogger<AlvoAssistant>
{
    internal const string NoResponse = "none";

    /// <summary>The status of the last failure logged, <see cref="NoResponse"/> for one without, or <see langword="null"/>.</summary>
    internal string? Status { get; private set; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (exception is not null)
        {
            Status = StatusOf(exception);
        }
    }

    internal static string StatusOf(Exception failure) =>
        failure is ClientResultException { Status: > 0 } answered
            ? answered.Status.ToString(CultureInfo.InvariantCulture)
            : NoResponse;
}
