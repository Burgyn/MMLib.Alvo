using MMLib.Alvo.Management;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Components.Home;

/// <summary>
/// What one descriptor declares and this build does not honour — what Overview lists under "Declared, not honoured
/// by this build": the build's warned slots the descriptor really declares, then the project metadata this dashboard
/// does not show.
/// </summary>
/// <remarks>
/// <para>
/// <b>Intersected with the descriptor, because a list of subsystems nobody asked for trains an operator to ignore the
/// panel.</b> <c>capabilities.warned</c> names every block and key this build does not fully honour; a project that
/// declares none of them has nothing to be told. What counts as declared is <see cref="DeclaredSlots"/>' call.
/// </para>
/// <para>
/// <b>Not split into "not running" and "partly running", and that is the capability contract's call rather than
/// this screen's.</b> A warned row carries a name and the framework's own consequence sentence, and no signal of how
/// much of the block runs — <c>templates</c> and <c>webhooks</c> are delivered from an after-hook and dead from
/// automation, <c>functions</c> never runs at all, and both arrive in the same shape. Telling them apart here would
/// mean reading the prose, which is served verbatim precisely so that no client reinterprets it. So the panel's
/// subtitle says some rows run in part, and each consequence says which.
/// </para>
/// <para>
/// <b>The metadata rows are the dashboard's own sentence, and they say so.</b> <c>description</c>, <c>branding</c>
/// and a format's <c>description</c> are metadata, which the build honours by definition (<c>CapabilityReport</c>
/// lists them in neither half), so no core sentence exists for them; what is not honoured is this dashboard's
/// rendering of them, which is #268's and #271's. A sentence about the dashboard belongs to the dashboard.
/// </para>
/// </remarks>
internal static class DeclaredLimits
{
    /// <summary>The warned rows <paramref name="descriptorJson"/> declares, in the order the build reports them.</summary>
    /// <param name="descriptorJson">The descriptor as stored.</param>
    /// <param name="capabilities">What this build honours, warns about and refuses.</param>
    public static IReadOnlyList<ManagementWarnedBlock> Of(string descriptorJson, ManagementCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        using var document = Parse(descriptorJson);
        if (document is null)
        {
            return [];
        }

        var root = document.RootElement;
        return [.. capabilities.Warned.Where(block => DeclaredSlots.Declares(root, block.Block))];
    }

    /// <summary>The project metadata <paramref name="descriptorJson"/> declares that this dashboard does not show.</summary>
    /// <param name="descriptorJson">The descriptor as stored.</param>
    public static IReadOnlyList<UnshownKey> Unshown(string descriptorJson)
    {
        using var document = Parse(descriptorJson);
        if (document is null)
        {
            return [];
        }

        var root = document.RootElement;
        return [.. _metadata.Where(key => key.IsDeclaredBy(root)).Select(key => key.Row)];
    }

    /// <summary>The metadata keys, each with the sentence the Overview gives it.</summary>
    private static readonly IReadOnlyList<(UnshownKey Row, Func<JsonElement, bool> IsDeclaredBy)> _metadata =
    [
        (new("description", "The project's description is not shown anywhere in this dashboard yet (#268)."),
            root => DeclaredSlots.NotEmpty(DeclaredSlots.Member(root, "description"))),
        (new("branding", "The project's title and logo are not shown anywhere in this dashboard yet (#268)."),
            root => DeclaredSlots.NotEmpty(DeclaredSlots.Member(root, "branding"))),
        (new("formats.*.description", "The formats are honoured; what each one's description says is not shown "
            + "anywhere in this dashboard yet (#271)."),
            FormatDescribed),
    ];

    /// <summary>Whether any declared format carries a description.</summary>
    private static bool FormatDescribed(JsonElement root)
        => DeclaredSlots.Member(root, "formats") is { ValueKind: JsonValueKind.Object } formats
            && formats.EnumerateObject().Any(format => DeclaredSlots.Text(format.Value, "description") is { Length: > 0 });

    /// <summary>The descriptor's document when it is a JSON object, else <see langword="null"/>.</summary>
    private static JsonDocument? Parse(string descriptorJson)
    {
        try
        {
            var document = JsonDocument.Parse(descriptorJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
        }
        catch (JsonException)
        {
            /* A descriptor that does not parse declares nothing. */
        }

        return null;
    }
}

/// <summary>A declared metadata key this dashboard does not render, with the dashboard's sentence about it.</summary>
/// <param name="Key">The key as the descriptor spells it; <c>*</c> stands for every format.</param>
/// <param name="Sentence">What is not shown, and the issue that will show it.</param>
internal sealed record UnshownKey(string Key, string Sentence);
