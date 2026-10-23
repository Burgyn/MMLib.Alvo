namespace MMLib.Alvo.Events;

/// <summary>What one <see cref="IAlvoEventPump.DrainAsync"/> round did.</summary>
public sealed record AlvoDrainResult
{
    /// <summary>Gets how many work items the round claimed across every dispatcher; <c>0</c> means nothing was due.</summary>
    public required int Claimed { get; init; }

    /// <summary>
    /// Gets how many claimed work items failed an attempt and were handed back for a later round — one per work item,
    /// however many of its subscribers it had reached.
    /// </summary>
    public required int Failed { get; init; }

    /// <summary>
    /// Gets the subscribers the round attempted, each once, in the order they were first attempted — a subscriber that
    /// threw is listed too, and the work item it failed is counted once in <see cref="Failed"/>. A descriptor
    /// after-hook is named by its JSON pointer, such as <c>/entities/tickets/hooks/afterCreate/0</c>.
    /// </summary>
    public required IReadOnlyList<string> Subscribers { get; init; }

    /// <summary>Gets whether the round found nothing due.</summary>
    public bool IsQuiet => Claimed == 0;
}
