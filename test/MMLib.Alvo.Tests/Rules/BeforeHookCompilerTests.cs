using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Expressions;
using System.Text.Json;

using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Rules;

/// <summary>
/// Before-hooks compiled into the <see cref="PolicyCatalog"/> and run through
/// <see cref="IBeforeHookRunner"/> — the <c>reject</c>/<c>mutate</c> pair the frozen schema allows
/// in-transaction, resolved at <b>apply</b> and evaluated with nothing left to resolve.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every fact goes through <see cref="PolicyCatalog.TryBuild"/> and the real runner</b>, for the reason
/// <c>AfterHookCompilerTests</c> states: the compiled hooks ride the one primed catalog, so a fact driven
/// through the compiler directly would prove the compiler works and say nothing about R11. The runner here is
/// the product's own, over the product's own <c>PolicyCatalogProvider</c>.
/// </para>
/// <para>
/// <b>What this file deliberately does not measure is the transaction.</b> "The refusal leaves no row" and
/// "the mutated value is the one stored" are facts about a write, and they live in the shared contract suite
/// (<c>MMLib.Alvo.Testing.Data.AlvoDataBeforeHookTests</c>) so both shipped engines answer them. Here the
/// subject is what compiles, what is refused at apply, and what the runner returns.
/// </para>
/// </remarks>
public class BeforeHookCompilerTests
{
    [Fact]
    public void A_reject_whose_condition_holds_refuses_the_write()
    {
        var refusal = Should.Throw<AlvoAuthorizationException>(
            () => Run(BeforeCreate(Reject("Deals must carry a title."), condition: "!has(new.title)"), Untitled));

        refusal.Message.ShouldContain("Deals must carry a title.");
        refusal.Message.ShouldContain(
            "/entities/deals/hooks/beforeCreate/0",
            Case.Sensitive,
            "the hook's pointer is descriptor-authored, so naming it tells an author which hook refused");
    }

    [Fact]
    public void A_reject_whose_condition_is_false_lets_the_write_through()
        => Run(BeforeCreate(Reject("Deals must carry a title."), condition: "!has(new.title)"), Titled)
            .ShouldBeEmpty();

    [Fact]
    public void A_reject_with_no_condition_at_all_refuses_every_write()
        => Should.Throw<AlvoAuthorizationException>(
            () => Run(BeforeCreate(Reject("This entity is frozen.")), Titled));

    /// <summary>
    /// The whole point of the <c>Mutate</c> profile: a value expression over the candidate row, evaluated
    /// against the row the write is about to produce.
    /// </summary>
    [Fact]
    public void A_mutate_expression_is_evaluated_against_the_candidate_row()
        => Run(BeforeCreate(Mutate("title", Cel("lowerAscii(new.title)"))), Titled)
            .ShouldContainKeyAndValue("title", "big deal");

    [Fact]
    public void A_mutate_can_read_now_and_gets_the_writes_own_bound_instant()
        => Run(BeforeCreate(Mutate("approved_at", Cel("now()"))), Titled)
            .ShouldContainKeyAndValue("approved_at", Stamp);

    /// <summary>
    /// A literal is converted when the descriptor is applied, so the runner assigns a value the field's own
    /// type already holds rather than a <see cref="JsonElement"/> a driver would have to interpret.
    /// </summary>
    [Theory]
    [InlineData("title", "\"fixed\"", "fixed")]
    [InlineData("total", "12", 12L)]
    [InlineData("is_public", "true", true)]
    public void A_literal_mutate_is_converted_at_apply_and_assigned_as_that_value(
        string field, string json, object expected)
        => Run(BeforeCreate(Mutate(field, Literal(json))), Titled).ShouldContainKeyAndValue(field, expected);

    [Fact]
    public void A_null_literal_mutate_on_an_optional_field_stores_nothing_rather_than_being_dropped()
        => Run(BeforeCreate(Mutate("approved_at", Literal("null"))), Titled)
            .ShouldContainKeyAndValue("approved_at", null);

