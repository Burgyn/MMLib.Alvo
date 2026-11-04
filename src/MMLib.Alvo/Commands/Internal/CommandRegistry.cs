namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// The commands mapped with <c>MapAlvoCommand</c>. Mapping happens after the container is built, so mapped commands are
/// kept here and joined to the DI-registered extensions by <c>ExtensionRegistry</c>; the first binding seals it (Rulings 13, 40).
/// </summary>
internal sealed class CommandRegistry
{
    private const int MappedSequenceBase = 1 << 20;

    private readonly List<CommandRegistration> _mapped = [];
    private readonly Lock _gate = new();
    private bool _sealed;

    /// <summary>Gets the mapped commands, in mapping order.</summary>
    internal IReadOnlyList<CommandRegistration> Mapped
    {
        get
        {
            lock (_gate)
            {
                return [.. _mapped];
            }
        }
    }

    /// <summary>Records a mapped command.</summary>
    /// <param name="name">The command's name.</param>
    /// <param name="pattern">Its route pattern.</param>
    /// <param name="entity">The entity its <c>{id}</c> binds to; null for none.</param>
    /// <param name="access">Its access rule.</param>
    /// <exception cref="InvalidOperationException">The host has already booted.</exception>
    internal CommandRegistration Map(string name, string pattern, string? entity, CommandAccess access)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(access);

        lock (_gate)
        {
            if (_sealed)
            {
                throw new InvalidOperationException(
                    $"Command '{name}' was mapped after Alvo first bound its extensions — at boot, or when a descriptor was applied. "
                    + "A binding made then would never contain it: map every command before app.Run() and before applying a descriptor.");
            }

            var command = new CommandRegistration(name, MappedSequenceBase + _mapped.Count, access, entity, pattern, null, null);
            _mapped.Add(command);
            return command;
        }
    }

    /// <summary>Refuses every later <see cref="Map"/>; the boot calls it before it checks the registrations.</summary>
    internal void Seal()
    {
        lock (_gate)
        {
            _sealed = true;
        }
    }
}
