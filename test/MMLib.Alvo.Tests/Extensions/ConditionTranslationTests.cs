using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Events;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Tests.Data;
using MMLib.Alvo.Tests.Expressions;

using System.Linq.Expressions;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>
/// The <c>when</c> column of spec 2026-10-14 §4.3 as data (HX-05 acceptance (1)): each lambda's CEL, which reads back as
/// itself and compiles against the entity; every refused construct throws naming the extension and a fix (acceptance (4)).
/// </summary>
public sealed class ConditionTranslationTests
{
    private const decimal Ceiling = 1000m;

    private static readonly Guid _fleet = Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6");
    private static readonly DateTimeOffset _cutoff = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly decimal _threshold = 250.5m;
    private static readonly OrderStatus[] _open = [OrderStatus.Draft, OrderStatus.InReview];
    private static readonly string[] _noNotes = [];
    private static readonly HashSet<string> _tags = ["a", "b"];
    private static readonly string _longNote = new('x', CelParser.MaxSourceLength);

    public static TheoryData<string, string, Expression<Func<EventCondition<Order>, bool>>, string> Translated()
    {
        const long limit = 99;

        return new()
        {
            { "snake_case field", "Updated", e => e.New.Note == "x", "new.note == 'x'" },
            { "[AlvoField] name", "Updated", e => e.New.Reference == "R-1", "new.ref_no == 'R-1'" },
            { "both images", "Updated", e => e.Old.Quantity < e.New.Quantity, "old.quantity < new.quantity" },
            { "changed, several", "Updated", e => e.Changed(x => x.Status, x => x.Total), "changed(status) || changed(total)" },
            { "changed and a value", "Updated", e => e.Changed(x => x.Status) && e.New.Status == OrderStatus.Approved, "changed(status) && new.status == 'ok'" },
            { "changed inside and", "Updated", e => e.Changed(x => x.Status, x => x.Total) && e.New.IsPaid, "(changed(status) || changed(total)) && new.is_paid" },
            { "relations", "Updated", e => e.New.Total >= 10m && e.New.Quantity != 3, "new.total >= 10.0 && new.quantity != 3" },
            { "negation", "Updated", e => !e.New.IsPaid || e.New.Total > 0.5m, "!new.is_paid || new.total > 0.5" },
            { "negated group", "Updated", e => !(e.New.IsPaid && e.New.Quantity > 1), "!(new.is_paid && new.quantity > 1)" },
            { "arithmetic", "Updated", e => e.New.Quantity + 1 > 5 && e.New.Total * 2m > 100m, "new.quantity + 1 > 5 && new.total * 2.0 > 100.0" },
            { "arithmetic precedence", "Updated", e => (e.New.Quantity + 1) * 2 > 10, "(new.quantity + 1) * 2 > 10" },
            { "null test", "Updated", e => e.New.Code == null, "!has(new.code)" },
            { "not-null tests", "Updated", e => e.New.Code != null && e.New.DueAt.HasValue, "has(new.code) && has(new.due_at)" },
            { "null on the left", "Updated", e => null == e.New.Code, "!has(new.code)" },
            { "text tests", "Updated", e => e.New.Note.StartsWith("ab") || e.New.Note.EndsWith("yz", StringComparison.Ordinal) || e.New.Note.Contains("mid"), "startsWith(new.note, 'ab') || endsWith(new.note, 'yz') || contains(new.note, 'mid')" },
            { "list of enum members", "Updated", e => _open.Contains(e.New.Status), "new.status == 'draft' || new.status == 'in_review'" },
            { "list of texts", "Updated", e => new[] { "a", "b" }.Contains(e.New.Note), "new.note == 'a' || new.note == 'b'" },
            { "enum member", "Updated", e => e.New.Status != OrderStatus.Draft, "new.status != 'draft'" },
            { "nullable enum", "Updated", e => e.New.Previous == OrderStatus.InReview, "new.previous == 'in_review'" },
            { "enum images", "Updated", e => e.Old.Status != e.New.Status, "old.status != new.status" },
            { "role", "Updated", e => e.Caller.HasRole("manager"), "'manager' in @user.roles" },
            { "caller id", "Updated", e => e.New.OwnerId == e.Caller.User, "new.owner_id == @user.id" },
            { "caller tenant", "Updated", e => e.Caller.Tenant == e.New.OwnerId, "@tenant.id == new.owner_id" },
            { "static readonly and const", "Updated", e => e.New.Total > _threshold && e.New.Total < Ceiling, "new.total > 250.5 && new.total < 1000.0" },
            { "local const", "Updated", e => e.New.Quantity < limit, "new.quantity < 99" },
            { "id constant", "Updated", e => e.New.OwnerId == _fleet, "string(new.owner_id) == '3fa85f64-5717-4562-b3fc-2c963f66afa6'" },
            { "instant constant", "Updated", e => e.New.DueAt < _cutoff, "new.due_at < timestamp('2026-01-01T00:00:00Z')" },
            { "escaped text", "Updated", e => e.New.Note == "it's \\ \"quoted\"\n", @"new.note == 'it\'s \\ ""quoted""\n'" },
            { "negative constant", "Updated", e => e.New.Quantity > -5, "new.quantity > -5" },
            { "negative decimal", "Updated", e => e.New.Total > -1.5m, "new.total > -1.5" },
            { "list of ids", "Updated", e => new[] { _fleet }.Contains(e.New.OwnerId!.Value), "string(new.owner_id) == '3fa85f64-5717-4562-b3fc-2c963f66afa6'" },
            { "list of enum numbers", "Updated", e => new[] { 0, 1 }.Contains((int)e.New.Status), "new.status == 'draft' || new.status == 'in_review'" },
            { "bool literal", "Updated", e => e.New.IsPaid == true, "new.is_paid == true" },
            { "value of a nullable", "Updated", e => e.New.Rank!.Value > 2, "new.rank > 2" },
            { "created", "Created", e => e.New.Total > 0m, "new.total > 0.0" },
            { "deleted", "Deleted", e => e.Old.IsPaid, "old.is_paid" },
            { "saved", "Saved", e => e.New.Quantity >= 1, "new.quantity >= 1" },
        };
    }