    /// <summary>
    /// The hook list is a pipeline: the second hook's condition sees the first hook's patch, because that is
    /// the order an author reads the array in.
    /// </summary>
    [Fact]
    public void A_later_hook_sees_what_an_earlier_hook_mutated()
    {
        var hooks = new EntityHooks
        {
            BeforeCreate =
            [
                new BeforeHook { Action = Mutate("status", Literal("\"approved\"")) },
                new BeforeHook { Condition = "new.status == 'approved'", Action = Mutate("is_public", Literal("true")) },
            ],
        };

        Run(hooks, Titled).ShouldContainKeyAndValue("is_public", true);
    }

    /// <summary>
    /// Inside <b>one</b> hook the mutations are simultaneous, so the stored row does not depend on the order a
    /// JSON object's members happen to be enumerated in — which neither JSON nor .NET promises.
    /// </summary>
    [Fact]
    public void Two_mutations_in_one_hook_are_evaluated_against_the_same_candidate()
    {
        var mutate = new BeforeHookAction
        {
            Mutate = new Dictionary<string, ValueOrExpr>(StringComparer.Ordinal)
            {
                ["status"] = Literal("\"approved\""),
                ["title"] = Cel("lowerAscii(new.status)"),
            },
        };

        Run(new EntityHooks { BeforeCreate = [new BeforeHook { Action = mutate }] }, Titled)
            .ShouldContainKeyAndValue("title", "draft", "the sibling mutation's value is not visible here");
    }

    [Fact]
    public void An_entity_declaring_no_before_hook_carries_the_empty_catalog_rather_than_null()
        => Compile(hooks: null).ShouldBeSameAs(
            EntityBeforeHooks.None,
            "the shared empty catalog, so no consumer has to null-check and no allocation happens per entity");

    /// <summary>
    /// All three points are compiled onto their own list, so a <c>beforeDelete</c> hook cannot be silently
    /// filed under <c>beforeCreate</c> and refuse the wrong operation.
    /// </summary>
    [Theory]
    [InlineData("beforeCreate", DataOperation.Create)]
    [InlineData("beforeUpdate", DataOperation.Update)]
    [InlineData("beforeDelete", DataOperation.Delete)]
    public void The_point_an_operation_selects_is_the_one_named_after_it(string point, DataOperation operation)
        => Compile(At(point, Reject("no"))).For(operation)
            .ShouldHaveSingleItem().Path.ShouldBe($"/entities/deals/hooks/{point}/0");

    /// <summary>
    /// A read selects no hook: the frozen schema declares no <c>beforeGet</c>/<c>beforeList</c> point, so the
    /// lookup answers "none" rather than throwing on an operation this subsystem is not about.
    /// </summary>
    [Theory]
    [InlineData(DataOperation.List)]
    [InlineData(DataOperation.Get)]
    public void An_operation_with_no_before_point_selects_no_hook(DataOperation operation)
        => Compile(At("beforeUpdate", Reject("no"))).For(operation).ShouldBeEmpty();

    /// <summary>
    /// R11's structural half, and the half a behavioural fact cannot see: the before-hook compiler has exactly
    /// one caller in shipped code, and it is the pass that primes the policy catalog.
    /// </summary>
    [Fact]
    public void The_before_hook_compiler_is_reached_from_the_policy_catalog_builder_and_nowhere_else()
        => ShippedSources.FileNamesMentioning(nameof(BeforeHookCompiler)).ShouldBe(
            ["BeforeHookCompiler.cs", "PolicyCatalogBuilder.cs"],
            ignoreOrder: true,
            "a second caller is a second priming site, which is R11's failure");

