using CsCheck;
using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Schema.Tests;

public class RoundTripTests
{
    public static IEnumerable<object[]> Examples() =>
        SchemaPaths.Examples().Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(Examples))]
    public void Canonicalize_is_idempotent_and_member_order_insensitive(string path)
    {
        string original = File.ReadAllText(path);
        string once = Canonicalizer.Canonicalize(original);

        Canonicalizer.Canonicalize(once)
            .ShouldBe(once, $"canonicalization must be idempotent for {Path.GetFileName(path)}");
        Canonicalizer.Canonicalize(ReverseTopLevelMembers(original))
            .ShouldBe(once, $"canonicalization must ignore member order for {Path.GetFileName(path)}");
    }

    [Fact]
    public void A_mutated_valid_example_fails_validation()
    {
        var schema = SchemaValidator.Load();
        string crm = File.ReadAllText(SchemaPaths.Examples().First(path => path.Contains("complex-crm")));

        // A typo'd keyword must never slip through: an unknown, non-`x-` key is
        // rejected for any name, not just the ones a fixed example happens to try.
        Gen.String[1, 12].Where(s => s.Length > 0 && s.All(char.IsLetter)).Sample(key =>
        {
            using JsonDocument document = JsonDocument.Parse(crm);
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            writer.WritePropertyName("zzz" + key);
            writer.WriteStringValue("boom");
            foreach (JsonProperty member in document.RootElement.EnumerateObject())
            {
                member.WriteTo(writer);
            }

            writer.WriteEndObject();
            writer.Flush();

            string mutated = Encoding.UTF8.GetString(stream.ToArray());
            return SchemaValidator.Failures(schema, mutated).Count > 0;
        });
    }

    private static string ReverseTopLevelMembers(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        foreach (JsonProperty member in document.RootElement.EnumerateObject().Reverse())
        {
            member.WriteTo(writer);
        }

        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
