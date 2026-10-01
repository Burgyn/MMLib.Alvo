using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>
/// Every graded turn in full, one JSON line each, so a pass can be audited as well as a failure.
/// </summary>
/// <remarks>
/// <para>
/// The table says <em>how many</em> turns passed; only the turn itself says whether a grader passed it for the right
/// reason. A line holds the verdict and its diagnostic, the reply, the proposal's changed paths and refusals, and every
/// tool call with its arguments and answer.
/// </para>
/// <para>
/// <b>No secret reaches it.</b> The connection's key is never part of a turn, the model's endpoint is written as its
/// host only (the query string is where credentials end up in practice), and a failed turn carries the assistant's
/// own secret-free sentence plus a status code. The file lands under <c>artifacts/</c>, which is gitignored.
/// </para>
/// </remarks>
internal sealed class EvalTrace : IDisposable
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly StreamWriter _writer;

    private EvalTrace(string path, StreamWriter writer)
    {
        Path = path;
        _writer = writer;
    }

    /// <summary>The file the trace is written to.</summary>
    internal string Path { get; }

    /// <summary>Opens a new trace file under <paramref name="directory"/> and writes its header line.</summary>
    /// <param name="directory">Where trace files go.</param>
    /// <param name="options">The run being traced.</param>
    internal static EvalTrace Open(string directory, EvalOptions options)
    {
        Directory.CreateDirectory(directory);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var path = System.IO.Path.Combine(directory, $"eval-assistant-{stamp}.jsonl");
        var trace = new EvalTrace(path, new StreamWriter(path, append: false) { AutoFlush = true });
        trace.WriteLine(new { model = options.Connection.Model, endpointHost = options.Connection.Endpoint.Host, options.Runs, options.Case, options.Languages });
        return trace;
    }

    /// <summary>Writes one graded turn.</summary>
    /// <param name="run">The turn and its verdict.</param>
    internal void Write(CaseRun run) => WriteLine(Line(run));

    public void Dispose() => _writer.Dispose();

    internal static object Line(CaseRun run) => new
    {
        run.Case,
        run.Language,
        run.Verdict.Passed,
        run.Verdict.Why,
        ElapsedSeconds = Math.Round(run.Turn.Elapsed.TotalSeconds, 2),
        run.Turn.Requests,
        run.Turn.ToolRounds,
        run.Turn.Tokens,
        run.Turn.ProviderStatus,
        run.Turn.Answer,
        run.Turn.ChangedPaths,
        Refusals = run.Turn.Proposal?.Refusals,
        Calls = run.Turn.Calls.Select(call => new { call.Round, call.Tool, call.Arguments, call.Result }),
    };

    private void WriteLine(object value) => _writer.WriteLine(JsonSerializer.Serialize(value, _json));
}
