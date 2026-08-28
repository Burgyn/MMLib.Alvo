using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Internal;
using MMLib.Alvo.Schema;
using System.Text.Json;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// Compiles one entity's <c>before*</c> hooks: the condition through <see cref="CelProfile.Condition"/>, every
/// <c>mutate</c> value through <see cref="CelProfile.Mutate"/> or converted from its JSON literal, and every
/// field a <c>mutate</c> names resolved against the entity's schema — all at <b>apply</b> time, into the
/// <see cref="PolicyCatalog"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything is resolved here so that nothing is resolved inside a transaction.</b> A before-hook runs
/// while the write holds its locks, and there is nobody to report an authoring mistake to at that point: a
/// mutate naming a field the entity does not declare would be one failed write per request, which an author
/// reads as a broken endpoint rather than as the typo it is. So an unknown field, an unresolvable CEL
/// reference, a value type the field cannot hold and a literal of the wrong shape are all refused when the
/// descriptor is applied — the same rule the policy catalog applies to every rule it compiles, and the
/// <c>alvo-security-core-review</c> checklist's "fail-fast compile" item.
/// </para>
/// <para>
/// <b>The three points differ in which row images exist, and that difference is enforced rather than
/// documented.</b> A create has no pre-image and a delete produces no post-image, so <c>old.</c> in a
/// <c>beforeCreate</c> and <c>new.</c> in a <c>beforeDelete</c> are references the phase cannot answer —
/// and an unanswerable reference resolves to <see langword="null"/>, which the interpreter's null rule
/// collapses <em>every</em> comparison against, including <c>!=</c>, so a condition written as "every row
/// except…" fires for every row. That is the identical failure <c>AfterHookCompiler</c> refuses
/// <c>@tenant.id</c> for, one phase earlier, and it is refused here for the same reason: silently, and not
/// always in the denying direction.
/// </para>
/// <para>
/// <b>A <c>mutate</c> under <c>beforeDelete</c> is refused outright</b>, because nothing writes it: the row is
/// being removed, so a patch against it is a slot that compiles cleanly and is then discarded — the exact
/// failure mode <see cref="Descriptor.Internal.UnhonouredFeatures.EmailData"/> was refused for, at an
/// implementation rate of zero.
/// </para>
/// <para>
/// <b>A <c>mutate</c> may not name a framework-managed column, and that one is a tenancy guard rather than
/// tidiness.</b> The verdict over the candidate's post-image (<c>WITH CHECK</c> plus the synthesized tenant
/// scope) is what stops a write placing a row in another tenant, and a hook that could rewrite
/// <c>tenant_id</c> would be doing so from inside the transaction, after the caller's own payload had already
/// been judged. Refusing the whole managed set — <c>id</c>, <c>tenant_id</c> and the audit columns — keeps the
/// rule one line instead of a per-column argument, and the audit columns are the framework's own record of who
/// wrote what, which a hook rewriting them would make a record of nothing.
/// </para>
/// </remarks>
internal static class BeforeHookCompiler
{
    /// <summary>Compiles the three <c>before*</c> lists an entity declares, appending every problem to the scope.</summary>
    /// <param name="hooks">The entity's declared hooks, or <see langword="null"/> when it declares none.</param>
    /// <param name="scope">The schema, compiler, pointer prefix and error accumulator.</param>
    /// <returns>
    /// The compiled hooks, or <see cref="EntityBeforeHooks.None"/> when the entity declares no before-hook at
    /// all. A hook that failed to compile is absent from the result <em>and</em> present in the errors, so a
    /// catalog is never built holding a half-compiled hook.
    /// </returns>
    internal static EntityBeforeHooks Compile(EntityHooks? hooks, BeforeHookScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (hooks is null)
        {
            return EntityBeforeHooks.None;
        }

        var create = CompilePoint(_beforeCreate, hooks.BeforeCreate, scope);
        var update = CompilePoint(_beforeUpdate, hooks.BeforeUpdate, scope);
        var delete = CompilePoint(_beforeDelete, hooks.BeforeDelete, scope);

        return create.Count + update.Count + delete.Count == 0
            ? EntityBeforeHooks.None
            : new EntityBeforeHooks(create, update, delete);
    }

