using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// The null-semantics proof obligation, over a real engine. An <c>update</c> rule is enforced twice — as a SQL
/// <c>USING</c> predicate over the stored row and as an in-memory <c>WITH CHECK</c> delegate over the
/// candidate one — so if the two could disagree, one half would permit what the other denies. PR1 proved the
/// renderer against an in-process three-valued evaluator; this proves the same matrix against SQLite's and
/// PostgreSQL's own evaluation, where a dialect's boolean handling, a type mapping or a collation can still
/// make them differ.
/// </summary>
/// <remarks>
/// <para>
/// The compiler, renderer, evaluator and field renderer arrive as abstract members because this library
/// references <c>MMLib.Alvo.Abstractions</c> alone; an engine's own test project resolves them from
/// <c>AddAlvo()</c>.
/// </para>
/// <para>
/// The whole matrix runs inside <b>one</b> fact over one probe rather than as a theory row per case. Three
/// reasons, and the first is the load-bearing one: a loop can assert a <em>non-vacuity counter</em> afterwards,
/// so "no disagreement" cannot be satisfied by a probe that answers <see langword="false"/> to everything
/// while the evaluator happens to as well. It also reports every divergence at once instead of the first, and
/// it needs one database rather than one per case.
/// </para>
/// </remarks>
public abstract class AlvoDataDifferentialTests
{
    /// <summary>The parameter prefix the <c>USING</c> predicate is rendered with, matching the data path's own.</summary>
    private const string UsingPrefix = "alvo_u";

    /// <summary>Creates a probe over a freshly created table shaped like <paramref name="entity"/>.</summary>
    /// <param name="entity">The entity to create a table for.</param>
    protected abstract Task<IDifferentialProbe> CreateProbeAsync(EntitySchema entity);

    /// <summary>Gets the CEL compiler.</summary>
    protected abstract ICelCompiler Compiler { get; }

    /// <summary>Gets the SQL predicate renderer.</summary>
    protected abstract IPredicateRenderer Renderer { get; }

    /// <summary>Gets the in-memory predicate evaluator.</summary>
    protected abstract IPredicateEvaluator Evaluator { get; }

    /// <summary>Gets the engine's own field/dialect renderer.</summary>
    protected abstract IFieldSqlRenderer Fields { get; }

    /// <summary>
    /// The entity every case is compiled against — the field names <see cref="DifferentialRuleCases"/>
    /// documents, with the nullability the matrix needs (every field but <c>id</c> is nullable, because half
    /// the cases are about a <see langword="null"/> operand).
    /// </summary>
    public static EntitySchema DifferentialEntity { get; } = new()
    {
        Name = "orders",
        Tenancy = TenancyMode.Scoped,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "tenant_id", Type = FieldType.Uuid, Nullable = true },
            new FieldSchema { Name = "owner_id", Type = FieldType.Uuid, Nullable = true },
            new FieldSchema { Name = "status", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "title", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "total", Type = FieldType.Decimal, Nullable = true, Precision = 18, Scale = 2 },
            new FieldSchema { Name = "created_at", Type = FieldType.DateTime, Nullable = true },
            new FieldSchema { Name = "approved_at", Type = FieldType.DateTime, Nullable = true },
            new FieldSchema { Name = "is_public", Type = FieldType.Boolean, Nullable = true },
        ],
    };

    /// <summary>
    /// Every entry of the shared matrix, replayed through this engine and through the in-memory backend, with
    /// both verdicts compared row by row.
    /// </summary>
    [Fact]
    public async Task This_engine_and_the_in_memory_backend_agree_on_every_case()
    {
        await using var probe = await CreateProbeAsync(DifferentialEntity);
        var divergences = new List<string>();
        var admitted = 0;

        foreach (var testCase in DifferentialRuleCases.All)
        {
            var verdicts = await VerdictsAsync(probe, testCase);
            if (verdicts.ViaEngine != verdicts.InMemory)
            {
                divergences.Add(Divergence(testCase, verdicts));
            }

            admitted += verdicts.ViaEngine ? 1 : 0;
        }

        divergences.ShouldBeEmpty(
            $"This engine and the in-memory backend disagreed on {divergences.Count} of "
            + $"{DifferentialRuleCases.All.Count} cases:{Environment.NewLine}"
            + string.Join(Environment.NewLine, divergences));

        admitted.ShouldBeInRange(
            1,
            DifferentialRuleCases.All.Count - 1,
            "The engine admitted every case or none of them, so agreement proves nothing — the probe is not "
            + "really evaluating the rendered predicate.");
    }

    private async Task<Verdicts> VerdictsAsync(IDifferentialProbe probe, DifferentialRuleCase testCase)
    {
        var context = DifferentialRuleCases.ContextFor(testCase.ContextName);
        var compiled = Compile(testCase.Rule);
        var predicate = Renderer.Render(compiled, context, Fields, UsingPrefix);

        var inMemory = Evaluator.Evaluate(compiled, testCase.Row, previous: null, context);
        var viaEngine = await probe.MatchesAsync(testCase.Row, predicate);

        return new Verdicts(inMemory, viaEngine, predicate);
    }

    private CompiledExpression Compile(string rule)
    {
        var result = Compiler.Compile(rule, CelProfile.Rule, DifferentialEntity);
        return result.IsSuccess
            ? result.Expression!
            : throw new InvalidOperationException(
                $"'{rule}' did not compile against the differential entity: "
                + string.Join("; ", result.Errors.Select(error => error.Message)));
    }

    private static string Divergence(DifferentialRuleCase testCase, Verdicts verdicts)
    {
        var parameters = string.Join(
            ", ", verdicts.Predicate.Parameters.Select(pair => $"{pair.Key}={pair.Value ?? "null"}"));
        var fields = string.Join(", ", testCase.Row.Values.Select(pair => $"{pair.Key}={pair.Value ?? "null"}"));
        return $"""
            Rule '{testCase.Rule}' disagreed for caller '{testCase.ContextName}'.
              Rendered SQL: {verdicts.Predicate.Sql}
              Parameters: {parameters}
              Row: {fields}
              In-memory verdict: {verdicts.InMemory}
              Engine verdict: {verdicts.ViaEngine}
            """;
    }

    private sealed record Verdicts(bool InMemory, bool ViaEngine, SqlPredicate Predicate);
}
