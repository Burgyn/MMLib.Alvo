using MMLib.Alvo.Api;
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// The default <see cref="IBeforeHookRunner"/>: evaluates one write's compiled <c>before*</c> hooks against
/// the candidate row, in declaration order, inside the transaction the caller already opened.
/// </summary>
/// <remarks>
/// <para>
/// <b>It holds the policy catalog and nothing else, and that is the network ban's other half.</b> The port's
/// synchronous signature closes the direct route to an I/O call; this type's dependency list closes the
/// indirect one. Nothing reachable from its constructor can reach a socket — no <see cref="System.Net.Http.HttpClient"/>,
/// no <c>IHttpClientFactory</c>, no mail port, no webhook delivery — and that is asserted as an architecture
/// fact over this type's actual dependencies rather than left to a naming convention, because the
/// <c>alvo-security-core-review</c> checklist requires a network call from a before-hook to be
/// <em>inexpressible</em> and not merely discouraged. A hook that needs one belongs on a different rung: an
/// after-hook, which runs after the commit and therefore holds no lock.
/// </para>
/// <para>
/// <b>The hooks come out of the same catalog the rules judging this write came out of.</b> That is what makes
/// "the hook and the <c>WITH CHECK</c> predicate agree about what the row's fields are" true by construction
/// rather than by two primings happening to be in step — see <see cref="EntityBeforeHooks"/>.
/// </para>
/// <para>
/// <b>Nothing here compiles, parses or resolves anything.</b> Every condition, every mutate value and every
/// field name was resolved when the descriptor was applied (<see cref="BeforeHookCompiler"/>), so this type
/// evaluates and nothing else. There is no author to report a mistake to from inside a transaction, and the
/// time this runs is time the row's locks are held.
/// </para>
/// <para>
/// <b>An unprimed catalog answers "no hooks", not a throw.</b> A write cannot reach this type without
/// <see cref="IPolicyEngine.Resolve"/> having allowed it first, and that call denies outright while the
/// catalog is unprimed — so an unprimed catalog here is unreachable on the write path, and answering with an
/// empty patch keeps this type from being a second, differently-worded copy of that default-deny decision.
/// </para>
/// </remarks>
internal sealed class BeforeHookRunner : IBeforeHookRunner
{
    private static readonly IReadOnlyDictionary<string, object?> _noPatch =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    private readonly IPolicyCatalogProvider _catalog;

