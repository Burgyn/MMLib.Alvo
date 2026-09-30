using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// The single fail-fast boundary between authored CEL source and a <see cref="CompiledExpression"/>
/// a renderer can trust: tokenize/parse, cap the tree's depth, type-check and profile-filter, then
/// verify the whole expression's result type matches what the profile requires. No exception ever
/// escapes <see cref="Compile(string, CelProfile, EntitySchema)"/> for any source string — every rejection, from a
/// syntax error to a too-deep tree, comes back as a failed <see cref="CelCompilationResult"/>.
/// </summary>
/// <remarks>
/// <b>A quoted predicate (D42): the refusal names the quotes.</b> The accepted set is untouched — the check runs only
/// on a source already refused for its result type, and only ever rewrites that refusal's text. The unwrapped content
/// is compiled once more with the check off (<c>detectQuoted: false</c>), so the inner compile cannot recurse: at most
/// two compilations per source, by construction. Every result-type refusal also echoes its source, cut at
/// <see cref="EchoLength"/> characters on a whole character, with control characters turned into spaces.
/// </remarks>
internal sealed class CelCompiler : ICelCompiler
{
    /// <summary>
    /// The maximum depth of the parsed tree, measured as the number of nodes on the deepest
    /// root-to-leaf path. A flat, well-under-the-length-limit source like a long <c>+</c> chain
    /// still builds a tree whose depth grows with its term count; capping it here — once, before
    /// any recursive consumer (this checker, the interpreter, the SQL renderer) walks it — is what
    /// stands between that input and a stack overflow. 128 leaves room for roughly a 120-clause
    /// <c>||</c> chain while bounding every downstream walker.
    /// </summary>
    internal const int MaxTreeDepth = 128;

    /// <summary>How much of a refused source a result-type refusal echoes; a longer one is cut here, plus <c>…</c>.</summary>
    internal const int EchoLength = 120;

    /// <summary>How the fix for a predicate wrapped whole in quotes begins; the unwrapped content follows (D42).</summary>
    internal const string QuotedFixLead = "Remove the outer quotes; the value is the expression itself: ";

    /// <inheritdoc/>
    public CelCompilationResult Compile(string source, CelProfile profile, EntitySchema entity)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(entity);

