using MMLib.Alvo.Management.Internal;

namespace MMLib.Alvo.Tests.Management;

public sealed class JsonPointerPathTests
{
    [Fact]
    public void Segments_split_on_slash_and_unescape_tilde_one_before_tilde_zero() =>
        JsonPointerPath.Segments("/entities/a~1b/fields/c~0d/computed")
            .ShouldBe(["entities", "a/b", "fields", "c~d", "computed"]);

    [Theory]
    [InlineData("")]
    [InlineData("entities/orders")]
    [InlineData("/")]
    [InlineData("/entities//rules")]
    public void A_pointer_that_names_no_node_is_refused(string jsonPointer) =>
        Should.Throw<MMLib.Alvo.Management.ManagementRequestException>(() => JsonPointerPath.Segments(jsonPointer));

    [Theory]
    [InlineData("/entities/o/rules/list", "/entities/o/rules/list", true)]
    [InlineData("/entities/o/hooks/beforeCreate/0", "/entities/o/hooks/beforeCreate/0/condition", false)]
    [InlineData("/entities/o/hooks/beforeCreate/0/action", "/entities/o/hooks/beforeCreate/0/action/mutate/status", false)]
    [InlineData("/entities/o/rules/list/x", "/entities/o/rules/list", true)]
    [InlineData("/entities/o/rules/listing", "/entities/o/rules/list", false)]
    [InlineData("/entities/o/rules", "/entities/o/rules/list", false)]
    [InlineData("#/entities/o/rules/list", "/entities/o/rules/list", true)]
    [InlineData("#/entities/o/rules/list/x", "/entities/o/rules/list", true)]
    [InlineData("#/entities/o/rules/listing", "/entities/o/rules/list", false)]
    [InlineData("#/entities/o/rules", "/entities/o/rules/list", false)]
    public void A_path_is_at_or_under_a_pointer_by_whole_segments(string path, string jsonPointer, bool expected) =>
        JsonPointerPath.IsAtOrUnder(path, jsonPointer).ShouldBe(expected);
}