    /// <summary>
    /// One hook point, described by the two row images its phase actually has — which is what decides both
    /// the references a condition may read and whether a <c>mutate</c> has anywhere to land.
    /// </summary>
    /// <param name="Point">The point's key under <c>hooks</c>, as the schema spells it.</param>
    /// <param name="HasPreImage">Whether a row exists before the write, so <c>old.</c> can be answered.</param>
    /// <param name="HasPostImage">Whether the write produces a row, so <c>new.</c> and a <c>mutate</c> can.</param>
    private sealed record BeforeHookPoint(string Point, bool HasPreImage, bool HasPostImage);

    private static readonly BeforeHookPoint _beforeCreate = new("beforeCreate", HasPreImage: false, HasPostImage: true);

    private static readonly BeforeHookPoint _beforeUpdate = new("beforeUpdate", HasPreImage: true, HasPostImage: true);

    private static readonly BeforeHookPoint _beforeDelete = new("beforeDelete", HasPreImage: true, HasPostImage: false);

    private const string ConditionSlot = "condition";
    private const string MutateSlot = "mutate";

    private static List<CompiledBeforeHook> CompilePoint(
        BeforeHookPoint point, IReadOnlyList<BeforeHook>? declared, BeforeHookScope scope)
    {
        if (declared is null || declared.Count == 0)
        {
            return [];
        }

        var compiled = new List<CompiledBeforeHook>(declared.Count);
        for (var index = 0; index < declared.Count; index++)
        {
            var path = $"{scope.EntityPath}/hooks/{point.Point}/{index}";
            var hook = CompileHook(declared[index], point, path, scope);
            if (hook is not null)
            {
                compiled.Add(hook);
            }
        }

        return compiled;
    }

    private static CompiledBeforeHook? CompileHook(
        BeforeHook hook, BeforeHookPoint point, string path, BeforeHookScope scope)
    {
        var condition = CompileCondition(hook.Condition, point, $"{path}/{ConditionSlot}", scope);
        if (hook.Condition is not null && condition is null)
        {
            return null;
        }

        return CompileAction(hook.Action, point, path, scope) is { } action
            ? new CompiledBeforeHook(path, condition, action.Reject, action.Mutations)
            : null;
    }

    /// <summary>
    /// Compiles a hook condition in the <see cref="CelProfile.Condition"/> profile — the profile a hook
    /// condition is written in, and the only one where <c>old.</c>, <c>new.</c> and <c>changed(field)</c> are
    /// legal at all — and then refuses the references <em>this phase</em> cannot answer.
    /// </summary>
    /// <remarks>
    /// <c>@user</c> and <c>@tenant</c> are not refused here, unlike in an after-hook condition: a before-hook
    /// runs inside the request, so there is a real caller for both to resolve against. That asymmetry is stated
    /// at <c>AfterHookCompiler.HonoursTheEnvelope</c>, which is where the refusal that does not apply here
    /// lives.
    /// </remarks>
    private static CompiledExpression? CompileCondition(
        string? source, BeforeHookPoint point, string path, BeforeHookScope scope)
    {
        if (source is null)
        {
            return null;
        }

        var result = scope.Compiler.Compile(source, CelProfile.Condition, scope.Schema);
        if (!result.IsSuccess)
        {
            scope.Errors.AddRange(result.Errors.Select(error => Error(path, error.Message, error.FixSuggestion)));
            return null;
        }

        return HonoursThePhase(result.Expression!, point, path, scope) ? result.Expression : null;
    }

    /// <summary>
    /// Refuses a reference to a row image the hook's own phase does not have, by name — the phase-level
    /// counterpart to the envelope refusal an after-hook condition gets.
    /// </summary>
    private static bool HonoursThePhase(
        CompiledExpression expression, BeforeHookPoint point, string path, BeforeHookScope scope)
    {
        var refusals = Unanswerable(expression.Root, point).ToList();
        scope.Errors.AddRange(refusals.Select(refusal => Error(path, refusal.Message, refusal.Fix)));

        return refusals.Count == 0;
    }

    private static IEnumerable<PhaseRefusal> Unanswerable(CelNode root, BeforeHookPoint point)
    {
        if (!point.HasPreImage && Reads(root, CelRecordState.Old))
        {
            yield return NoPreImage(point);
        }

        if (!point.HasPostImage && Reads(root, CelRecordState.New))
        {
            yield return NoPostImage(point);
        }

        if (!(point.HasPreImage && point.HasPostImage) && ReadsChanged(root))
        {
            yield return NoComparison(point);
        }
    }

