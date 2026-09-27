using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>The Fields tab's badges: every one read off <see cref="FieldSchema"/>, never a guess.</summary>
public class FieldBadgesTests
{
    [Fact]
    public void A_field_is_badged_with_what_changes_what_a_caller_may_send_first()
        => FieldBadges.Of(new FieldSchema
        {
            Name = "code",
            Type = FieldType.String,
            Required = true,
            Unique = true,
            MaxLength = 12,
            Indexed = true,
            Default = JsonDocument.Parse("\"X\"").RootElement.Clone(),
        }).ShouldBe(["required", "default \"X\"", "unique", "max 12", "indexed"]);

    /// <summary>Only a nullability that differs from the one <c>required</c> implies is badged — anything else is noise on every row.</summary>
    [Theory]
    [InlineData(true, true, "nullable")]
    [InlineData(false, false, "not null")]
    public void An_explicit_nullability_is_badged(bool required, bool nullable, string badge)
        => FieldBadges.Of(new FieldSchema { Name = "f", Type = FieldType.Text, Required = required, Nullable = nullable })
            .ShouldContain(badge);

    [Fact]
    public void A_derived_nullability_is_not_badged()
        => FieldBadges.Of(new FieldSchema { Name = "f", Type = FieldType.Text, Nullable = true }).ShouldBeEmpty();

    [Fact]
    public void A_rollup_and_a_computed_field_say_so()
    {
        FieldBadges.Of(new FieldSchema
        {
            Name = "n",
            Type = FieldType.Integer,
            Nullable = true,
            Rollup = new RollupSchema { From = "lines", Op = RollupOperation.Count, Via = string.Empty },
        }).ShouldBe(["rollup"]);
        FieldBadges.Of(new FieldSchema { Name = "t", Type = FieldType.Integer, Nullable = true, ComputedExpression = "a + a" })
            .ShouldBe(["computed"]);
    }

    /// <summary>
    /// <c>hidden</c> and <c>readOnly</c> are read off the declaration, which <see cref="FieldSchema"/> does not carry
    /// (#267's read half): <c>true</c> is always, a CEL string is for some callers, <c>false</c> is nothing.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"text","hidden":true}""", "hidden")]
    [InlineData("""{"type":"text","hidden":"!('dispatcher' in @user.roles)"}""", "hidden (conditional)")]
    [InlineData("""{"type":"text","readOnly":true}""", "readOnly")]
    [InlineData("""{"type":"text","readOnly":"@user.id != owner"}""", "readOnly (conditional)")]
    public void A_declared_policy_is_badged(string declaration, string badge)
        => FieldBadges.Of(Text, Declared(declaration)).ShouldBe([badge]);

    [Fact]
    public void A_policy_written_false_is_not_one()
        => FieldBadges.Of(Text, Declared("""{"type":"text","hidden":false,"readOnly":false}""")).ShouldBeEmpty();

    /// <summary>After what a caller may send, before the storage: a hidden field changes what a caller sees.</summary>
    [Fact]
    public void The_policy_follows_the_constraints_and_precedes_the_storage()
        => FieldBadges.Of(
                new FieldSchema { Name = "code", Type = FieldType.String, Required = true, MaxLength = 12 },
                Declared("""{"type":"string","required":true,"maxLength":12,"readOnly":true,"hidden":"false"}"""))
            .ShouldBe(["required", "hidden (conditional)", "readOnly", "max 12"]);

    private static FieldSchema Text { get; } = new() { Name = "notes", Type = FieldType.Text, Nullable = true };

    private static JsonElement Declared(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
