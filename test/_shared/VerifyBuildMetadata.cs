using System.Text.RegularExpressions;

namespace MMLib.Alvo.Tests.Api;

/// <summary>
/// Strips Verify's build-time <c>[assembly: AssemblyMetadata("Verify.&lt;key&gt;", …)]</c> entries out of a
/// generated public-API baseline.
/// </summary>
/// <remarks>
/// <para>
/// Verify's build targets stamp one entry per build-time fact it wants to recover at runtime — directories,
/// the solution name, the target frameworks. <b>None of them are Alvo's public API</b>, and every one is
/// derived from how and where the build ran, so each is a way for a baseline to fail for a reason that has
/// nothing to do with the public surface: the directories carry an absolute checkout path (different on every
/// machine and in CI), and the rest vary with the build driver — a Stryker mutation run builds the solution
/// itself and an approval test errored there while passing under <c>dotnet test</c>. Verify 31.27.0 also
/// <em>added</em> one (<c>Verify.IntermediateDirectory</c>) to the two that existed when this filter was first
/// written, so a patch bump alone was enough to break the gate. The whole family therefore goes. Every
/// non-Verify <c>AssemblyMetadata</c> entry (<c>RepositoryUrl</c>) is genuine metadata and stays.
/// </para>
/// <para>
/// Shared rather than copied because two assemblies now need it, and for a reason worth stating: it applies to
/// an assembly that references Verify <em>transitively</em> as much as to one that references it directly.
/// <c>MMLib.Alvo.Testing.EntityFrameworkCore</c> declares no Verify dependency at all and still gets stamped,
/// because <c>MMLib.Alvo.Testing</c>'s <c>Verify.XunitV3</c> reference flows to it — which is exactly the kind
/// of thing a second hand-written copy of this regex would have been written without knowing.
/// </para>
/// </remarks>
internal static class VerifyBuildMetadata
{
    private const string EntryPattern =
        "\\[assembly: System\\.Reflection\\.AssemblyMetadata\\(\"Verify\\.\\w+\", .*?\\)\\]\r?\n";

    /// <summary>Removes every Verify build-metadata entry from <paramref name="publicApi"/>.</summary>
    /// <param name="publicApi">A generated public-API baseline.</param>
    internal static string RemoveFrom(string publicApi) =>
        Regex.Replace(publicApi, EntryPattern, string.Empty, RegexOptions.Singleline);
}
