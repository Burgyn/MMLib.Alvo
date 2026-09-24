using MMLib.Alvo.Admin.Components.Rules;

namespace MMLib.Alvo.Admin.Tests.Rules;

/// <summary>A rule is typed, then saved on purpose: never staged by leaving the box (inventory defect #7).</summary>
public sealed class RuleDraftsTests
{
    private const string Declared = "'admin' in @user.roles";

    [Fact]
    public void Untouched_it_reads_what_the_descriptor_declares_and_is_clean()
    {
        var drafts = new RuleDrafts();

        drafts.Text("list", Declared).ShouldBe(Declared);
        drafts.IsDirty("list", Declared).ShouldBeFalse();
    }

    [Fact]
    public void Typing_makes_it_dirty_and_typing_it_back_makes_it_clean()
    {
        var drafts = new RuleDrafts();

        drafts.Set("list", "true");
        drafts.IsDirty("list", Declared).ShouldBeTrue();

        drafts.Set("list", Declared + "  ");
        drafts.IsDirty("list", Declared).ShouldBeFalse("surrounding space is not a change the apply sees");
    }

    [Fact]
    public void Take_answers_the_trimmed_draft_and_forgets_it()
    {
        var drafts = new RuleDrafts();
        drafts.Set("get", "  true ");

        drafts.Take("get", null).ShouldBe("true");
        drafts.IsDirty("get", "true").ShouldBeFalse();
        drafts.Text("get", "true").ShouldBe("true");
    }

    [Fact]
    public void Revert_puts_back_what_the_descriptor_declares()
    {
        var drafts = new RuleDrafts();
        drafts.Set("delete", "false");

        drafts.Revert("delete");

        drafts.Text("delete", Declared).ShouldBe(Declared);
    }

    [Fact]
    public void Any_dirty_asks_each_draft_against_what_its_operation_declares_and_clear_forgets_them_all()
    {
        var drafts = new RuleDrafts();
        drafts.Set("list", Declared);
        drafts.AnyDirty(_ => Declared).ShouldBeFalse("a draft typed back to the declaration saves nothing");

        drafts.Set("get", "true");
        drafts.AnyDirty(_ => Declared).ShouldBeTrue();

        drafts.Clear();
        drafts.AnyDirty(_ => null).ShouldBeFalse();
        drafts.Text("get", Declared).ShouldBe(Declared);
    }

    [Fact]
    public void Every_change_to_a_draft_is_announced_so_the_owner_redraws_its_guard()
    {
        var drafts = new RuleDrafts();
        var raised = 0;
        drafts.Changed += () => raised++;

        drafts.Set("list", "true");
        drafts.Revert("list");
        drafts.Set("get", "true");
        drafts.Take("get", null);
        drafts.Clear();

        raised.ShouldBe(5);
    }

    [Fact]
    public void An_operation_with_no_rule_reads_empty()
        => new RuleDrafts().Text("create", null).ShouldBe(string.Empty);
}
