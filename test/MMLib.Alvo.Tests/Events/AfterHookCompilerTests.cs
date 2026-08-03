using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Expressions;

using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// After-hooks compiled into the <see cref="PolicyCatalog"/> — conditions in
/// <see cref="MMLib.Alvo.Expressions.CelProfile.Condition"/>, every template parsed and validated once, and
/// every reference to an endpoint or a message template resolved, all at <b>apply</b> time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every fact here goes through <see cref="PolicyCatalog.TryBuild"/>, and that is R11.</b>
/// <c>EntitySchema</c>/<c>SchemaModel</c> carry no hooks, so a hook catalog had two possible homes: the
/// policy catalog's existing priming, or a fourth independently primed holder. The second is the failure
/// <c>IPolicyCatalogProvider</c>'s remarks were written to prevent — it would mean a hook compiled against a
/// different schema revision than the rules judging the same write. Driving these facts through the catalog
/// rather than through the compiler directly is what makes the one-priming-site claim load-bearing instead of
/// documentary, and
/// <see cref="The_hook_compiler_is_reached_from_the_policy_catalog_builder_and_nowhere_else"/> closes the
/// structural half.
/// </para>
/// <para>
/// <b>Refusals live at the slot's own JSON pointer</b>, with a leading slash, exactly as every other
/// apply-time refusal in this build does (<c>/entities/deals/rules/list</c>,
/// <c>/entities/orders/fields/owner_id/hidden</c>). A subject or body refusal points at
/// <c>/templates/{name}</c> instead of at the hook, because that is where the author fixes it — the same
/// template can be referenced from several entities and is validated against each of their schemas.
/// </para>
/// </remarks>
public class AfterHookCompilerTests
{
    [Fact]
    public void An_after_hook_condition_compiles_in_the_condition_profile_so_changed_is_legal()
    {
        var hooks = Compile(AfterUpdate(Webhook(), condition: "changed(stage) && new.stage == 'won'"));

        hooks.AfterUpdate.ShouldHaveSingleItem().Condition.ShouldNotBeNull()
            .Source.ShouldBe("changed(stage) && new.stage == 'won'");
    }

    /// <summary>
    /// The security core's fail-fast rule, for hooks: a condition naming a column the entity does not declare
    /// is refused when the descriptor is applied, never when a row moves at 3am.
    /// </summary>
    [Fact]
    public void An_after_hook_condition_naming_an_undeclared_column_fails_at_save_not_at_request_time()
        => CompileErrors(AfterUpdate(Webhook(), condition: "new.stagee == 'won'"))
            .ShouldHaveSingleItem().Path.ShouldBe("/entities/deals/hooks/afterUpdate/0/condition");

    [Fact]
    public void An_after_hook_with_no_condition_compiles_to_a_null_condition_and_always_fires()
        => Compile(AfterUpdate(Webhook())).AfterUpdate.ShouldHaveSingleItem().Condition.ShouldBeNull();

    /// <summary>
    /// All three points are compiled, and each lands on its own list — so an <c>afterDelete</c> hook cannot be
    /// silently filed under <c>afterCreate</c> and run on the wrong operation.
    /// </summary>
    [Theory]
    [InlineData("afterCreate")]
    [InlineData("afterUpdate")]
    [InlineData("afterDelete")]
    public void Every_after_point_the_schema_declares_is_compiled_onto_its_own_list(string point)
    {
        var hooks = Compile(At(point, Webhook()));

        Point(hooks, point).ShouldHaveSingleItem().Path.ShouldBe($"/entities/deals/hooks/{point}/0");
        OtherPoints(hooks, point).ShouldAllBe(other => other.Count == 0);
    }

    /// <summary>
    /// The operation an event carries selects the point, so the dispatcher never has to know the descriptor's
    /// spelling of a hook point.
    /// </summary>
    [Theory]
    [InlineData("afterCreate", DataOperation.Create)]
    [InlineData("afterUpdate", DataOperation.Update)]
    [InlineData("afterDelete", DataOperation.Delete)]
    public void The_point_an_operation_selects_is_the_one_named_after_it(string point, DataOperation operation)
        => Compile(At(point, Webhook())).For(operation)
            .ShouldHaveSingleItem().Path.ShouldBe($"/entities/deals/hooks/{point}/0");

    /// <summary>
    /// A read operation subscribes to nothing: the schema declares no <c>afterList</c>/<c>afterGet</c> point,
    /// so the lookup answers "no hooks" rather than throwing on an operation nobody emits an event for.
    /// </summary>
    [Theory]
    [InlineData(DataOperation.List)]
    [InlineData(DataOperation.Get)]
    public void An_operation_with_no_after_point_selects_no_hook(DataOperation operation)
        => Compile(At("afterUpdate", Webhook())).For(operation).ShouldBeEmpty();

