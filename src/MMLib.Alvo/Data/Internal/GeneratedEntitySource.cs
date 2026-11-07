using System.Reflection;

namespace MMLib.Alvo.Data.Internal;

/// <summary>
/// The classes the typed-entity generator emitted into this host: found once, on first use, from the entry assembly and the
/// assemblies it references that reference the core.
/// </summary>
internal sealed class GeneratedEntitySource
{
    private readonly Lazy<IReadOnlyList<Type>> _types;

    /// <summary>Initializes a new instance of the <see cref="GeneratedEntitySource"/> class rooted at the entry assembly.</summary>
    internal GeneratedEntitySource()
        : this(Assembly.GetEntryAssembly())
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GeneratedEntitySource"/> class rooted at <paramref name="root"/>.</summary>
    /// <param name="root">The assembly discovery starts from; <see langword="null"/> finds nothing.</param>
    internal GeneratedEntitySource(Assembly? root)
        : this(() => GeneratedEntityCheck.Discover(root))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GeneratedEntitySource"/> class over exactly <paramref name="types"/>.</summary>
    /// <param name="types">The generated classes.</param>
    internal GeneratedEntitySource(IReadOnlyList<Type> types)
        : this(() => types)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GeneratedEntitySource"/> class over what <paramref name="discover"/> finds.</summary>
    /// <param name="discover">Finds the generated classes; run once, on first use.</param>
    internal GeneratedEntitySource(Func<IReadOnlyList<Type>> discover) => _types = new(discover);

    /// <summary>Gets the generated classes.</summary>
    internal IReadOnlyList<Type> Types => _types.Value;
}
