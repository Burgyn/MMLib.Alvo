namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The refusal every framework store's write meets inside an open transaction unit, at the one place they all pass — the
/// database's write gate.
/// </summary>
/// <remarks>
/// <c>IOutboxStore</c>, <c>IDeliveryStore</c>, the secret, descriptor-version, runtime-schema and management-idempotency
/// stores are public and DI-registered, so host code can reach them. Inside a unit such a write would run on its own
/// connection: on SQLite it would wait for the unit's own write turn and then commit beside the unit after its deadline;
/// on PostgreSQL it would commit beside the unit at once. Refusing at the gate fails closed for every store that writes,
/// present and future, on every engine. A unit takes its own turn before it is on the flow, and the writes inside it never
/// ask again, so the refusal never meets a unit's own work. A store's reads take no turn and are not refused: they commit
/// nothing.
/// </remarks>
internal static class StoreWrites
{
    /// <summary>The refused call, as the refusal names it.</summary>
    internal const string Call =
        "A framework store's write (IOutboxStore, IDeliveryStore, or a secret, descriptor-version, runtime-schema or management-idempotency store)";

    /// <summary>Refuses a store write made while a unit is open on this flow, and fails the unit.</summary>
    internal static void RefuseInsideUnit() => AlvoFlow.RefuseInsideUnit(Call);
}