    public static TheoryData<string, string, Expression<Func<EventCondition<Order>, bool>>, string> Refused()
    {
        var threshold = 5m;

        return new()
        {
            { "method on a field", "Updated", e => e.New.Note.GetHashCode() == 1, "method 'GetHashCode' has no CEL equivalent" },
            { "clock", "Updated", e => e.New.DueAt < DateTimeOffset.UtcNow, "conditions have no clock yet" },
            { "local clock", "Updated", e => e.New.DueAt < DateTime.Now, "conditions have no clock yet" },
            { "captured variable", "Updated", e => e.New.Total > threshold, "'threshold' is a captured variable" },
            { "static property", "Updated", e => e.New.Note == Environment.NewLine, "'NewLine' is a static property" },
            { "pre-image on create", "Created", e => e.Old.Total > 0m, "carries no record before the write" },
            { "post-image on delete", "Deleted", e => e.New.Total > 0m, "carries no record after the write" },
            { "changed on save", "Saved", e => e.Changed(x => x.Status), "changed() compares the record before and after the write" },
            { "culture comparison", "Updated", e => e.New.Note.StartsWith("a", StringComparison.OrdinalIgnoreCase), "only the ordinal comparison translates" },
            { "conditional", "Updated", e => (e.New.IsPaid ? 1 : 0) == 1, "a conditional (?:) has no CEL equivalent" },
            { "relational enum", "Updated", e => e.New.Status > OrderStatus.Draft, "an enum field compares with == and != only" },
            { "smallest long", "Updated", e => e.New.Quantity > long.MinValue, "long.MinValue has no CEL literal" },
            { "unmapped property", "Updated", e => e.New.Summary == "x", "Order.Summary maps to no field" },
            { "empty list", "Updated", e => _noNotes.Contains(e.New.Note), "an empty list matches nothing" },
            { "computed constant", "Updated", e => e.New.Note == "a".Trim(), "it is computed when the condition runs" },
            { "unordered set", "Updated", e => _tags.Contains(e.New.Note), "has no fixed order" },
            { "old/new relational enum", "Updated", e => e.Old.Status < e.New.Status, "an enum field compares with == and != only" },
            { "arithmetic on an enum", "Updated", e => (int)e.New.Status + 1 == 2, "converts to a number only to compare with a member of its enum" },
            { "enum against a number field", "Updated", e => (int)e.New.Status == e.New.Quantity, "converts to a number only to compare with a member of its enum" },
            { "undeclared enum number", "Updated", e => (int)e.New.Status == 99, "OrderStatus has no member 99" },
            { "caller null test", "Updated", e => e.Caller.Tenant == null, "is the caller's, not a field of Order" },
            { "role on another caller", "Updated", e => default(AlvoConditionCaller)!.HasRole("manager"), "HasRole is read from the event's own caller" },
        };
    }

