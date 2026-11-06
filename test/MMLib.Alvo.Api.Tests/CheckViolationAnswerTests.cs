using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// A write the engine refuses for a field's validation is 422 <c>validation</c>, code <c>check</c>, pointed at the field
/// the port named — or at the whole document when it named none — and its wording carries no value (Ruling 24).
/// </summary>
public sealed class CheckViolationAnswerTests
{
    [Fact]
    public async Task A_check_on_a_named_field_is_422_validation_with_its_pointer()
    {
        var (status, document) = await ProblemWriting.WrittenAsync(ProblemResultFactory.PortRefusalFor(
            new AlvoConstraintViolationException(AlvoConstraintKind.Check, ["stock"])).ShouldNotBeNull());
        status.ShouldBe(422);
        document["type"]!.GetValue<string>().ShouldEndWith(AlvoProblemTypes.Validation);
        document["violations"]![0]!["pointer"]!.GetValue<string>().ShouldBe("/stock");
        document["violations"]![0]!["code"]!.GetValue<string>().ShouldBe("check");
    }

    [Fact]
    public async Task A_check_naming_no_field_points_at_the_document()
    {
        var (status, document) = await ProblemWriting.WrittenAsync(ProblemResultFactory.PortRefusalFor(
            new AlvoConstraintViolationException(AlvoConstraintKind.Check, [])).ShouldNotBeNull());
        status.ShouldBe(422);
        document["violations"]![0]!["pointer"]!.GetValue<string>().ShouldBe("");
    }

    /// <summary>The exact wire text for both shapes — the field's and the document's — and that neither echoes anything else.</summary>
    [Fact]
    public async Task The_wording_is_the_declared_text_and_names_no_value()
    {
        var (_, named) = await ProblemWriting.WrittenAsync(ProblemResultFactory.PortRefusalFor(
            new AlvoConstraintViolationException(AlvoConstraintKind.Check, ["stock"])).ShouldNotBeNull());
        var (_, unnamed) = await ProblemWriting.WrittenAsync(ProblemResultFactory.PortRefusalFor(
            new AlvoConstraintViolationException(AlvoConstraintKind.Check, [])).ShouldNotBeNull());

        (named["violations"]![0]!["message"]!.GetValue<string>(), named["violations"]![0]!["fixSuggestion"]!.GetValue<string>()).ShouldBe((
            "This value breaks the validation the descriptor declares for this field.",
            "Send a value the field's validation accepts."));
        (unnamed["violations"]![0]!["message"]!.GetValue<string>(), unnamed["violations"]![0]!["fixSuggestion"]!.GetValue<string>()).ShouldBe((
            "This write would leave a record breaking a validation the descriptor declares.",
            "Change the values this write sends so every record it touches keeps its validations."));
        named["detail"]!.GetValue<string>().ShouldBe("This value breaks the validation the descriptor declares for this field.");
    }
}