    /// <summary>
    /// The security core's fail-fast rule: an author's mistake is refused when the descriptor is written, never
    /// from inside a transaction where there is nobody to report it to and the row's locks are held.
    /// </summary>
    /// <param name="hooks">The hook block the fixture is refused for.</param>
    /// <param name="slot">The JSON pointer the refusal must land on — the slot the author edits.</param>
    [Theory]
    [MemberData(nameof(RefusedAtApply))]
    public void A_hook_an_author_got_wrong_is_refused_at_apply_on_the_slot_they_edit(
        EntityHooks hooks, string slot)
    {
        var error = CompileErrors(hooks).ShouldHaveSingleItem();

        error.Path.ShouldBe(slot);
        error.FixSuggestion.ShouldNotBeNull().ShouldNotBeEmpty("a refusal with no alternative sends an agent hunting");
    }

    public static TheoryData<EntityHooks, string> RefusedAtApply() => new()
    {
        // A condition naming a column the entity does not declare — the sibling of the rule/after-hook case.
        { BeforeCreate(Reject("no"), condition: "new.titel == 'x'"), "/entities/deals/hooks/beforeCreate/0/condition" },

        // A mutate naming a field that does not exist: the plan's own named fail-fast case.
        { BeforeCreate(Mutate("titel", Literal("\"x\""))), "/entities/deals/hooks/beforeCreate/0/action/mutate/titel" },

        // An unresolvable CEL reference inside the mutate value, as opposed to in the condition.
        { BeforeCreate(Mutate("title", Cel("lowerAscii(new.titel)"))), "/entities/deals/hooks/beforeCreate/0/action/mutate/title" },

        // A framework-managed column: the tenancy guard, refused for the whole managed set. Both values are
        // ones the Mutate profile accepts — a literal uuid and now() — so the guard is the only thing that can
        // be refusing them. A '@tenant.id' value would have been refused by the profile table anyway (context
        // references are not admitted to Mutate yet), and this row would then have passed with no guard at all.
        { BeforeCreate(Mutate("tenant_id", Literal(OtherTenant))), "/entities/deals/hooks/beforeCreate/0/action/mutate/tenant_id" },
        { BeforeCreate(Mutate("created_at", Cel("now()"))), "/entities/deals/hooks/beforeCreate/0/action/mutate/created_at" },

        // A value type the target field cannot hold.
        { BeforeCreate(Mutate("total", Cel("lowerAscii(new.title)"))), "/entities/deals/hooks/beforeCreate/0/action/mutate/total" },

        // A literal of a shape the target field cannot hold.
        { BeforeCreate(Mutate("total", Literal("\"soon\""))), "/entities/deals/hooks/beforeCreate/0/action/mutate/total" },

        // A null literal into a required field: an INSERT of NULL into a NOT NULL column.
        { BeforeCreate(Mutate("title", Literal("null"))), "/entities/deals/hooks/beforeCreate/0/action/mutate/title" },

        // A tree past the compiler's depth cap — the bound that makes a before-hook's run time finite.
        { BeforeCreate(Mutate("title", Cel(DeepExpression))), "/entities/deals/hooks/beforeCreate/0/action/mutate/title" },
    };

    /// <summary>
    /// A create has no row before it, so <c>old.</c> and <c>changed(...)</c> are references the phase cannot
    /// answer — and an unanswerable reference collapses every comparison against it, including <c>!=</c>, so a
    /// condition reading "every deal except the won ones" fires for every deal.
    /// </summary>
    [Theory]
    [InlineData("old.title == 'x'")]
    [InlineData("!(old.title == 'x')")]
    [InlineData("changed(title)")]
    public void A_before_create_expression_reading_the_row_that_does_not_exist_yet_is_refused(string condition)
        => CompileErrors(BeforeCreate(Reject("no"), condition)).ShouldHaveSingleItem()
            .Path.ShouldBe("/entities/deals/hooks/beforeCreate/0/condition");