    private static PhaseRefusal NoPreImage(BeforeHookPoint point) => new(
        $"This '{point.Point}' expression reads 'old.<field>', which a create cannot answer: there is no row "
        + "before a create, so the reference resolves to null and every comparison against it — including "
        + "'!=' — collapses to false, silently and not always in the denying direction.",
        "Read the field without a qualifier, or as 'new.<field>', which on a create is the row being written.");

    private static PhaseRefusal NoPostImage(BeforeHookPoint point) => new(
        $"This '{point.Point}' expression reads 'new.<field>', which a delete cannot answer: a delete produces "
        + "no row, so the reference resolves to null and every comparison against it — including '!=' — "
        + "collapses to false, silently and not always in the denying direction.",
        "Read the field without a qualifier, or as 'old.<field>', which on a delete is the row being removed.");

    private static PhaseRefusal NoComparison(BeforeHookPoint point) => new(
        $"This '{point.Point}' expression calls 'changed(<field>)', which compares the row before the write "
        + "with the row after it. This phase has only one of the two, so the call answers from a missing "
        + "image rather than from a change.",
        "Compare the field you have — 'old.<field>' on a delete, 'new.<field>' or the bare field name on a "
        + "create — or move the hook to 'beforeUpdate', which is the phase a change exists in.");

    /// <summary>One refusal about a row image the phase does not have.</summary>
    /// <param name="Message">What cannot be answered, and what happens instead.</param>
    /// <param name="Fix">The reference to use instead.</param>
    private sealed record PhaseRefusal(string Message, string Fix);

    private static bool Reads(CelNode node, CelRecordState state) =>
        (node is CelFieldRef fieldRef && fieldRef.State == state)
        || CelTree.Children(node).Any(child => Reads(child, state));

    private static bool ReadsChanged(CelNode node) =>
        node is CelChanged || CelTree.Children(node).Any(ReadsChanged);

    /// <summary>
    /// Compiles the one action a before-hook carries. The schema's <c>oneOf</c> makes <c>reject</c> and
    /// <c>mutate</c> alternatives, so a hook that named both or neither is refused here rather than compiled
    /// into a shape whose meaning depends on which slot a consumer reads first.
    /// </summary>
    private static BeforeAction? CompileAction(
        BeforeHookAction action, BeforeHookPoint point, string path, BeforeHookScope scope) =>
        (action.Reject, action.Mutate) switch
        {
            ({ } reject, null) => new BeforeAction(reject, []),
            (null, { } mutate) => CompileMutate(mutate, point, path, scope),
            _ => Refuse(action, path, scope),
        };

    /// <summary>One before-hook action, as this compiler carries it between helpers.</summary>
    /// <param name="Reject">The refusal text, or <see langword="null"/> for a mutate.</param>
    /// <param name="Mutations">The compiled field patches, empty for a reject.</param>
    private sealed record BeforeAction(string? Reject, IReadOnlyList<CompiledMutation> Mutations);

    private static BeforeAction? Refuse(BeforeHookAction action, string path, BeforeHookScope scope)
    {
        var both = action.Reject is not null && action.Mutate is not null;
        scope.Errors.Add(Error(
            $"{path}/action",
            both
                ? "This hook declares both 'reject' and 'mutate'. They are alternatives — a rejected write has "
                + "no payload left to patch — so which one ran would depend on the order a consumer read them in."
                : "This hook's action declares neither 'reject' nor 'mutate', so it would run and do nothing.",
            both
                ? "Keep one. Two hooks in one list, the reject first, expresses \"refuse these writes and patch "
                + "the rest\" unambiguously."
                : "Add a 'reject' text or a 'mutate' patch. These are the only two actions the schema allows "
                + "in-transaction."));

        return null;
    }

    private static BeforeAction? CompileMutate(
        IReadOnlyDictionary<string, ValueOrExpr> mutate, BeforeHookPoint point, string path, BeforeHookScope scope)
    {
        if (!point.HasPostImage)
        {
            scope.Errors.Add(Error($"{path}/action/{MutateSlot}", NoRowToPatch(point), NoRowToPatchFix));
            return null;
        }

        var mutations = mutate
            .Select(pair => CompileMutation(pair.Key, pair.Value, point, $"{path}/action/{MutateSlot}", scope))
            .ToList();

        return mutations.Any(mutation => mutation is null)
            ? null
            : new BeforeAction(null, [.. mutations.OfType<CompiledMutation>()]);
    }

    private static string NoRowToPatch(BeforeHookPoint point) =>
        $"A '{point.Point}' hook cannot 'mutate': the row is being removed, so there is no payload to patch and "
        + "the values declared here would be compiled, validated and then read by nothing — a hook that looks "
        + "like it maintains a field and silently does not.";

