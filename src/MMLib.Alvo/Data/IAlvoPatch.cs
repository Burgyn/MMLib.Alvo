using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace MMLib.Alvo.Data;

/// <summary>The fields a typed update changes, named by the record type's properties, with their new values.</summary>
/// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>.</typeparam>
/// <remarks>
/// Built with <see cref="AlvoPatch.For{T}"/> and handed to <c>UpdateAsync&lt;T&gt;</c>; a field the patch does not set keeps
/// its stored value, and setting one to <see langword="null"/> clears it. Alvo implements this interface; it is not meant to
/// be implemented by host code.
/// </remarks>
public interface IAlvoPatch<T>
    where T : class
{
    /// <summary>Gets the fields the patch sets, by field name, with the values they are written as.</summary>
    IReadOnlyDictionary<string, object?> Values { get; }

    /// <summary>Sets one mapped property — or, on a wrapper, one <c>Record["field"]</c> — to <paramref name="value"/>.</summary>
    /// <typeparam name="TValue">The property's type.</typeparam>
    /// <param name="field">The property, such as <c>x =&gt; x.Status</c>.</param>
    /// <param name="value">Its new value; an enum member is written as its declared value and a whole number as a <see cref="long"/>, on a <c>Record["field"]</c> too.</param>
    /// <returns>This patch, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="field"/> names no mapped field, or a field Alvo manages.</exception>
    [SuppressMessage(
        "Naming",
        "CA1716:Identifiers should not match keywords",
        Justification = "The patch's verb is Set, as AlvoPatch.For<T>().Set(x => x.Field, value) reads; Alvo targets C# hosts and implements this interface itself.")]
    IAlvoPatch<T> Set<TValue>(Expression<Func<T, TValue>> field, TValue value);
}