    /// <summary>The mirror image: a delete produces no row, so <c>new.</c> and <c>changed(...)</c> cannot be answered.</summary>
    [Theory]
    [InlineData("new.title == 'x'")]
    [InlineData("!(new.title == 'x')")]
    [InlineData("changed(title)")]
    public void A_before_delete_expression_reading_the_row_that_will_not_exist_is_refused(string condition)
        => CompileErrors(At("beforeDelete", Reject("no"), condition)).ShouldHaveSingleItem()
            .Path.ShouldBe("/entities/deals/hooks/beforeDelete/0/condition");

    /// <summary>
    /// <c>old.</c>/<c>changed(...)</c> are exactly what a <c>beforeUpdate</c> is for, so the phase check must
    /// not have refused the legitimate case along with the two impossible ones.
    /// </summary>
    [Fact]
    public void A_before_update_condition_may_read_both_images_and_call_changed()
        => Compile(At("beforeUpdate", Reject("no"), "changed(status) && old.status == 'draft'"))
            .BeforeUpdate.ShouldHaveSingleItem().Condition.ShouldNotBeNull();

    /// <summary>
    /// A <c>mutate</c> under <c>beforeDelete</c> is refused rather than compiled and discarded — the dead-slot
    /// failure mode, which looks like a maintained field and is not one.
    /// </summary>
    [Fact]
    public void A_mutate_on_a_delete_is_refused_because_nothing_would_write_it()
    {
        var error = CompileErrors(At("beforeDelete", Mutate("title", Literal("\"x\"")))).ShouldHaveSingleItem();

        error.Path.ShouldBe("/entities/deals/hooks/beforeDelete/0/action/mutate");
        error.Message.ShouldContain("read by nothing");
    }

    /// <summary>
    /// <c>reject</c> and <c>mutate</c> are alternatives in the schema's own <c>oneOf</c>. A hook carrying both
    /// or neither is refused, because otherwise which one ran would depend on the order a consumer read them.
    /// </summary>
    [Theory]
    [MemberData(nameof(MalformedActions))]
    public void A_hook_action_that_is_not_exactly_one_of_the_two_is_refused(BeforeHookAction action)
        => CompileErrors(new EntityHooks { BeforeCreate = [new BeforeHook { Action = action }] })
            .ShouldHaveSingleItem().Path.ShouldBe("/entities/deals/hooks/beforeCreate/0/action");

    public static TheoryData<BeforeHookAction> MalformedActions() => new()
    {
        new BeforeHookAction
        {
            Reject = "no",
            Mutate = new Dictionary<string, ValueOrExpr>(StringComparer.Ordinal) { ["title"] = Literal("\"x\"") },
        },
        new BeforeHookAction(),
    };

    /// <summary>
    /// A hook may patch a field the caller may not write, which is the ruling the DoD asks for in its
    /// compile-time half: <c>readOnly</c> is a <em>caller</em> mask, and a hook is not the caller. The write
    /// path's half — that <c>WritePayloadGuard</c> does not re-run over a patched payload — is in the shared
    /// contract suite, because only a real write can show it.
    /// </summary>
    [Fact]
    public void A_mutate_may_patch_a_field_a_caller_is_forbidden_to_write()
        => Run(BeforeCreate(Mutate("status", Literal("\"approved\""))), Titled, ReadOnlyStatus)
            .ShouldContainKeyAndValue("status", "approved");

    private static DateTimeOffset Stamp { get; } = new(2026, 8, 4, 9, 30, 0, TimeSpan.Zero);

    /// <summary>A tenant that is not the caller's — the value a hook rewriting <c>tenant_id</c> would store.</summary>
    private const string OtherTenant = "\"22222222-0000-0000-0000-000000000002\"";

