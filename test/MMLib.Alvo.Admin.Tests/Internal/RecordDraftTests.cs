using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// What a save sends: the changed fields only, as the types the data port binds.
/// </summary>
public class RecordDraftTests
{
    private static readonly FieldSchema _number = new() { Name = "order_number", Type = FieldType.String, Required = true };
    private static readonly FieldSchema _hours = new() { Name = "labour_hours", Type = FieldType.Decimal, Precision = 6, Scale = 2 };
    private static readonly FieldSchema _secret = new() { Name = "internal_notes", Type = FieldType.Text };

    [Fact]
    public void Nothing_changed_is_nothing_to_send()
    {
        var draft = Opened();

        draft.Dirty.ShouldBeFalse();
        draft.Read().Values.ShouldBeEmpty();
    }

    [Fact]
    public void Retyping_the_same_value_is_not_a_change()
    {
        var draft = Opened();
        draft.Set("labour_hours", "1.00");

        draft.Dirty.ShouldBeFalse("the control opened on 1.00 and still says 1.00");
    }

    [Fact]
    public void Only_the_changed_field_is_sent_and_as_a_decimal()
    {
        var draft = Opened();
        draft.Set("labour_hours", "2.5");

        var changes = draft.Read();

        changes.Values.Keys.ShouldBe(["labour_hours"]);
        changes.Values["labour_hours"].ShouldBe(2.5m);
        changes.Problems.ShouldBeEmpty();
    }

    [Fact]
    public void A_field_the_caller_cannot_read_is_not_sent_back_as_null()
        => Opened().Read().Values.ShouldNotContainKey("internal_notes");

    [Fact]
    public void Text_that_cannot_be_read_is_a_problem_and_not_a_value()
    {
        var draft = Opened();
        draft.Set("labour_hours", "2,5");

        var changes = draft.Read();

        changes.Values.ShouldBeEmpty();
        changes.Problems.Keys.ShouldBe(["labour_hours"]);
    }

    [Fact]
    public void A_new_record_sends_what_was_filled_in()
    {
        var draft = new RecordDraft([_number, _hours, _secret], new Dictionary<string, object?>());
        draft.Set("order_number", "SO-2026-0200");

        draft.Read().Values.ShouldBe(new Dictionary<string, object?> { ["order_number"] = "SO-2026-0200" });
    }

    [Fact]
    public void Clearing_a_value_sends_null()
    {
        var draft = Opened();
        draft.Set("labour_hours", string.Empty);

        draft.Read().Values.ShouldBe(new Dictionary<string, object?> { ["labour_hours"] = null });
    }

    private static RecordDraft Opened() => new(
        [_number, _hours, _secret],
        new Dictionary<string, object?> { ["order_number"] = "SO-2026-0163", ["labour_hours"] = 1.0m });
}
