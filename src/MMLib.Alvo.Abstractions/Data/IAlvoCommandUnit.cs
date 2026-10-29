using MMLib.Alvo.Events;

namespace MMLib.Alvo.Data;

/// <summary>
/// What a command needs from its transaction unit besides the host's own calls: its completion event and its key's commit
/// mark, written in the unit so they commit or roll back with the command's writes. Implemented by the EF driver's
/// <c>IAlvoTransaction</c>; never exposed to the host.
/// </summary>
internal interface IAlvoCommandUnit
{
    /// <summary>Appends a framework event to the outbox in the unit's transaction.</summary>
    Task PublishFrameworkAsync(AlvoEvent @event, CancellationToken cancellationToken);

    /// <summary>
    /// Marks <paramref name="key"/> committed in the unit's transaction, only while the run holds its claim
    /// (<see cref="CommandKey.Token"/>); otherwise throws <see cref="CommandClaimLostException"/> and poisons the unit, so
    /// the run's writes roll back rather than commit beside the run that took the key over.
    /// </summary>
    Task CommitCommandAsync(CommandKey key, DateTimeOffset now, CancellationToken cancellationToken);
}
