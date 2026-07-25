using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// The single fail-fast boundary between authored CEL source and a <see cref="CompiledExpression"/>
/// a renderer can trust: tokenize/parse, cap the tree's depth, type-check and profile-filter, then
/// verify the whole expression's result type matches what the profile requires. No exception ever
/// escapes <see cref="Compile"/> for any source string — every rejection, from a syntax error to a
/// too-deep tree, comes back as a failed <see cref="CelCompilationResult"/>.
/// </summary>
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

    /// <inheritdoc/>
    public CelCompilationResult Compile(string source, CelProfile profile, EntitySchema entity)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(entity);

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

        return CheckAndAssemble(source, profile, entity, parsed);
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

    private static CelCompilationResult CheckAndAssemble(string source, CelProfile profile, EntitySchema entity, CelNode parsed)
    {
        var (root, resultType, errors) = CelTypeChecker.Check(parsed, source, entity, profile);
        var allErrors = AppendResultTypeError(errors, profile, resultType);

        if (allErrors.Count > 0)
        {
            return CelCompilationResult.Failure([.. allErrors]);
        }

        return CelCompilationResult.Success(new CompiledExpression
        {
            Root = root,
            Profile = profile,
            ResultType = resultType,
            Source = source,
            EntityName = entity.Name,
        });
    }

    private static List<CelCompilationError> AppendResultTypeError(
        IReadOnlyList<CelCompilationError> errors, CelProfile profile, CelValueType resultType)
    {
        var resultTypeError = ValidateResultType(profile, resultType);
        return resultTypeError is null ? [.. errors] : [.. errors, resultTypeError];
    }

    private static CelCompilationError? ValidateResultType(CelProfile profile, CelValueType resultType) => profile switch
    {
        CelProfile.Computed when resultType == CelValueType.Bool => new CelCompilationError(
            "A computed-field expression must evaluate to a non-boolean scalar; a bare boolean expression cannot be a computed column's value.",
            "Wrap the condition in a ternary, e.g. condition ? whenTrue : whenFalse.",
            0),
        CelProfile.Rule or CelProfile.Condition when resultType != CelValueType.Bool => new CelCompilationError(
            $"A {profile} expression must evaluate to a boolean; this expression evaluates to {resultType}.",
            "Add a comparison, e.g. field == value, so the expression yields true/false.",
            0),
        _ => null,
    };

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

            foreach (var child in Children(node))
            {
                stack.Push((child, depth + 1));
            }
        }

        return maxDepth;
    }

    private static IEnumerable<CelNode> Children(CelNode node) => node switch
    {
        CelUnary unary => [unary.Operand],
        CelBinary binary => [binary.Left, binary.Right],
        CelConditional conditional => [conditional.Condition, conditional.WhenTrue, conditional.WhenFalse],
        CelHas has => [has.Field],
        _ => [],
    };
}
