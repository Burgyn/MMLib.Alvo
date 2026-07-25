using CsCheck;
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The proof obligation for the milestone's null-semantics decision: an <c>update</c> rule is
/// enforced twice — once as a SQL <c>USING</c> predicate over the stored row, once as an in-memory
/// <c>WITH CHECK</c> delegate over the candidate row — so if the two backends could ever disagree,
/// one half would permit what the other denies. This class proves they cannot, first over a curated
/// matrix of every shape the two backends' null handling could plausibly diverge on
/// (<see cref="MMLib.Alvo.Testing.DifferentialRuleCases"/>), then over thousands of randomly
/// generated rule trees and rows, so a collapse the curated matrix happens not to cover still gets
/// caught.
/// </summary>
public class DifferentialBackendTests
{
    private static readonly IFieldSqlRenderer _fields = new TestFieldSqlRenderer();

    private static readonly SqlPredicateRenderer _renderer = new();

    private static readonly string[] _fixedLeaves =
    [
        "owner_id == @user.id",
        "owner_id != @user.id",
        "tenant_id == @tenant.id",
        "tenant_id != @tenant.id",
        "status == 'draft'",
        "status == 'approved'",
        "status != 'draft'",
        "status != 'approved'",
        "is_public",
        "!is_public",
        "has(owner_id)",
        "!has(owner_id)",
        "has(is_public)",
        "!has(is_public)",
        "has(created_at)",
        "!has(created_at)",
        "'editor' in @user.roles",
        "'admin' in @user.roles",
        "'authenticated' in @user.roles",
        "status in @user.roles",
        "created_at == approved_at",
        "created_at != approved_at",
        "created_at < approved_at",
        "created_at <= approved_at",
        "created_at > approved_at",
        "created_at >= approved_at",
    ];

    private static readonly string[] _contextNames =
    [
        "Alice", "Bob", "Editor", "Admin", "AcmeUser", "OtherTenantUser", "TenantlessAlice",
    ];

    private static readonly Guid[] _userIdPool =
    [
        DifferentialRuleCases.Alice.User.Value,
        DifferentialRuleCases.Bob.User.Value,
        DifferentialRuleCases.Editor.User.Value,
        DifferentialRuleCases.Admin.User.Value,
    ];

    private static readonly Guid[] _tenantIdPool =
    [
        DifferentialRuleCases.Alice.Tenant!.Value.Value,
        DifferentialRuleCases.OtherTenantUser.Tenant!.Value.Value,
    ];

    private static readonly Gen<string> _titleLiteralGen =
        Gen.Char["abcXYZ01_ "].Array[1, 10].Select(characters => new string(characters));

    private static readonly Gen<string> _totalLeafGen =
        Gen.Select(Gen.OneOfConst("==", "!=", "<", "<=", ">", ">="), Gen.Int[-100, 1000], (op, value) => $"total {op} {value}");

    private static readonly Gen<string> _titleLeafGen =
        Gen.Select(Gen.OneOfConst("==", "!="), _titleLiteralGen, (op, literal) => $"title {op} '{literal}'");

    private static readonly Gen<string> _leafGen =
        Gen.OneOf(Gen.OneOfConst(_fixedLeaves), _totalLeafGen, _titleLeafGen);

    private static readonly Gen<string> _ruleTreeGen = Gen.Recursive<string>((depth, rule) =>
        depth >= 3
            ? _leafGen
            : Gen.Frequency(
                (4, _leafGen),
                (2, Gen.Select(rule, rule, (a, b) => $"({a} && {b})")),
                (2, Gen.Select(rule, rule, (a, b) => $"({a} || {b})")),
                (1, rule.Select(a => $"!({a})"))));

    private static readonly Gen<AlvoRecord> _rowGen = Gen.Select(
        Nullable(Gen.OneOfConst(_userIdPool)),
        Nullable(Gen.OneOfConst("draft", "approved")),
        Nullable(Gen.Int[-100, 1000].Select(i => (decimal)i)),
        Nullable(_titleLiteralGen),
        Nullable(Gen.OneOfConst(_tenantIdPool)),
        Nullable(Gen.DateTime),
        Nullable(Gen.DateTime),
        Nullable(Gen.Bool))
        .Select((ownerId, status, total, title, tenantId, createdAt, approvedAt, isPublic) => CelFixtures.Row(
            ("owner_id", ownerId),
            ("status", status),
            ("total", total),
            ("title", title),
            ("tenant_id", tenantId),
            ("created_at", createdAt),
            ("approved_at", approvedAt),
            ("is_public", isPublic)));

