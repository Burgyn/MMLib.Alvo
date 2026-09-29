using System.Text;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>
/// The agent's instructions, embedded in the package and fixed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not configurable, and that is #29 §5 rather than an omission.</b> A deployment that could rewrite these could
/// delete "propose, never apply" and "quote the framework's refusals verbatim". The real guard is still the tool set,
/// which has no member that writes; this text is what makes the agent useful inside that guard.
/// </para>
/// <para>
/// <b>A Markdown resource rather than a <c>const</c></b>, so it is diffable in review and its drift tests can parse
/// it: the tool list, the field types and every worked example are checked against what they describe.
/// </para>
/// </remarks>
internal static class AssistantInstructions
{
    /// <summary>The manifest name the csproj gives the resource.</summary>
    internal const string ResourceName = "MMLib.Alvo.Ai.Instructions.schema-assistant.md";

    /// <summary>The first line, which names the version a transcript was produced under.</summary>
    internal const string VersionLine = "<!-- alvo-schema-assistant v3 -->";

    /// <summary>The instructions the agent runs under.</summary>
    internal static string Text { get; } = Load();

    private static string Load()
    {
        var assembly = typeof(AssistantInstructions).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource '{ResourceName}' is missing from {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        return reader.ReadToEnd();
    }
}
