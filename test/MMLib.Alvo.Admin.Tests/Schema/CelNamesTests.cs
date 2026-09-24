using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// A field rename inside its own entity's CEL: a column reference is rewritten, nothing else is — and where that
/// cannot be told, the expression is left alone and named (deviation 1 in the plan).
/// </summary>
public class CelNamesTests
{
    [Theory]
    [InlineData("status == 'status'", "state == 'status'")]
    [InlineData("new.status != old.status", "new.state != old.state")]
    [InlineData("changed(status) && has(new.status)", "changed(state) && has(new.state)")]
    [InlineData("@user.status == status", "@user.status == state")]
    [InlineData("other.status == 1", "other.status == 1")]
    [InlineData("statuses == 1 || status_code == 2", "statuses == 1 || status_code == 2")]
    [InlineData("status(1) == 2", "status(1) == 2")]
    [InlineData("status == 'it\\'s status'", "state == 'it\\'s status'")]
    [InlineData("lowerAscii(new.status) == 'x'", "lowerAscii(new.state) == 'x'")]
    [InlineData("status+1 > 2", "state+1 > 2")]
    [InlineData("!status", "!state")]
    [InlineData("(status)", "(state)")]
    [InlineData("new . status == old .status", "new . state == old .state")]
    [InlineData("x.new.status == 1", "x.new.status == 1")]
    [InlineData("status == '.all(x, x)'", "state == '.all(x, x)'")]
    public void Only_a_reference_to_the_column_is_renamed(string cel, string renamed)
        => CelNames.Rename(cel, "status", "state").ShouldBe(renamed);

    [Theory]
    [InlineData("items.all(x, x.status == 'open')")]
    [InlineData("status == '''a'''")]
    public void An_expression_whose_names_cannot_be_told_apart_is_declined(string cel)
        => CelNames.Rename(cel, "status", "state").ShouldBeNull();

    [Theory]
    [InlineData("in")]
    [InlineData("has")]
    public void A_field_named_like_a_reserved_word_is_declined(string field)
        => CelNames.Rename($"true == {field}", field, "inside").ShouldBeNull();

    /// <summary>A keyword as the new name would turn <c>is_public || …</c> into <c>true || …</c> — a rule that still compiles.</summary>
    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("in")]
    [InlineData("has")]
    public void A_rename_to_a_reserved_word_is_declined(string to)
        => CelNames.Rename("is_public || owner_id == @user.id", "is_public", to).ShouldBeNull();

    [Theory]
    [InlineData("items.all(x, x.status)", true)]
    [InlineData("statuses", false)]
    public void A_declined_expression_may_still_be_named(string cel, bool names)
        => CelNames.MayName(cel, "status").ShouldBe(names);
}
