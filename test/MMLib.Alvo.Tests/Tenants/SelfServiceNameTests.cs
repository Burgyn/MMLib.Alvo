using MMLib.Alvo.Tenants.SelfService;
using System.Text.Json;

namespace MMLib.Alvo.Tests.Tenants;

/// <summary>
/// Every name a person types on a self-service route — their display name, a tenant's name — follows one set of rules
/// (<see cref="SelfServiceName"/>): the same body judged the same way whatever it names, only the noun in the wording
/// differing. Two copies of the rules were how a fix to one would have missed the other.
/// </summary>
public sealed class SelfServiceNameTests
{
    private static readonly string _tooLong = new('a', SelfServiceName.MaxLength + 1);

    public static TheoryData<string, string?, string[]> Bodies => new()
    {
        { """{"name":"  Acme  "}""", "Acme", [] },
        { $$"""{"name":"{{new string('a', SelfServiceName.MaxLength)}}"}""", new string('a', SelfServiceName.MaxLength), [] },
        { """{"name":"   "}""", null, ["/name:required"] },
        { """{"name":null}""", null, ["/name:required"] },
        { """{}""", null, ["/name:required"] },
        { """{"name":42}""", null, ["/name:invalid-value"] },
        { """{"name":"a\u0007b"}""", null, ["/name:invalid-value"] },
        { """{"name":"a\ud800"}""", null, ["/name:invalid-value"] },
        { $$"""{"name":"{{_tooLong}}"}""", null, ["/name:max-length"] },
        { """{"name":"Acme","email":"x@example.test"}""", null, ["/email:unknown-field"] },
    };

    [Theory]
    [MemberData(nameof(Bodies))]
    public void A_display_name_and_a_tenant_name_are_judged_alike(string json, string? expected, string[] violations)
    {
        foreach (var noun in new[] { "display name", "tenant name" })
        {
            using var document = JsonDocument.Parse(json);

            var (name, refused) = new SelfServiceName(noun).Validate(document.RootElement);

            name.ShouldBe(expected, noun);
            refused.Select(violation => $"{violation.Pointer}:{violation.Code}").ShouldBe(violations, noun);
        }
    }

    [Fact]
    public void The_wording_names_what_was_named()
    {
        using var document = JsonDocument.Parse("""{"name":""}""");

        var (_, display) = new SelfServiceName("display name").Validate(document.RootElement);
        var (_, tenant) = new SelfServiceName("tenant name").Validate(document.RootElement);

        display.ShouldHaveSingleItem().Message.ShouldStartWith("A display name is required");
        tenant.ShouldHaveSingleItem().Message.ShouldStartWith("A tenant name is required");
    }
}
