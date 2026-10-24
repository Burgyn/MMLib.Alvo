namespace MMLib.Alvo.Data;

/// <summary>
/// A host type that wraps a record instead of copying it: <c>As&lt;T&gt;()</c> hands the record to the type's public
/// constructor taking one <see cref="AlvoRecord"/>, so fields the type does not declare survive the round trip.
/// </summary>
/// <remarks>
/// Its properties marked <see cref="AlvoFieldAttribute"/> read from <see cref="Record"/>, and a condition or query may
/// name any field through <c>x.Record["field"]</c>. A wrapper without that constructor is refused when the host starts.
/// </remarks>
public interface IAlvoEntity
{
    /// <summary>Gets the record this instance wraps, every field included.</summary>
    AlvoRecord Record { get; }
}