        return Compile(source, profile, entity, detectQuoted: true);
    }

    private static CelCompilationResult Compile(string source, CelProfile profile, EntitySchema entity, bool detectQuoted)
    {
        var parsed = TryParse(source, out var syntaxError);
        if (parsed is null)
        {
            return CelCompilationResult.Failure(syntaxError!);
        }

        var depthError = ValidateTreeDepth(parsed);
        if (depthError is not null)
        {
            return CelCompilationResult.Failure(depthError);
        }

        return CheckAndAssemble(new Authored(source, profile, entity, parsed, detectQuoted));
    }

    private static CelNode? TryParse(string source, out CelCompilationError? syntaxError)
    {
        try
        {
            syntaxError = null;
            return CelParser.Parse(source);
        }
        catch (CelSyntaxException ex)
        {
            syntaxError = new CelCompilationError(ex.Message, ex.FixSuggestion, ex.Position);
            return null;
        }
    }

    private static CelCompilationResult CheckAndAssemble(Authored authored)
    {
        var (root, resultType, position, errors) =
            CelTypeChecker.Check(authored.Parsed, authored.Source, authored.Entity, authored.Profile);
        var allErrors = AppendResultTypeError(errors, authored, resultType, position);

        if (allErrors.Count > 0)
        {
            return CelCompilationResult.Failure([.. allErrors]);
        }

        return CelCompilationResult.Success(
            new CompiledExpression(root, authored.Profile, resultType, authored.Source, authored.Entity));
    }

    private static List<CelCompilationError> AppendResultTypeError(
        IReadOnlyList<CelCompilationError> errors, Authored authored, CelValueType resultType, int position)
    {
        var resultTypeError = ValidateResultType(authored, resultType, position);
        return resultTypeError is null ? [.. errors] : [.. errors, resultTypeError];
    }

    private static CelCompilationError? ValidateResultType(Authored authored, CelValueType resultType, int position) =>
        ValidateValueResult(authored, resultType, position) ?? ValidatePredicateResult(authored, resultType, position);

    /// <summary>The Computed and Mutate refusals: each keeps its first sentence byte for byte, then echoes the source.</summary>
    private static CelCompilationError? ValidateValueResult(Authored authored, CelValueType resultType, int position)
    {
        var profile = authored.Profile;
        if (profile == CelProfile.Computed && resultType == CelValueType.Bool)
        {
            return new CelCompilationError(
                "A computed-field expression must evaluate to a non-boolean scalar; a bare boolean expression cannot be a computed column's value."
                + EchoSentence(authored.Source),
                "Wrap the condition in a ternary, e.g. condition ? whenTrue : whenFalse.",
                position);
        }

        if (profile == CelProfile.Computed && !IsScalar(resultType))
        {
            return new CelCompilationError(
                $"A computed-field expression must evaluate to a non-boolean scalar; {resultType} is not a scalar value a database column can hold."
                + EchoSentence(authored.Source),
                "Compare, extract, or convert to a scalar (string/number/date/uuid) before assigning it as the computed value.",
                position);
        }

        if (profile == CelProfile.Mutate && !IsMutateValue(resultType))
        {
            return new CelCompilationError(
                $"A {CelProfile.Mutate} expression must evaluate to a value a field can hold — a scalar or a "
                + $"boolean; {resultType} is not one." + EchoSentence(authored.Source),
                "Fold, compare or convert to a scalar (string/number/date/uuid) or a boolean before assigning it "
                + "as the mutate value.",
                position);
        }

        return null;
    }

    /// <summary>
    /// The predicate refusal. Its first sentence is kept byte for byte; a string literal whose content is itself a
    /// predicate gets a fix that names the outer quotes, and anything else keeps the generic one.
    /// </summary>
    private static CelCompilationError? ValidatePredicateResult(Authored authored, CelValueType resultType, int position)
    {
        if (!IsPredicateProfile(authored.Profile) || resultType == CelValueType.Bool)
        {
            return null;
        }

        var quoted = QuotedPredicate(authored);
        return new CelCompilationError(
            $"A {authored.Profile} expression must evaluate to a boolean; this expression evaluates to {resultType}."
            + (quoted is null ? EchoSentence(authored.Source) : $" The whole expression is one quoted string: {Echo(authored.Source)}."),
            quoted is null
                ? "Add a comparison, e.g. field == value, so the expression yields true/false."
                : QuotedFixLead + Echo(quoted),
            position);
    }

    /// <summary>
    /// The content of a string literal that is itself a predicate, or <see langword="null"/>. Compiled once, with this
    /// check off, so nesting cannot recurse: at most two compilations per source, and only for a source already refused.
    /// </summary>
    /// <remarks>
    /// Success under a predicate profile already implies a boolean result, because the branch that calls this refuses
    /// anything else. The inner call is the same no-throw path as <see cref="Compile(string, CelProfile, EntitySchema)"/>.
    /// </remarks>
    private static string? QuotedPredicate(Authored authored) =>
        authored.DetectQuoted
        && authored.Parsed is CelLiteral { Type: CelValueType.String, Value: string content }
        && Compile(content, authored.Profile, authored.Entity, detectQuoted: false).IsSuccess
            ? content
            : null;

    private static string EchoSentence(string source) => $" The expression: {Echo(source)}.";

    /// <summary>
    /// The source as a refusal quotes it: control characters become spaces, so the refusal stays one line, and a source
    /// longer than <see cref="EchoLength"/> is cut there, plus <c>…</c> — never between the halves of a surrogate pair.
    /// </summary>
    private static string Echo(string source)
    {
        var length = source.Length <= EchoLength ? source.Length : EchoLength;
        if (length < source.Length && char.IsHighSurrogate(source[length - 1]))
        {
            length--;
        }

        var echoed = string.Create(length, source, static (span, text) =>
        {
            for (var index = 0; index < span.Length; index++)
            {
                span[index] = char.IsControl(text[index]) ? ' ' : text[index];
            }
        });

        return length < source.Length ? echoed + "…" : echoed;
    }

    /// <summary>
    /// The profiles whose whole expression is a verdict rather than a value. <see cref="CelProfile.Access"/>
    /// joins them: an access level that is not a predicate can never answer "may this caller manage the
    /// project", and a bare string there would otherwise be accepted and then never match.
    /// </summary>
    private static bool IsPredicateProfile(CelProfile profile) => profile is
        CelProfile.Rule or CelProfile.Condition or CelProfile.Access;

    private static bool IsScalar(CelValueType type) => type is
        CelValueType.Int or CelValueType.Decimal or CelValueType.String or CelValueType.Timestamp or CelValueType.Uuid;

    /// <summary>
    /// What a <see cref="CelProfile.Mutate"/> expression may evaluate to. A <c>mutate</c> value is assigned
    /// to one field and written as a bound parameter, so the bar is "a value a column can hold" — which is
    /// looser than <see cref="CelProfile.Computed"/>'s in exactly one place, and that difference is the
    /// point: a boolean is a legitimate <c>mutate</c> result (<c>"is_closed": {"$cel": "new.stage == 'won'"}</c>
    /// targets a boolean column), whereas <see cref="CelProfile.Computed"/> has to refuse one because a
    /// generated column cannot hold "predicate" as its value. <see cref="CelValueType.Json"/>,
    /// <see cref="CelValueType.StringList"/> and <see cref="CelValueType.Null"/> stay refused here: the first
    /// two are not a single column value, and the third is the checker's "unresolved" placeholder rather than
    /// an authored intent to store null — an author who means null writes it as JSON in the descriptor, with
    /// no <c>$cel</c> at all.
    /// </summary>
    private static bool IsMutateValue(CelValueType type) => IsScalar(type) || type == CelValueType.Bool;

    /// <summary>What was authored, and how: the one argument the result-type check needs.</summary>
    private readonly record struct Authored(string Source, CelProfile Profile, EntitySchema Entity, CelNode Parsed, bool DetectQuoted);

    private static CelCompilationError? ValidateTreeDepth(CelNode root)
    {
        var depth = MeasureDepth(root);
        if (depth <= MaxTreeDepth)
        {
            return null;
        }

        return new CelCompilationError(
            $"The expression tree nests {depth} levels deep, exceeding the maximum of {MaxTreeDepth}.",
            "Simplify the expression, or split it across multiple rules/hooks.",
            0);
    }

    private static int MeasureDepth(CelNode root)
    {
        var stack = new Stack<(CelNode Node, int Depth)>();
        stack.Push((root, 1));
        var maxDepth = 0;

        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            maxDepth = Math.Max(maxDepth, depth);

            foreach (var child in CelTree.Children(node))
            {
                stack.Push((child, depth + 1));
            }
        }

        return maxDepth;
    }
}
