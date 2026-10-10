namespace MMLib.Alvo.DocsGen.Tests.Xml;

/// <summary>The member shapes no shipped extension class has, so <c>DocId</c> is tested against each.</summary>
public static class DocIdFixture
{
    /// <summary>A name.</summary>
    public static string Name { get; set; } = string.Empty;

    /// <summary>Joins parts.</summary>
    /// <param name="parts">The parts.</param>
    /// <returns>The joined text.</returns>
    public static string Join(params string[] parts) => string.Concat(parts);

    /// <summary>Reads a number.</summary>
    /// <param name="text">The text.</param>
    /// <param name="value">The number read.</param>
    /// <returns>Whether it was one.</returns>
    public static bool TryRead(string text, out int value) => int.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out value);

    /// <summary>Echoes a value.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="seen">Where it is recorded.</param>
    /// <returns>The same value.</returns>
    public static T Echo<T>(T value, IList<T> seen)
    {
        ArgumentNullException.ThrowIfNull(seen);
        seen.Add(value);
        return value;
    }

    /// <summary>A nested type.</summary>
    public sealed class Nested;
}
