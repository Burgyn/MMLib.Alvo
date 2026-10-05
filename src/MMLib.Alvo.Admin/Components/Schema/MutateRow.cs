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

    /// <summary>Gets or sets the literal as typed, or the CEL.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether a literal row sets the field to empty (<c>null</c>).</summary>
    public bool Empty { get; set; }
}
