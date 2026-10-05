using MMLib.Alvo.Admin.Internal;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Components.Integrations;

/// <summary>What the template sheet holds, and what it refuses at a field (spec §6.2).</summary>
/// <remarks>
/// Subject and body only: <c>bodyFile</c> is refused by this build and has no control here. A placeholder's field is judged
/// on apply, against the entity of each email hook that sends the template; what is refused here is what no entity changes —
/// a line break in the subject header, a root an event envelope cannot answer.
/// </remarks>
internal sealed partial class TemplateDraft
{
    /// <summary>The name input's id.</summary>
    public const string NameField = "template-name";

    /// <summary>The subject input's id.</summary>
    public const string SubjectField = "template-subject";

    /// <summary>The body input's id.</summary>
    public const string BodyField = "template-body";

    /// <summary>Gets the placeholder roots an email can resolve — <c>TemplatePlaceholder.Roots</c>, pinned by Host.Tests.</summary>
    public static IReadOnlyList<string> Roots { get; } = ["new", "old", "event", "@user"];

    /// <summary>Gets or sets the template's name — its key under <c>templates</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the subject line.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Gets or sets the body.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>A draft holding a declared template.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="declared">Its declaration.</param>
    /// <returns>The draft.</returns>
    public static TemplateDraft From(string name, JsonElement declared) => new()
    {
        Name = name,
        Subject = DescriptorLens.TextOf(declared, "subject") ?? string.Empty,
        Body = DescriptorLens.TextOf(declared, "body") ?? string.Empty,
    };

    /// <summary>Each field's refusal, in the order the sheet draws the fields.</summary>
    /// <param name="declared">The template names the working copy declares.</param>
    /// <param name="editing">Whether an existing template is edited, whose name is fixed.</param>
    /// <returns>Field id to refusal, one per refused field.</returns>
    public IReadOnlyList<KeyValuePair<string, string>> Refusals(IReadOnlyCollection<string> declared, bool editing)
    {
        var refusals = new List<KeyValuePair<string, string>>();
        refusals.AddRefusal(NameField, editing ? null : NameRefusal(Name.Trim(), declared));
        refusals.AddRefusal(SubjectField, SubjectRefusal(Subject));
        refusals.AddRefusal(BodyField, BodyRefusal(Body));
        return refusals;
    }

    /// <summary>Why a name cannot be a template's key, or <see langword="null"/>.</summary>
    /// <param name="name">The name.</param>
    /// <param name="declared">The names already declared.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public static string? NameRefusal(string name, IReadOnlyCollection<string> declared)
    {
        if (!Identifier().IsMatch(name))
        {
            return $"'{name}' is not a template name. A name is lower case, starts with a letter, and holds letters, digits, "
                + "dashes and underscores — up to 63 characters, such as 'order-ready'.";
        }

        return declared.Contains(name, StringComparer.Ordinal)
            ? $"A template named '{name}' is already declared. Edit it, or choose another name."
            : null;
    }

    /// <summary>Why a subject cannot be sent, or <see langword="null"/>: it is one header line.</summary>
    /// <param name="subject">The subject.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public static string? SubjectRefusal(string subject)
        => subject.Any(BreaksALine)
            ? "A subject is one line: a line break or another control character in a mail header is header injection, and the build refuses it."
            : PlaceholderRefusal(subject);

    /// <summary>Why a body cannot be sent, or <see langword="null"/>.</summary>
    /// <param name="body">The body.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public static string? BodyRefusal(string body)
        => string.IsNullOrWhiteSpace(body)
            ? "A template carries a body. Write one — this sheet does not offer 'bodyFile', which this build does not read."
            : PlaceholderRefusal(body);

    /// <summary>The first placeholder whose root an email cannot resolve, said, or <see langword="null"/>.</summary>
    /// <param name="text">A subject or a body.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public static string? PlaceholderRefusal(string text)
    {
        foreach (Match match in Placeholder().Matches(text))
        {
            var inner = match.Groups[1].Value.Trim();
            if (inner.StartsWith("@tenant", StringComparison.Ordinal) || inner == "@user.roles")
            {
                return $"'{{{{{inner}}}}}' cannot be answered by an email: the event it is sent from carries no tenant and no roles. "
                    + "Read a field of the row instead, such as new.tenant_id.";
            }

            if (!Roots.Contains(inner.Split('.', 2)[0], StringComparer.Ordinal))
            {
                return $"'{{{{{inner}}}}}' names no root an email can resolve. Available roots: {string.Join(", ", Roots)}.";
            }
        }

        return null;
    }

    /// <summary><c>MailHeaders.BreaksALine</c>'s test: a control character but tab, or a line or paragraph separator.</summary>
    private static bool BreaksALine(char character)
        => (char.IsControl(character) && character != '\t')
           || char.GetUnicodeCategory(character) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    [GeneratedRegex(@"\{\{([^{}]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
