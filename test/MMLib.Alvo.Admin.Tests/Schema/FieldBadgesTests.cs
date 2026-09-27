using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Management;
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

    /// <summary>
    /// A staged field carrying a facet the build refuses says so on its row, not only in the editor (#269): the three
    /// field slots, each detected in the form the apply refuses.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"text","validation":"size(this) > 2"}""", "field.validation")]
    [InlineData("""{"type":"text","default":{"$cel":"@user.id"}}""", "field.default")]
    [InlineData("""{"type":"integer","rollup":{"from":"lines","op":"count","where":"x > 1"}}""", "rollup.where")]
    public void A_refused_facet_is_found_on_the_row(string declaration, string slot)
        => FieldBadges.Refused(Declared(declaration), _published).ShouldHaveSingleItem().Slot.ShouldBe(slot);

    /// <summary>The form the build honours is not a refusal: a literal default, a rollup with no filter.</summary>
    [Theory]
    [InlineData("""{"type":"text","default":"x"}""")]
    [InlineData("""{"type":"integer","rollup":{"from":"lines","op":"count"}}""")]
    [InlineData("""{"type":"text","validation":null}""")]
    public void An_honoured_form_is_not_refused(string declaration)
        => FieldBadges.Refused(Declared(declaration), _published).ShouldBeEmpty();

    /// <summary>
    /// The build is the authority, slot by slot: with every other slot still published, the one it stopped publishing is
    /// not badged — while the field's other refused facet still is.
    /// </summary>
    [Fact]
    public void A_slot_the_build_does_not_publish_is_not_badged()
    {
        var declared = Declared("""{"type":"text","validation":"size(this) > 2","default":{"$cel":"@user.id"}}""");
        var withoutValidation = _published.Where(refusal => refusal.Slot != "field.validation").ToArray();

        FieldBadges.Refused(declared, _published).Select(refusal => refusal.Slot)
            .ShouldBe(["field.validation", "field.default"]);
        FieldBadges.Refused(declared, withoutValidation).ShouldHaveSingleItem().Slot.ShouldBe("field.default");
    }

    /// <summary>A published slot that belongs to another screen is not a Fields-row refusal, whatever it is called.</summary>
    [Fact]
    public void A_slot_another_screen_owns_is_not_badged_on_a_row()
        => FieldBadges.Refused(Declared("""{"type":"text","softDelete":true}"""), _published).ShouldBeEmpty();

    [Fact]
    public void A_row_with_no_declaration_is_refused_nothing()
        => FieldBadges.Refused(null, _published).ShouldBeEmpty();

    private static readonly ManagementRefusedFeature[] _published =
    [
        new("field.validation", "c", "f"), new("field.default", "c", "f"), new("rollup.where", "c", "f"),
        new("entity.softDelete", "c", "f"),
    ];

    private static FieldSchema Text { get; } = new() { Name = "notes", Type = FieldType.Text, Nullable = true };

    private static JsonElement Declared(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
