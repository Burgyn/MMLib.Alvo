namespace MMLib.Alvo.Events;

/// <summary>Names the application event a payload type is, for AddEventHandler&lt;TMessage, THandler&gt;.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class AlvoEventAttribute : Attribute
{
    /// <summary>Initializes the attribute.</summary>
    /// <param name="type">The exact event type, such as orders.approved.</param>
    /// <exception cref="ArgumentException"><paramref name="type"/> is blank.</exception>
    public AlvoEventAttribute(string type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        Type = type;
    }

    /// <summary>Gets the event type.</summary>
    public string Type { get; }
}
