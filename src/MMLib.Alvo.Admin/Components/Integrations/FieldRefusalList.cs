namespace MMLib.Alvo.Admin.Components.Integrations;

/// <summary>The one way a draft adds a field's refusal to the list its sheet draws.</summary>
internal static class FieldRefusalList
{
    /// <summary>Adds the refusal under its field, when there is one.</summary>
    /// <param name="refusals">The refusals so far, in the order the sheet draws its fields.</param>
    /// <param name="field">The field's input id.</param>
    /// <param name="refusal">The refusal, or <see langword="null"/> when the field is fine.</param>
    public static void AddRefusal(this List<KeyValuePair<string, string>> refusals, string field, string? refusal)
    {
        if (refusal is not null)
        {
            refusals.Add(new KeyValuePair<string, string>(field, refusal));
        }
    }
}