    /// <summary>
    /// R11: hooks join the <see cref="PolicyCatalog"/>'s priming, not a fourth priming site.
    /// </summary>
    /// <remarks>
    /// Two independently primed holders means a hook could be compiled against a different schema revision
    /// than the rules judging the same write. Asserted structurally, because the failure is invisible at run
    /// time until the revisions differ.
    /// </remarks>
    [Fact]
    public void The_after_hook_catalog_is_reachable_from_the_one_primed_policy_catalog()
    {
        var catalog = PolicyCatalog.Build(Descriptor(AfterUpdate(Webhook())), Schema, CelFixtures.Compiler);

        catalog.TryGetEntity("deals", out var policy).ShouldBeTrue();
        policy.AfterHooks.AfterUpdate.ShouldNotBeEmpty();
    }

    /// <summary>
    /// The other half of R11, and the half a behavioural fact cannot see: there is exactly <b>one</b> caller
    /// of the hook compiler in shipped code, and it is the policy catalog's builder.
    /// </summary>
    /// <remarks>
    /// A second call site is how a fourth priming site arrives — not as a design decision anyone writes down,
    /// but as one convenient extra <c>Compile</c> call on a path that already has a descriptor. Nothing about
    /// the behaviour of either caller would look wrong, which is why this is a source fact.
    /// </remarks>
    [Fact]
    public void The_hook_compiler_is_reached_from_the_policy_catalog_builder_and_nowhere_else()
        => ShippedSources.FileNamesMentioning(nameof(AfterHookCompiler)).ShouldBe(
            ["AfterHookCompiler.cs", "PolicyCatalogBuilder.cs"],
            ignoreOrder: true,
            "the hook compiler has one caller, and it is the pass that primes the policy catalog — a second "
            + "caller is a second priming site, which is R11's failure");

    [Fact]
    public void An_entity_declaring_no_hooks_carries_the_empty_catalog_rather_than_null()
        => Compile(hooks: null).ShouldBeSameAs(
            EntityAfterHooks.None,
            "the shared empty catalog, so no consumer has to null-check and no allocation happens per entity");

    /// <summary>
    /// An entity declaring only <c>before*</c> hooks — everything PR5a still refuses — carries the empty
    /// catalog too, rather than a hook block that exists and holds nothing.
    /// </summary>
    [Fact]
    public void An_entity_declaring_only_before_hooks_carries_the_empty_catalog()
        => Compile(new EntityHooks { BeforeUpdate = [] }).ShouldBeSameAs(EntityAfterHooks.None);

    /// <summary>
    /// Every template in an action is parsed and validated once, at apply — so no placeholder is ever resolved
    /// for the first time on the dispatch path, where a refusal would be a delivery failure instead of an
    /// authoring error.
    /// </summary>
    [Fact]
    public void Every_template_in_an_action_is_parsed_at_apply_and_carried_compiled()
    {
        var hook = Compile(AfterUpdate(Email(to: "{{new.owner_email}}"))).AfterUpdate.ShouldHaveSingleItem();

        hook.Action.Templates.Keys.ShouldBe(["to", "subject", "body"], ignoreOrder: true);
        hook.Action.Templates["to"].Placeholders.ShouldBe(["new.owner_email"]);
    }

    [Fact]
    public void A_template_in_an_action_naming_an_undeclared_field_is_refused_at_apply()
        => CompileErrors(AfterUpdate(Email(to: "{{new.owner_emial}}")))
            .ShouldHaveSingleItem().Path.ShouldBe("/entities/deals/hooks/afterUpdate/0/action/to");

    /// <summary>
    /// A subject or body refusal points at the template, not at the hook — that is where the author edits it,
    /// and one template can be referenced from several entities.
    /// </summary>
    [Fact]
    public void A_placeholder_in_the_referenced_templates_body_is_refused_on_the_templates_own_pointer()
        => CompileErrors(AfterUpdate(Email()), body: "Deal: {{new.titel}}")
            .ShouldHaveSingleItem().Path.ShouldBe("/templates/deal-won/body");

    /// <summary>
    /// <c>email.to</c> is a plain-string sugar slot, so a placeholder-free literal address is legitimate and
    /// applies — the asymmetry with <see cref="JsonataSlot"/> that the schema's own typing creates.
    /// </summary>
    [Fact]
    public void A_literal_recipient_address_needs_no_placeholder_at_all()
        => Compile(AfterUpdate(Email(to: "ops@firma.sk"))).AfterUpdate.ShouldHaveSingleItem()
            .Action.Templates["to"].Placeholders.ShouldBeEmpty();

