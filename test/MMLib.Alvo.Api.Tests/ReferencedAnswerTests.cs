using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// A <c>referenced</c> refusal is 409 <c>conflict</c> pointed at the whole document either way, and its wording follows
/// the direction: a restricted delete tells the caller to remove what points here, a create or update whose <c>ref</c>
/// names a record it cannot point at tells them to point it elsewhere — one text whether that record exists nowhere or in
/// another tenant (#161).
/// </summary>
public sealed class ReferencedAnswerTests
{
    private const string DeleteFix = "Delete the records that reference this one, or point them at something else, then retry.";

    [Fact]
    public async Task A_refused_reference_on_a_write_is_409_conflict_worded_for_the_write()
    {
        var refused = AlvoConstraintViolationException.UnresolvedReference(innerException: null);

        var (status, document) = await ProblemWriting.WrittenAsync(ProblemResultFactory.PortRefusalFor(refused).ShouldNotBeNull());

        status.ShouldBe(409);
        document["type"]!.GetValue<string>().ShouldEndWith(AlvoProblemTypes.Conflict);
        var violation = document["violations"]!.AsArray().ShouldHaveSingleItem()!;
        (violation["pointer"]!.GetValue<string>(), violation["code"]!.GetValue<string>()).ShouldBe((string.Empty, "referenced"));
        violation["message"]!.GetValue<string>().ShouldBe(refused.Message);
        violation["fixSuggestion"]!.GetValue<string>().ShouldBe(AlvoConstraintViolationException.UnresolvedReferenceFix);
        violation["fixSuggestion"]!.GetValue<string>().ShouldNotBe(DeleteFix);
    }

    [Fact]
    public async Task A_restricted_delete_keeps_its_own_wording()
    {
        var refused = new AlvoConstraintViolationException(AlvoConstraintKind.Referenced, []);

        var (status, document) = await ProblemWriting.WrittenAsync(ProblemResultFactory.PortRefusalFor(refused).ShouldNotBeNull());

        status.ShouldBe(409);
        var violation = document["violations"]!.AsArray().ShouldHaveSingleItem()!;
        (violation["message"]!.GetValue<string>(), violation["fixSuggestion"]!.GetValue<string>()).ShouldBe((refused.Message, DeleteFix));
    }
}
