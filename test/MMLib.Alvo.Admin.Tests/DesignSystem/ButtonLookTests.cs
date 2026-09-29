using MMLib.Alvo.Admin.Components.DesignSystem;
using MudBlazor;

namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>Each tone is one Mud look, set in one place (MudBlazor 9 has no global defaults; study §1.2).</summary>
public sealed class ButtonLookTests
{
    [Theory]
    [InlineData(AlvoButton.ButtonTone.Primary, Variant.Filled, Color.Primary)]
    [InlineData(AlvoButton.ButtonTone.Danger, Variant.Filled, Color.Error)]
    [InlineData(AlvoButton.ButtonTone.Secondary, Variant.Outlined, Color.Default)]
    [InlineData(AlvoButton.ButtonTone.Ghost, Variant.Text, Color.Default)]
    public void A_tone_is_one_variant_and_one_colour(AlvoButton.ButtonTone tone, Variant variant, Color color)
        => ButtonLook.Of(tone).ShouldBe(new ButtonLook(variant, color));
}
