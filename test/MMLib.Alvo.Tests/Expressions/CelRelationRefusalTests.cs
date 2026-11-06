using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

using static MMLib.Alvo.Tests.Expressions.RelationFixtures;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// Relation syntax exists only where a compile asks for it, and a relation term reaches no evaluator that would
/// answer it from the wrong thing: every slot compiled without a catalog refuses a path exactly as before HX-19,
/// and the interpreter and the public renderer refuse a relation rather than reading it as <see langword="null"/>.
/// </summary>
public class CelRelationRefusalTests
{
    private const string NestedAccess = "Alvo has no nested field access; use a single field name.";

    private const string MacroFix =
        "CEL comprehension macros (all, exists, map, filter) are optional extensions and are not part of any Alvo "
        + "profile; express row conditions in hooks.beforeUpdate instead.";

    private const string Interpretation =
        "A relation term is evaluated by the database driver; this evaluator cannot read related rows.";

    public static TheoryData<CelProfile, string> SlotsAndPaths()
    {
        var data = new TheoryData<CelProfile, string>();
        foreach (var profile in new[] { CelProfile.Condition, CelProfile.Computed, CelProfile.Mutate, CelProfile.Access, CelProfile.Rule })
        {
            data.Add(profile, "customer_id.owner_id == 'x'");
            data.Add(profile, "members.exists(m, true)");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SlotsAndPaths))]
    public void Every_slot_compiled_without_a_catalog_refuses_a_path_exactly_as_before(CelProfile profile, string source)
    {
        var error = new CelCompiler().Compile(source, profile, Orders).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe(NestedAccess);
        error.FixSuggestion.ShouldBe(MacroFix);
        error.Position.ShouldBe(0);
    }

    [Fact]
    public void A_compiler_that_does_not_override_the_catalog_overload_ignores_the_catalog_and_refuses()
    {
        ICelCompiler compiler = new PublicOnlyCompiler();

        var error = compiler.Compile("customer_id.owner_id == @user.id", CelProfile.Rule, Orders, Catalog).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe(NestedAccess);
    }

    [Theory]
    [MemberData(nameof(RelationTrees))]
    public void The_interpreter_throws_on_a_relation_and_never_reads_it_as_null(string kind, CelNode root)
    {
        _ = kind;
        var mutate = Expression(root, CelProfile.Mutate);

        var failure = Should.Throw<CelFunctionException>(
            () => CelInterpreter.EvaluateMutation(mutate, AlvoRecord.Empty, null, DateTimeOffset.UnixEpoch));

        failure.InnerException.ShouldBeOfType<NotSupportedException>().Message.ShouldBe(Interpretation);
    }

    [Theory]
    [MemberData(nameof(RelationTrees))]
    public void A_relation_and_its_negation_both_evaluate_to_false(string kind, CelNode root)
    {
        _ = kind;
        var relation = Expression(root, CelProfile.Rule);
        var negated = Expression(new CelUnary(CelUnaryOperator.Not, root), CelProfile.Rule);

        CelInterpreter.EvaluatePredicate(relation, AlvoRecord.Empty, null, CelFixtures.Alice).ShouldBeFalse();
        CelInterpreter.EvaluatePredicate(negated, AlvoRecord.Empty, null, CelFixtures.Alice).ShouldBeFalse();
        CelFixtures.Evaluator.Evaluate(relation, AlvoRecord.Empty, null, CelFixtures.Alice).ShouldBeFalse();
        CelFixtures.Evaluator.Evaluate(negated, AlvoRecord.Empty, null, CelFixtures.Alice).ShouldBeFalse();
    }

    [Theory]
    [InlineData("customer_id.owner_id == @user.id")]
    [InlineData("title == 'x' || customer_id.owner_id == @user.id")]
    public void The_public_renderer_refuses_a_relation_tree(string source)
    {
        var expression = CompileRule(source, Orders);

        Should.Throw<NotSupportedException>(
            () => new SqlPredicateRenderer().Render(expression, CelFixtures.Alice, new TestFieldSqlRenderer(), "p"));
    }

    public static TheoryData<string, CelNode> RelationTrees()
    {
        var outer = new CelOuterFieldRef("region", CelValueType.String);
        var relation = (CelRelation)CompileRule("customer_id.owner_id == @user.id", Orders).Root;
        return new TheoryData<string, CelNode>
        {
            { "unbound relation", relation },
            { "bound relation", relation with { Scope = RelationScope.Deny } },
            { "outer field", new CelBinary(CelBinaryOperator.Equal, outer, new CelLiteral(CelValueType.String, "eu")) },
            { "outer has", new CelOuterHas(outer) },
        };
    }

    private static CompiledExpression Expression(CelNode root, CelProfile profile) =>
        new(root, profile, CelValueType.Bool, "relation", Orders);

    private sealed class PublicOnlyCompiler : ICelCompiler
    {
        private readonly CelCompiler _inner = new();

        public CelCompilationResult Compile(string source, CelProfile profile, EntitySchema entity) =>
            _inner.Compile(source, profile, entity);
    }
}
