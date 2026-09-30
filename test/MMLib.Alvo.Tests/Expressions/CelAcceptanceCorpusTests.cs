using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

using System.Reflection;
using System.Text.Json;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// D42 changes only the text of refusals: every source the CEL test corpus compiles, under every profile, compiles to
/// the same outcome it did before — accepted or refused, the same result type, the same error positions, and each
/// error keeping its earlier message as its prefix.
/// </summary>
/// <remarks>
/// <para>
/// The baseline (<c>CelAcceptanceBaseline.jsonl</c>) was generated at f6c45de+T11, <em>before</em> the compiler
/// changed: every distinct string argument of every <c>[InlineData]</c> in this namespace, compiled against
/// <see cref="CelFixtures.Orders"/> under each of the five profiles. A string that is no CEL at all is in the corpus
/// too; it is refused before and after, and that is part of the claim.
/// </para>
/// <para>
/// The baseline is the corpus, so a new <c>[InlineData]</c> never breaks this fact. A change that is meant to move
/// the accepted set must regenerate it, and that regeneration is the diff a reviewer reads.
/// </para>
/// </remarks>
public sealed class CelAcceptanceCorpusTests
{
    private const string BaselineResource = "MMLib.Alvo.Tests.Expressions.CelAcceptanceBaseline.jsonl";

    private readonly CelCompiler _compiler = new();

    /// <summary>One source under one profile, as the compiler judged it.</summary>
    /// <param name="Profile">The profile's name.</param>
    /// <param name="Source">The CEL source.</param>
    /// <param name="Accepted">Whether it compiled.</param>
    /// <param name="ResultType">The result type, when it compiled.</param>
    /// <param name="Errors">Each refusal's position and message, when it did not.</param>
    internal sealed record Outcome(string Profile, string Source, bool Accepted, string? ResultType, IReadOnlyList<Refusal> Errors);

    /// <summary>One refusal: where, and in what words.</summary>
    internal sealed record Refusal(int Position, string Message);

    [Fact]
    public void The_corpus_is_large_enough_to_mean_something() =>
        Baseline().Count(outcome => outcome.Accepted).ShouldBeGreaterThan(100);

    [Fact]
    public void Every_source_in_the_corpus_compiles_to_the_outcome_it_had_before_D42()
    {
        var drifted = Baseline().Where(expected => !Matches(expected, Compile(expected))).Select(expected => $"{expected.Profile}: {expected.Source}");

        drifted.ShouldBeEmpty();
    }

    internal static Outcome Judge(CelCompiler compiler, string source, CelProfile profile)
    {
        var result = compiler.Compile(source, profile, CelFixtures.Orders);
        return new Outcome(
            profile.ToString(), source, result.IsSuccess, result.Expression?.ResultType.ToString(),
            [.. result.Errors.Select(error => new Refusal(error.Position, error.Message))]);
    }

    private Outcome Compile(Outcome expected) => Judge(_compiler, expected.Source, Enum.Parse<CelProfile>(expected.Profile));

    private static bool Matches(Outcome expected, Outcome actual) =>
        expected.Accepted == actual.Accepted
        && expected.ResultType == actual.ResultType
        && expected.Errors.Count == actual.Errors.Count
        && expected.Errors.Zip(actual.Errors).All(pair =>
            pair.First.Position == pair.Second.Position && pair.Second.Message.StartsWith(pair.First.Message, StringComparison.Ordinal));

    private static IReadOnlyList<Outcome> Baseline()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(BaselineResource)!;
        using var reader = new StreamReader(stream);
        return [.. reader.ReadToEnd().Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .Select(line => JsonSerializer.Deserialize<Outcome>(line)!)];
    }
}