    /// <summary>
    /// A malformed placeholder in a sugar slot is a <b>structured</b> refusal, not the
    /// <see cref="ArgumentException"/> the parser throws.
    /// </summary>
    /// <remarks>
    /// The classifier catches this for a <c>$defs/jsonata</c> slot, because an unbalanced brace is not a
    /// well-formed template. A sugar slot asks the classifier nothing, so this is the one route by which a
    /// malformed template reaches the parser — and an unhandled <c>ArgumentException</c> at apply is an
    /// authoring mistake reported as a framework crash.
    /// </remarks>
    [Fact]
    public void A_malformed_placeholder_in_a_sugar_slot_is_a_structured_refusal_not_a_thrown_exception()
    {
        var error = CompileErrors(AfterUpdate(Email(to: "{{new.owner_email}"))).ShouldHaveSingleItem();

        error.Path.ShouldBe("/entities/deals/hooks/afterUpdate/0/action/to");
        error.Message.ShouldContain("well-formed");
    }

    [Fact]
    public void An_email_action_naming_an_undeclared_template_is_refused_at_apply()
    {
        var error = CompileErrors(AfterUpdate(Email(template: "deal-wonn"))).ShouldHaveSingleItem();

        error.Path.ShouldBe("/entities/deals/hooks/afterUpdate/0/action/template");
        error.FixSuggestion.ShouldNotBeNull().ShouldContain("deal-won");
    }

    [Fact]
    public void A_webhook_action_naming_an_undeclared_endpoint_is_refused_at_apply()
    {
        var error = CompileErrors(AfterUpdate(Webhook(endpoint: "crm-syncc"))).ShouldHaveSingleItem();

        error.Path.ShouldBe("/entities/deals/hooks/afterUpdate/0/action/endpoint");
        error.FixSuggestion.ShouldNotBeNull().ShouldContain("crm-sync");
    }

    /// <summary>
    /// The two caller references an event envelope cannot answer are refused <b>by name</b> in an after-hook
    /// condition, exactly as a template refuses them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The positive form and the negated one are both asserted, because they failed in opposite directions
    /// and only one of them looked like a denial.</b> <c>@tenant.id</c> resolved to <see langword="null"/> for
    /// the dispatcher, and the interpreter's null rule collapses every comparison — <c>!=</c> included — to
    /// <see langword="false"/>, so <c>!(@tenant.id == 'x')</c> was <b>true</b>: a hook reading "every tenant
    /// except ours" delivered every tenant's unmasked row to an external endpoint. <c>@user.roles</c> resolved to
    /// a value and was worse for it: the dispatcher's own <c>admin</c> role, so <c>'admin' in @user.roles</c> was
    /// true for every event whoever wrote the row.
    /// </para>
    /// <para>
    /// A refusal at apply is the fix rather than a documented caveat, and it is the call issue #153 already made
    /// for the template half. <c>@user.id</c> is deliberately <em>not</em> refused: the envelope carries
    /// <c>authid</c>, so it can be answered — resolve what you can answer, refuse what you cannot.
    /// </para>
    /// <para>
    /// <b>The <c>@tenant.id</c> cases compare against a <em>column</em> and not a string literal, because a
    /// literal is unreachable:</b> <c>@tenant.id</c> is typed <c>Uuid</c>, so <c>@tenant.id == 'acme'</c> is
    /// already refused by the CEL type checker as <c>Cannot compare String to Uuid</c> and never reaches this
    /// refusal at all. The reachable — and realistic — shape is a row's own tenant column against the caller's,
    /// which is exactly how a rule writes it.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("new.tenant_id == @tenant.id", "@tenant.id")]
    [InlineData("!(new.tenant_id == @tenant.id)", "@tenant.id")]
    [InlineData("'admin' in @user.roles", "@user.roles")]
    [InlineData("!('admin' in @user.roles)", "@user.roles")]
    public void A_condition_naming_provenance_the_envelope_lacks_is_refused_at_apply(string condition, string name)
    {
        var error = CompileErrors(AfterUpdate(Webhook(), condition)).ShouldHaveSingleItem();

        error.Path.ShouldBe("/entities/deals/hooks/afterUpdate/0/condition");
        error.Message.ShouldContain(name);
        error.FixSuggestion.ShouldNotBeNull().ShouldNotBeEmpty();
    }

