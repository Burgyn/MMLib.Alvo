using System.Text.RegularExpressions;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// The id grammars of the one catalog namespace (spec 2026-10-14 E3): extension ids, and — for a retired id, which may
/// have been any kind — command and function names too. Event names derived from ids map <c>-</c> to <c>_</c>.
/// </summary>
internal static partial class ExtensionIds
{
    internal static bool IsExtensionId(string id) => Extension().IsMatch(id);

    internal static bool IsRetirableId(string id) => Extension().IsMatch(id) || Function().IsMatch(id) || Command().IsMatch(id);

    internal static string DerivedSegment(string id) => id.Replace('-', '_');

    [GeneratedRegex(@"\A[a-z][a-z0-9-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Extension();

    [GeneratedRegex(@"\A[a-z][a-z0-9_-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Function();

    [GeneratedRegex(@"\A[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Command();
}
