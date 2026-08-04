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
        var current = candidate;

        foreach (var hook in hooks)
        {
            if (!Fires(hook, current, previous, context))
            {
                continue;
            }

            EnsureNotRejected(hook);
            foreach (var (field, value) in Mutations(hook, current, previous, now))
            {
                patch[field] = value;
            }

            current = Patched(current, patch);
        }

        return patch;
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
    /// <b>The unreachable half is the caught exception, and its direction is defence-in-depth rather than a
    /// live decision.</b> Nothing in a <see cref="CelProfile.Condition"/> tree can throw:
    /// <c>CelInterpreter.Evaluate</c>'s node switch ends in <c>_ =&gt; null</c>, every comparison funnels
    /// through a <c>TryNormalize</c> that answers <see langword="false"/> rather than converting
    /// unsuccessfully, and the profile admits no arithmetic — the one family that could overflow. That is the
    /// property `CelInterpreter`'s own remarks assert for any well-typed expression and any record, including
    /// one whose stored value is of an unexpected CLR type. So no input reaches the <c>catch</c>, and a
    /// fail-closed entry point for this one caller would be a change to the security core that <b>no fact
    /// could discriminate</b> — which is the shape this repository requires a killing mutant for.
    /// </para>
    /// <para>
    /// <b>The obligation this creates, which is the real answer.</b> The argument above is a property of the
    /// profile's grammar, not a guarantee of the interpreter's signature. Admitting arithmetic into
    /// <see cref="CelProfile.Condition"/> — or any construct that can throw — makes the open direction
    /// <em>reachable</em>, and at that moment a <c>reject</c> gate needs its own fail-closed evaluation and
    /// this paragraph is what should be re-read. Recorded as deviation 84 so it is a decision on the record
    /// rather than an inherited default.
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

    private static IEnumerable<KeyValuePair<string, object?>> Mutations(
        CompiledBeforeHook hook, AlvoRecord candidate, AlvoRecord? previous, DateTimeOffset now) =>
        hook.Mutations.Select(mutation => new KeyValuePair<string, object?>(
            mutation.Field, Value(mutation, candidate, previous, now)));

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
