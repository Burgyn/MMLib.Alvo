using System.Globalization;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>What one eval run was asked to measure.</summary>
/// <param name="RepositoryRoot">The repository, for <c>examples/bike-workshop</c>.</param>
/// <param name="Connection">The model to measure; its key comes from <see cref="ApiKeyVariable"/>, never an argument.</param>
/// <param name="Runs">How many times each case is asked in each language.</param>
/// <param name="Case">The one case to run, or <see langword="null"/> for the whole suite.</param>
/// <param name="Languages">The languages each case is asked in.</param>
/// <param name="TraceDirectory">Where every graded turn is written in full, or <see langword="null"/> for nowhere.</param>
internal sealed record EvalOptions(
    string RepositoryRoot, AlvoAiConnection Connection, int Runs, string? Case, IReadOnlyList<string> Languages,
    string? TraceDirectory = null)
{
    internal const string ApiKeyVariable = "ALVO_EVAL_API_KEY";
    private const int DefaultRuns = 3;
    private static readonly string[] _allLanguages = [ReplyLanguage.English, ReplyLanguage.Slovak];
    private static readonly HashSet<string> _known = new(StringComparer.Ordinal)
    {
        "--repository", "--endpoint", "--model", "--kind", "--runs", "--case", "--language", "--trace",
    };

    /// <summary>Parses the arguments <c>scripts/eval-assistant</c> passes through.</summary>
    /// <param name="args">The process arguments.</param>
    /// <exception cref="ArgumentException">An argument is missing, unknown or malformed.</exception>
    internal static EvalOptions Parse(string[] args)
    {
        var values = Pairs(args);
        return new EvalOptions(
            Required(values, "--repository"),
            ConnectionFrom(values),
            RunsFrom(values.GetValueOrDefault("--runs")),
            CaseFrom(values.GetValueOrDefault("--case")),
            LanguagesFrom(values.GetValueOrDefault("--language")),
            values.GetValueOrDefault("--trace"));
    }

    private static AlvoAiConnection ConnectionFrom(Dictionary<string, string> values)
    {
        var kind = Kind(values.GetValueOrDefault("--kind"));
        var key = Environment.GetEnvironmentVariable(ApiKeyVariable) is { Length: > 0 } present ? present : null;
        if (kind == AiConnectionKind.AzureOpenAi && key is null)
        {
            throw new ArgumentException($"--kind azure-openai always needs a key: set {ApiKeyVariable}.");
        }

        return new AlvoAiConnection(kind, Endpoint(Required(values, "--endpoint")), Required(values, "--model"), key);
    }

    private static Dictionary<string, string> Pairs(string[] args)
    {
        if (args.Length % 2 != 0)
        {
            throw new ArgumentException("Every option takes one value, e.g. --model qwen3:8b.");
        }

        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!_known.Contains(args[index]) || !pairs.TryAdd(args[index], args[index + 1]))
            {
                throw new ArgumentException($"'{args[index]}' is not an option, or is given twice.");
            }
        }

        return pairs;
    }

    private static string Required(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && value.Length > 0 ? value : throw new ArgumentException($"{name} is required.");

    private static Uri Endpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : throw new ArgumentException($"--endpoint '{endpoint}' is not an absolute http(s) address.");

    private static AiConnectionKind Kind(string? kind) => kind switch
    {
        null or "openai-compatible" => AiConnectionKind.OpenAiCompatible,
        "azure-openai" => AiConnectionKind.AzureOpenAi,
        _ => throw new ArgumentException($"--kind '{kind}' is neither openai-compatible nor azure-openai."),
    };

    private static int RunsFrom(string? runs)
    {
        if (runs is null)
        {
            return DefaultRuns;
        }

        return int.TryParse(runs, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0
            ? count
            : throw new ArgumentException($"--runs '{runs}' is not a positive whole number.");
    }

    private static string? CaseFrom(string? name) =>
        name is null || EvalCases.All.Any(candidate => candidate.Name == name)
            ? name
            : throw new ArgumentException(
                $"--case '{name}' is not a case; the cases are: {string.Join(", ", EvalCases.All.Select(candidate => candidate.Name))}.");

    private static string[] LanguagesFrom(string? language) => language switch
    {
        null => _allLanguages,
        ReplyLanguage.English or ReplyLanguage.Slovak => [language],
        _ => throw new ArgumentException($"--language '{language}' is neither en nor sk."),
    };
}
