namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The refusal every framework store's write meets inside an open transaction unit, at the one place they all pass — the
/// database's write gate.
/// </summary>
/// <remarks>
/// <para>
/// <c>IOutboxStore</c>, <c>IDeliveryStore</c>, the secret, descriptor-version, runtime-schema and management-idempotency
/// stores are public and DI-registered, so host code can reach them. Inside a unit such a write would run on its own
/// connection: on SQLite it would wait for the unit's own write turn and then commit beside the unit after its deadline;
/// on PostgreSQL it would commit beside the unit at once. Refusing at the gate fails closed, on every engine, for every
/// store write that passes the relational write gate — the six named here, and any later one that waits there. A store
/// whose write runs work of its own before the gate (the descriptor-version store and the runtime-schema writer ensure
/// their tables first) refuses as its first statement too. A unit takes its own turn before it is on the flow, and the
/// writes inside it never ask again, so the refusal never meets a unit's own work. A store's reads take no turn and are
/// not refused: they commit nothing.
/// </para>
/// <para>
/// <b>Outside its reach:</b> a write that never passes this gate. <c>MMLib.Alvo.Identity</c>'s writes
/// (<c>AlvoIdentityUnitOfWork</c>) run on their own connection and transaction without it, so inside a unit they are not
/// refused here: on SQLite they wait for the unit's turn until the busy timeout, on PostgreSQL they commit beside the unit.
/// </para>
/// </remarks>
internal static class StoreWrites
{
    /// <summary>The refused call, as the refusal names it.</summary>
    internal const string Call =
        "A framework store's write (IOutboxStore, IDeliveryStore, or a secret, descriptor-version, runtime-schema or management-idempotency store)";

    /// <summary>Refuses a store write made while a unit is open on this flow, and fails the unit.</summary>
    internal static void RefuseInsideUnit() => AlvoFlow.RefuseInsideUnit(Call);
}
