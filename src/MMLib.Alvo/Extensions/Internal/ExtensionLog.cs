using Microsoft.Extensions.Logging;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>What the extension subsystem logs — ids and locators, never a URL, a body or an exception message.</summary>
internal static partial class ExtensionLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Alvo found extensions stating one order {Order} on {Target}: {Ids}. They run in registration order; "
            + "give each its own Order if the order matters.")]
    internal static partial void ExtensionOrderTie(ILogger logger, string target, int order, string ids);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Alvo could not bind extension {Id} to the current schema ({Problems}). Every event it would "
            + "handle is recorded as a dead delivery (unbound) until the descriptor or the registration is fixed.")]
    internal static partial void ExtensionUnbound(ILogger logger, string id, string problems);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Alvo retired extension {Id} ({Retirement}): {Deliveries} unfinished deliveries were ended.")]
    internal static partial void ExtensionRetired(ILogger logger, string id, string retirement, int deliveries);
}
