using System.Diagnostics;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Invariants;

/// <summary>One violation Vacuum reported, attributed to the rule that reported it.</summary>
/// <param name="Code">The rule id — <c>alvo-operation-id-shape</c>, <c>oas3-api-servers</c>, and so on.</param>
/// <param name="Severity">0 = error, 1 = warning, 2 = info, 3 = hint.</param>
/// <param name="Message">The rule's own message, which carries the offending value.</param>
internal sealed record VacuumViolation(string Code, int Severity, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{Code} (severity {Severity}): {Message}";
}

/// <summary>
/// Runs the pinned Vacuum binary over one OpenAPI document with <c>schema/openapi-ruleset.yaml</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>spectral-report</c> rather than <c>lint</c>, and that is not a preference.</b> <c>vacuum lint</c>
/// prints a rule's <em>description</em> and never its id, so an exit code is all a caller gets — and this
/// suite has to assert <em>which</em> rule fired, or the mutation battery would pass for a document that
/// violated some entirely different rule. <c>spectral-report</c> writes a JSON array whose entries carry
/// <c>code</c>, <c>severity</c>, <c>message</c> and <c>range</c>.
/// </para>
/// <para>
/// <b>Exit code 2 is a crash, not a verdict.</b> Measured: an unresolvable <c>$ref</c> makes vacuum 0.30.3
/// either report <c>unable to build unresolved model</c> or panic outright, and a panic exits 2. Treating
/// anything but 0 or 1 as a lint result would let a crash read as a legitimate red — and, worse, would leave
/// a caller unable to tell "no violations" from "never ran".
/// </para>
/// <para>
/// <b>The binary is never skipped when missing.</b> <c>scripts/ensure-vacuum</c> resolves it once per
/// process, and a failure there throws with the script's own stderr. A suite that self-skipped would report
/// success on a machine where nothing was linted at all, which is the failure mode CI's
/// <c>TESTINGPLATFORM_EXITCODE_IGNORE=8</c> on Windows makes invisible.
/// </para>
/// </remarks>
internal static class VacuumRunner
{
    /// <summary>Resolved once per process: the script is idempotent, but it still costs a process launch.</summary>
    private static readonly Lazy<string> _binary = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The one ruleset, shared with <c>scripts/lint-api</c>.</summary>
    internal static string Ruleset { get; } =
        Path.Combine(RepositoryRoot.Find(), "schema", "openapi-ruleset.yaml");

