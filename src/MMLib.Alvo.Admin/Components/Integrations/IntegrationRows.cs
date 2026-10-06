using MMLib.Alvo.Admin.Internal;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Components.Integrations;

/// <summary>One declared endpoint, as Integrations lists it.</summary>
/// <param name="Name">Its key.</param>
/// <param name="Url">Its URL.</param>
/// <param name="SecretRef">Its secret's name.</param>
/// <param name="Staged">Whether the applied revision lacks it, or holds it otherwise.</param>
/// <param name="Drawable">Whether the sheet can open it: only the three keys it draws, each a string.</param>
/// <param name="Uses">The hooks that post to it.</param>
internal sealed record EndpointRow(
    string Name, string Url, string SecretRef, bool Staged, bool Drawable, IReadOnlyList<DescriptorLens.IntegrationUse> Uses);

/// <summary>One declared template, as Integrations lists it.</summary>
/// <param name="Name">Its key.</param>
/// <param name="Subject">Its subject, or empty.</param>
/// <param name="HasBodyFile">Whether it reads a <c>bodyFile</c>, which this build refuses to send.</param>
/// <param name="Staged">Whether the applied revision lacks it, or holds it otherwise.</param>
/// <param name="Drawable">Whether the sheet can open it: subject and body only, a body present.</param>
/// <param name="Uses">The hooks that send it.</param>
internal sealed record TemplateRow(
    string Name, string Subject, bool HasBodyFile, bool Staged, bool Drawable, IReadOnlyList<DescriptorLens.IntegrationUse> Uses);

/// <summary>The working copy's endpoints and templates as Integrations lists them (spec §4.7, ruling B5).</summary>
internal static class IntegrationRows
{
    private static readonly HashSet<string> _endpointKeys = new(StringComparer.Ordinal) { "url", "secretRef", "description" };
    private static readonly HashSet<string> _templateKeys = new(StringComparer.Ordinal) { "subject", "body" };

    /// <summary>The endpoints, in the working copy's order.</summary>
    /// <param name="workingJson">The working copy's text.</param>
    /// <param name="appliedJson">The applied revision's text.</param>
    /// <returns>One row per declared endpoint.</returns>
    public static IReadOnlyList<EndpointRow> Endpoints(string workingJson, string appliedJson)
    {
        var applied = ByName(DescriptorLens.Endpoints(appliedJson));
        var uses = DescriptorLens.IntegrationUses(workingJson);
        return [.. DescriptorLens.Endpoints(workingJson).Select(pair => new EndpointRow(
            pair.Key,
            DescriptorLens.TextOf(pair.Value, "url") ?? string.Empty,
            DescriptorLens.TextOf(pair.Value, "secretRef") ?? string.Empty,
            IsStaged(pair, applied),
            Drawable(pair.Value, _endpointKeys, "url", "secretRef"),
            [.. uses.Where(use => use.Kind == "endpoint" && use.Name == pair.Key)]))];
    }

    /// <summary>The templates, in the working copy's order.</summary>
    /// <param name="workingJson">The working copy's text.</param>
    /// <param name="appliedJson">The applied revision's text.</param>
    /// <returns>One row per declared template, a <c>bodyFile</c> one included.</returns>
    public static IReadOnlyList<TemplateRow> Templates(string workingJson, string appliedJson)
    {
        var applied = ByName(DescriptorLens.Templates(appliedJson));
        var uses = DescriptorLens.IntegrationUses(workingJson);
        return [.. DescriptorLens.Templates(workingJson).Select(pair => new TemplateRow(
            pair.Key,
            DescriptorLens.TextOf(pair.Value, "subject") ?? string.Empty,
            DescriptorLens.HasBodyFile(pair.Value),
            IsStaged(pair, applied),
            Drawable(pair.Value, _templateKeys, "body"),
            [.. uses.Where(use => use.Kind == "template" && use.Name == pair.Key)]))];
    }

    private static Dictionary<string, JsonElement> ByName(IReadOnlyList<KeyValuePair<string, JsonElement>> declared)
        => declared.DistinctBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static bool IsStaged(KeyValuePair<string, JsonElement> declared, Dictionary<string, JsonElement> applied)
        => !applied.TryGetValue(declared.Key, out var before) || !JsonElement.DeepEquals(before, declared.Value);

    private static bool Drawable(JsonElement declared, HashSet<string> keys, params string[] required)
        => declared.ValueKind == JsonValueKind.Object
           && declared.EnumerateObject().All(property => keys.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.String)
           && required.All(key => declared.TryGetProperty(key, out _));
}
