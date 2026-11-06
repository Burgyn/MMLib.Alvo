using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Rules.Internal;

using static MMLib.Alvo.Tests.Rules.RelationDescriptors;

namespace MMLib.Alvo.Tests.Rules;

/// <summary>
/// A rollup's <c>via</c> and a relation's <c>&lt;child&gt;_via_&lt;field&gt;</c> are resolved by two walks — one over the
/// descriptor, one over the applied schema — so they must agree on which references a child has to its parent.
/// </summary>
public sealed class RelationViaParityTests
{
    [Fact]
    public void The_rollup_walk_and_the_relation_walk_find_the_same_references()
    {
        var descriptor = AlvoDescriptor.Parse(Project(
            ("people", Entity([Field("name", "string")])),
            ("teams", Entity([Field("name", "string")])),
            ("follows", Entity([Ref("follower", "people"), Ref("followee", "people"), Ref("team_id", "teams"), Field("note", "string")]))));
        var catalog = RelationCatalogBuilder.From(descriptor, DescriptorToSchemaMapper.Map(descriptor));
        var follows = descriptor.Entities["follows"];
        string[] candidates = ["follower", "followee", "team_id", "note", "id"];

        foreach (var parent in new[] { "people", "teams" })
        {
            var byRollup = RollupResolver.ReferencesTo(parent, follows).Order(StringComparer.Ordinal);
            var byRelation = candidates.Where(field => catalog.Reverse(parent, $"follows_via_{field}") is not null).Order(StringComparer.Ordinal);

            byRelation.ShouldBe(byRollup);
        }
    }
}