    /// <summary>
    /// An expression past <c>CelCompiler.MaxTreeDepth</c>. Written as a <c>+</c> chain because a flat source
    /// still builds a tree whose depth grows with its term count, which is the case the cap exists for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The terms are <c>1</c> and not <c>new.title</c>, and that is the whole fact.</b> Two hundred
    /// <c>new.title</c> terms is 2397 characters, which is past the <em>source-length</em> cap of 2000 —
    /// and that cap is checked before the source is parsed. So the row that claimed to exercise the depth
    /// cap was refused for its length, never reached a tree at all, and passed anyway, because the theory
    /// it feeds asserts the JSON pointer an author edits and every refusal in that member lands on the
    /// same pointer. Two hundred <c>1</c> terms is 797 characters and two hundred levels deep, so the
    /// depth cap is what answers.
    /// </para>
    /// <para>
    /// Binary arithmetic is not admitted to the <see cref="CelProfile.Mutate"/> profile either, so the
    /// source still has more than one possible refusal. The order in <c>CelCompiler.Compile</c> settles
    /// which one answers: the length check, then parse, then the depth check, then the profile and type
    /// check — so the cap fires and the profile never sees the tree.
    /// <see cref="The_deep_expression_is_refused_for_its_depth_and_not_for_its_arithmetic"/> holds all of
    /// that to its word by asserting the message.
    /// </para>
    /// </remarks>
    private static string DeepExpression { get; } = string.Join(" + ", Enumerable.Repeat("1", 200));

    private const string Deals = "deals";

    private static readonly AlvoContext _caller = new()
    {
        User = new UserId(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001")),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = new TenantId(Guid.Parse("11111111-0000-0000-0000-000000000001")),
    };

    private static AlvoRecord Titled { get; } = new(new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["title"] = "BIG Deal",
        ["status"] = "draft",
    });

