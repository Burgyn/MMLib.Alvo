using System.Text.Json;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// The name patterns <c>schema/project.schema.json</c> freezes, and a check over a descriptor
/// before the dashboard renders one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the dashboard checks names at all, when the apply already does.</b> Two reasons, and
/// only the second is about the apply. The first is that Import is the one place this application
/// takes input it did not produce — <i>somebody sends you a descriptor to look at</i> — and every
/// screen renders names into markup. A name the frozen schema cannot carry is therefore refused
/// before anything renders it, which closes that at the boundary instead of relying on every sink
/// downstream. The second is the product rule: a control must never produce a descriptor the apply
/// would reject, so refusing here says the same thing the apply would, earlier and with the
/// pointer.
/// </para>
/// <para>
/// <b>The patterns are copied, and a test holds the copy honest.</b> This package cannot read
/// <c>schema/project.schema.json</c> at runtime — it is a repository file, not a shipped asset —
/// so the three patterns live here as constants and <c>DescriptorNameTests</c> reads the schema and
/// asserts they are the same strings. A drift is then a failing test rather than a screen that
/// admits what the apply refuses.
/// </para>
/// </remarks>
internal static class DescriptorNames
{
    /// <summary>The pattern an entity or field name must match.</summary>
    public const string Member = "^[a-z][a-z0-9_]{0,62}$";

    /// <summary><see cref="Member"/> as a sentence, for the hint under a name box.</summary>
    /// <remarks>
    /// The regex itself goes in the hint's <c>title</c>, for the developer who wants it: shown as the hint it
    /// was the one line of the form an operator could not read.
    /// </remarks>
    public const string Hint = "Lower case letters, digits and _; starts with a letter.";

    /// <summary>The pattern a webhook endpoint or function name must match.</summary>
    public const string Identifier = "^[a-z][a-z0-9-]{0,62}$";

    /// <summary>The pattern the project's own name must match.</summary>
    public const string Project = "^[a-z][a-z0-9-]{1,62}$";

    private static readonly Regex _member = new(Member, RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex _identifier = new(Identifier, RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex _project = new(Project, RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Whether a name may be an entity or a field.</summary>
    public static bool IsMember(string name) => _member.IsMatch(name);

    /// <summary>Whether a name may be an endpoint or a function.</summary>
    public static bool IsIdentifier(string name) => _identifier.IsMatch(name);

    /// <summary>
    /// The first name in a descriptor that the frozen schema cannot carry.
    /// </summary>
    /// <remarks>
    /// Names only. Everything else about a descriptor — a facet a type may not have, a CEL profile
    /// violation, a ref to an entity that is not declared — is the apply's to judge, and
    /// re-deciding it here would be a second validator to keep in step.
    /// </remarks>
    /// <param name="descriptorJson">The text an operator pasted.</param>
    /// <returns>A sentence naming what is wrong, or <see langword="null"/> when every name is fine.</returns>
    public static string? FirstRefusal(string descriptorJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(descriptorJson);
        }
        catch (JsonException exception)
        {
            return $"That is not JSON: {exception.Message}";
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return "A descriptor is a JSON object.";
            }

            if (root.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && !_project.IsMatch(name.GetString()!))
            {
                return $"The project name must match {Project}.";
            }

            if (root.TryGetProperty("entities", out var entities)
                && entities.ValueKind == JsonValueKind.Object)
            {
                foreach (var entity in entities.EnumerateObject())
                {
                    if (!IsMember(entity.Name))
                    {
                        return $"The entity name must match {Member}.";
                    }

                    if (entity.Value.TryGetProperty("fields", out var fields)
                        && fields.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var field in fields.EnumerateObject())
                        {
                            if (!IsMember(field.Name))
                            {
                                return $"A field name on {entity.Name} must match {Member}.";
                            }
                        }
                    }
                }
            }

            return null;
        }
    }
}