    [Theory]
    [MemberData(nameof(Translated))]
    public void A_condition_translates_to_its_cel(string row, string verb, Expression<Func<EventCondition<Order>, bool>> when, string cel)
    {
        var text = ConditionTranslator.Translate(when, "row-handler", Enum.Parse<HandlerVerb>(verb));

        text.ShouldBe(cel, row);
        CelText.Write(CelParser.Parse(text)).ShouldBe(text, $"{row}: the writer's text reads back as itself");
        CelFixtures.Compiler.Compile(text, CelProfile.Condition, TypedFixtures.OrdersSchema).IsSuccess.ShouldBeTrue($"{row}: {text} compiles");
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void A_construct_with_no_cel_equivalent_is_refused_naming_the_extension_and_a_fix(
        string row, string verb, Expression<Func<EventCondition<Order>, bool>> when, string reason)
    {
        var refusal = Should.Throw<AlvoTranslationException>(() => ConditionTranslator.Translate(when, "row-handler", Enum.Parse<HandlerVerb>(verb)));

        refusal.Message.ShouldContain("in extension 'row-handler' (when): ", customMessage: row);
        refusal.Message.ShouldContain(reason, customMessage: row);
        refusal.Message.ShouldContain(". Fix: ", customMessage: row);
        refusal.FixSuggestion.ShouldNotBeNullOrWhiteSpace(row);
    }

    [Fact]
    public void The_refusal_names_the_construct_the_extension_and_the_fix()
    {
        var refusal = Should.Throw<AlvoTranslationException>(() =>
            ConditionTranslator.Translate<Order>(e => e.New.Note.GetHashCode() == 1, "send-confirmation", HandlerVerb.Updated));

        refusal.Message.ShouldBe(
            "Cannot translate 'e.New.Note.GetHashCode()' in extension 'send-confirmation' (when): method 'GetHashCode' has no CEL "
            + "equivalent. Fix: write the condition as a CEL string — when: \"…\" — or move the check into the handler body.");
        refusal.FixSuggestion.ShouldBe(TranslationRefusal.WhenFix);
    }

    [Fact]
    public void A_condition_longer_than_cel_allows_is_refused_at_registration() =>
        Should.Throw<AlvoTranslationException>(() => ConditionTranslator.Translate<Order>(e => e.New.Note == _longNote, "long", HandlerVerb.Updated))
            .Message.ShouldContain($"past the {CelParser.MaxSourceLength} a condition may hold");

    [Fact]
    public void A_condition_nested_deeper_than_cel_allows_is_refused_at_registration()
    {
        var e = Expression.Parameter(typeof(EventCondition<Order>), "e");
        Expression paid = Expression.Property(Expression.Property(e, nameof(EventCondition<Order>.New)), nameof(Order.IsPaid));
        var body = paid;
        for (var level = 0; level <= CelParser.MaxDepth; level++)
        {
            body = Expression.Not(Expression.AndAlso(paid, body));
        }

        Should.Throw<AlvoTranslationException>(() => ConditionTranslator.Translate(Expression.Lambda<Func<EventCondition<Order>, bool>>(body, e), "deep", HandlerVerb.Updated))
            .Message.ShouldContain("its CEL does not parse back");
    }

    [Fact]
    public void A_record_key_that_is_not_a_field_name_is_refused() =>
        Should.Throw<AlvoTranslationException>(() => ConditionTranslator.Translate<OrderView>(e => (string?)e.New.Record["a == 'x' || true"] == "y", "row", HandlerVerb.Updated))
            .Message.ShouldContain("is not a field name a condition can spell");

    [Fact]
    public void A_wrapper_reads_any_field_through_its_record() =>
        ConditionTranslator.Translate<OrderView>(e => (string?)e.New.Record["legacy_code"] == "L-9" && e.New.Note != null, "row", HandlerVerb.Updated)
            .ShouldBe("new.legacy_code == 'L-9' && has(new.note)");

    [Fact]
    public void A_record_key_that_is_not_a_literal_is_refused()
    {
        var key = "note";

        Should.Throw<AlvoTranslationException>(() => ConditionTranslator.Translate<OrderView>(e => (string?)e.New.Record[key] == "y", "row", HandlerVerb.Updated))
            .Message.ShouldContain("a record's key must be a literal string");
    }

    [Fact]
    public void An_enum_number_outside_its_underlying_type_is_refused() =>
        Should.Throw<AlvoTranslationException>(() => ConditionTranslator.Translate<Gauge>(e => (int)e.New.Level == 257, "row", HandlerVerb.Updated))
            .Message.ShouldContain("257 is outside the range of GaugeLevel's underlying Byte");
}

public sealed class Gauge
{
    public GaugeLevel Level { get; init; }
}

#pragma warning disable CA1028
public enum GaugeLevel : byte
#pragma warning restore CA1028
{
    Low,
    High,
}
