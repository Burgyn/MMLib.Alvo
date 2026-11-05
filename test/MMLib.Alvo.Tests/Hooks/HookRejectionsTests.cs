using MMLib.Alvo.Hooks;
using MMLib.Alvo.Hooks.Internal;

namespace MMLib.Alvo.Tests.Hooks;

/// <summary>The rejection model (spec 2026-10-14 §4.4.2): precedence, every violation of the winning kind, RFC 6901 pointers, Ruling X.</summary>
public sealed class HookRejectionsTests
{
    private static readonly string[] _declared = ["title", "slot", "starts_at", "a/b~c", "internal_score"];

    private static readonly string[] _hidden = ["internal_score"];

    /// <summary>What the runner hands a hook's rejections: the declared fields the descriptor never hides (Ruling 53).</summary>
    private static readonly IReadOnlySet<string> _pointable = _declared.Except(_hidden, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Nothing_recorded_is_no_rejection() => new HookRejections("guard", _pointable).Winner().ShouldBeNull();

    [Fact]
    public void Forbidden_beats_conflict_beats_invalid_and_carries_every_violation_of_its_kind()
    {
        var rejections = new HookRejections("guard", _pointable);
        rejections.Record(AlvoWriteRejectionKind.Invalid, "too-short", "Too short.", "title");
        rejections.Record(AlvoWriteRejectionKind.Conflict, "taken", "Taken.", "slot");
        rejections.Record(AlvoWriteRejectionKind.Conflict, "overlap", "Overlaps.", "starts_at");

        var winner = rejections.Winner().ShouldNotBeNull();
        winner.Kind.ShouldBe(AlvoWriteRejectionKind.Conflict);
        winner.Violations.Select(violation => (violation.Pointer, violation.Code)).ShouldBe([("/slot", "taken"), ("/starts_at", "overlap")]);
        (winner.Code, winner.Pointer, winner.ExtensionId).ShouldBe(("taken", "/slot", "guard"));

        rejections.Record(AlvoWriteRejectionKind.Forbidden, "not-yours", "Not yours.", null);
        rejections.Winner()!.Kind.ShouldBe(AlvoWriteRejectionKind.Forbidden);
    }

    [Theory]
    [InlineData("internal_score", "")]
    [InlineData("title", "/title")]
    public void A_declared_hidden_field_gets_the_body_pointer_and_a_visible_one_its_own(string field, string expected) =>
        Recorded(AlvoWriteRejectionKind.Invalid, "too-low", "Below the bar.", field).Pointer.ShouldBe(expected);

    [Fact]
    public void Rejections_recorded_from_parallel_tasks_are_all_kept()
    {
        var rejections = new HookRejections("guard", _pointable);

        Parallel.For(0, 1000, index => rejections.Record(AlvoWriteRejectionKind.Invalid, $"bad-{index}", "Bad.", "title"));

        rejections.Winner()!.Violations.Count.ShouldBe(1000);
    }

    [Theory]
    [InlineData("undeclared_field")]
    [InlineData("Title")]
    public void A_name_that_is_not_a_declared_visible_field_gets_the_body_pointer(string field) =>
        Recorded(AlvoWriteRejectionKind.Invalid, "bad", "Bad.", field).Pointer.ShouldBe(string.Empty);

    [Fact]
    public void A_field_name_is_escaped_as_a_json_pointer() =>
        Recorded(AlvoWriteRejectionKind.Invalid, "bad", "Bad.", "a/b~c").Pointer.ShouldBe("/a~1b~0c");

    [Theory]
    [InlineData("", "message")]
    [InlineData("code", " ")]
    public void A_rejection_needs_a_code_and_a_message(string code, string message) =>
        Should.Throw<ArgumentException>(() => new HookRejections("guard", _pointable).Record(AlvoWriteRejectionKind.Invalid, code, message, null));

    [Fact]
    public void A_rejection_is_one_batch_row_s_refusal()
    {
        var rejection = Recorded(AlvoWriteRejectionKind.Conflict, "taken", "Taken.", "slot");

        var row = ((MMLib.Alvo.Rules.IRowRefusal)rejection).At(4);

        (row.Index, row.Code, row.Message, row.FixSuggestion).ShouldBe((4, "taken", "Taken.", (string?)null));
    }

    private static AlvoWriteRejectedException Recorded(AlvoWriteRejectionKind kind, string code, string message, string? field)
    {
        var rejections = new HookRejections("guard", _pointable);
        rejections.Record(kind, code, message, field);

        return rejections.Winner()!;
    }
}
