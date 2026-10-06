using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* The templates block: declaring and editing a message template, through Edit like every other block. */
internal sealed partial class WorkingCopy
{
    /// <summary>
    /// Declares a message template, or edits a declared one in place.
    /// </summary>
    /// <remarks>
    /// Re-checked under the gate as <see cref="DeclareEndpoint"/> is, the declaration as the sheet drew it included
    /// (<see cref="TemplateAsDrawn"/>, Ruling V-B). A template that reads a <c>bodyFile</c> is never edited
    /// here: the sheet offers no <c>bodyFile</c>, and writing a <c>body</c> beside one would leave two bodies to disagree.
    /// </remarks>
    /// <param name="name">The template's key.</param>
    /// <param name="subject">The subject, or blank for none.</param>
    /// <param name="body">The body.</param>
    /// <param name="editing">Whether an existing template is edited.</param>
    /// <param name="drawn">For an edit, the template as the sheet drew it when it opened; <see langword="null"/> checks only that it exists.</param>
    /// <returns><see langword="true"/> when it was written.</returns>
    public bool DeclareTemplate(string name, string? subject, string body, bool editing, string? drawn = null) => Edit(root =>
    {
        if (!Settable(root["templates"]))
        {
            return false;
        }

        var current = (root["templates"] as JsonObject)?[name];
        if ((current is not null) != editing
            || (editing && current is not JsonObject)
            || (current as JsonObject)?.ContainsKey("bodyFile") == true
            || ChangedSinceDrawn(current, drawn))
        {
            return false;
        }

        var template = current as JsonObject ?? new JsonObject();
        SetOrRemoveBlank(template, "subject", subject);
        template["body"] = body;
        if (!editing)
        {
            Ensure(root, "templates")[name] = template;
        }

        return true;
    });

    /// <summary>
    /// The template <paramref name="name"/> as an editor draws it — the text <see cref="DeclareTemplate"/>'s guard compares —
    /// or <see langword="null"/> when nothing declares it.
    /// </summary>
    /// <param name="name">The template's key.</param>
    public string? TemplateAsDrawn(string name)
        => Read(() => _working?["templates"]?[name] is { } template ? Readable(template, "{}") : null);
}
