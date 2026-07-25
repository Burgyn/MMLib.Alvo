using System.Text.Json;

namespace MMLib.Alvo.Abstractions.Tests.Identity;

public class IdentityPrimitiveTests
{
    private static readonly Guid _guid = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void UserId_serializes_as_a_bare_json_string()
    {
        var json = JsonSerializer.Serialize(new UserId(_guid));

        json.ShouldBe($"\"{_guid}\"");
        JsonSerializer.Deserialize<UserId>(json).ShouldBe(new UserId(_guid));
    }

    [Fact]
    public void TenantId_serializes_as_a_bare_json_string()
    {
        var json = JsonSerializer.Serialize(new TenantId(_guid));

        json.ShouldBe($"\"{_guid}\"");
        JsonSerializer.Deserialize<TenantId>(json).ShouldBe(new TenantId(_guid));
    }

    [Fact]
    public void UserId_round_trips_through_TryParse()
    {
        UserId.TryParse(_guid.ToString(), provider: null, out var parsed).ShouldBeTrue();

        parsed.ShouldBe(new UserId(_guid));
        parsed.ToString().ShouldBe(_guid.ToString());
    }

    [Fact]
    public void TenantId_rejects_text_that_is_not_a_guid()
    {
        TenantId.TryParse("not-a-guid", provider: null, out var parsed).ShouldBeFalse();

        parsed.ShouldBe(default(TenantId));
    }

    [Fact]
    public void Deserializing_a_non_string_token_fails_loudly()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<UserId>("42"));
    }
}
