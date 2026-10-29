using System.Text;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>A command's OpenAPI operationId: <c>commands.</c> and its name in lower camel case, the shape the ruleset accepts.</summary>
internal static class CommandOperationIds
{
    /// <summary>The prefix every command operationId carries.</summary>
    internal const string Prefix = "commands.";

    /// <summary>The operationId of <paramref name="name"/>: <c>confirm_booking</c> → <c>commands.confirmBooking</c>.</summary>
    /// <param name="name">The command's name.</param>
    internal static string Of(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var id = new StringBuilder(Prefix, Prefix.Length + name.Length);
        var upper = false;
        foreach (var character in name)
        {
            if (character is '.' or '_')
            {
                upper = id.Length > Prefix.Length;
                continue;
            }

            id.Append(upper ? char.ToUpperInvariant(character) : character);
            upper = false;
        }

        return id.ToString();
    }
}
