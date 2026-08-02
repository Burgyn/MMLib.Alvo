using System.Runtime.CompilerServices;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Verify's per-project settings. A copy rather than the shared <c>test/_shared</c> one, for the reason this
/// project opts out of <c>_shared</c> altogether: that file comes with the public-API approval gate, which
/// this project must not run (it has no sibling production assembly of its own). The same trade
/// <c>MMLib.Alvo.Schema.Tests</c> makes.
/// </summary>
internal static class VerifyModuleInit
{
    /// <summary>
    /// UTF-8 with no BOM, so a baseline is a plain text file. <c>.gitattributes</c> pins
    /// <c>*.verified.txt</c> to LF, and a BOM would put three bytes in front of a JSON document that no
    /// reader of the snapshot ever asked about.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize() => VerifierSettings.UseUtf8NoBom();
}