    /// <summary>Lints one document and returns everything reported at error or warning severity.</summary>
    /// <param name="document">The served OpenAPI document.</param>
    /// <param name="name">A name for the temporary file, so a failure message says which document it was.</param>
    /// <remarks>
    /// Warnings are included because the gate runs at <c>--fail-severity warn</c>: after the mutes the
    /// ruleset declares, the document is clean at every severity, so a new warning is a regression rather
    /// than noise to accumulate. Info and hint are dropped — vacuum emits those for stylistic preferences
    /// this repository has not adopted.
    /// </remarks>
    internal static IReadOnlyList<VacuumViolation> Lint(JsonObject document, string name)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var directory = Directory.CreateTempSubdirectory("alvo-openapi-");
        try
        {
            var documentFile = Path.Combine(directory.FullName, $"{name}.json");
            var reportFile = Path.Combine(directory.FullName, $"{name}.report.json");
            File.WriteAllText(documentFile, document.ToJsonString());

            Run(documentFile, reportFile, name);

            return Violations(reportFile);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Invokes the binary, and distinguishes "reported violations" from "did not run".</summary>
    private static void Run(string documentFile, string reportFile, string name)
    {
        using var process = Process.Start(new ProcessStartInfo(_binary.Value)
        {
            ArgumentList = { "spectral-report", "--ruleset", Ruleset, documentFile, reportFile },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException($"vacuum did not start for '{name}'");

        // Both streams at once, deliberately. Reading stdout to the end and *then* stderr is the classic
        // Process deadlock: enough output on the stream nobody is reading fills its pipe buffer, the child
        // blocks writing and the parent blocks reading. The path where that matters is exactly the one this
        // method exists to report — a vacuum crash, which is chatty on stderr — and a hung run costs the
        // whole CI job rather than one red test.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Task.WaitAll(stdout, stderr);
        process.WaitForExit();
        var output = stdout.Result;
        var error = stderr.Result;

        if (process.ExitCode is not (0 or 1))
        {
            throw new InvalidOperationException(
                $"vacuum exited {process.ExitCode} for '{name}' — that is a crash, not a lint verdict. "
                + $"stdout: {output}{Environment.NewLine}stderr: {error}");
        }

        if (!File.Exists(reportFile))
        {
            throw new InvalidOperationException(
                $"vacuum exited {process.ExitCode} for '{name}' and wrote no report — nothing was linted. "
                + $"stdout: {output}{Environment.NewLine}stderr: {error}");
        }
    }

    /// <summary>Reads the Spectral-shaped report, keeping errors and warnings.</summary>
    private static List<VacuumViolation> Violations(string reportFile)
    {
        var report = JsonNode.Parse(File.ReadAllText(reportFile));

        // An empty report is written as `null` rather than `[]` by some vacuum versions, and "nothing to
        // read" and "nothing reported" must not be told apart by a crash.
        return report is not JsonArray results
            ? []
            :
            [
                .. from result in results
                   let entry = result!.AsObject()
                   let severity = entry["severity"]!.GetValue<int>()
                   where severity <= 1
                   select new VacuumViolation(
                       entry["code"]!.GetValue<string>(),
                       severity,
                       entry["message"]?.GetValue<string>() ?? string.Empty)
            ];
    }

    /// <summary>Locates the binary <c>scripts/ensure-vacuum</c> has already put in place.</summary>
    /// <remarks>
    /// <para>
    /// <b>It looks the file up rather than running the script, and that is a Windows decision.</b> Invoking
    /// <c>bash scripts/ensure-vacuum</c> and taking the path it prints works on Linux and macOS and fails on
    /// the Windows CI leg: Git Bash prints an MSYS path (<c>/d/a/MMLib.Alvo/…</c>) that .NET on Windows
    /// cannot open, so the suite would look for a binary that is sitting right there. Reading the one
    /// location the script writes to needs no shell, no path translation and no process launch, and keeps
    /// the script the single authority on <em>acquiring</em> it.
    /// </para>
    /// <para>
    /// <c>scripts/test-ring2</c> runs <c>ensure-vacuum</c> before the integration loop, so by the time this
    /// runs the file exists. A run that skipped that step fails here with the instruction — deliberately,
    /// because a suite that self-skipped would report success on a machine where nothing was linted.
    /// </para>
    /// <para>
    /// A <c>vacuum</c> already on <c>PATH</c> is <b>not</b> used, unlike in the script: every document this
    /// suite lints is linted by the pinned build, whatever a developer happens to have installed.
    /// </para>
    /// </remarks>
    private static string Resolve()
    {
        var binary = OperatingSystem.IsWindows() ? "vacuum.exe" : "vacuum";
        var path = Path.Combine(RepositoryRoot.Find(), "artifacts", "tools", "vacuum", binary);

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"the pinned Vacuum binary is not at '{path}'. Run scripts/ensure-vacuum (scripts/test-ring2 "
                + "does it for you) — this suite fails rather than skipping, because a skipped lint is a lint "
                + "nobody ran.");
        }

        return path;
    }

    /// <summary>The rule ids the ruleset declares as Alvo's own.</summary>
    /// <remarks>
    /// Read out of the YAML with a line match rather than with a YAML parser: the ids are the only lines
    /// shaped <c>  alvo-…:</c> in the file, and taking a dependency to read six lines would be the more
    /// surprising choice. What matters is that the set comes from the ruleset and not from a list restated in
    /// a test — a rule added there must not be able to arrive with no mutation proving it fires.
    /// </remarks>
    internal static IReadOnlySet<string> AlvoRuleIds()
    {
        var ids = File.ReadLines(Ruleset)
            .Select(line => line.TrimEnd())
            .Where(line => line.StartsWith("  alvo-", StringComparison.Ordinal) && line.EndsWith(':'))
            .Select(line => line.Trim().TrimEnd(':'))
            .ToHashSet(StringComparer.Ordinal);

        if (ids.Count == 0)
        {
            throw new InvalidOperationException(
                $"no Alvo rule ids were found in {Ruleset} — the completeness check below would pass vacuously");
        }

        return ids;
    }
}
