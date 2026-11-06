using CsCheck;
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;

using System.Collections.Concurrent;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// No value ever reaches a relation's SQL text: over hostile generated text placed as a forward body's literal, a reverse
/// body's literal, a literal of the related <c>list</c> rule, and an outer post-image value, the rendered SQL is
/// byte-identical to the benign baseline and the value sits in a parameter.
/// </summary>
/// <remarks>
/// The proof is byte-identity, not a substring search, for the reason <c>NoInterpolationPropertyTests</c> gives: a short
/// value can coincide with structural SQL text. Each placement counts its renders and refusals, so a regression that made
/// the compile start failing cannot turn the property vacuously green.
/// </remarks>
public sealed class RelationNoInterpolationPropertyTests
{
    private const int Iterations = 10_000;
    private const string Benign = "x";

    private static readonly RelationRenderWorld _world = RelationRenderWorld.Instance;
    private static readonly Guid _customer = Guid.Parse("0b9f6a57-6a4d-4c1e-9a0e-6f1b1d1c0003");

    private static readonly Gen<string> _hostile =
        Gen.Char["'\";-\\é中 aZ0()%_\n"].Array[1, 16].Select(characters => new string(characters));

    private static readonly Dictionary<string, Func<string, SqlPredicate?>> _placements = new(StringComparer.Ordinal)
    {
        ["forward body"] = value => Read("orders", $"customer_id.region == '{EscapeForCel(value)}'"),
        ["reverse body"] = value => Read("projects", $"project_members.exists(m, m.note == '{EscapeForCel(value)}')"),
        ["related using"] = RelatedUsing,
        ["outer image"] = OuterImage,
    };

    [Fact]
    public void No_value_ever_reaches_a_relation_s_sql_text()
    {
        var baselines = _placements.ToDictionary(pair => pair.Key, pair => pair.Value(Benign)!.Sql, StringComparer.Ordinal);
        var rendered = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var refused = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        _hostile.Sample(value => _placements.All(pair => Holds(pair.Key, pair.Value(value), value, baselines, rendered, refused)),
            iter: Iterations);

        _placements.Keys.ShouldAllBe(placement => rendered.GetValueOrDefault(placement) + refused.GetValueOrDefault(placement) == Iterations);
        _placements.Keys.ShouldAllBe(placement => rendered.GetValueOrDefault(placement) > 1_000);
    }

    private static bool Holds(
        string placement, SqlPredicate? predicate, string value, Dictionary<string, string> baselines,
        ConcurrentDictionary<string, int> rendered, ConcurrentDictionary<string, int> refused)
    {
        if (predicate is null)
        {
            refused.AddOrUpdate(placement, 1, (_, count) => count + 1);
            return true;
        }

        rendered.AddOrUpdate(placement, 1, (_, count) => count + 1);
        return predicate.Sql == baselines[placement] && predicate.Parameters.Values.Contains(value);
    }

    private static SqlPredicate? Read(string entity, string source)
    {
        var result = CelFixtures.Compiler.Compile(source, CelProfile.Rule, _world.EntityOf(entity), _world.Relations);
        return result.IsSuccess ? RelationSqlRenderTests.Read("test", entity, source) : null;
    }

    private static SqlPredicate? RelatedUsing(string value)
    {
        var relation = (CelRelation)_world.Bound("orders", "customer_id.active == true", CelFixtures.Alice).Root;
        var related = _world.Compile("customers", $"owner_id == @user.id && region == '{EscapeForCel(value)}'");
        var rebound = relation with { Scope = relation.Scope! with { Using = related } };
        var unbound = _world.Compile("orders", "customer_id.active == true");
        var expression = new CompiledExpression(rebound, unbound.Profile, unbound.ResultType, unbound.Source, unbound.Entity);
        return RelationSqlRenderTests.Render("test", expression, CelFixtures.Alice, RelationSqlRenderTests.ReadOf("test", "orders"));
    }

    private static SqlPredicate OuterImage(string value)
    {
        var relation = (CelRelation)_world.Bound("orders", "customer_id.region == region", CelFixtures.Alice).Root;
        var row = RelationSqlRenderTests.Image(("customer_id", _customer), ("region", value));
        return RelationSqlRenderTests.Probe("test", relation, "orders", row);
    }

    private static string EscapeForCel(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
