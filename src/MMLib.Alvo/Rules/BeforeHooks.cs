using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Rules;

/// <summary>
/// One entity's compiled <c>before*</c> hooks, one list per hook point.
/// </summary>
/// <remarks>
/// <para>
/// <b>They ride the policy catalog for the reason <see cref="EntityAfterHooks"/> does</b>, and the reason
/// binds harder here: a before-hook runs inside the same write the rules are judging, so a hook compiled
/// against a different schema revision than the <c>WITH CHECK</c> predicate over the same candidate row would
/// be two views of one write disagreeing about what the row's fields are. One priming site makes that
/// unrepresentable rather than unlikely.
/// </para>
/// <para>
/// <b>The list is a pipeline, not a set of independent patches.</b> Hooks fire in declaration order and each
/// one sees the candidate as the hooks before it left it — so a second hook's <c>condition</c> can test a
/// field the first one mutated, and two hooks writing one field resolve last-writer-wins in the order an
/// author reads them in the descriptor. Evaluating every condition against the original candidate instead
/// would make the outcome depend on nothing the author can see.
/// </para>
/// </remarks>
/// <param name="BeforeCreate">The hooks declared under <c>hooks.beforeCreate</c>, in declaration order.</param>
/// <param name="BeforeUpdate">The hooks declared under <c>hooks.beforeUpdate</c>, in declaration order.</param>
/// <param name="BeforeDelete">The hooks declared under <c>hooks.beforeDelete</c>, in declaration order.</param>
internal sealed record EntityBeforeHooks(
    IReadOnlyList<CompiledBeforeHook> BeforeCreate,
    IReadOnlyList<CompiledBeforeHook> BeforeUpdate,
    IReadOnlyList<CompiledBeforeHook> BeforeDelete)
{
    /// <summary>The one instance every entity declaring no before-hook shares, so no consumer null-checks.</summary>
    internal static EntityBeforeHooks None { get; } = new([], [], []);

    /// <summary>The hooks one write operation runs, in declaration order.</summary>
    /// <remarks>
    /// A read operation selects none: the frozen schema declares no <c>beforeGet</c>/<c>beforeList</c> point,
    /// so the answer is "no hooks" rather than a throw on an operation this subsystem is not about — the same
    /// ruling <see cref="EntityAfterHooks.For"/> makes.
    /// </remarks>
    /// <param name="operation">The write being performed.</param>
    internal IReadOnlyList<CompiledBeforeHook> For(DataOperation operation) => operation switch
    {
        DataOperation.Create => BeforeCreate,
        DataOperation.Update => BeforeUpdate,
        DataOperation.Delete => BeforeDelete,
        _ => [],
    };
}

/// <summary>
/// One compiled before-hook: where it was declared, the condition that gates it, and exactly one of the two
/// actions the frozen schema allows in-transaction.
/// </summary>
/// <remarks>
/// <c>reject</c> and <c>mutate</c> are alternatives in the schema (<c>$defs/beforeHookList</c>'s
/// <c>oneOf</c>), so exactly one of <paramref name="Reject"/> and a non-empty
/// <paramref name="Mutations"/> is set on any hook this compiler produces — a hook that named both was
/// refused at apply, and a hook that named neither cannot be parsed at all.
/// </remarks>
/// <param name="Path">
/// The hook's own JSON pointer, such as <c>/entities/deals/hooks/beforeUpdate/0</c> — carried so a refusal
/// names the hook an author wrote rather than an index into a list they cannot see.
/// </param>
/// <param name="Condition">
/// The compiled <see cref="CelProfile.Condition"/> expression gating the hook, or <see langword="null"/> when
/// it declares none and therefore always fires.
/// </param>
/// <param name="Reject">
/// The author's refusal text when this hook is a <c>reject</c>; <see langword="null"/> when it is a
/// <c>mutate</c>. It becomes the RFC 7807 <c>detail</c> a caller reads.
/// </param>
/// <param name="Mutations">The field patches this hook applies, in the order the descriptor declares them.</param>
internal sealed record CompiledBeforeHook(
    string Path,
    CompiledExpression? Condition,
    string? Reject,
    IReadOnlyList<CompiledMutation> Mutations);

/// <summary>
/// One field a <c>mutate</c> writes, with its value already resolved as far as apply time can resolve it:
/// a compiled <see cref="CelProfile.Mutate"/> expression, or a literal converted to the representation
/// <see cref="Field"/>'s own declared type holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>The literal is converted at apply and not at write time</b>, which is what makes a literal of the
/// wrong shape — <c>"mutate": {"mileage": "soon"}</c> — an authoring error rather than a per-request failure
/// inside a transaction. It is the same fail-fast rule the policy catalog applies to every rule it compiles.
/// </para>
/// <para>
/// <b>Both slots may legitimately carry <see langword="null"/>, and they mean different things.</b>
/// <see cref="Expression"/> is <see langword="null"/> on a literal mutation; <see cref="Value"/> is
/// <see langword="null"/> either because this is an expression mutation or because the author wrote a JSON
/// <c>null</c> literal, which is an authored intent to store nothing. Read <see cref="Expression"/> first —
/// it is the discriminator.
/// </para>
/// </remarks>
/// <param name="Field">The field this mutation writes; known to exist on the entity and to be caller-writable.</param>
/// <param name="Expression">The compiled value expression, or <see langword="null"/> when the value is a literal.</param>
/// <param name="Value">The literal value, already in the target field's own representation.</param>
internal sealed record CompiledMutation(string Field, CompiledExpression? Expression, object? Value);
