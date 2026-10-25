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

        if (reason.Any(char.IsControl))
        {
            throw new ManagementRequestException(
                "A reason is one line of text: it may not hold a line break, a tab or another control character, because it is written into the audit log as one field.");
        }
    }
}
