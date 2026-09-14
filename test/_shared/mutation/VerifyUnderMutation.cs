using System.Runtime.CompilerServices;
using VerifyTests;

namespace MMLib.Alvo.Tests;

/// <summary>
/// Turns off Verify's process-wide "prefix has already been used" guard when the test assembly is running
/// under Stryker, and only then.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why, measured (issues #142 and #222).</b> Verify's <c>PrefixUnique</c> remembers every snapshot file
/// prefix a <em>process</em> has verified, so that two tests writing to one baseline are caught. Stryker's
/// MTP runner keeps <b>one test-server process alive across mutants</b> and re-runs the whole assembly in it
/// once per mutant, so from the second mutant onwards every Verify-based test throws
/// <c>The prefix has already been used: …</c>. xUnit reports that as an MTP
/// <c>ErrorTestNodeStateProperty</c>, Stryker counts an errored node as a failing test, and the mutant is
/// recorded <c>Killed</c> — every mutant, for a reason that has nothing to do with the mutant.
/// </para>
/// <para>
/// The first mutant of a run is clean, because the process is fresh and the prefix set empty. That is exactly
/// why #142's investigation ruled the approval gate out: Stryker's <em>initial</em> test run passes, so
/// <c>--break-on-initial-test-failure</c> never fires. It is also why the three tests #222 named as flaky
/// were never at fault — Stryker's trace line prints one node's state next to the following node's name, so
/// whichever innocent test was reported after the Verify test took the blame, and that changed run to run.
/// </para>
/// <para>
/// <b>Why it is gated rather than always on.</b> Outside mutation the guard is a real safety net — two tests
/// sharing one baseline is a defect — so it stays on for <c>dotnet test</c> and every ring. Inside a mutation
/// run "this prefix was used before" is the expected state, not a defect.
/// </para>
/// </remarks>
internal static class VerifyUnderMutation
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (MutationRun.IsActive)
        {
            VerifierSettings.DisableRequireUniquePrefix();
        }
    }
}