    /// <summary>
    /// The non-vacuity control for the refusal above: <c>@user.id</c> <b>is</b> answerable from the envelope's
    /// <c>authid</c>, so a condition reading it compiles and is carried with its actor requirement recorded.
    /// </summary>
    /// <remarks>
    /// Without this, "provenance is refused" would also hold for a build that refused every <c>@</c> reference in
    /// a condition — which would delete the most useful after-hook condition there is.
    /// </remarks>
    [Fact]
    public void A_condition_reading_user_id_compiles_and_records_that_it_needs_an_actor()
    {
        var hook = Compile(AfterUpdate(Webhook(), condition: "new.owner_id != @user.id"))
            .AfterUpdate.ShouldHaveSingleItem();

        hook.Condition.ShouldNotBeNull();
        hook.Required.UserId.ShouldBeTrue();
        hook.Required.TenantId.ShouldBeFalse();
    }

    /// <summary>
    /// A condition reading no caller value at all records no requirement, so nothing gates it.
    /// </summary>
    [Fact]
    public void A_condition_reading_no_caller_value_records_no_requirement()
        => Compile(AfterUpdate(Webhook(), condition: "changed(stage)"))
            .AfterUpdate.ShouldHaveSingleItem().Required.ShouldBe(RequiredContext.None);

    /// <summary>
    /// A webhook endpoint's URL is parsed and its scheme checked at <b>apply</b>, not at delivery.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The schema's <c>"format": "uri"</c> is an annotation and asserts nothing, so before this the only check
    /// was <c>new Uri(...)</c> inside the delivery — a <c>UriFormatException</c> per attempt, retried to the
    /// ceiling and abandoned, which an author reads as an endpoint outage rather than as the typo it is. It is
    /// also the endpoint mistake an author is most likely to make.
    /// </para>
    /// <para>
    /// Cleartext is refused for a different reason: the body is the record's complete unmasked image, the
    /// delivery is unsigned, and the slot's own description says <em>HTTPS target</em> — so an on-path observer,
    /// who is nobody's author, would read what decision D7 bounded to a declared endpoint.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("/hooks/crm")]
    [InlineData("example.test/hook")]
    [InlineData("http://example.test/hook")]
    public void An_endpoint_url_that_could_never_deliver_is_refused_at_apply(string url)
        => CompileErrors(AfterUpdate(Webhook()), endpointUrl: url)
            .ShouldHaveSingleItem().Path.ShouldBe("/webhooks/endpoints/crm-sync/url");

    /// <summary>
    /// The one cleartext carve-out, and the control that keeps the refusal above about the <em>scheme</em>
    /// rather than about the string <c>http</c>: a loopback host has no network to observe, and
    /// <c>http://127.0.0.1:port/hook</c> is the shape a local receiver — including this repository's own
    /// end-to-end suites — uses.
    /// </summary>
    [Theory]
    [InlineData("http://localhost:5000/hook")]
    [InlineData("http://127.0.0.1:5000/hook")]
    [InlineData("https://example.test/hook")]
    public void A_deliverable_endpoint_url_is_carried_as_a_resolved_target(string url)
    {
        var endpoint = Compile(AfterUpdate(Webhook()), endpointUrl: url)
            .AfterUpdate.ShouldHaveSingleItem().Action.Endpoint.ShouldNotBeNull();

        endpoint.Name.ShouldBe("crm-sync");
        endpoint.Url.ShouldBe(new Uri(url));
    }

    /// <summary>
    /// A refused hook leaves no compiled hook behind, so a catalog is never built holding a half-compiled
    /// action — the same all-or-nothing the rule slots keep.
    /// </summary>
    [Fact]
    public void A_refused_hook_is_not_carried_as_a_compiled_one()
    {
        PolicyCatalog.TryBuild(
            Descriptor(AfterUpdate(Webhook(endpoint: "nope"))), Schema, CelFixtures.Compiler,
            out var catalog, out var errors).ShouldBeFalse();

        catalog.ShouldBeNull();
        errors.ShouldNotBeEmpty();
    }

    /// <summary>
    /// Every problem in one hook block is reported together, so an agent fixing a descriptor sees the whole
    /// list in one round trip rather than one refusal per apply.
    /// </summary>
    [Fact]
    public void Two_problems_in_one_hook_list_are_both_reported()
        => CompileErrors(
                new EntityHooks
                {
                    AfterUpdate =
                    [
                        new AfterHook { Condition = "new.stagee == 'won'", Action = Webhook() },
                        new AfterHook { Action = Webhook(endpoint: "nope") },
                    ],
                })
            .Select(error => error.Path)
            .ShouldBe(
                ["/entities/deals/hooks/afterUpdate/0/condition", "/entities/deals/hooks/afterUpdate/1/action/endpoint"],
                ignoreOrder: true);

