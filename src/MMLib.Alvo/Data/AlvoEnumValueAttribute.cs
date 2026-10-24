namespace MMLib.Alvo.Data;

/// <summary>Names the value an enum member stands for in an enum field when it is not the member's snake_case name.</summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
public sealed class AlvoEnumValueAttribute : Attribute
{
    /// <summary>Initializes the attribute.</summary>
    /// <param name="value">One of the field's declared values.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> is blank.</exception>
    public AlvoEnumValueAttribute(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    /// <summary>Gets the declared value.</summary>
    public string Value { get; }
}
