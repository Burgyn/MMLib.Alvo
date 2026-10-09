using Microsoft.Playwright;
using System.Reflection;
using Xunit.v3;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A Playwright trace of every session, kept only for a scenario that failed: <c>playwright-traces/</c> beside the
/// test assembly, which CI uploads when the job fails.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why every session records.</b> The flakes this suite has had fail one run in thirty and do not reproduce on
/// demand; a timeout's stack says which wait expired, never what the page was doing. A trace holds the DOM at each
/// step, the console and the network, so the next one is read rather than guessed at. Recording is cheap; keeping a
/// trace of every passing scenario is not, so a passing scenario's trace is deleted.
/// </para>
/// <para>
/// DOM snapshots without screenshots: the screencast is the expensive half, and the snapshots already draw the page
/// in the trace viewer.
/// </para>
/// <para>
/// A session stops its trace as it is disposed, which is before the test's result is known; it waits under
/// <c>pending/</c> until <see cref="KeepTracesOfFailedScenariosAttribute"/> has read the result. A scenario that times
/// out abandons its session undisposed, so it leaves no trace — the Playwright waits inside it expire well before the
/// scenario's own timeout, which is how every flake so far has failed.
/// </para>
/// </remarks>
internal static class ScenarioTraces
{
    /// <summary>Where a failed scenario's traces are kept: the path CI uploads.</summary>
    public static string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "playwright-traces");

    private static string Pending { get; } = Path.Combine(Folder, "pending");

    /// <summary>Starts recording the context.</summary>
    /// <param name="context">A session's browser context, before its first page.</param>
    /// <returns>A task that completes once recording has started.</returns>
    public static Task StartAsync(IBrowserContext context)
        => context.Tracing.StartAsync(new() { Snapshots = true, Title = TestContext.Current.Test?.TestDisplayName });

    /// <summary>Stops recording and leaves the trace waiting for the test's result.</summary>
    /// <param name="context">The session's browser context, still open.</param>
    /// <returns>A task that completes once the trace is written.</returns>
    public static async Task StopAsync(IBrowserContext context)
    {
        Directory.CreateDirectory(Pending);
        var path = Path.Combine(Pending, $"{Key(TestContext.Current.Test?.UniqueID)}-{Guid.NewGuid():N}.zip");
        try
        {
            await context.Tracing.StopAsync(new() { Path = path }).ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            /* The browser has gone: there is no trace to keep, and the scenario's own failure is the one to report. */
        }
    }

    /// <summary>Keeps the test's pending traces when it failed, and deletes them otherwise.</summary>
    /// <param name="test">The test that has just finished.</param>
    /// <param name="failed">Whether it failed.</param>
    public static void Settle(IXunitTest test, bool failed)
    {
        if (!Directory.Exists(Pending))
        {
            return;
        }

        var traces = Directory.GetFiles(Pending, $"{Key(test.UniqueID)}-*.zip");
        for (var index = 0; index < traces.Length; index++)
        {
            if (failed)
            {
                File.Move(traces[index], Path.Combine(Folder, $"{Name(test.TestDisplayName)}-{index + 1}.zip"), overwrite: true);
            }
            else
            {
                File.Delete(traces[index]);
            }
        }
    }

    private static string Key(string? uniqueId) => uniqueId ?? "no-test";

    /// <summary>The display name as a file name: a theory's arguments carry characters no file system takes.</summary>
    private static string Name(string displayName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string([.. displayName.Select(character => invalid.Contains(character) || character is ' ' or '"' ? '_' : character)]);
        return name.Length <= 150 ? name : name[..150];
    }
}

/// <summary>Hands each finished test's traces to <see cref="ScenarioTraces.Settle"/> with its result.</summary>
/// <remarks>
/// <c>After</c> runs once the result is known (<see cref="TestContext.TestState"/> is set by then; measured), and after
/// the scenario's <c>await using</c> session has stopped its trace.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class KeepTracesOfFailedScenariosAttribute : BeforeAfterTestAttribute
{
    /// <inheritdoc/>
    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        ScenarioTraces.Settle(test, TestContext.Current.TestState?.Result is TestResult.Failed);
    }
}
