using MMLib.Alvo.Admin.Components.DesignSystem;

namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>Closing an editor with unsaved changes asks first (spec §3.4; inventory defect #5).</summary>
public sealed class EditorCloseGuardTests
{
    [Fact]
    public void A_clean_editor_closes_at_once()
    {
        var guard = new EditorCloseGuard();

        guard.RequestClose(dirty: false).ShouldBeTrue();
        guard.Asking.ShouldBeFalse();
    }

    [Fact]
    public void A_dirty_editor_asks_instead_of_closing()
    {
        var guard = new EditorCloseGuard();

        guard.RequestClose(dirty: true).ShouldBeFalse();
        guard.Asking.ShouldBeTrue();
    }

    [Fact]
    public void Asking_again_does_not_turn_the_question_into_a_discard()
    {
        var guard = new EditorCloseGuard();
        guard.RequestClose(dirty: true);

        guard.RequestClose(dirty: true).ShouldBeFalse();
        guard.Asking.ShouldBeTrue();
    }

    [Fact]
    public void Keep_editing_and_discard_both_end_the_question()
    {
        var kept = new EditorCloseGuard();
        kept.RequestClose(dirty: true);
        kept.KeepEditing();
        kept.Asking.ShouldBeFalse();

        var discarded = new EditorCloseGuard();
        discarded.RequestClose(dirty: true);
        discarded.Discard();
        discarded.Asking.ShouldBeFalse();
    }
}
