using MMLib.Alvo.Ai.Internal;

using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>What the engine owes beyond the suite: atomicity, the pointer it names, and what it touched.</summary>
public sealed class JsonPatchTests
{
    private const long MaximumAllocatedBytes = 8 * 1024 * 1024;

    [Fact]
    public void A_failing_third_operation_leaves_the_document_untouched()
    {
        var document = JsonNode.Parse("""{"a":1}""");

        var result = JsonPatch.Apply(document, Ops("""
            [{"op":"add","path":"/b","value":2},{"op":"replace","path":"/a","value":3},{"op":"remove","path":"/missing"}]
            """));

        result.Succeeded.ShouldBeFalse();
        result.Error!.Op.ShouldBe(2);
        result.Document.ShouldBeNull();
        JsonNode.DeepEquals(document, JsonNode.Parse("""{"a":1}""")).ShouldBeTrue();
    }

    [Fact]
    public void A_missing_parent_is_named_with_the_keys_that_do_exist()
    {
        var document = JsonNode.Parse("""{"entities":{"customers":{},"bikes":{}}}""");

        var error = JsonPatch.Apply(document, Ops("""
            [{"op":"add","path":"/entities/customer/fields/x","value":{"type":"string"}}]
            """)).Error!;

        error.Code.ShouldBe(JsonPatchError.PathNotFound);
        error.Pointer.ShouldBe("/entities/customer/fields/x");
        error.Op.ShouldBe(0);
        error.Fix.ShouldBe("'/entities' has: customers, bikes.");
    }

    [Fact]
    public void Changed_paths_are_what_the_mutating_operations_addressed_in_order()
    {
        var document = JsonNode.Parse("""{"a":{"b":1},"c":2}""");

        var result = JsonPatch.Apply(document, Ops("""
            [{"op":"test","path":"/c","value":2},{"op":"move","from":"/a/b","path":"/d"},{"op":"add","path":"/d","value":5}]
            """));

        result.ChangedPaths.ShouldBe(["/a/b", "/d"]);
    }

    [Fact]
    public void Targets_are_one_resolved_pointer_per_operation_in_operation_order()
    {
        var document = JsonNode.Parse("""{"a":{"b":1},"c":2,"x":[1,2,3]}""");

        var result = JsonPatch.Apply(document, Ops("""
            [{"op":"test","path":"/c","value":2},{"op":"move","from":"/a/b","path":"/d"},
             {"op":"remove","path":"/x/0"},{"op":"add","path":"/x/-","value":4},{"op":"replace","path":"/d","value":5}]
            """));

        result.Targets.ShouldBe([null, "/d", "/x/0", "/x/2", "/d"]);
    }

    [Fact]
    public void An_escaped_pointer_token_addresses_the_member_it_spells()
    {
        var result = JsonPatch.Apply(JsonNode.Parse("""{"a/b":{"m~n":1}}"""), Ops("""
            [{"op":"replace","path":"/a~1b/m~0n","value":2}]
            """));

        JsonNode.DeepEquals(result.Document, JsonNode.Parse("""{"a/b":{"m~n":2}}""")).ShouldBeTrue();
    }

    [Fact]
    public void A_replaced_member_keeps_its_place_among_its_siblings()
    {
        var result = JsonPatch.Apply(JsonNode.Parse("""{"a":1,"b":2,"c":3}"""), Ops("""
            [{"op":"replace","path":"/b","value":{"x":true}}]
            """));

        result.Document!.ToJsonString().ShouldBe("""{"a":1,"b":{"x":true},"c":3}""");
    }

    [Fact]
    public void A_value_with_diacritics_and_quotes_arrives_literally()
    {
        var result = JsonPatch.Apply(JsonNode.Parse("{}"), Ops("""
            [{"op":"add","path":"/d","value":"Dielňa \"U Ťava\" + ž"}]
            """));

        result.Document!["d"]!.GetValue<string>().ShouldBe("Dielňa \"U Ťava\" + ž");
    }

    [Fact]
    public void A_patch_that_is_not_an_array_is_refused() =>
        JsonPatch.Apply(JsonNode.Parse("{}"), Ops("""{"op":"add"}""")).Error!.Code
            .ShouldBe(JsonPatchError.InvalidOperation);

