using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// A command's completion event: <c>alvo.command.&lt;name&gt;.completed</c>, about the bound row or the command, carrying
/// only its name and the row it acted on — never its input or its answer (Ruling 10).
/// </summary>
internal sealed class CommandCompletion(TimeProvider time)
{
    /// <summary>The completion event of <paramref name="invocation"/>, minted now as its caller.</summary>
    internal AlvoEvent EventOf(CommandInvocation invocation) => AlvoFrameworkEvent.Create(
        invocation.Command.EventType, invocation.Command.Subject(invocation.RowId), Payload(invocation), invocation.Caller, time.GetUtcNow());

    /// <summary>Writes the completion and the key's commit mark into <paramref name="unit"/>, as its last calls.</summary>
    /// <exception cref="InvalidOperationException">The unit is not one of Alvo's own.</exception>
    internal async Task WriteInUnitAsync(CommandInvocation invocation, IAlvoTransaction unit, CancellationToken ct)
    {
        if (unit is not IAlvoCommandUnit framework)
        {
            throw new InvalidOperationException(
                "A command's unit must come from Alvo's own IAlvoTransactions: this one cannot carry the command's completion event and idempotency record.");
        }

        await framework.PublishFrameworkAsync(EventOf(invocation), ct).ConfigureAwait(false);
        if (invocation.Key is { } key)
        {
            await framework.CommitCommandAsync(key, time.GetUtcNow(), ct).ConfigureAwait(false);
        }
    }

    private static Dictionary<string, object?> Payload(CommandInvocation invocation)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal) { ["command"] = invocation.Command.Id };
        if (invocation.Command.Entity is { } entity && invocation.RowId is { } id)
        {
            payload["entity"] = entity;
            payload["id"] = id;
        }

        return payload;
    }
}
