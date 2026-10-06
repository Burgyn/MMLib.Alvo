using CsCheck;
using MMLib.Alvo.Admin.Components.Schema;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// <c>Recognize(Generate(c)) == c</c> and <c>Generate(Recognize(t)) == t</c> for every condition the generator can write,
/// over hostile values (spec §7.3, acceptance criterion 4: 2,000 cases, none failing); and a mutated text is read as rows
/// only when those rows are ones the form offers at that point.
/// </summary>
public sealed partial class ConditionTextRoundTripTests
{
    private const int Cases = 2_000;
    private const string Alphabet = "ab '\\\"&|!()\n\t\rčé中 ‍";

    /// <summary>
    /// What a text value is built from: the alphabet's characters, plus the two joiners and an escaped quote as whole pieces —
    /// so a joiner inside a literal, and a joiner next to an escaped quote, are cases, not lucky draws.
    /// </summary>
    private static readonly string[] _pieces = [" && ", " || ", "\\'", .. Alphabet.Select(character => character.ToString())];

    private static readonly ConditionScope _scope = new(
    [
        new("title", ConditionFieldKind.Text, false, []),
        new("note", ConditionFieldKind.Text, true, []),
        new("status", ConditionFieldKind.Choice, false, ["open", "it's", "a && b", "čaj", "back\\slash", "x' || y"]),
        new("tier", ConditionFieldKind.Choice, true, ["gold"]),
        new("quantity", ConditionFieldKind.Number, false, []),
        new("discount", ConditionFieldKind.Number, true, []),
        new("paid", ConditionFieldKind.Flag, true, []),
        new("due_on", ConditionFieldKind.Moment, true, []),
        new("owner_id", ConditionFieldKind.Identity, true, []),
        new("extra", ConditionFieldKind.Json, true, []),
    ],
    ["manager", "it's-ok"]);

    /// <summary>
    /// The pieces a mutation swaps for one another: images, joiners, relations, the literal-less right sides, functions and
    /// field names (an undeclared one included) — each swap keeps the text canonical-looking, so only the legality filter
    /// can refuse it.
    /// </summary>
    private static readonly string[] _tokens =
    [
        "new.", "old.", " && ", " || ", " == ", " != ", " < ", " <= ", " > ", " >= ", "true", "false", "@user.id", "@user.roles",
        "has(", "!has(", "changed(", "!(", "startsWith(", "endsWith(", "contains(", ", ", "''", "'manager'", "'gold'", "'open'", "0",
        "1.5", "99999999999999999999", .. _scope.Fields.Select(field => field.Name), "nope",
    ];

    /// <summary>
    /// What a mutation inserts: the generator's alphabet, plus characters the form refuses in a value (a direction override,
    /// a control character, a tag character above U+FFFF) — so a canonical text holding one is a case too.
    /// </summary>
    private static readonly string[] _inserts = [.. Alphabet.Select(character => character.ToString()), "\u202E", "\u0007", "\U000E0041"];

    private static readonly Gen<(int Seed, string Point)> _cases = Gen.Select(Gen.Int, Gen.OneOfConst([.. HookBuilder.Points]));

    [Fact]
    public void Every_condition_the_generator_writes_is_read_back_as_itself()
        => _cases.Sample((seed, point) => RoundTrips(Condition(new Random(seed), point), point), iter: Cases);

    [Fact]
    public void A_mutated_condition_is_read_as_rows_only_when_the_form_offers_every_row_at_the_point_it_is_read_at()
    {
        var cases = 0;
        var read = 0;
        var refused = 0;
        _cases.Sample(
            (seed, point) =>
            {
                Interlocked.Increment(ref cases);
                var random = new Random(seed);
                var (mutated, readAt) = Mutated(random, ConditionText.Generate(Condition(random, point)), point);
                if (ConditionText.Recognize(mutated, readAt, _scope) is not { } rows)
                {
                    Interlocked.Increment(ref refused);
                    return true;
                }

                Interlocked.Increment(ref read);
                return IsOffered(rows, readAt, mutated);
            },
            iter: Cases);

        /* Not vacuous: the mutations reach both outcomes — measured against the cases actually run, which CsCheck_Time or
           CsCheck_Iter may make other than Cases. */
        (read + refused).ShouldBe(cases);
        read.ShouldBeGreaterThan(cases / 100);
        refused.ShouldBeGreaterThan(cases / 100);
    }

    private static bool RoundTrips(GuidedCondition condition, string point)
    {
        var text = ConditionText.Generate(condition);
        var back = ConditionText.Recognize(text, point, _scope);
        return back is not null && ConditionText.Generate(back) == text && back.Equals(ConditionText.Normalize(condition));
    }

    /// <summary>
    /// The oracle. Its legality clauses are what is independent of <c>Recognize</c>: the form would write these rows without
    /// a refusal, and every row is offered by the table at this point. The last two are the spec-level canonicity contract
    /// (§7.3): the rows write back exactly the text they were read from, and reading that text again gives the same rows.
    /// </summary>
    private static bool IsOffered(GuidedCondition rows, string point, string text)
        => ConditionText.Refusal(rows) is null
           && rows.Rows.All(row => IsOffered(row, point))
           && ConditionText.Generate(rows) == text
           && rows.Equals(ConditionText.Recognize(ConditionText.Generate(rows), point, _scope));

