using CsCheck;
using MMLib.Alvo.Analyzers.Generator;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Analyzers.Tests.Generator;

/// <summary>
/// The generator's own JSON reader against <see cref="JsonDocument"/>: the same tree for every shipped example and for
/// generated documents, and a malformed document named by line and column.
/// </summary>
public sealed class DescriptorJsonTests
{
    private const string Seed = "0N0XIzNsQ0O2";

    private static readonly Gen<string> _piece = Gen.Frequency(
        (4, Gen.Char['a', 'z'].Select(c => c.ToString())),
        (1, Gen.OneOfConst("é", "\"", "\\", "/", "\n", "\t", "\u0001", "\u001f", "😀", "x y", "\u2028")));

    private static readonly Gen<string> _string = Gen.Select(_piece.Array[0, 8], Gen.Bool, (pieces, escapeAll) => Quote(string.Concat(pieces), escapeAll));

    private static readonly Gen<string> _number = Gen.Select(
        Gen.OneOfConst("", "-"),
        Gen.OneOf(Gen.Const("0"), Gen.Long[1, long.MaxValue].Select(n => n.ToString(CultureInfo.InvariantCulture))),
        Gen.OneOfConst("", ".5", ".0001", ".123456789"),
        Gen.OneOfConst("", "e5", "E-3", "e+12"),
        (sign, whole, fraction, exponent) => sign + whole + fraction + exponent);

    private static readonly Gen<string> _scalar = Gen.Frequency(
        (3, _string), (3, _number), (1, Gen.OneOfConst("true", "false", "null")));

    private static readonly Gen<string> _document = Gen.Recursive<string>((depth, self) => depth >= 4
        ? _scalar
        : Gen.Frequency(
            (3, _scalar),
            (1, self.Array[0, 4].Select(items => "[" + string.Join(", ", items) + "]")),
            (1, Gen.Select(_string, self).Array[0, 4].Select(members => "{ " + string.Join(",\n", members.Select(m => m.Item1 + " : " + m.Item2)) + " }"))));

    public static TheoryData<string> Examples() =>
    [
        .. Directory.GetFiles(Path.Combine(RepositoryRoot.Find(), "examples"), "*.alvo.json", SearchOption.AllDirectories)
            .Where(path => !path.Contains("_negative", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot.Find(), path)),
    ];

    [Theory]
    [MemberData(nameof(Examples))]
    public void Every_example_parses_to_the_same_tree_as_System_Text_Json(string example)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), example));

        ShouldMatch(text);
    }

    [Fact]
    public void Generated_json_round_trips_like_System_Text_Json() =>
        _document.Sample(ShouldMatch, iter: 2000, seed: Seed);

    [Fact]
    public void A_malformed_document_names_line_and_column()
    {
        var error = Should.Throw<DescriptorJsonException>(() => DescriptorJson.Parse("{\n  \"a\": ,\n}"));

        error.Line.ShouldBe(2);
        error.Column.ShouldBe(8);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"a\": 1,}")]
    [InlineData("[1 2]")]
    [InlineData("\"unterminated")]
    [InlineData("\"bad \\x escape\"")]
    [InlineData("01")]
    [InlineData("1.")]
    [InlineData("tru")]
    [InlineData("{} {}")]
    [InlineData("\"raw \u0001 control\"")]
    public void Malformed_documents_are_refused_like_System_Text_Json(string text)
    {
        Should.Throw<JsonException>(() => JsonDocument.Parse(text));
        Should.Throw<DescriptorJsonException>(() => DescriptorJson.Parse(text));
    }

    [Fact]
    public void A_document_nested_past_the_depth_limit_is_refused_not_overflowed() =>
        Should.Throw<DescriptorJsonException>(() => DescriptorJson.Parse(new string('[', 100_000)));

    [Fact]
    public void Duplicate_keys_keep_the_last_value()
    {
        var root = (JsonObject)DescriptorJson.Parse("{\"a\": 1, \"a\": \"two\"}");

        root.Get("a").ShouldBe(new JsonString("two"));
    }

    [Fact]
    public void A_leading_byte_order_mark_is_skipped() =>
        DescriptorJson.Parse("\uFEFF{}").ShouldBeOfType<JsonObject>();

    private static void ShouldMatch(string text)
    {
        using var document = JsonDocument.Parse(text);

        ShouldMatch(DescriptorJson.Parse(text), document.RootElement, "$");
    }

    private static void ShouldMatch(JsonNode node, JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                ShouldMatchObject(node.ShouldBeOfType<JsonObject>(path), element, path);
                break;
            case JsonValueKind.Array:
                ShouldMatchArray(node.ShouldBeOfType<JsonArray>(path), element, path);
                break;
            case JsonValueKind.String:
                node.ShouldBe(new JsonString(element.GetString()!), path);
                break;
            case JsonValueKind.Number:
                node.ShouldBe(new JsonNumber(element.GetRawText()), path);
                break;
            case JsonValueKind.True or JsonValueKind.False:
                node.ShouldBe(new JsonBool(element.GetBoolean()), path);
                break;
            default:
                node.ShouldBeOfType<JsonNull>(path);
                break;
        }
    }

    private static void ShouldMatchObject(JsonObject node, JsonElement element, string path)
    {
        var members = element.EnumerateObject().ToList();
        node.Members.Count.ShouldBe(members.Count, path);
        for (var i = 0; i < members.Count; i++)
        {
            node.Members[i].Key.ShouldBe(members[i].Name, path);
            ShouldMatch(node.Members[i].Value, members[i].Value, $"{path}.{members[i].Name}");
        }
    }

    private static void ShouldMatchArray(JsonArray node, JsonElement element, string path)
    {
        var items = element.EnumerateArray().ToList();
        node.Items.Count.ShouldBe(items.Count, path);
        for (var i = 0; i < items.Count; i++)
        {
            ShouldMatch(node.Items[i], items[i], $"{path}[{i}]");
        }
    }

    private static string Quote(string value, bool escapeAll)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            builder.Append(Escaped(c, escapeAll));
        }

        return builder.Append('"').ToString();
    }

    private static string Escaped(char c, bool escapeAll) => c switch
    {
        '"' => "\\\"",
        '\\' => "\\\\",
        '/' when escapeAll => "\\/",
        '\n' => "\\n",
        '\t' => "\\t",
        _ when c < 0x20 || (escapeAll && c > 0x7E) => "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture),
        _ => c.ToString(),
    };
}