    private static EntityAfterHooks Compile(
        EntityHooks? hooks, string? body = null, string endpointUrl = DeclaredEndpointUrl)
    {
        PolicyCatalog.TryBuild(
            Descriptor(hooks, body, endpointUrl), Schema, CelFixtures.Compiler, out var catalog, out var errors)
            .ShouldBeTrue($"expected a clean build, got: {string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}"))}");

        catalog.ShouldNotBeNull().TryGetEntity("deals", out var policy).ShouldBeTrue();
        return policy.AfterHooks;
    }

    private static IReadOnlyList<DescriptorValidationError> CompileErrors(
        EntityHooks hooks, string? body = null, string endpointUrl = DeclaredEndpointUrl)
    {
        PolicyCatalog.TryBuild(
            Descriptor(hooks, body, endpointUrl), Schema, CelFixtures.Compiler, out _, out var errors)
            .ShouldBeFalse("this fixture is written to be refused");

        return errors;
    }

    private const string DeclaredEndpointUrl = "https://example.test/hook";

    private static EntityHooks AfterUpdate(AutomationAction action, string? condition = null) =>
        At("afterUpdate", action, condition);

    private static EntityHooks At(string point, AutomationAction action, string? condition = null)
    {
        IReadOnlyList<AfterHook> hooks = [new AfterHook { Condition = condition, Action = action }];

        return point switch
        {
            "afterCreate" => new EntityHooks { AfterCreate = hooks },
            "afterUpdate" => new EntityHooks { AfterUpdate = hooks },
            "afterDelete" => new EntityHooks { AfterDelete = hooks },
            _ => throw new ArgumentOutOfRangeException(nameof(point), point, "Not an after-hook point."),
        };
    }

    private static IReadOnlyList<CompiledAfterHook> Point(EntityAfterHooks hooks, string point) => point switch
    {
        "afterCreate" => hooks.AfterCreate,
        "afterUpdate" => hooks.AfterUpdate,
        "afterDelete" => hooks.AfterDelete,
        _ => throw new ArgumentOutOfRangeException(nameof(point), point, "Not an after-hook point."),
    };

    private static readonly string[] _afterPoints = ["afterCreate", "afterUpdate", "afterDelete"];

    private static IEnumerable<IReadOnlyList<CompiledAfterHook>> OtherPoints(EntityAfterHooks hooks, string point) =>
        _afterPoints
            .Where(candidate => !string.Equals(candidate, point, StringComparison.Ordinal))
            .Select(candidate => Point(hooks, candidate));

    private static WebhookAction Webhook(string endpoint = "crm-sync", string? payload = null) =>
        new() { Endpoint = endpoint, Payload = payload };

    private static EmailAction Email(
        string template = "deal-won", string to = "ops@firma.sk", string? data = null) =>
        new() { Template = template, To = to, Data = data };

    private static AlvoDescriptor Descriptor(
        EntityHooks? hooks, string? body = null, string endpointUrl = DeclaredEndpointUrl) => new()
        {
            ApiVersion = "alvo.dev/v1",
            Name = "test",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
            {
                ["deals"] = new()
                {
                    Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal),
                    Hooks = hooks,
                },
            },
            Templates = new Dictionary<string, MessageTemplate>(StringComparer.Ordinal)
            {
                ["deal-won"] = new() { Subject = "Deal won: {{new.title}}", Body = body ?? "{{new.title}} closed." },
            },
            Webhooks = new Webhooks
            {
                Endpoints = new Dictionary<string, WebhookEndpoint>(StringComparer.Ordinal)
                {
                    ["crm-sync"] = new() { Url = endpointUrl, SecretRef = "crm-sync-secret" },
                },
            },
        };

    private static SchemaModel Schema { get; } = new([
        new EntitySchema
        {
            Name = "deals",
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = "id", Type = FieldType.Uuid },
                new FieldSchema { Name = "title", Type = FieldType.String, MaxLength = 200 },
                new FieldSchema { Name = "stage", Type = FieldType.Enum, EnumValues = ["lead", "won", "lost"] },
                new FieldSchema { Name = "owner_email", Type = FieldType.String, MaxLength = 200 },
                new FieldSchema { Name = "owner_id", Type = FieldType.Uuid },
                new FieldSchema { Name = "tenant_id", Type = FieldType.Uuid },
            ],
        },
    ]);
}
