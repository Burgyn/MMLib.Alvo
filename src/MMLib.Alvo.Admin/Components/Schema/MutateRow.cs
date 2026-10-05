using System.Text.Json;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>How a mutate row writes its value.</summary>
internal enum MutateMode
{
    /// <summary>A JSON literal the field's type holds.</summary>
    Literal,

    /// <summary>A <c>{"$cel": "…"}</c> expression in the Mutate profile.</summary>
    Expression,
}

/// <summary>One field a mutate patches, as the hook editor holds it while it is typed.</summary>
internal sealed class MutateRow
{
    private string _text = string.Empty;

    /// <summary>Initializes a blank row that writes a value.</summary>
    public MutateRow()
    {
    }

    /// <summary>Initializes a row.</summary>
    /// <param name="field">The field it patches.</param>
    /// <param name="mode">How it writes its value.</param>
    /// <param name="text">The literal as typed, or the CEL.</param>
    public MutateRow(string field, MutateMode mode, string text)
    {
        Field = field;
        Mode = mode;
        Text = text;
    }

    /// <summary>Gets or sets the field it patches.</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Gets or sets how it writes its value.</summary>
    public MutateMode Mode { get; set; } = MutateMode.Literal;

    /// <summary>Gets or sets the literal as typed, or the CEL; setting it forgets <see cref="DeclaredKind"/>.</summary>
    public string Text
    {
        get => _text;
        set
        {
            _text = value;
            DeclaredKind = null;
        }
    }

    /// <summary>
    /// Gets or sets the JSON kind the literal was declared with, while the operator has not typed over it — or
    /// <see langword="null"/> for a literal typed here.
    /// </summary>
    /// <remarks>
    /// A declared <c>"3"</c> on an integer field reads as the text <c>3</c>, which the field would take — and Save would
    /// write <c>3</c>, a change nobody made. Remembered, the row is refused instead (<see cref="MutateLiteral.TryValue"/>)
    /// until the value is typed again; set it after <see cref="Text"/>, which clears it.
    /// </remarks>
    public JsonValueKind? DeclaredKind { get; set; }

    /// <summary>Gets or sets a value indicating whether a literal row sets the field to empty (<c>null</c>).</summary>
    public bool Empty { get; set; }
}
