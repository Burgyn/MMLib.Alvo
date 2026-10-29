namespace MMLib.Alvo.Data;

/// <summary>Facts about an <see cref="AlvoFilterOperator"/> that more than one implementation of the port needs.</summary>
internal static class AlvoFilterOperatorExtensions
{
    /// <summary>
    /// Whether <paramref name="op"/> is one of the literal text tests — <see cref="AlvoFilterOperator.StartsWith"/>,
    /// <see cref="AlvoFilterOperator.EndsWith"/> and <see cref="AlvoFilterOperator.Contains"/>: C#-only, never on the wire,
    /// and only on a text field with a string value.
    /// </summary>
    /// <param name="op">The operator.</param>
    internal static bool IsLiteralText(this AlvoFilterOperator op) =>
        op is AlvoFilterOperator.StartsWith or AlvoFilterOperator.EndsWith or AlvoFilterOperator.Contains;
}
