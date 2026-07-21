using CsCheck;
using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Schema.Tests;

public class RoundTripTests
{
    [Fact]
    public void Canonicalize_is_idempotent_and_member_order_insensitive()
    {
        foreach (string path in SchemaPaths.Examples())
        {
            string original = File.ReadAllText(path);
            string once = Canonicalizer.Canonicalize(original);
            string twice = Canonicalizer.Canonicalize(once);
            twice.ShouldBe(once, $"canonicalization must be idempotent for {Path.GetFileName(path)}");
            Canonicalizer.Canonicalize(ReverseTopLevelMembers(original)).ShouldBe(once);
        }
    }

    [Fact]
    public void A_mutated_valid_example_fails_validation()
    {
        var schema = SchemaValidator.Load();
        string crm = File.ReadAllText(SchemaPaths.Examples().First(path => path.Contains("complex-crm")));

        // Property: injecting an unknown, non-`x-` top-level key into a valid
        // descriptor always yields at least one validation failure.
        Gen.String[1, 12].Where(s => s.Length > 0 && s.All(char.IsLetter)).Sample(key =>
        {
            using JsonDocument document = JsonDocument.Parse(crm);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("zzz" + key);
                writer.WriteStringValue("boom");
                foreach (JsonProperty member in document.RootElement.EnumerateObject())
                {
                    member.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            string mutated = Encoding.UTF8.GetString(stream.ToArray());
            return SchemaValidator.Failures(schema, mutated).Count > 0;
        });
    }

    private static string ReverseTopLevelMembers(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (JsonProperty member in document.RootElement.EnumerateObject().Reverse())
            {
                member.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
