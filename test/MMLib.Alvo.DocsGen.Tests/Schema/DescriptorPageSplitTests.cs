using MMLib.Alvo.DocsGen.Schema;

namespace MMLib.Alvo.DocsGen.Tests.Schema;

public class DescriptorPageSplitTests
{
    [Theory]
    [InlineData("entities.<name>.fields.<name>.rollup.of", "entities-computed-and-rollups")]
    [InlineData("entities.<name>.fields.<name>.maxLength", "entities-fields")]
    [InlineData("entities.<name>.hooks.beforeCreate[].action", "entities-hooks")]
    [InlineData("entities.<name>.audit", "entities")]
    [InlineData("auth.roles", "auth")]
    [InlineData("dynamicEntities.defaultRules.list", "dynamic-entities")]
    public void A_key_path_maps_to_its_page(string keyPath, string slug) => DescriptorPageSplit.PageOf(keyPath).ShouldBe(slug);
}
