using Microsoft.Extensions.Logging;

namespace MMLib.Alvo.Reads.Internal;

/// <summary>The read extensions' log lines: ids, reasons, field names and exception types — never a value or a host's message.</summary>
internal static partial class ReadLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Read extension {Extension} failed ({Reason}) with {ExceptionType}; the read answered nothing.")]
    internal static partial void ReadExtensionFailed(ILogger logger, string extension, string reason, string exceptionType);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Narrowing {Extension} filters by {Field}, which the policy hides from some callers: which rows survive tells them something of it.")]
    internal static partial void NarrowingNamesHiddenField(ILogger logger, string extension, string field);
}
