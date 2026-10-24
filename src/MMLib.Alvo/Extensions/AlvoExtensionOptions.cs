using MMLib.Alvo.Data;

namespace MMLib.Alvo.Extensions;

/// <summary>
/// How host code extending Alvo is bounded (<c>Alvo:Extensions</c>), validated when the host starts.
/// </summary>
public sealed class AlvoExtensionOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Alvo:Extensions";

    /// <summary>
    /// Gets or sets the deepest causation chain a write or publish may sit in; deeper is refused with
    /// <c>508 chain-depth-exceeded</c>. One counter spans nested writes and event hops; default <c>8</c>, at least <c>1</c>.
    /// </summary>
    public int MaxDepth { get; set; } = AlvoChainDepthLimit.DefaultMaxDepth;

    /// <summary>
    /// Gets or sets whether the extension catalog names each class handler's .NET type; default <see langword="false"/>,
    /// because a type name tells every viewer of the catalog — and the schema assistant's model, which reads the catalog
    /// through its <c>get_extensions</c> tool — how the host's code is laid out. A delegate handler is never named.
    /// </summary>
    public bool ExposeTypeNames { get; set; }
}
