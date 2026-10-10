using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// <see cref="AppliedSchemaView"/> — the per-request read of the applied schema that lets a runtime apply govern
/// the next request's fields (#353) — and the one-revision guarantee it gives a decision and a field set.
/// </summary>
/// <remarks>
/// Driven directly, because the interleaving these facts need (an apply landing <em>between</em> a decision and
/// the field read) cannot be scheduled over HTTP. The HTTP behaviour is
/// <c>DataApiEngineTests</c>' runtime-apply facts, on every engine.
/// </remarks>
public class AppliedSchemaViewTests
{
    private static readonly SchemaModel _boot = Schema("title");
    private static readonly SchemaModel _applied = Schema("title", "category");

    [Fact]
    public void A_decision_taken_while_an_apply_lands_is_retaken_against_the_new_revision()
    {
        var registry = new SwitchableRegistry(_boot);
        var view = new AppliedSchemaView(new EntityRouteCatalog(registry));
        var attempts = 0;

        var (decision, entity) = view.Decide("tickets", () =>
        {
            if (++attempts == 1)
            {
                registry.Current = _applied;
            }

            return attempts;
        });

        decision.ShouldBe(2, "the first decision straddled the apply, so it was taken again");
        entity.Schema.ShouldBeSameAs(_applied.Entities[0], "the fields are the revision the kept decision saw");
    }

    [Fact]
    public void A_registry_that_never_holds_still_fails_the_request_rather_than_pairing_two_revisions()
    {
        var registry = new SwitchableRegistry(_boot);
        var view = new AppliedSchemaView(new EntityRouteCatalog(registry));

        Should.Throw<InvalidOperationException>(() => view.Decide("tickets", () =>
        {
            registry.Current = registry.Current == _boot ? _applied : _boot;
            return 0;
        }));
    }

    [Fact]
    public void An_entity_the_decided_revision_does_not_declare_is_refused_like_an_unmapped_one()
    {
        var view = new AppliedSchemaView(new EntityRouteCatalog(new SwitchableRegistry(_boot)));

        var refusal = Should.Throw<AlvoAuthorizationException>(() => view.Decide("dropped", () => 0));

        refusal.Message.ShouldBe("The operation was not authorized.");
    }

    [Fact]
    public void A_revision_is_built_once_and_a_new_one_replaces_it()
    {
        var registry = new SwitchableRegistry(_boot);
        var view = new AppliedSchemaView(new EntityRouteCatalog(registry));

        var first = view.Current;
        view.Current.ShouldBeSameAs(first, "an unchanged revision is not rebuilt per request");
        registry.Current = _applied;

        view.Current.Schema.ShouldBeSameAs(_applied);
        view.Current.Entities["tickets"].Fields.Select(field => field.Name).ShouldContain("category");
    }

    [Fact]
    public void A_revision_the_query_guard_refuses_fails_the_request_rather_than_being_served()
    {
        var registry = new SwitchableRegistry(_boot);
        var view = new AppliedSchemaView(new EntityRouteCatalog(registry));
        registry.Current = Schema("title", ReservedQueryKeys.Limit);

        Should.Throw<InvalidOperationException>(() => view.Decide("tickets", () => 0));
    }

    private static SchemaModel Schema(params string[] fields) => new([
        new EntitySchema
        {
            Name = "tickets",
            Fields = [.. fields.Select(name => new FieldSchema { Name = name, Type = FieldType.String })],
        },
    ]);

    private sealed class SwitchableRegistry(SchemaModel initial) : ISchemaRegistry
    {
        public SchemaModel Current { get; set; } = initial;

        public SchemaModel GetSchema() => Current;
    }
}
