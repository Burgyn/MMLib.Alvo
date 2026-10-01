using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Components.Data;

/// <summary>
/// The record form's controls as text, beside the text they opened with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only what changed is sent.</b> A save used to send every field, so a Save with nothing changed still
/// wrote the row and bumped <c>updated_at</c>, and a field hidden from the caller went back as
/// <see langword="null"/> over a value they cannot see. A change is text that differs from the text the
/// control opened with — compared as text, because that is what the operator edited, and a value that
/// round-trips to the same text is the same value to them.
/// </para>
/// <para>
/// <b>The same rule covers a create.</b> A new record opens empty, so what changed is what was filled in.
/// </para>
/// </remarks>
internal sealed class RecordDraft
{
    private readonly IReadOnlyList<FieldSchema> _fields;
    private readonly Dictionary<string, string> _original = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _current = new(StringComparer.Ordinal);

    /// <summary>A draft of <paramref name="fields"/>, opened on <paramref name="values"/>.</summary>
    public RecordDraft(IReadOnlyList<FieldSchema> fields, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(values);

        _fields = fields;
        foreach (var field in fields)
        {
            var text = FormValue.Format(field, values.TryGetValue(field.Name, out var value) ? value : null);
            _original[field.Name] = text;
            _current[field.Name] = text;
        }
    }

    /// <summary>The control's current text.</summary>
    public string Text(string field) => _current.TryGetValue(field, out var text) ? text : string.Empty;

    /// <summary>Replaces the control's text.</summary>
    public void Set(string field, string? text) => _current[field] = text ?? string.Empty;

    /// <summary>Whether the control's text differs from what it opened with.</summary>
    public bool Changed(string field)
        => !string.Equals(Text(field), _original.TryGetValue(field, out var text) ? text : string.Empty, StringComparison.Ordinal);

    /// <summary>Whether anything at all differs.</summary>
    public bool Dirty => _fields.Any(column => Changed(column.Name));

    /// <summary>The changed fields as the values to send, and what could not be read.</summary>
    public DraftChanges Read()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in _fields.Where(field => Changed(field.Name)))
        {
            var parsed = FormValue.Parse(field, Text(field.Name));
            if (parsed.Problem is { } problem)
            {
                problems[field.Name] = problem;
            }
            else
            {
                values[field.Name] = parsed.Value;
            }
        }

        return new DraftChanges(values, problems);
    }
}

/// <summary>What a save would send.</summary>
/// <param name="Values">The changed fields, as the types the data port binds.</param>
/// <param name="Problems">The changed fields whose text could not be read, and what it should look like.</param>
internal sealed record DraftChanges(
    Dictionary<string, object?> Values, IReadOnlyDictionary<string, string> Problems);
