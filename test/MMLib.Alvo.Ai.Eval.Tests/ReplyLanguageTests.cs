using static MMLib.Alvo.Ai.Eval.Tests.Turns;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>The language grader tells Slovak from English and from Czech, on the model's own prose.</summary>
public sealed class ReplyLanguageTests
{
    [Theory]
    [InlineData("I proposed the entity `customers_audit`; nothing changes until you apply it from Preview.", "en")]
    [InlineData("Navrhol som entitu `customers_audit`. Kým ju neaplikujete v Preview, nič sa nezmení.", "sk")]
    [InlineData("Pojďme na to: entita je připravena.", "cs")]
    [InlineData("Entita je pripravená, pojďme ďalej.", "cs")]
    [InlineData("Navrhol som to pro vás.", "cs")]
    [InlineData("`customers_audit`", "unknown")]
    public void The_language_of_a_reply_is_read_from_its_words(string prose, string language) =>
        ReplyLanguage.Of(prose).ShouldBe(language);

    [Fact]
    public void A_slovak_reply_after_an_english_quote_block_is_slovak()
    {
        const string Refusal = "Field 'id' is a framework-managed column and cannot be declared.";
        var turn = Turn(answer: $"> {Refusal}\n\nPole `id` pridáva framework sám, preto som ho z návrhu vynechal.",
            calls: Propose(Refused("validation", Refusal)));

        ReplyLanguage.Judge(turn, ReplyLanguage.Slovak).Passed.ShouldBeTrue();
    }

    [Fact]
    public void An_english_reply_to_a_slovak_question_fails() =>
        ReplyLanguage.Judge(Turn(answer: "I proposed the notes field; apply it from Preview."), ReplyLanguage.Slovak).Passed.ShouldBeFalse();

    [Fact]
    public void A_czech_reply_to_a_slovak_question_fails() =>
        ReplyLanguage.Judge(Turn(answer: "Pojďme dál: navrhl jsem pole notes."), ReplyLanguage.Slovak).Passed.ShouldBeFalse();

    [Fact]
    public void A_reply_with_no_prose_of_its_own_fails() =>
        ReplyLanguage.Judge(Turn(answer: "`customers_audit`"), ReplyLanguage.English).Passed.ShouldBeFalse();
}
