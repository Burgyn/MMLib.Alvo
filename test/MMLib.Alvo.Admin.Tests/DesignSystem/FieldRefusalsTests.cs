using MMLib.Alvo.Admin.Components.DesignSystem;

namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>A refusal that belongs to one field, drawn under it and handed focus (spec §3.3).</summary>
public sealed class FieldRefusalsTests
{
    [Fact]
    public void A_form_starts_with_nothing_refused()
    {
        var refusals = new FieldRefusals();

        refusals.Any.ShouldBeFalse();
        refusals.Has("email").ShouldBeFalse();
        refusals.For("email").ShouldBeNull();
        refusals.DescribedBy("email").ShouldBeNull();
        refusals.Under("email", () => ValueTask.CompletedTask).ShouldBeNull();
    }

    [Fact]
    public void A_refused_field_says_why_and_is_the_one_to_focus()
    {
        var refusals = new FieldRefusals();

        refusals.Refuse("email", "Type the address.");

        refusals.Has("email").ShouldBeTrue();
        refusals.For("email").ShouldBe("Type the address.");
        refusals.Focus.ShouldBe("email");
        refusals.DescribedBy("email").ShouldBe("email-problem");
        refusals.Under("email", () => ValueTask.CompletedTask).ShouldNotBeNull();
        refusals.Has("tenant").ShouldBeFalse();
    }

    [Fact]
    public void A_hinted_field_is_described_by_its_hint_and_while_refused_by_the_reason_too()
    {
        var refusals = new FieldRefusals();

        refusals.DescribedBy("tenant", "tenant-hint").ShouldBe("tenant-hint");
        refusals.Refuse("tenant", "Not a uuid.");
        refusals.DescribedBy("tenant", "tenant-hint").ShouldBe("tenant-hint tenant-problem");
    }

    [Fact]
    public void Every_refusal_is_a_new_attempt_so_the_same_field_takes_focus_again()
    {
        var refusals = new FieldRefusals();

        refusals.Refuse("email", "Type the address.");
        var first = refusals.Attempt;
        refusals.Refuse("email", "Type the address.");

        refusals.Attempt.ShouldBeGreaterThan(first);
    }

    [Fact]
    public void Typing_into_a_refused_field_clears_it_and_leaves_the_others()
    {
        var refusals = new FieldRefusals();
        refusals.Refuse("tenant", "Not a uuid.");
        refusals.Refuse("email", "Type the address.");

        refusals.Clear("email");

        refusals.Has("email").ShouldBeFalse();
        refusals.Has("tenant").ShouldBeTrue();
        refusals.Focus.ShouldBeNull("a cleared field is not asked to take focus again");
    }

    [Fact]
    public void Clearing_everything_forgets_every_field()
    {
        var refusals = new FieldRefusals();
        refusals.Refuse("tenant", "Not a uuid.");

        refusals.ClearAll();

        refusals.Any.ShouldBeFalse();
        refusals.Focus.ShouldBeNull();
    }
}