    private static AlvoRecord Untitled { get; } =
        new(new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = "draft" });

    /// <summary>
    /// Runs <paramref name="hooks"/> over <paramref name="candidate"/> through the product's own runner and
    /// the product's own catalog provider, and answers with the patch a driver would apply.
    /// </summary>
    /// <param name="hooks">The hook block the descriptor declares.</param>
    /// <param name="candidate">The candidate row the write would produce.</param>
    /// <param name="fields">The entity's field descriptors, for the one fact that needs a <c>readOnly</c> flag.</param>
    private static IReadOnlyDictionary<string, object?> Run(
        EntityHooks hooks,
        AlvoRecord candidate,
        IReadOnlyDictionary<string, FieldDescriptor>? fields = null)
    {
        var provider = new PolicyCatalogProvider();
        provider.SetCurrent(Deals, PolicyCatalog.Build(Descriptor(hooks, fields), Schema, CelFixtures.Compiler));

        return new BeforeHookRunner(provider)
            .Run(Deals, DataOperation.Create, candidate, previous: null, _caller, Stamp);
    }

    private static EntityBeforeHooks Compile(EntityHooks? hooks)
    {
        PolicyCatalog.Build(Descriptor(hooks), Schema, CelFixtures.Compiler)
            .TryGetEntity(Deals, out var policy).ShouldBeTrue();

        return policy.BeforeHooks;
    }

    /// <summary>
    /// The depth-cap row of <see cref="RefusedAtApply"/> is refused <b>for its depth</b> — asserted here
    /// because that row, like every other, asserts the slot an author edits, and every refusal in that member
    /// lands on the same slot.
    /// </summary>
    /// <remarks>
    /// Without this the row would go on passing after the cap was deleted: the profile table would refuse the
    /// same <c>+</c> chain for its arithmetic instead, on the same pointer, with the same shape of error. See
    /// <see cref="DeepExpression"/> for why the source has two possible refusals and which one the
    /// compiler's order gives it.
    /// </remarks>
    [Fact]
    public void The_deep_expression_is_refused_for_its_depth_and_not_for_its_arithmetic()
    {
        var error = CompileErrors(BeforeCreate(Mutate("title", Cel(DeepExpression)))).ShouldHaveSingleItem();

        error.Message.ShouldContain("levels deep");
        error.Message.ShouldContain("exceeding the maximum of");
    }

    private static IReadOnlyList<DescriptorValidationError> CompileErrors(EntityHooks hooks)
    {
        PolicyCatalog.TryBuild(Descriptor(hooks), Schema, CelFixtures.Compiler, out _, out var errors)
            .ShouldBeFalse("this fixture is written to be refused");

        return errors;
    }

    private static BeforeHookAction Reject(string text) => new() { Reject = text };

    private static BeforeHookAction Mutate(string field, ValueOrExpr value) => new()
    {
        Mutate = new Dictionary<string, ValueOrExpr>(StringComparer.Ordinal) { [field] = value },
    };

    private static ValueOrExpr Cel(string source) => ValueOrExpr.FromExpression(source);

    private static ValueOrExpr Literal(string json) =>
        ValueOrExpr.FromLiteral(JsonDocument.Parse(json).RootElement);

    private static EntityHooks BeforeCreate(BeforeHookAction action, string? condition = null) =>
        At("beforeCreate", action, condition);

    private static EntityHooks At(string point, BeforeHookAction action, string? condition = null)
    {
        IReadOnlyList<BeforeHook> hooks = [new BeforeHook { Condition = condition, Action = action }];

        return point switch
        {
            "beforeCreate" => new EntityHooks { BeforeCreate = hooks },
            "beforeUpdate" => new EntityHooks { BeforeUpdate = hooks },
            "beforeDelete" => new EntityHooks { BeforeDelete = hooks },
            _ => throw new ArgumentOutOfRangeException(nameof(point), point, "Not a before-hook point."),
        };
    }

    private static readonly Dictionary<string, FieldDescriptor> _noFields = new(StringComparer.Ordinal);

    /// <summary>The one entity's descriptor, with <c>status</c> frozen for callers.</summary>
    private static IReadOnlyDictionary<string, FieldDescriptor> ReadOnlyStatus { get; } =
        new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["status"] = new() { Type = MMLib.Alvo.Descriptor.FieldType.String, ReadOnly = BoolOrCel.FromBoolean(true) },
        };

    private static AlvoDescriptor Descriptor(
        EntityHooks? hooks, IReadOnlyDictionary<string, FieldDescriptor>? fields = null) => new()
        {
            ApiVersion = "alvo.dev/v1",
            Name = Deals,
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
            {
                [Deals] = new()
                {
                    Tenancy = EntityTenancy.Scoped,
                    Fields = fields ?? _noFields,
                    Hooks = hooks,
                },
            },
        };

    /// <summary>
    /// One tenant-scoped, audited entity: <c>tenant_id</c> and the audit columns are then framework-managed,
    /// which is what the managed-column refusals are measured against.
    /// </summary>
    private static SchemaModel Schema { get; } = new([
        new EntitySchema
        {
            Name = Deals,
            Tenancy = TenancyMode.Scoped,
            Audit = true,
            Fields =
            [
                new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                new FieldSchema { Name = "title", Type = FieldType.String, Required = true, MaxLength = 200 },
                new FieldSchema { Name = "status", Type = FieldType.String, Nullable = true },
                new FieldSchema { Name = "total", Type = FieldType.Integer, Nullable = true },
                new FieldSchema { Name = "is_public", Type = FieldType.Boolean, Nullable = true },
                new FieldSchema { Name = "approved_at", Type = FieldType.DateTime, Nullable = true },
                new FieldSchema { Name = "tenant_id", Type = FieldType.Uuid, Required = true, Indexed = true },
                new FieldSchema { Name = "created_at", Type = FieldType.DateTime, Nullable = true },
                new FieldSchema { Name = "created_by", Type = FieldType.Uuid, Nullable = true },
                new FieldSchema { Name = "updated_at", Type = FieldType.DateTime, Nullable = true },
                new FieldSchema { Name = "updated_by", Type = FieldType.Uuid, Nullable = true },
            ],
        },
    ]);
}