    [Theory]
    [InlineData("/e", "/e/k")]
    [InlineData("", "/k")]
    public void Self_doubling_copies_are_refused_within_bounded_memory(string from, string pathPrefix)
    {
        var document = JsonNode.Parse("""{"e":{"v":"0123456789"}}""");
        var operations = "[" + string.Join(",", Enumerable.Range(0, 20).Select(n =>
            $$"""{"op":"copy","from":"{{from}}","path":"{{pathPrefix}}{{n}}"}""")) + "]";
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        var result = JsonPatch.Apply(document, Ops(operations));

        (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore).ShouldBeLessThan(MaximumAllocatedBytes);
        result.Error!.Code.ShouldBe(JsonPatchError.PatchTooLarge);
        result.Error.Pointer.ShouldStartWith(pathPrefix);
        JsonNode.DeepEquals(document, JsonNode.Parse("""{"e":{"v":"0123456789"}}""")).ShouldBeTrue();
    }

    [Fact]
    public void Copies_and_values_share_one_byte_budget()
    {
        var value = new string('x', PatchAdmission.MaximumValueBytes - 100);

        var error = JsonPatch.Apply(JsonNode.Parse("""{"s":"0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"}"""),
            Ops($$"""[{"op":"add","path":"/a","value":"{{value}}"},{"op":"copy","from":"/s","path":"/b"}]""")).Error!;

        error.Code.ShouldBe(JsonPatchError.PatchTooLarge);
        error.Op.ShouldBe(1);
    }

    [Fact]
    public void A_small_copy_is_applied()
    {
        var result = JsonPatch.Apply(JsonNode.Parse("""{"a":{"b":1}}"""), Ops("""
            [{"op":"copy","from":"/a","path":"/c"}]
            """));

        result.Document!.ToJsonString().ShouldBe("""{"a":{"b":1},"c":{"b":1}}""");
    }

    [Theory]
    [InlineData("""[{"op":"test","path":"/a","value":{"x":1,"x":2}}]""")]
    [InlineData("""[{"op":"add","path":"/b","value":{"x":1,"x":2}}]""")]
    [InlineData("""[{"op":"replace","path":"/a","value":[{"y":{"x":1,"x":2}}]}]""")]
    public void A_value_with_duplicate_member_names_is_refused_not_thrown(string operations)
    {
        var error = JsonPatch.Apply(JsonNode.Parse("""{"a":1}"""), Ops(operations)).Error!;

        error.Code.ShouldBe(JsonPatchError.InvalidOperation);
        error.Op.ShouldBe(0);
    }

    [Fact]
    public void A_value_nested_past_the_default_depth_is_refused_not_thrown()
    {
        var nested = new string('[', 200) + new string(']', 200);
        var operations = JsonDocument.Parse(
            $$"""[{"op":"add","path":"/b","value":{{nested}}}]""", new JsonDocumentOptions { MaxDepth = 1000 })
            .RootElement.Clone();

        JsonPatch.Apply(JsonNode.Parse("{}"), operations).Error!.Code.ShouldBe(JsonPatchError.InvalidOperation);
    }

    [Fact]
    public void An_append_records_the_index_it_landed_at()
    {
        var result = JsonPatch.Apply(JsonNode.Parse("""{"x":[1,2,3]}"""), Ops("""
            [{"op":"add","path":"/x/-","value":4}]
            """));

        result.ChangedPaths.ShouldBe(["/x/3"]);
    }

    [Theory]
    [InlineData("""[{"op":"replace","path":"/a/3","value":0}]""", JsonPatchError.IndexOutOfRange)]
    [InlineData("""[{"op":"remove","path":"/a/3"}]""", JsonPatchError.IndexOutOfRange)]
    [InlineData("""[{"op":"remove","path":"/a/-"}]""", JsonPatchError.IndexOutOfRange)]
    [InlineData("""[{"op":"replace","path":"/n/b","value":0}]""", JsonPatchError.NotAContainer)]
    [InlineData("""[{"op":"remove","path":"/n/b"}]""", JsonPatchError.NotAContainer)]
    [InlineData("""[{"op":"remove","path":"/o/missing"}]""", JsonPatchError.PathNotFound)]
    public void Remove_and_replace_name_the_same_failure_add_would(string operations, string code) =>
        JsonPatch.Apply(JsonNode.Parse("""{"a":[1,2,3],"n":1,"o":{}}"""), Ops(operations)).Error!.Code.ShouldBe(code);

    [Fact]
    public void Replacing_an_array_item_keeps_its_position()
    {
        var result = JsonPatch.Apply(JsonNode.Parse("""{"a":[1,2,3]}"""), Ops("""
            [{"op":"replace","path":"/a/1","value":9}]
            """));

        result.Document!.ToJsonString().ShouldBe("""{"a":[1,9,3]}""");
    }

    private static JsonElement Ops(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