    /// <summary>Every entry of the shared matrix, as an xUnit theory row: rule source, caller name, and the case's own index.</summary>
    public static IEnumerable<object[]> Cases()
    {
        for (var index = 0; index < DifferentialRuleCases.All.Count; index++)
        {
            var testCase = DifferentialRuleCases.All[index];
            yield return [testCase.Rule, testCase.ContextName, index];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_sql_predicate_and_the_in_memory_delegate_agree_over_the_curated_matrix(string rule, string contextName, int caseIndex)
    {
        var row = DifferentialRuleCases.All[caseIndex].Row;
        var context = DifferentialRuleCases.ContextFor(contextName);
        var compiled = CelFixtures.CompileRule(rule);

        AssertAgreement(compiled, rule, context, contextName, row);
    }

    /// <summary>
    /// The arm that actually catches a missed collapse: random rule trees built from the fixture's
    /// fields and operators, over random rows with roughly a 30% chance of <see langword="null"/> per
    /// field, compared across both backends for at least 5,000 samples. Every generated tree is built
    /// from combinations known to type-check, but a tree that still fails to compile is skipped and
    /// counted rather than silently dropped, so a regression that made the generator (or the compiler)
    /// start rejecting everything cannot pass this test vacuously — the assertion on
    /// <c>compiledCount</c> below is what catches that.
    /// </summary>
    [Fact]
    public void Randomly_generated_rule_trees_and_rows_agree_across_both_backends()
    {
        const long Iterations = 5_000;
        long compiledCount = 0;

        Gen.Select(_ruleTreeGen, _rowGen, Gen.OneOfConst(_contextNames)).Sample(
            (rule, row, contextName) =>
            {
                var result = CelFixtures.Compiler.Compile(rule, CelProfile.Rule, CelFixtures.Orders);
                if (!result.IsSuccess)
                {
                    return;
                }

                Interlocked.Increment(ref compiledCount);
                var context = DifferentialRuleCases.ContextFor(contextName);
                AssertAgreement(result.Expression!, rule, context, contextName, row);
            },
            iter: Iterations);

        compiledCount.ShouldBeGreaterThanOrEqualTo((long)(Iterations * 0.8), "the generator must produce mostly-compilable trees, or this property goes vacuous.");
    }

    private static Gen<T?> Nullable<T>(Gen<T> gen)
        where T : struct =>
        Gen.Select(Gen.Int[0, 99], gen, (chance, value) => chance < 30 ? (T?)null : value);

    private static Gen<string?> Nullable(Gen<string> gen) =>
        Gen.Select(Gen.Int[0, 99], gen, (chance, value) => chance < 30 ? null : value);

    private static void AssertAgreement(CompiledExpression compiled, string rule, AlvoContext context, string contextName, AlvoRecord row)
    {
        var inMemory = CelInterpreter.EvaluatePredicate(compiled, row, previous: null, context);
        var predicate = _renderer.Render(compiled, context, _fields);
        var viaSql = SqlVerdict.Evaluate(predicate, row);

        viaSql.ShouldBe(inMemory, DivergenceMessage(rule, contextName, row, predicate, inMemory, viaSql));
    }

    private static string DivergenceMessage(
        string rule, string contextName, AlvoRecord row, SqlPredicate predicate, bool inMemory, bool viaSql)
    {
        var parameters = string.Join(", ", predicate.Parameters.Select(pair => $"{pair.Key}={pair.Value ?? "null"}"));
        var fields = string.Join(", ", row.Values.Select(pair => $"{pair.Key}={pair.Value ?? "null"}"));
        return $"""
            Rule '{rule}' disagreed between the SQL and in-memory backends for caller '{contextName}'.
            Rendered SQL: {predicate.Sql}
            Parameters: {parameters}
            Row: {fields}
            In-memory verdict: {inMemory}
            SQL verdict: {viaSql}
            """;
    }
}