    private static bool IsOffered(ConditionRow row, string point)
    {
        var spec = ConditionTable.Of(row.Operator);
        if (spec.Operand == OperandKind.Role)
        {
            return ConditionTable.ForWriter(point).Contains(spec) && _scope.Roles.Contains(row.Value)
                   && row is { Field: ConditionTable.Writer, Image: RowImage.New, Kind: ConditionFieldKind.Text };
        }

        return _scope.Field(row.Field) is { } field
               && field.Kind == row.Kind
               && ConditionTable.For(point, field.Kind, field.Nullable).Contains(spec)
               && (row.Operator == ConditionOperator.Changed ? row.Image == RowImage.New : ConditionTable.ImagesAt(point).Contains(row.Image))
               && IsOfferedValue(spec, field, row.Value);
    }

    private static bool IsOfferedValue(OperatorSpec spec, ConditionField field, string value) => spec.Operand switch
    {
        OperandKind.None => value.Length == 0,
        _ when field.Kind == ConditionFieldKind.Choice => field.Values.Contains(value),
        _ when field.Kind == ConditionFieldKind.Number => NumberLiteral().IsMatch(value),
        _ => true,
    };

    private static GuidedCondition Condition(Random random, string point)
    {
        var candidates = Candidates(point);
        var rows = Enumerable.Range(0, random.Next(1, 5)).Select(_ => Row(random, point, candidates[random.Next(candidates.Count)])).ToList();
        return new GuidedCondition(random.Next(2) == 0, rows);
    }

    private static List<(ConditionField? Field, OperatorSpec Spec)> Candidates(string point)
        =>
        [
            .. _scope.Fields.SelectMany(field => ConditionTable.For(point, field.Kind, field.Nullable).Select(spec => ((ConditionField?)field, spec))),
            .. ConditionTable.ForWriter(point).Select(spec => ((ConditionField?)null, spec)),
        ];

    private static ConditionRow Row(Random random, string point, (ConditionField? Field, OperatorSpec Spec) pick)
    {
        if (pick.Field is not { } field)
        {
            return new ConditionRow(pick.Spec.Operator, RowImage.New, ConditionTable.Writer, ConditionFieldKind.Text, _scope.Roles[random.Next(_scope.Roles.Count)]);
        }

        var images = ConditionTable.ImagesAt(point);
        return new ConditionRow(pick.Spec.Operator, images[random.Next(images.Count)], field.Name, field.Kind, Value(random, pick.Spec, field));
    }

    private static string Value(Random random, OperatorSpec spec, ConditionField field) => spec.Operand != OperandKind.Literal
        ? string.Empty
        : field.Kind switch
        {
            ConditionFieldKind.Number => random.Next(2) == 0
                ? random.Next(100_000).ToString(CultureInfo.InvariantCulture)
                : $"{random.Next(1000).ToString(CultureInfo.InvariantCulture)}.{random.Next(100).ToString(CultureInfo.InvariantCulture)}",
            ConditionFieldKind.Choice => field.Values[random.Next(field.Values.Count)],
            _ => string.Concat(Enumerable.Range(0, random.Next(MinimumPieces(spec), 13)).Select(_ => _pieces[random.Next(_pieces.Length)])),
        };

    /// <summary>A text test needs a value (an empty one is refused, so it is no condition the form writes); equality may be empty.</summary>
    private static int MinimumPieces(OperatorSpec spec)
        => spec.Operator is ConditionOperator.StartsWith or ConditionOperator.EndsWith or ConditionOperator.Contains ? 1 : 0;

    /// <summary>
    /// One change: the text read at another point, a token swapped for another, or one character inserted or removed.
    /// </summary>
    private static (string Text, string Point) Mutated(Random random, string text, string point) => random.Next(3) switch
    {
        0 => (text, HookBuilder.Points[random.Next(HookBuilder.Points.Count)]),
        1 when _tokens.Any(token => text.Contains(token, StringComparison.Ordinal)) => (Swapped(random, text), point),
        _ => (Edited(random, text), point),
    };

    private static string Swapped(Random random, string text)
    {
        var present = _tokens.Where(token => text.Contains(token, StringComparison.Ordinal)).ToList();
        var token = present[random.Next(present.Count)];
        var at = Occurrence(random, text, token);
        return string.Concat(text.AsSpan(0, at), _tokens[random.Next(_tokens.Length)], text.AsSpan(at + token.Length));
    }

    private static string Edited(Random random, string text)
    {
        var index = random.Next(text.Length + 1);
        return index < text.Length && random.Next(2) == 0
            ? text.Remove(index, 1)
            : text.Insert(index, _inserts[random.Next(_inserts.Length)]);
    }

    private static int Occurrence(Random random, string text, string token)
    {
        var starts = new List<int>();
        for (var at = text.IndexOf(token, StringComparison.Ordinal); at >= 0; at = text.IndexOf(token, at + 1, StringComparison.Ordinal))
        {
            starts.Add(at);
        }

        return starts[random.Next(starts.Count)];
    }

    [GeneratedRegex(@"^(?:0|[1-9][0-9]*)(?:\.[0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex NumberLiteral();
}
