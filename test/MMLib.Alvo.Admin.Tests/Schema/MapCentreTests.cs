using MMLib.Alvo.Admin.Components.Schema.Map;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>Which entity the Schema map centres on — none up to the whole-graph limit, then the asked-for one or the first.</summary>
public sealed class MapCentreTests
{
    private static readonly string[] _twelve = [.. Enumerable.Range(1, 12).Select(i => $"e{i}")];
    private static readonly string[] _thirteen = [.. Enumerable.Range(1, 13).Select(i => $"e{i}")];

    [Fact]
    public void Up_to_the_limit_the_map_draws_whole_whatever_was_asked()
    {
        MapCentre.Choose(_twelve, "e5").ShouldBeNull();
        MapCentre.Choose(_twelve, null).ShouldBeNull();
    }

    [Fact]
    public void Past_the_limit_a_declared_centre_is_kept()
        => MapCentre.Choose(_thirteen, "e7").ShouldBe("e7");

    /// <summary>A link sent before a rename names an entity the copy no longer has: the first stands in.</summary>
    [Theory]
    [InlineData("gone")]
    [InlineData(null)]
    public void Past_the_limit_an_unknown_or_missing_centre_falls_back_to_the_first(string? requested)
        => MapCentre.Choose(_thirteen, requested).ShouldBe("e1");

    [Fact]
    public void No_entities_is_no_centre()
        => MapCentre.Choose([], "e1").ShouldBeNull();

    [Fact]
    public void The_limit_is_the_argument()
        => MapCentre.Choose(["a", "b", "c"], "b", limit: 2).ShouldBe("b");
}
