using MMLib.Alvo.Management;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Components.Home;

/// <summary>
/// What one descriptor declares and this build does not honour — what Overview lists under "Declared, not honoured
/// by this build" — and, apart from it, the one quiet line naming the metadata this dashboard does not show.
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
/// <b>The metadata is not in the panel, and is one line in the dashboard's own words.</b> <c>description</c>,
/// <c>branding</c> and a format's <c>description</c> are metadata, which the build honours by definition
/// (<c>CapabilityReport</c> lists them in neither half), so a row under "not honoured by this build" would be false;
/// what is missing is this dashboard's rendering of them, which is #268's and #271's (B2 review, finding 1).
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

    /// <summary>
    /// The one line naming the project metadata <paramref name="descriptorJson"/> declares and this dashboard does not
    /// show, with the issues that will show it; <see langword="null"/> when it declares none.
    /// </summary>
    /// <param name="descriptorJson">The descriptor as stored.</param>
    public static string? UnshownLine(string descriptorJson)
    {
        using var document = Parse(descriptorJson);
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;
        var declared = _metadata.Where(key => key.IsDeclaredBy(root)).ToList();
        return declared.Count == 0
            ? null
            : $"Declared metadata this dashboard does not show yet: {string.Join(", ", declared.Select(key => key.Key))} "
                + $"({string.Join(", ", declared.Select(key => key.Issue).Distinct(StringComparer.Ordinal))}).";
    }

    /// <summary>The metadata keys, each with the issue that will show it.</summary>
    private static readonly IReadOnlyList<(string Key, string Issue, Func<JsonElement, bool> IsDeclaredBy)> _metadata =
    [
        ("description", "#268", root => DeclaredSlots.NotEmpty(DeclaredSlots.Member(root, "description"))),
        ("branding", "#268", root => DeclaredSlots.NotEmpty(DeclaredSlots.Member(root, "branding"))),
        ("formats.*.description", "#271", FormatDescribed),
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
