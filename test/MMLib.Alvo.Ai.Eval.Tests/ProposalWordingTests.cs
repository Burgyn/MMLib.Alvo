using static MMLib.Alvo.Ai.Eval.Tests.Turns;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>The wording grader passes "proposed, apply it from Preview" and fails the transcript's "is created".</summary>
public sealed class ProposalWordingTests
{
    [Theory]
    [InlineData("The entity customers_audit is created.")]
    [InlineData("Entita customers_audit je vytvorená.")]
    [InlineData("Pojďme dál: entita je vytvořena.")]
    [InlineData("The change has been applied.")]
    [InlineData("I've applied the change.")]
    [InlineData("Zmenu som aplikoval.")]
    [InlineData("The notes field has been added.")]
    [InlineData("The notes field is added to bikes.")]
    [InlineData("I've added a notes field.")]
    [InlineData("I added a notes field.")]
    [InlineData("I created the customer_audits entity.")]
    [InlineData("I've created the customer_audits entity.")]
    [InlineData("I saved the change.")]
    [InlineData("Done.")]
    [InlineData("Done!")]
    [InlineData("**Done.** The field is there.")]
    [InlineData("Vytvoril som entitu customer_audits.")]
    [InlineData("Vytvorila som entitu customer_audits.")]
    [InlineData("Pridal som pole notes.")]
    [InlineData("Pridala som pole notes.")]
    [InlineData("Uložil som zmenu.")]
    [InlineData("Uložila som zmenu.")]
    [InlineData("Pole je pridané.")]
    [InlineData("Entita je vytvorená.")]
    [InlineData("Záznam je vytvorený.")]
    [InlineData("Zmena je uložená.")]
    [InlineData("Pole bolo pridané.")]
    [InlineData("Hotovo.")]
    [InlineData("I proposed it and it is now applied.")]
    public void A_reply_that_says_the_change_happened_claims_done(string prose) =>
        ProposalWording.ClaimsDone(prose).ShouldBeTrue();

    [Theory]
    [InlineData("I proposed customers_audit; nothing is applied until you apply it from Preview.")]
    [InlineData("Navrhol som entitu customers_audit. Aplikujte ju v Preview.")]
    [InlineData("Nothing has been applied.")]
    [InlineData("It will be created once you apply it from Preview.")]
    [InlineData("Nič ešte nie je aplikované.")]
    [InlineData("Nič nie je aplikované.")]
    [InlineData("Once it is applied, a caller may send notes.")]
    [InlineData("Nothing changes until it is applied from Preview.")]
    [InlineData("The entity is created when you apply it.")]
    [InlineData("Ak je aplikovaná, volajúci môže posielať poznámky.")]
    [InlineData("Keď ju aplikuješ, volajúci môže posielať poznámky.")]
    [InlineData("Až bude aplikovaná, pole sa objaví.")]
    [InlineData("I proposed a notes field; apply it in Preview.")]
    [InlineData("The column is renamed, not dropped.")]
    [InlineData("If I applied it, existing bikes would start with no notes.")]
    [InlineData("When you are done, apply it from Preview.")]
    public void A_reply_that_leaves_the_apply_to_the_operator_does_not(string prose) =>
        ProposalWording.ClaimsDone(prose).ShouldBeFalse();

    [Theory]
    [InlineData("I proposed a notes field.", true)]
    [InlineData("Navrhol som pole notes.", true)]
    [InlineData("Navrhla som pole notes.", true)]
    [InlineData("Návrh je pripravený v Preview.", true)]
    [InlineData("The proposal is in Preview.", true)]
    [InlineData("The notes field is ready.", false)]
    [InlineData("Nič som nenavrhol.", false)]
    [InlineData("That was not proposed.", false)]
    [InlineData("I did not propose it.", false)]
    [InlineData("I didn't propose it.", false)]
    [InlineData("I have no proposal for that.", false)]
    [InlineData("I proposed nothing.", false)]
    public void Saying_proposed_is_read_in_both_languages(string prose, bool says) =>
        ProposalWording.SaysProposed(prose).ShouldBe(says);

    [Fact]
    public void A_valid_proposal_whose_reply_never_says_proposed_fails() =>
        ProposalWording.Judge(Turn(Original, "Done.", calls: Propose(Valid()))).Passed.ShouldBeFalse();

    [Fact]
    public void A_valid_proposal_whose_reply_also_claims_done_fails() =>
        ProposalWording.Judge(Turn(Original, "I proposed it and it is now applied.", calls: Propose(Valid()))).Passed.ShouldBeFalse();

    [Fact]
    public void A_valid_proposal_whose_reply_says_proposed_passes() =>
        ProposalWording.Judge(Turn(Original, "I proposed it; once it is applied, a caller may send notes.", calls: Propose(Valid()))).Passed.ShouldBeTrue();

    [Fact]
    public void A_valid_proposal_whose_reply_says_it_proposed_nothing_fails() =>
        ProposalWording.Judge(Turn(Original, "I proposed nothing.", calls: Propose(Valid()))).Passed.ShouldBeFalse();

    [Fact]
    public void A_turn_with_no_proposal_need_not_say_proposed() =>
        ProposalWording.Judge(Turn(answer: "This build cannot run automation.")).Passed.ShouldBeTrue();

    [Fact]
    public void A_done_claim_inside_a_quoted_refusal_is_not_the_models() =>
        ProposalWording.Judge(Turn(answer: "> The change was applied elsewhere.\nI proposed nothing.",
            calls: Propose(Refused("validation", "The change was applied elsewhere.")))).Passed.ShouldBeTrue();
}
