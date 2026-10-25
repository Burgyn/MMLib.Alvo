using Microsoft.Extensions.Logging;

using MMLib.Alvo.Events;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// The <c>ManagementAudit</c> log event (spec E11): who made which operation on what, how much it changed, and why —
/// written once per committed change, beside the operations log row the store wrote in the change's own transaction.
/// </summary>
/// <remarks>
/// Warning, so it survives a production log level that drops information. Ids and the operator's own one-line reason only —
/// never a payload, a URL or an exception message.
/// </remarks>
internal static partial class OperationsAudit
{
    internal static void Write(ILogger logger, DeliveryOperationEntry entry) =>
        ManagementAudit(logger, entry.Actor, entry.Operation, entry.Target, entry.Affected ?? 0, entry.Reason ?? string.Empty);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Management audit: {Actor} made '{Operation}' on '{Target}' ({Affected} changed). Reason: {Reason}")]
    private static partial void ManagementAudit(ILogger logger, string actor, string operation, string target, int affected, string reason);
}
