using CsCheck;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The fail-fast compile property for the two SQL-rendered profiles: <b>an expression the compiler accepts
/// renders without throwing</b>. A shape the renderer cannot produce must be refused at save time with a
/// structured error; one that slipped through compiled cleanly and then threw on every request that read the
/// entity. Hand-written cases only cover the shapes someone thought of, so the trees here are generated over
/// every operator, every leaf kind and every nesting the grammar allows.
/// </summary>
/// <remarks>
/// Each property counts what it accepted and asserts it afterwards, so a generator that drifted into producing
/// only refusals would fail loudly instead of passing on a corpus that never reached the renderer.
/// </remarks>
public class CompiledRendersPropertyTests
{
    private const int Iterations = 20_000;

    private static readonly SqlPredicateRenderer _renderer = new();
    private static readonly TestFieldSqlRenderer _fields = new();

    /// <summary>
    /// Every leaf kind a rule can hold — fields of every type, literals of every type, every context
    /// value, and the temporal ones (<c>now()</c>, duration constants, a <c>date</c> field; Task 3 review M4) —
    /// so the type checker, not the generator, decides what is well-typed.
    /// </summary>
    private static readonly string[] _ruleLeaves =
    [
        "id", "owner_id", "status", "total", "title", "tenant_id", "created_at", "is_public",
        "'draft'", "'x'", "1", "2.5", "true", "false", "null", "@user.id", "@tenant.id", "@user.roles",
        "approved_at", "due_on", "now()", "duration('90m')", "duration('-1.5h')",
    ];

    /// <summary>
    /// Bound instants for <c>now()</c>: an ordinary one and both edges of CEL's range, where a folded shift leaves the
    /// range and the whole predicate must render as denied rather than throw (Ruling 57).
    /// </summary>
    private static readonly DateTimeOffset[] _instants =
    [
        new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero),
        new(9999, 12, 31, 23, 0, 0, TimeSpan.Zero),
        new(1, 1, 1, 1, 0, 0, TimeSpan.Zero),
    ];

    private static readonly string[] _temporalLeaves =
        ["created_at", "approved_at", "due_on", "now()", "duration('90m')", "duration('-1.5h')", "duration('0')", "null"];

    private static readonly string[] _comparisons = ["==", "!=", "<", "<=", ">", ">="];

    /// <summary>
    /// The numeric and boolean leaves only. A computed expression must be a non-boolean scalar built from
    /// arithmetic and ternaries, and over the full leaf set almost every generated tree is ill-typed there,
    /// which would leave the renderer barely exercised.
    /// </summary>
    private static readonly string[] _computedLeaves = ["total", "1", "2.5", "is_public", "true", "false"];

    private static readonly string[] _binaryOperators =
        ["==", "!=", "<", "<=", ">", ">=", "&&", "||", "in", "+", "-", "*", "/"];

    private static Gen<string> Expressions(string[] leaves) => Gen.Recursive<string>((depth, self) =>
        depth >= 5
            ? Gen.OneOfConst(leaves)
            : Gen.Frequency(
                (3, Gen.OneOfConst(leaves)),
                (4, Gen.Select(self, Gen.OneOfConst(_binaryOperators), self, (left, op, right) => $"({left} {op} {right})")),
                (1, self.Select(operand => $"!({operand})")),
                (1, self.Select(operand => $"-({operand})")),
                (1, Gen.OneOfConst("title", "is_public", "total").Select(field => $"has({field})")),
                (1, Gen.Select(self, self, self, (condition, whenTrue, whenFalse) => $"({condition} ? {whenTrue} : {whenFalse})"))));

    private static readonly AlvoContext[] _callers =
        [CelFixtures.Alice, CelFixtures.Editor, CelFixtures.TenantlessAlice];

    [Fact]
    public void Every_rule_the_compiler_accepts_renders_to_a_predicate()
    {
        long accepted = 0;

        Expressions(_ruleLeaves).Sample(source =>
        {
            var result = CelFixtures.Compiler.Compile(source, CelProfile.Rule, CelFixtures.Orders);
            if (!result.IsSuccess)
            {
                return;
            }

            Interlocked.Increment(ref accepted);
            RenderForEveryCaller(result.Expression!);
        },
        iter: Iterations);

        accepted.ShouldBeGreaterThan(Iterations / 50, "too few generated rules compiled for the property to mean anything.");
    }

    /// <summary>
    /// Random trees over every leaf rarely type as temporal arithmetic, so durations, <c>now()</c> and their operators get
    /// their own generator: arithmetic over the temporal leaves, compared and combined, rendered under each bound instant
    /// (Task 3 review M4).
    /// </summary>
    [Fact]
    public void Every_temporal_rule_the_compiler_accepts_renders_to_a_predicate()
    {
        long accepted = 0;

        TemporalRules.Sample(source =>
        {
            var result = CelFixtures.Compiler.Compile(source, CelProfile.Rule, CelFixtures.Orders);
            if (!result.IsSuccess)
            {
                return;
            }

            Interlocked.Increment(ref accepted);
            RenderForEveryCaller(result.Expression!);
        },
        iter: Iterations);

        accepted.ShouldBeGreaterThan(Iterations / 50, "too few generated temporal rules compiled for the property to mean anything.");
    }

    private static Gen<string> TemporalTerms => Gen.Recursive<string>((depth, self) =>
        depth >= 3
            ? Gen.OneOfConst(_temporalLeaves)
            : Gen.Frequency(
                (2, Gen.OneOfConst(_temporalLeaves)),
                (3, Gen.Select(self, Gen.OneOfConst("+", "-"), self, (left, op, right) => $"({left} {op} {right})"))));

    private static Gen<string> TemporalRules => Gen.Recursive<string>((depth, self) =>
        depth >= 3
            ? TemporalComparisons
            : Gen.Frequency(
                (3, TemporalComparisons),
                (2, Gen.Select(self, Gen.OneOfConst("&&", "||"), self, (left, op, right) => $"({left} {op} {right})")),
                (1, self.Select(operand => $"!({operand})"))));

    private static Gen<string> TemporalComparisons =>
        Gen.Select(TemporalTerms, Gen.OneOfConst(_comparisons), TemporalTerms, (left, op, right) => $"({left} {op} {right})");

    private static void RenderForEveryCaller(CompiledExpression expression)
    {
        foreach (var now in _instants)
        {
            using var instant = AlvoFlow.EnterInstant(now);
            foreach (var caller in _callers)
            {
                _renderer.Render(expression, caller, _fields);
            }
        }
    }

    [Fact]
    public void Every_computed_expression_the_compiler_accepts_renders_to_a_scalar()
    {
        long accepted = 0;

        Expressions(_computedLeaves).Sample(source =>
        {
            var result = CelFixtures.Compiler.Compile(source, CelProfile.Computed, CelFixtures.Orders);
            if (!result.IsSuccess)
            {
                return;
            }

            Interlocked.Increment(ref accepted);
            _renderer.Render(result.Expression!, _fields);
        },
        iter: Iterations);

        accepted.ShouldBeGreaterThan(Iterations / 50, "too few generated computed expressions compiled for the property to mean anything.");
    }
}