    /// <summary>Initializes a new instance of the <see cref="BeforeHookRunner"/> class.</summary>
    /// <param name="catalog">Holds the catalog this runner reads compiled hooks from; read once per call.</param>
    public BeforeHookRunner(IPolicyCatalogProvider catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object?> Run(
        string entity,
        DataOperation operation,
        AlvoRecord candidate,
        AlvoRecord? previous,
        AlvoContext context,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(context);

        var hooks = Declared(entity, operation);

        return hooks.Count == 0 ? _noPatch : Applied(hooks, candidate, previous, context, now);
    }

    private IReadOnlyList<CompiledBeforeHook> Declared(string entity, DataOperation operation) =>
        _catalog.Current is { } catalog && catalog.TryGetEntity(entity, out var policy)
            ? policy.BeforeHooks.For(operation)
            : [];

    /// <summary>
    /// Runs every hook in declaration order, advancing the candidate between hooks so the list reads as the
    /// pipeline an author wrote.
    /// </summary>
    /// <remarks>
    /// <b>Between hooks, not between mutations.</b> A hook's own mutations are all evaluated against the
    /// candidate as that hook received it, because a <c>mutate</c> is a JSON object and neither JSON nor .NET
    /// promises the order its members are enumerated in — letting one mutation see another's value would make
    /// the stored row depend on that unspecified order. Between hooks the order <em>is</em> specified: it is
    /// the array order the author reads in the descriptor, so a later hook's condition legitimately sees an
    /// earlier hook's patch.
    /// </remarks>
    private static Dictionary<string, object?> Applied(
        IReadOnlyList<CompiledBeforeHook> hooks,
        AlvoRecord candidate,
        AlvoRecord? previous,
        AlvoContext context,
        DateTimeOffset now)
    {
        var patch = new Dictionary<string, object?>(StringComparer.Ordinal);
        var writers = new Dictionary<string, (CompiledBeforeHook Hook, CompiledMutation Mutation)>(StringComparer.Ordinal);
        var current = candidate;

        foreach (var hook in hooks)
        {
            if (!Fires(hook, current, previous, context))
            {
                continue;
            }

            EnsureNotRejected(hook);
            foreach (var mutation in hook.Mutations)
            {
                patch[mutation.Field] = Value(mutation, current, previous, now);
                writers[mutation.Field] = (hook, mutation);
            }

            current = Patched(current, patch);
        }

        EnsureEveryValueFits(patch, writers);
        return patch;
    }

    /// <summary>
    /// Refuses the write when a value the chain would store breaks its target field's declared facets — measured once,
    /// on the final patch, and blamed on the hook whose value that is (Ruling V, #308; Ruling W).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured here, in the core, before any driver applies the patch</b>, so every engine answers the same: SQLite
    /// enforces no length and would store the value, PostgreSQL's <c>varchar(n)</c> would refuse it as an anonymous
    /// 500. The checks are the payload validator's own (<see cref="MutationTarget.Violation"/>).
    /// </para>
    /// <para>
    /// <b>The final patch, not each hook's output (Ruling W).</b> A later hook may repair what an earlier one wrote —
    /// shorten it, or replace it — and the list reads as the pipeline an author wrote, so only the value that would
    /// actually be stored is measured. An intermediate value is never stored, and before Ruling V both engines stored
    /// the repaired one. The refusal names the hook that last wrote the field, because its value is the one refused.
    /// </para>
    /// </remarks>
    private static void EnsureEveryValueFits(
        Dictionary<string, object?> patch,
        Dictionary<string, (CompiledBeforeHook Hook, CompiledMutation Mutation)> writers)
    {
        foreach (var (field, value) in patch)
        {
            var (hook, mutation) = writers[field];
            if (mutation.Target.Violation(value) is { } violation)
            {
                throw Refusal(hook, mutation, violation);
            }
        }
    }

    /// <summary>
    /// Whether a hook's condition selects this write. A hook with no condition always fires — that is what
    /// the frozen schema's optional <c>condition</c> means.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gate uses <see cref="CelInterpreter.EvaluatePredicate"/>, whose "false on anything it cannot
    /// resolve" direction means a <c>reject</c> does not fire — so the direction is stated here rather than
    /// inherited silently.</b> For an authorization predicate <see langword="false"/> is <em>deny</em> and the
    /// direction is closed; for a <c>reject</c> gate <see langword="false"/> is "not guarded", so the same
    /// direction is open. <see cref="CelInterpreter.EvaluateMask"/> exists because that asymmetry is real and
    /// a mask needed the other side of it, and this is the third consumer, so it owes the argument.
    /// </para>
    /// <para>
    /// <b>The reachable half of that direction is correct, and it is the two-valued null rule rather than a
    /// failure.</b> A condition whose operand is missing collapses to <see langword="false"/> — a
    /// <c>reject</c> gated on <c>old.stage == 'won'</c> must <em>not</em> fire on a deal that was never won,
    /// and one gated on <c>new.amount &gt; 10000</c> must not fire on a row whose amount is null. What makes
    /// that honest is the <b>complete post-image</b> this type is handed: a field the caller merely omitted
    /// reads as its stored value, so the collapse never stands in for "the caller did not mention it".
    /// </para>
    /// <para>
    /// <b>A function failure is not collapsed.</b> A <see cref="CelProfile.Condition"/> tree can call a CEL
    /// function, and a function is the one construct whose failure is reachable: <c>CelInterpreter.EvaluatePredicate</c>
    /// lets it escape, so the write is refused (fail closed) instead of the hook silently not firing. Everything
    /// else in the tree still cannot throw — the node switch ends in <c>_ =&gt; null</c> and every comparison
    /// funnels through a <c>TryNormalize</c> that answers <see langword="false"/> — so the open direction remains
    /// only for the two-valued null rule above.
    /// </para>
    /// <para>
    /// <b>The obligation deviation 84 recorded is now discharged:</b> admitting a construct that can throw into
    /// <see cref="CelProfile.Condition"/> was the trigger it named, and the answer is that the exception is not
    /// swallowed by this gate, so a <c>reject</c> gate cannot be bypassed by making its condition fail.
    /// </para>
    /// </remarks>
    private static bool Fires(
        CompiledBeforeHook hook, AlvoRecord candidate, AlvoRecord? previous, AlvoContext context) =>
        hook.Condition is null
        || CelInterpreter.EvaluatePredicate(hook.Condition, candidate, previous, context);

    /// <summary>
    /// Refuses the write when the hook that fired is a <c>reject</c>, carrying the author's own text and the
    /// hook's JSON pointer.
    /// </summary>
    /// <remarks>
    /// Thrown from inside the caller's transaction, which is the whole point: the refusal reaches the caller
    /// as a rolled-back write with no row and no event, rather than as a row that has to be compensated for.
    /// Both halves of the message are descriptor-authored — the <c>reject</c> text and the pointer — so
    /// neither is caller-supplied text this framework would be echoing back, which is the same argument
    /// <see cref="AlvoAuthorizationException"/>'s own remarks make for naming a read-only field.
    /// </remarks>
    private static void EnsureNotRejected(CompiledBeforeHook hook)
    {
        if (hook.Reject is { } reject)
        {
            throw new AlvoAuthorizationException($"{reject} (refused by the before-hook at '{hook.Path}')");
        }
    }

    /// <summary>
    /// The refusal for a value that breaks its target field's facets, in the family a hook's own <c>reject</c> uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One rule, whatever produced the value</b> — a literal (already measured at apply), a field copy, a built-in
    /// such as <c>replace</c>, or a host function: the descriptor's hook would store a value its own field refuses, so
    /// the hook refuses the write, in the family a <c>reject</c> uses (<see cref="AlvoAuthorizationException"/>, HTTP
    /// 403 <c>forbidden</c>, a per-row refusal in a batch). Not a 422: that tells the caller to fix a field of their
    /// payload, and the field named here may be one they never sent. Not <c>function-failed</c>: that is a function
    /// body that failed, and the same overrun is reachable with no function at all.
    /// </para>
    /// <para>
    /// <b>The message names the hook's pointer, the field and the facet, never the value</b>: the value may be the
    /// caller's own text grown by <c>replace</c>, or whatever a host function returned. The pointer and the field are
    /// descriptor-authored — the argument <see cref="EnsureNotRejected"/> makes for the <c>reject</c> text.
    /// </para>
    /// <para>
    /// <b>Unless the field is hidden (Ruling X).</b> For a target the descriptor carries any <c>hidden</c> flag for,
    /// the message names no field, no facet and no limit: the data API never publishes a hidden field's name, and a
    /// refusal naming it — for a field the caller never sent — would disclose that it exists and how wide it is. The
    /// hook's pointer is still named, as a <c>reject</c>'s refusal names it: it locates the descriptor rule that refused
    /// and says nothing about the row's shape.
    /// </para>
    /// </remarks>
    private static AlvoAuthorizationException Refusal(
        CompiledBeforeHook hook, CompiledMutation mutation, AlvoViolation violation) =>
        new(mutation.Target.Disclosable
            ? $"The before-hook at '{hook.Path}' computed a value for '{mutation.Field}' that breaks the "
                + $"'{violation.Code}' facet the field declares: {violation.Message} Nothing was written."
            : $"The before-hook at '{hook.Path}' computed a value one of the fields it writes cannot hold. "
                + "Nothing was written.");

    /// <summary>
    /// One mutation's value: the compiled expression evaluated against the candidate, or the literal the
    /// compiler already converted into the target field's own representation.
    /// </summary>
    private static object? Value(
        CompiledMutation mutation, AlvoRecord candidate, AlvoRecord? previous, DateTimeOffset now) =>
        mutation.Expression is { } expression
            ? CelInterpreter.EvaluateMutation(expression, candidate, previous, now)
            : mutation.Value;

    private static AlvoRecord Patched(AlvoRecord candidate, Dictionary<string, object?> patch)
    {
        var patched = candidate;
        foreach (var (field, value) in patch)
        {
            patched = patched.With(field, value);
        }

        return patched;
    }
}
