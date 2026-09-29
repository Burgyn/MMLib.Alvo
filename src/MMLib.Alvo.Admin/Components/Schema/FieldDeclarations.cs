using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Management;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// Each Fields row's declaration as the descriptor writes it, cascaded from the entity screen to the list.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the list needs the JSON and not only <see cref="MMLib.Alvo.Schema.FieldSchema"/>.</b> The resolved schema
/// carries what the Data API serves, and <c>hidden</c>/<c>readOnly</c> are policy it does not carry (#267); a staged
/// field's refused facets are not in it either. The declaration is the one place both are.
/// </para>
/// <para>
/// <b>The working copy first, then the applied revision.</b> Every row the list draws is a field the copy declares,
/// except one the copy removed — which is drawn struck through until the apply, from what the applied revision says.
/// </para>
/// <para>
/// Cascaded and internal, for <see cref="StagedView"/>'s reason: a parameter would put it in the package's surface.
/// </para>
/// </remarks>
internal sealed class FieldDeclarations
{
    private readonly IReadOnlyDictionary<string, JsonElement> _working;
    private readonly IReadOnlyDictionary<string, JsonElement> _applied;
    private readonly IReadOnlyList<ManagementRefusedFeature> _refused;

    private FieldDeclarations(
        IReadOnlyDictionary<string, JsonElement> working, IReadOnlyDictionary<string, JsonElement> applied,
        IReadOnlyList<ManagementRefusedFeature> refused)
    {
        _working = working;
        _applied = applied;
        _refused = refused;
    }

    /// <summary>No declarations — what the list reads when no entity screen cascaded any.</summary>
    public static FieldDeclarations None { get; } = new(
        new Dictionary<string, JsonElement>(), new Dictionary<string, JsonElement>(), []);

    /// <summary>Reads one entity's field declarations out of the working and the applied document.</summary>
    /// <param name="workingJson">The working copy.</param>
    /// <param name="appliedJson">The applied revision the copy was taken from.</param>
    /// <param name="entity">The entity, by the name the copy gives it.</param>
    /// <param name="refused">Every refusal the build publishes, which a staged row may carry.</param>
    /// <remarks>
    /// The applied side is read under the name the entity is <em>applied</em> under, which is not its working name
    /// once the copy renamed it: the rename's <c>renamedFrom</c>, resolved the way <c>WorkingCopy.AppliedEntityOf</c>
    /// resolves it (<see cref="StagedChanges.OriginOf"/>). Read under the working name, a renamed entity's removed
    /// fields lost their badges.
    /// </remarks>
    public static FieldDeclarations From(
        string workingJson, string appliedJson, string entity, IReadOnlyList<ManagementRefusedFeature> refused)
        => new(
            DescriptorLens.FieldDeclarations(workingJson, entity),
            DescriptorLens.FieldDeclarations(appliedJson, AppliedNameOf(workingJson, appliedJson, entity)),
            refused);

    /// <summary>The name the applied document holds the entity under; its working name when it holds neither.</summary>
    private static string AppliedNameOf(string workingJson, string appliedJson, string entity)
        => StagedChanges.OriginOf(
            (Parse(workingJson)?["entities"] as JsonObject)?[entity], entity, Parse(appliedJson)?["entities"] as JsonObject)
            ?? entity;

    /// <summary>Parses, or answers nothing, for <c>DescriptorLens</c>'s reason: a pasted copy may not parse.</summary>
    private static JsonNode? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The build's refusals one field's declaration carries (<see cref="FieldBadges.Refused"/>).</summary>
    /// <param name="field">The field's name.</param>
    public IReadOnlyList<ManagementRefusedFeature> RefusedOf(string field) => FieldBadges.Refused(Of(field), _refused);

    /// <summary>One field's declaration, or <see langword="null"/> when neither document declares it.</summary>
    /// <param name="field">The field's name.</param>
    public JsonElement? Of(string field)
        => _working.TryGetValue(field, out var working) ? working
            : _applied.TryGetValue(field, out var applied) ? applied
            : null;
}
