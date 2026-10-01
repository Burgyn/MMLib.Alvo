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

    /// <summary>Reads <paramref name="segment"/> as an array index under RFC 6901: <c>0</c> or digits with no leading zero.</summary>
    /// <param name="segment">An unescaped pointer segment.</param>
    /// <param name="count">The length of the array.</param>
    /// <param name="index">The index, when the segment is one and lies inside the array.</param>
    internal static bool TryIndex(string segment, int count, out int index)
    {
        index = -1;
        var grammar = segment.Length > 0 && segment.All(char.IsAsciiDigit) && (segment.Length == 1 || segment[0] != '0');

        return grammar && int.TryParse(segment, out index) && index < count;
    }

    private static string Unescape(string segment) =>
        segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
}
