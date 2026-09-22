using MMLib.Alvo.Secrets;

namespace MMLib.Alvo.Abstractions.Tests.Secrets;

/// <summary>
/// The name a secret is stored and fetched under.
/// </summary>
/// <remarks>
/// It is a validated type for the reason <c>AlvoOptions.SchemaPrefix</c> is one: the name is interpolated
/// into a configuration key and used as a primary key, so it must be a validated identifier rather than
/// caller-supplied data at either end.
/// </remarks>
public class SecretNameTests
{
    [Theory]
    [InlineData("alvo.ai.connection")]
    [InlineData("a")]
    [InlineData("webhook-signing_key.v1")]
    public void Accepts_a_lower_snake_dotted_identifier(string candidate) =>
        SecretName.Parse(candidate).Value.ShouldBe(candidate);

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Alvo.Ai")]
    [InlineData("1leading")]
    [InlineData(".leading")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    public void Refuses_anything_that_is_not_one(string? candidate) =>
        SecretName.TryParse(candidate, out _).ShouldBeFalse();

    [Fact]
    public void Refuses_a_name_longer_than_64_characters() =>
        SecretName.TryParse(new string('a', 65), out _).ShouldBeFalse();

    [Fact]
    public void Accepts_a_name_of_exactly_64_characters() =>
        SecretName.TryParse(new string('a', 64), out _).ShouldBeTrue();

    /// <summary>Two names spelled the same are one key, which is what a store's dictionary needs.</summary>
    [Fact]
    public void Equality_is_by_value() =>
        SecretName.Parse("one.two").ShouldBe(SecretName.Parse("one.two"));

    /// <summary>
    /// The refusal names the value, because the author of a descriptor reading it has no other way to tell
    /// which of their names was rejected.
    /// </summary>
    [Fact]
    public void Parse_names_the_offending_value() =>
        Should.Throw<ArgumentException>(() => SecretName.Parse("NOPE"))
            .Message.ShouldContain("NOPE");
}
