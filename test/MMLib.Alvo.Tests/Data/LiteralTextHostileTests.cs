using CsCheck;

namespace MMLib.Alvo.Tests.Data;

/// <summary>
/// The shape of <see cref="LiteralText.Hostile"/>, pinned: the CHECK literal properties sample it unfiltered, so a
/// generator that drifted toward the empty string, or stopped drawing the characters SQL gives meaning to, would leave
/// them green and vacuous (HX-11 Task 5 review m1).
/// </summary>
public sealed class LiteralTextHostileTests
{
    private const string Seed = "0N0XIzNsQ0O2";
    private const int Draws = 2_000;

    private static readonly string[] _texts = Draw();

    [Theory]
    [InlineData("a quote", 0.70)]
    [InlineData("a backslash", 0.45)]
    [InlineData("a dollar", 0.45)]
    [InlineData("a comment opener", 0.45)]
    [InlineData("a payload naming the sentinel", 0.30)]
    [InlineData("a declined character", 0.25)]
    public void Each_dangerous_shape_is_drawn_often(string shape, double floor) =>
        Share(Shapes[shape]).ShouldBeGreaterThanOrEqualTo(floor, shape);

    [Fact]
    public void The_declined_characters_are_each_drawn() =>
        new[] { Share(text => text.Contains('\0', StringComparison.Ordinal)), Share(HasSeparator), Share(HasLoneSurrogate) }
            .ShouldAllBe(share => share >= 0.04);

    [Fact]
    public void The_empty_string_is_rare_and_lengths_are_spread()
    {
        Share(text => text.Length == 0).ShouldBeLessThanOrEqualTo(0.05);
        _texts.Average(text => text.Length).ShouldBeGreaterThanOrEqualTo(20);
    }

    private static Dictionary<string, Func<string, bool>> Shapes { get; } = new()
    {
        ["a quote"] = text => text.Contains('\'', StringComparison.Ordinal),
        ["a backslash"] = text => text.Contains('\\', StringComparison.Ordinal),
        ["a dollar"] = text => text.Contains('$', StringComparison.Ordinal),
        ["a comment opener"] = text => text.Contains("--", StringComparison.Ordinal) || text.Contains("/*", StringComparison.Ordinal),
        ["a payload naming the sentinel"] = text => text.Contains("DROP TABLE sentinel", StringComparison.Ordinal),
        ["a declined character"] = LiteralText.MustBeDeclined,
    };

    private static double Share(Func<string, bool> holds) => _texts.Count(holds) / (double)_texts.Length;

    private static bool HasSeparator(string text) =>
        text.Contains('\u2028', StringComparison.Ordinal) || text.Contains('\u2029', StringComparison.Ordinal);

    private static bool HasLoneSurrogate(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsSurrogatePair(text, index))
            {
                index++;
            }
            else if (char.IsSurrogate(text[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] Draw()
    {
        var texts = new List<string>(Draws);
        LiteralText.Hostile().Sample(texts.Add, iter: Draws, threads: 1, seed: Seed);
        return [.. texts];
    }
}