    private const string NoRowToPatchFix =
        "Use 'reject' to refuse the delete, or move the patch to 'beforeUpdate'. Marking a row deleted instead "
        + "of removing it is 'softDelete', which is its own declaration.";

    private static CompiledMutation? CompileMutation(
        string field, ValueOrExpr value, BeforeHookPoint point, string path, BeforeHookScope scope)
    {
        var slot = $"{path}/{field}";
        if (Target(field, slot, scope) is not { } target)
        {
            return null;
        }

        return value.IsExpression
            ? CompileMutationExpression(field, value.Expression!, target, point, slot, scope)
            : CompileMutationLiteral(field, value.Literal, target, slot, scope);
    }

    /// <summary>
    /// The field a mutation writes: it has to exist on the entity, and it must not be one the framework
    /// manages — see the type's own remarks for why the managed set is a tenancy guard and not a formality.
    /// </summary>
    private static FieldSchema? Target(string field, string path, BeforeHookScope scope)
    {
        if (AlvoManagedColumns.For(scope.Schema).Contains(field))
        {
            scope.Errors.Add(Error(path, ManagedColumn(field), ManagedColumnFix));
            return null;
        }

        var declared = scope.Schema.Fields
            .FirstOrDefault(candidate => string.Equals(candidate.Name, field, StringComparison.Ordinal));
        if (declared is null)
        {
            scope.Errors.Add(UnknownField(field, path, scope));
        }

        return declared;
    }

    private static string ManagedColumn(string field) =>
        $"'{field}' is a column the framework manages, so a hook may not patch it. The write's own verdict — "
        + "'WITH CHECK' and the tenant scope — is reached over the caller's payload before the transaction "
        + "opens, so a patch applied inside it would move the row without being judged.";

    private const string ManagedColumnFix =
        "Patch a field the entity declares. 'id', 'tenant_id' and the audit columns are written by the "
        + "framework: the tenant comes from the caller's own context, and the audit columns record who wrote "
        + "the row.";

