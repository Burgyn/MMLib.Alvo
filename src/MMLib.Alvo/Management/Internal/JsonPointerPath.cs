namespace MMLib.Alvo.Management.Internal;

/// <summary>The RFC 6901 JSON pointer, as far as a descriptor slot needs it.</summary>
internal static class JsonPointerPath
{
    /// <summary>The unescaped segments of <paramref name="pointer"/>.</summary>
    /// <param name="pointer">A pointer that starts with <c>/</c> and has no empty segment.</param>
    /// <exception cref="ManagementRequestException">The pointer names no node.</exception>
    internal static IReadOnlyList<string> Segments(string pointer)
    {
        if (string.IsNullOrEmpty(pointer) || pointer[0] != '/' || pointer.Contains("//", StringComparison.Ordinal) || pointer == "/")
        {
            throw new ManagementRequestException(
                $"'{pointer}' is not a pointer to a descriptor node. Send an RFC 6901 pointer that starts with '/', "
                + "for example '/entities/orders/rules/list'.");
        }

        return [.. pointer[1..].Split('/').Select(Unescape)];
    }

    /// <summary>Whether <paramref name="path"/> is the node <paramref name="pointer"/> names or one beneath it.</summary>
    /// <param name="path">A finding's path.</param>
    /// <param name="pointer">The slot's pointer.</param>
    internal static bool IsAtOrUnder(string path, string pointer) =>
        string.Equals(path, pointer, StringComparison.Ordinal)
        || path.StartsWith(pointer + "/", StringComparison.Ordinal);

    private static string Unescape(string segment) =>
        segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
}
