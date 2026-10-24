using MMLib.Alvo.Admin.Components.Home;

namespace MMLib.Alvo.Admin.Tests.Home;

/// <summary>
/// Overview's "Honoured blocks" count: descriptor blocks only, never the host kinds the report appends after them.
/// </summary>
public class HonouredBlocksTests
{
    [Fact]
    public void The_count_leaves_out_the_host_kinds()
        => HonouredBlocks.Count(["entities", "auth", "tenancy", "access", "formats", "extensions.eventHandler"]).ShouldBe(5);

    [Fact]
    public void Without_a_host_kind_every_entry_is_a_block()
        => HonouredBlocks.Count(["entities", "auth"]).ShouldBe(2);
}
