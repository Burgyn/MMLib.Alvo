using System.Globalization;

namespace MMLib.Alvo.Management.Internal;

/// <summary>What an operator's reason may hold: one line of at most 500 characters, because it becomes one audit field.</summary>
internal static class OperationReasons
{
    internal const int MaxLength = 500;

    internal static void Ensure(string? reason)
    {
        if (reason is null)
        {
            return;
        }

        if (reason.Length > MaxLength)
        {
            throw new ManagementRequestException(
                $"A reason holds at most {MaxLength} characters; this one has {reason.Length}. Shorten it — point at the ticket that tells the whole story.");
        }

        if (reason.Any(Breaks))
        {
            throw new ManagementRequestException(
                "A reason is one line of text: it may not hold a line or paragraph break, a tab, another control character or an invisible format character, because it is written into the audit log as one field.");
        }
    }

    private static bool Breaks(char character) =>
        char.IsControl(character)
        || CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Format;
}
