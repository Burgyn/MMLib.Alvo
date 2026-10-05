using System.Text;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Components.Schema;

/* The strict recognizer: a condition is rows only when it is byte for byte what Generate writes (spec §7.3, D5). */
internal static partial class ConditionText
{
    private const string FieldName = "[a-z][a-z0-9_]{0,62}";
    private const string QuotedText = @"'(?:[^'\\]|\\.)*'";
    private const string Number = @"(?:0|[1-9][0-9]*)(?:\.[0-9]+)?";

    private static readonly Dictionary<ConditionOperator, Regex> _patterns =
        ConditionTable.Rows.ToDictionary(spec => spec.Operator, spec => PatternOf(spec.Format));

    /// <summary>
    /// The rows a condition is, when it is exactly what the generator writes for this point and scope and the form would
    /// write it without a refusal; otherwise <see langword="null"/>, and the condition stays text.
    /// </summary>
    /// <param name="text">The condition's CEL.</param>
    /// <param name="point">The hook point.</param>
    /// <param name="scope">The entity's fields and the declared roles.</param>
    public static GuidedCondition? Recognize(string text, string point, ConditionScope scope)
    {
        if (text.Length == 0)
        {
            return GuidedCondition.Empty;
        }

        if (Split(text) is not { } split)
        {
            return null;
        }

        var rows = new List<ConditionRow>(split.Parts.Count);
        foreach (var part in split.Parts)
        {
            if (RecognizeRow(part, point, scope) is not { } row)
            {
                return null;
            }

            rows.Add(row);
        }

        var condition = new GuidedCondition(split.All || rows.Count < 2, rows);
        return string.Equals(Generate(condition), text, StringComparison.Ordinal) && Refusal(condition) is null ? condition : null;
    }

    /// <summary>The parts between the top-level joiners, outside quotes; <see langword="null"/> for a mix or an open quote.</summary>
    private static (bool All, List<string> Parts)? Split(string text)
    {
        var parts = new List<string>();
        var start = 0;
        var quoted = false;
        string? joiner = null;
        for (var index = 0; index < text.Length; index++)
        {
            if (quoted || text[index] == '\'')
            {
                quoted = StillQuoted(text, ref index, quoted);
                continue;
            }

            if (JoinerAt(text, index) is not { } found)
            {
                continue;
            }

            if (joiner is not null && joiner != found)
            {
                return null;
            }

            joiner = found;
            parts.Add(text[start..index]);
            start = index + found.Length;
            index = start - 1;
        }

        parts.Add(text[start..]);
        return quoted ? null : (joiner != Or, parts);
    }

    /// <summary>
    /// One step through a quote: an opening quote opens it, a backslash skips the character it escapes, a closing quote
    /// closes it.
    /// </summary>
    private static bool StillQuoted(string text, ref int index, bool quoted)
    {
        if (!quoted)
        {
            return true;
        }

        if (text[index] == '\\')
        {
            index++;
            return true;
        }

        return text[index] != '\'';
    }

    private static string? JoinerAt(string text, int index)
        => text.AsSpan(index).StartsWith(And, StringComparison.Ordinal) ? And
            : text.AsSpan(index).StartsWith(Or, StringComparison.Ordinal) ? Or
            : null;

    private static ConditionRow? RecognizeRow(string part, string point, ConditionScope scope)
    {
        foreach (var spec in ConditionTable.Rows)
        {
            if (_patterns[spec.Operator].Match(part) is { Success: true } match && Resolve(spec, match, point, scope) is { } row)
            {
                return row;
            }
        }

        return null;
    }

    private static ConditionRow? Resolve(OperatorSpec spec, Match match, string point, ConditionScope scope)
    {
        var raw = match.Groups["value"].Success ? match.Groups["value"].Value : null;
        if (spec.Operand == OperandKind.Role)
        {
            return ConditionTable.ForWriter(point).Contains(spec) && Unquote(raw!) is { } role && scope.Roles.Contains(role, StringComparer.Ordinal)
                ? new ConditionRow(spec.Operator, RowImage.New, ConditionTable.Writer, ConditionFieldKind.Text, role)
                : null;
        }

        if (scope.Field(match.Groups["field"].Value) is not { } field || !ConditionTable.Allows(spec, point, field.Kind, field.Nullable))
        {
            return null;
        }

        var image = match.Groups["image"].Value == "old" ? RowImage.Old : RowImage.New;
        if (match.Groups["image"].Success && !ConditionTable.ImagesAt(point).Contains(image))
        {
            return null;
        }

        return ValueOf(spec, field, raw) is { } value ? new ConditionRow(spec.Operator, image, field.Name, field.Kind, value) : null;
    }

    private static string? ValueOf(OperatorSpec spec, ConditionField field, string? raw)
    {
        if (spec.Operand == OperandKind.None)
        {
            return string.Empty;
        }

        if (field.Kind == ConditionFieldKind.Number)
        {
            return raw is not null && NumberLiteral().IsMatch(raw) ? raw : null;
        }

        if (raw is null || !raw.StartsWith('\'') || Unquote(raw) is not { } text)
        {
            return null;
        }

        return field.Kind != ConditionFieldKind.Choice || field.Values.Contains(text, StringComparer.Ordinal) ? text : null;
    }

    /// <summary>A quoted literal's text, or <see langword="null"/> for an escape the lexer does not read.</summary>
    private static string? Unquote(string quoted)
    {
        var text = new StringBuilder(quoted.Length);
        for (var index = 1; index < quoted.Length - 1; index++)
        {
            if (quoted[index] != '\\')
            {
                text.Append(quoted[index]);
                continue;
            }

            if (++index >= quoted.Length - 1 || Unescape(quoted[index]) is not { } character)
            {
                return null;
            }

            text.Append(character);
        }

        return text.ToString();
    }

    private static char? Unescape(char escaped) => escaped switch
    {
        '\\' => '\\',
        '\'' => '\'',
        'n' => '\n',
        'r' => '\r',
        't' => '\t',
        _ => null,
    };

    /// <summary>An anchored regex of one table format, its parts named <c>image</c>, <c>field</c> and <c>value</c>.</summary>
    /// <remarks><c>\z</c>, not <c>$</c>, which also matches before a final line feed.</remarks>
    private static Regex PatternOf(string format)
    {
        var pattern = Regex.Escape(format)
            .Replace(@"\{f}", $@"(?<image>new|old)\.(?<field>{FieldName})", StringComparison.Ordinal)
            .Replace(@"\{n}", $"(?<field>{FieldName})", StringComparison.Ordinal)
            .Replace(@"\{v}", $"(?<value>{QuotedText}|{Number})", StringComparison.Ordinal)
            .Replace(@"\{r}", $"(?<value>{QuotedText})", StringComparison.Ordinal);
        return new Regex($@"^{pattern}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
