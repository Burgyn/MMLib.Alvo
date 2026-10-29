using MMLib.Alvo.Commands.Internal;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// Every extension this host registers: the DI registrations, ordered by registration, then the mapped commands.
/// </summary>
/// <remarks><see cref="All"/> is read live, because commands are mapped after the container is built and before the boot.</remarks>
internal sealed class ExtensionRegistry
{
    private readonly IReadOnlyList<ExtensionRegistration> _registered;
    private readonly CommandRegistry? _commands;

    internal ExtensionRegistry(IEnumerable<ExtensionRegistration> registrations, CommandRegistry? commands = null)
    {
        _registered = [.. registrations.OrderBy(registration => registration.Sequence)];
        _commands = commands;
        Handlers = [.. _registered.OfType<HandlerRegistration>()];
        Retired = [.. _registered.OfType<RetiredExtension>()];
    }

    internal static ExtensionRegistry Empty { get; } = new([]);

    internal IReadOnlyList<ExtensionRegistration> All => _commands is null ? _registered : [.. _registered, .. _commands.Mapped];

    internal IReadOnlyList<HandlerRegistration> Handlers { get; }

    internal IReadOnlyList<RetiredExtension> Retired { get; }

    internal IReadOnlyList<CommandRegistration> Commands => [.. All.OfType<CommandRegistration>()];

    internal bool IsEmpty => Handlers.Count == 0 && Commands.Count == 0;

    /// <summary>Ends mapping: a command mapped after the first binding throws (Rulings 13, 40).</summary>
    internal void Seal() => _commands?.Seal();
}
