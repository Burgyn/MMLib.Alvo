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

    [Fact]
    public void Deserializing_a_non_string_token_fails_loudly_for_TenantId()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<TenantId>("42"));
    }

    /// <summary>
    /// The all-zero uuid is reserved to mean "no tenant", and a tenant that is present but
    /// all-zero is worse than no tenant at all: the predicate is attached and matches every row
    /// whose tenant was defaulted rather than assigned.
    /// </summary>
    [Fact]
    public void TenantId_refuses_the_reserved_all_zero_value()
    {
        TenantId.TryParse(Guid.Empty.ToString(), provider: null, out _).ShouldBeFalse(
            "all-zero means \"no tenant\", which is expressed as null and never as a TenantId");

        Should.Throw<FormatException>(() => TenantId.Parse(Guid.Empty.ToString(), provider: null));

        Should.Throw<JsonException>(
            () => JsonSerializer.Deserialize<TenantId>($"\"{Guid.Empty}\""));
    }

    /// <summary>
    /// <see cref="UserId"/> keeps parsing the reserved value, deliberately: it is what
    /// <see cref="AlvoContext.Anonymous"/> carries, so refusing it here would refuse the anonymous
    /// caller's own identifier. The reservation is enforced where a subject is read instead.
    /// </summary>
    [Fact]
    public void UserId_still_parses_the_reserved_value_because_Anonymous_carries_it()
    {
        UserId.TryParse(Guid.Empty.ToString(), provider: null, out var parsed).ShouldBeTrue();

        parsed.ShouldBe(AlvoContext.Anonymous.User);
    }
}
