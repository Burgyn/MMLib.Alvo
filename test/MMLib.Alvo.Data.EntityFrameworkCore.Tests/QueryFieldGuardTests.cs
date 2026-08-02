using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class QueryFieldGuardTests
{
    private static readonly EntitySchema _entity = new()
    {
        Name = "accounts",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "title", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "secret", Type = FieldType.String, Nullable = true },
        ],
    };

    [Fact]
    public void A_declared_visible_field_is_allowed()
        => QueryFieldGuard.EnsureAvailable(["title"], _entity, Hidden());

    [Fact]
    public void A_hidden_field_is_refused()
        => Should.Throw<AlvoAuthorizationException>(() => QueryFieldGuard.EnsureAvailable(["secret"], _entity, Hidden("secret")));

    [Fact]
    public void An_undeclared_field_is_refused_with_the_identical_message()
    {
        var undeclared = Should.Throw<AlvoAuthorizationException>(
            () => QueryFieldGuard.EnsureAvailable(["title\"; DROP TABLE items; --"], _entity, Hidden()));
        var hidden = Should.Throw<AlvoAuthorizationException>(
            () => QueryFieldGuard.EnsureAvailable(["secret"], _entity, Hidden("secret")));

        undeclared.Message.ShouldBe(hidden.Message);
        undeclared.Message.ShouldNotContain("DROP TABLE");
        undeclared.Message.ShouldBe(
            AlvoAuthorizationException.QueryFieldUnavailable,
            "the wording lives on the port because PR3's query-string parser refuses the same names one layer "
            + "up, and a caller able to tell the two refusals apart would have the oracle they exist to close");
    }

    [Fact]
    public void A_field_matching_only_case_insensitively_is_undeclared()
        => Should.Throw<AlvoAuthorizationException>(() => QueryFieldGuard.EnsureAvailable(["Title"], _entity, Hidden()));

    [Fact]
    public void An_unknown_entity_refuses_every_field_rather_than_waving_them_through()
        => Should.Throw<AlvoAuthorizationException>(() => QueryFieldGuard.EnsureAvailable(["title"], entity: null, Hidden()));

    [Fact]
    public void Every_field_in_the_list_is_checked_not_only_the_first()
        => Should.Throw<AlvoAuthorizationException>(
            () => QueryFieldGuard.EnsureAvailable(["title", "secret"], _entity, Hidden("secret")));

    [Fact]
    public void A_payload_naming_an_undeclared_field_is_refused()
        => Should.Throw<AlvoAuthorizationException>(() => QueryFieldGuard.EnsureDeclared(
            new Dictionary<string, object?> { ["nope"] = 1 }, _entity));

    [Fact]
    public void A_payload_may_name_a_hidden_field_because_writing_one_is_not_reading_it()
        => QueryFieldGuard.EnsureDeclared(new Dictionary<string, object?> { ["secret"] = "x" }, _entity);

    /// <summary>
    /// The two refusals are deliberately distinguishable from each other — a read leaks nothing either way,
    /// while a write's rejection is about the payload the caller sent — and neither echoes the field name.
    /// </summary>
    [Fact]
    public void Neither_refusal_echoes_the_caller_supplied_name()
    {
        var query = Should.Throw<AlvoAuthorizationException>(
            () => QueryFieldGuard.EnsureAvailable(["sneaky"], _entity, Hidden()));
        var payload = Should.Throw<AlvoAuthorizationException>(
            () => QueryFieldGuard.EnsureDeclared(new Dictionary<string, object?> { ["sneaky"] = 1 }, _entity));

        query.Message.ShouldNotContain("sneaky");
        payload.Message.ShouldNotContain("sneaky");
    }

    [Fact]
    public void A_payload_against_an_unknown_entity_is_refused()
        => Should.Throw<AlvoAuthorizationException>(() => QueryFieldGuard.EnsureDeclared(
            new Dictionary<string, object?> { ["title"] = 1 }, entity: null));

    [Fact]
    public void A_mask_that_hides_no_key_property_is_applied()
        => QueryFieldGuard.EnsureMaskable(Hidden("secret"), ReadModelFixture.Rows(_entity));

    /// <summary>
    /// The read path's fail-closed belt: a mask naming the row key is refused here, whatever schema source
    /// produced it, because a <c>NULL</c>-projected key throws at materialization with a different exception
    /// type on each engine.
    /// </summary>
    [Fact]
    public void A_mask_that_hides_the_row_key_is_refused()
        => Should.Throw<AlvoAuthorizationException>(
            () => QueryFieldGuard.EnsureMaskable(Hidden("id"), ReadModelFixture.Rows(_entity)));

    [Fact]
    public void A_model_with_no_key_at_all_is_refused_rather_than_masked()
        => Should.Throw<AlvoAuthorizationException>(
            () => QueryFieldGuard.EnsureMaskable(Hidden(), ReadModelFixture.KeylessRows()));

    [Fact]
    public void Every_argument_is_required()
    {
        var rows = ReadModelFixture.Rows(_entity);

        Should.Throw<ArgumentNullException>(() => QueryFieldGuard.EnsureAvailable(null!, _entity, Hidden()));
        Should.Throw<ArgumentNullException>(() => QueryFieldGuard.EnsureAvailable(["title"], _entity, null!));
        Should.Throw<ArgumentNullException>(() => QueryFieldGuard.EnsureDeclared(null!, _entity));
        Should.Throw<ArgumentNullException>(() => QueryFieldGuard.EnsureMaskable(null!, rows));
        Should.Throw<ArgumentNullException>(() => QueryFieldGuard.EnsureMaskable(Hidden(), null!));
    }

    private static HashSet<string> Hidden(params string[] fields) => fields.ToHashSet(StringComparer.Ordinal);
}
