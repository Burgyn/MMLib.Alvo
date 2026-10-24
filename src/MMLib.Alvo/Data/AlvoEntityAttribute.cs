namespace MMLib.Alvo.Data;

/// <summary>Names the entity a host type maps, for <c>Entity&lt;T&gt;</c> and the typed reads.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class AlvoEntityAttribute : Attribute
{
    /// <summary>Initializes the attribute.</summary>
    /// <param name="name">The entity's name, as the descriptor declares it.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
    public AlvoEntityAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Gets the entity's name.</summary>
    public string Name { get; }
}
