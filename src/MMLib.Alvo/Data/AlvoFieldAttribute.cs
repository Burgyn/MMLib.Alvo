namespace MMLib.Alvo.Data;

/// <summary>Names the field a property maps when it is not the property's snake_case name; on a get-only property it also makes the property mapped.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class AlvoFieldAttribute : Attribute
{
    /// <summary>Initializes the attribute.</summary>
    /// <param name="name">The field's name, as the descriptor declares it.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
    public AlvoFieldAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Gets the field's name.</summary>
    public string Name { get; }
}