    private static DescriptorValidationError UnknownField(string field, string path, BeforeHookScope scope)
    {
        var declared = scope.Schema.Fields.Select(candidate => candidate.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        var closest = NameSuggestion.Closest(field, declared);

        return Error(
            path,
            $"'{field}' is not a field of '{scope.Schema.Name}', so this mutate could never be written.",
            closest is not null
                ? $"Did you mean '{closest}'? Declared fields: {string.Join(", ", declared)}."
                : $"Declared fields: {string.Join(", ", declared)}.");
    }

    private static CompiledMutation? CompileMutationExpression(
        string field, string source, FieldSchema target, BeforeHookPoint point, string path, BeforeHookScope scope)
    {
        var result = scope.Compiler.Compile(source, CelProfile.Mutate, scope.Schema);
        if (!result.IsSuccess)
        {
            scope.Errors.AddRange(result.Errors.Select(error => Error(path, error.Message, error.FixSuggestion)));
            return null;
        }

        var expression = result.Expression!;
        if (!HonoursThePhase(expression, point, path, scope))
        {
            return null;
        }

        return Fits(expression.ResultType, target, path, scope) ? new CompiledMutation(field, expression, null) : null;
    }

    /// <summary>
    /// Whether a mutate value's type is one the target field can hold. An integer into a
    /// <c>decimal</c> field is the one widening allowed; every other mismatch is refused, because the
    /// alternative is a per-engine conversion decided by whichever provider sees the parameter.
    /// </summary>
    private static bool Fits(CelValueType value, FieldSchema target, string path, BeforeHookScope scope)
    {
        var declared = CelFieldType.Of(target);
        if (value == declared || (declared == CelValueType.Decimal && value == CelValueType.Int))
        {
            return true;
        }

        scope.Errors.Add(Error(
            path,
            $"This mutate evaluates to {value}, and '{target.Name}' is declared as '{target.Type}', which holds "
            + $"{declared}. Written anyway, the value would be converted by whichever engine saw the parameter, "
            + "or refused by it at write time.",
            $"Produce a {declared} value, or patch a field declared to hold {value}."));

        return false;
    }

    private static CompiledMutation? CompileMutationLiteral(
        string field, JsonElement? literal, FieldSchema target, string path, BeforeHookScope scope)
    {
        if (literal is not { } json)
        {
            return null;
        }

        return LiteralValue(json, target, path, scope) is { } value
            ? new CompiledMutation(field, null, value.Value)
            : null;
    }

    /// <summary>
    /// One JSON literal as the target field holds it, converted at apply so a literal of the wrong shape is an
    /// authoring error rather than a refusal from inside a transaction.
    /// </summary>
    /// <returns>The converted value, wrapped so a legitimate <see langword="null"/> is not a failure.</returns>
    private static LiteralResult? LiteralValue(
        JsonElement json, FieldSchema target, string path, BeforeHookScope scope)
    {
        if (json.ValueKind == JsonValueKind.Null)
        {
            return target.Required ? RefuseNull(target, path, scope) : new LiteralResult(null);
        }

        var converted = Convert(json, CelFieldType.Of(target));

        return converted is null ? RefuseShape(json, target, path, scope) : new LiteralResult(converted);
    }

    /// <summary>A converted literal, so <see langword="null"/> can be a value rather than a failure signal.</summary>
    /// <param name="Value">The value to store, possibly <see langword="null"/>.</param>
    private sealed record LiteralResult(object? Value);

    /// <summary>
    /// A JSON literal in the representation a field of <paramref name="declared"/> holds, or
    /// <see langword="null"/> when the literal's shape does not fit that type at all.
    /// </summary>
    /// <remarks>
    /// A timestamp and a UUID are parsed here rather than passed through as text: the storage driver would
    /// convert them anyway, and parsing at apply is what turns <c>"not-a-date"</c> into an authoring error.
    /// </remarks>
    private static object? Convert(JsonElement json, CelValueType declared) => (declared, json.ValueKind) switch
    {
        (CelValueType.String, JsonValueKind.String) => json.GetString(),
        (CelValueType.Bool, JsonValueKind.True or JsonValueKind.False) => json.GetBoolean(),
        (CelValueType.Int, JsonValueKind.Number) => json.TryGetInt64(out var integer) ? integer : null,
        (CelValueType.Decimal, JsonValueKind.Number) => json.TryGetDecimal(out var number) ? number : null,
        (CelValueType.Timestamp, JsonValueKind.String) =>
            json.TryGetDateTimeOffset(out var instant) ? instant : null,
        (CelValueType.Uuid, JsonValueKind.String) => json.TryGetGuid(out var id) ? id : null,
        _ => null,
    };

    private static LiteralResult? RefuseNull(FieldSchema target, string path, BeforeHookScope scope)
    {
        scope.Errors.Add(Error(
            path,
            $"This mutate writes null into '{target.Name}', which the entity declares as required, so every "
            + "write the hook fires on would carry a null into a column the engine refuses one in.",
            "Write a value, or remove 'required' from the field if it is genuinely optional."));

        return null;
    }

    private static LiteralResult? RefuseShape(
        JsonElement json, FieldSchema target, string path, BeforeHookScope scope)
    {
        scope.Errors.Add(Error(
            path,
            $"This mutate's literal is a JSON {json.ValueKind.ToString().ToLowerInvariant()}, and "
            + $"'{target.Name}' is declared as '{target.Type}'. A value the field's type cannot hold is "
            + "refused here rather than at every write the hook fires on.",
            $"Write a literal a '{target.Type}' field holds, or use a tagged expression "
            + "({\"$cel\": \"…\"}) that produces one."));

        return null;
    }

    private static DescriptorValidationError Error(string path, string message, string? fix) =>
        new(path, message, fix, DescriptorValidationSeverity.Error);
}

/// <summary>
/// Everything one entity's before-hooks compile against: its schema, the CEL compiler, the entity's own JSON
/// pointer, and the shared error accumulator.
/// </summary>
/// <remarks>
/// Bundled for the reason <c>AfterHookScope</c> is, and it is deliberately <em>smaller</em> than that one: a
/// before-hook action names nothing project-level — no endpoint, no template — because neither of its two
/// actions leaves the transaction.
/// </remarks>
/// <param name="Schema">The entity every condition, mutate value and field name is checked against.</param>
/// <param name="Compiler">The CEL compiler every expression goes through.</param>
/// <param name="EntityPath">The entity's JSON pointer, such as <c>/entities/deals</c>.</param>
/// <param name="Errors">The accumulator every problem is appended to — the policy catalog builder's own.</param>
internal sealed record BeforeHookScope(
    EntitySchema Schema,
    ICelCompiler Compiler,
    string EntityPath,
    List<DescriptorValidationError> Errors);
