using Microsoft.Extensions.Logging;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>The write hooks' log lines: ids, reasons and exception types — never a value, a payload or a host's message.</summary>
internal static partial class HookLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Write hook {Extension} failed ({Reason}) with {ExceptionType}; the write was rolled back.")]
    internal static partial void HookFailed(ILogger logger, string extension, string reason, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Write hook {Extension} set {Field}, which it may not set ({Problem}); the write was rolled back.")]
    internal static partial void HookSetRefused(ILogger logger, string extension, string field, string problem);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Write hook {Extension} acted as the system in tenant {Tenant} on {Entity} ({Operation}) for {Actor}, correlation {CorrelationId}.")]
    internal static partial void ExtensionSystemAccess(
        ILogger logger, string extension, string tenant, string entity, string operation, string actor, string correlationId);
}
