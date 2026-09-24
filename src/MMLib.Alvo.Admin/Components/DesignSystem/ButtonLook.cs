using MudBlazor;

namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>What each <see cref="AlvoButton.ButtonTone"/> looks like in the library, decided once.</summary>
/// <param name="Variant">The library's fill style.</param>
/// <param name="Color">The library's colour role.</param>
internal readonly record struct ButtonLook(Variant Variant, Color Color)
{
    /// <summary>The look of <paramref name="tone"/>.</summary>
    public static ButtonLook Of(AlvoButton.ButtonTone tone) => tone switch
    {
        AlvoButton.ButtonTone.Primary => new(Variant.Filled, Color.Primary),
        AlvoButton.ButtonTone.Danger => new(Variant.Filled, Color.Error),
        AlvoButton.ButtonTone.Ghost => new(Variant.Text, Color.Default),
        _ => new(Variant.Outlined, Color.Default),
    };
}
