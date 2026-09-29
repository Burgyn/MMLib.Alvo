using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>The eval's own account of what a proposal changed, independent of what the tool claimed.</summary>
public sealed class DescriptorDiffTests
{
    [Fact]
    public void Identical_documents_differ_nowhere() =>
        DescriptorDiff.Paths("""{"a":{"b":1}}""", """{ "a": { "b": 1 } }""").ShouldBeEmpty();

    [Fact]
    public void An_added_and_a_removed_member_are_reported_where_they_are_not_their_children() =>
        DescriptorDiff.Paths("""{"a":{"gone":{"x":1}}}""", """{"a":{"new":{"y":2}}}""")
            .ShouldBe(["/a/gone", "/a/new"]);

    [Fact]
    public void A_changed_value_is_reported_at_the_deepest_object_it_differs_in() =>
        DescriptorDiff.Paths("""{"a":{"b":{"c":1,"d":2}}}""", """{"a":{"b":{"c":1,"d":3}}}""").ShouldBe(["/a/b/d"]);

    [Fact]
    public void An_array_that_changed_is_reported_as_a_whole() =>
        DescriptorDiff.Paths("""{"hooks":[1,2]}""", """{"hooks":[1,2,3]}""").ShouldBe(["/hooks"]);

    [Fact]
    public void Keys_with_a_slash_or_a_tilde_are_escaped_as_rfc_6901_says() =>
        DescriptorDiff.Paths("""{}""", """{"a/b":1,"c~d":2}""").ShouldBe(["/a~1b", "/c~0d"]);

    [Fact]
    public void At_reads_objects_arrays_and_escaped_keys_and_answers_null_for_what_is_not_there()
    {
        var document = JsonNode.Parse("""{"a/b":{"items":["x","y"]}}""");

        DescriptorDiff.At(document, "/a~1b/items/1")!.GetValue<string>().ShouldBe("y");
        DescriptorDiff.At(document, "/a~1b/items/2").ShouldBeNull();
        DescriptorDiff.At(document, "/missing/deeper").ShouldBeNull();
        DescriptorDiff.At(document, "not-a-pointer").ShouldBeNull();
    }
}
