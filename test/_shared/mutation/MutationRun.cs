namespace MMLib.Alvo.Tests;

/// <summary>
/// Whether this test process was started by Stryker. Linked into <b>every</b> test project from
/// <c>test/Directory.Build.props</c>, because the two things that read it — Verify's prefix guard and the
/// public-API approval gate — are not confined to the projects that take <c>test/_shared</c>'s conditional
/// set.
/// </summary>
/// <remarks>
/// <para>
/// <c>STRYKER_MUTANT_FILE</c> is the variable Stryker sets in the test host; it names the memory-mapped file
/// carrying the active mutant id. It is an implementation detail of the runner rather than a documented
/// contract, so if it is ever renamed every gate below silently stops applying. What catches that is the
/// canary leg (<c>stryker-config.canary.json</c>): it mutates a file its test assembly cannot reach, so its
/// only honest score is 0 %, and anything above that says a test is being counted as a killer for a reason
/// that is not the mutant.
/// </para>
/// <para>
/// Read once into a static, because the value cannot change inside a process and the alternative is an
/// environment read per test.
/// </para>
/// </remarks>
internal static class MutationRun
{
    private const string StrykerTestHostMarker = "STRYKER_MUTANT_FILE";

    /// <summary>True when Stryker started this test host.</summary>
    internal static bool IsActive { get; } =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(StrykerTestHostMarker));

    /// <summary>
    /// The one reason string every public-API approval gate skips under. A mutation run instruments the
    /// assembly it mutates — Stryker injects its own public <c>MutantControl</c> type into it — so the
    /// surface the generator reads there is not the shipped one, and the PDB no longer matches the rewritten
    /// IL, which is what <c>Mono.Cecil.Cil.SymbolsNotMatchingException</c> reports. Neither is a fact about
    /// the mutant, and both were counted as kills (issue #142).
    /// </summary>
    internal const string PublicApiGateSkipReason =
        "The public-API approval gate does not apply under mutation: the assembly is instrumented, so the "
        + "surface read is not the shipped one and the symbols no longer match the IL (see #142).";
}
