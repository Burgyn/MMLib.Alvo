using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// An import replaces the working copy only with a descriptor-shaped document (docs/todo-admin.md §8d item 42): the
/// text <c>null</c> used to empty the copy, and an array or a number was kept and reported as parsed.
/// </summary>
public class WorkingCopyReplaceTests
{
    private const string Descriptor = """{"name":"p","entities":{"orders":{"fields":{"n":{"type":"string"}}}}}""";

    [Theory]
    [InlineData("null")]
    [InlineData("[1, 2]")]
    [InlineData("3")]
    [InlineData("\"a string\"")]
    [InlineData("{not json")]
    public void Anything_but_a_json_object_leaves_the_copy_untouched(string text)
    {
        var copy = Copy();
        var before = copy.Json;
        var changes = 0;
        copy.Changed += () => changes++;

        copy.Replace(text).ShouldBeFalse();

        copy.Loaded.ShouldBeTrue();
        copy.Json.ShouldBe(before);
        copy.IsDirty.ShouldBeFalse();
        changes.ShouldBe(0, "nothing moved, so nothing is announced");
    }

    [Fact]
    public void A_json_object_replaces_the_copy()
    {
        var copy = Copy();

        copy.Replace("""{"name":"p","entities":{}}""").ShouldBeTrue();

        copy.Entities.ShouldBeEmpty();
        copy.IsDirty.ShouldBeTrue();
    }

    private static WorkingCopy Copy()
    {
        var copy = new WorkingCopy();
        copy.Take(Descriptor, 1);
        return copy;
    }
}
