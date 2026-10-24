namespace MMLib.Alvo.Extensions.Internal;

/// <summary>Every registration of this host, in call order.</summary>
internal sealed class ExtensionRegistry
{
    internal ExtensionRegistry(IEnumerable<ExtensionRegistration> registrations)
    {
        All = [.. registrations.OrderBy(registration => registration.Sequence)];
        Handlers = [.. All.OfType<HandlerRegistration>()];
        Retired = [.. All.OfType<RetiredExtension>()];
    }

    internal static ExtensionRegistry Empty { get; } = new([]);

    internal IReadOnlyList<ExtensionRegistration> All { get; }

    internal IReadOnlyList<HandlerRegistration> Handlers { get; }

    internal IReadOnlyList<RetiredExtension> Retired { get; }
}
