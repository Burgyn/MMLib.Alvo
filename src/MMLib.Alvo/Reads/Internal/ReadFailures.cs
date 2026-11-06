using Microsoft.Extensions.Logging;

using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Reads.Internal;

/// <summary>How a read extension fails a read: logged with its id, reason and exception type, and blamed on the unit it runs in.</summary>
internal static class ReadFailures
{
    internal static HookFailedException Failed(ILogger logger, string id, string reason, Exception? cause)
    {
        ReadLog.ReadExtensionFailed(logger, id, reason, cause?.GetType().FullName ?? "-");
        var failure = HookFailedException.OfRead([id], reason);
        AlvoFlow.Unit?.Blame(failure);
        return failure;
    }
}

/// <summary>The services a delegate read extension is handed: none — it takes no service.</summary>
internal sealed class EmptyServices : IServiceProvider
{
    internal static EmptyServices Instance { get; } = new();

    public object? GetService(Type serviceType) => null;
}
