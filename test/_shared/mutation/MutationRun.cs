namespace MMLib.Alvo.Tests;

/// <summary>
/// Whether this test process was started by Stryker. Linked into <b>every</b> test project from
/// <c>test/Directory.Build.props</c>, because the two things that read it — Verify's prefix guard and the
/// public-API approval gate — are not confined to the projects that take <c>test/_shared</c>'s conditional
/// set.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both markers, because the variable is coupled to the RUNNER rather than to Stryker.</b> The MTP runner
/// — the one every <c>stryker-config*.json</c> pins — sets <c>STRYKER_MUTANT_FILE</c>, naming the
/// memory-mapped file that carries the active mutant id; the VsTest runner sets
/// <c>STRYKER_MUTANT_ID_CONTROL_VAR</c> instead. Only the first is reachable today, and that is exactly why
/// the second is here: a config that drops <c>test-runner: mtp</c>, or a new leg added without it, would
/// otherwise go back to reporting 100 % while the canary — which does pin <c>mtp</c> — stayed green.
/// </para>
/// <para>
/// Neither name is a documented contract, so if one is renamed upstream the gates below silently stop
/// applying. What catches that is the canary leg (<c>stryker-config.canary.json</c>): it mutates a file its
/// test assembly cannot reach, so its only honest score is 0 %, and anything above that says a test is being
/// counted as a killer for a reason that is not the mutant.
/// </para>
/// <para>
/// Read once into a static, because the value cannot change inside a process and the alternative is an
/// environment read per test.
/// </para>
/// </remarks>
internal static class MutationRun
{
    private static readonly string[] _strykerTestHostMarkers =
        ["STRYKER_MUTANT_FILE", "STRYKER_MUTANT_ID_CONTROL_VAR"];

    /// <summary>True when Stryker started this test host.</summary>
    internal static bool IsActive { get; } = _strykerTestHostMarkers.Any(
        marker => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(marker)));

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
