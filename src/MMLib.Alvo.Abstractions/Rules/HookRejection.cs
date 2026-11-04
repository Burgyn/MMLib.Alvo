namespace MMLib.Alvo.Rules;

/// <summary>One recorded rejection by a host write hook: ids and the hook's code only — no payload, no message.</summary>
/// <param name="At">When the hook rejected.</param>
/// <param name="Extension">The hook's id.</param>
/// <param name="Entity">The entity written.</param>
/// <param name="Operation">The write, as <c>create</c>, <c>update</c>, <c>replace</c> or <c>delete</c>.</param>
/// <param name="Kind">The kind: <c>invalid</c>, <c>conflict</c> or <c>forbidden</c>.</param>
/// <param name="Code">The hook's own code.</param>
/// <param name="Actor">The caller's identity, as <c>AlvoIdempotency.IdentityOf</c> spells it.</param>
internal sealed record HookRejection(
    DateTimeOffset At, string Extension, string Entity, string Operation, string Kind, string Code, string Actor);
