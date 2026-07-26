using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Testing.Rules;

/// <summary>
/// Behavioral contract every <see cref="IPolicyEngine"/> implementation must satisfy: the
/// per-operation <c>USING</c>/<c>WITH CHECK</c> mapping, default-deny, and the tenant guard. Inherit
/// this from a concrete test class that wires <see cref="CreateEngine"/> to the engine under test,
/// so a future engine (F7's dynamic-entity path) is held to the exact same judgment as the one built
/// over <c>PolicyCatalog</c> here.
/// </summary>
public abstract class PolicyEngineContractTests
{
    private static readonly TenantId _tenant = TenantId.New();

    /// <summary>Creates the <see cref="IPolicyEngine"/> under test, built over the given descriptor and schema.</summary>
    /// <param name="descriptor">The project descriptor the engine resolves policies from.</param>
    /// <param name="schema">The schema <paramref name="descriptor"/> maps to.</param>
    /// <returns>The engine instance to exercise.</returns>
    protected abstract IPolicyEngine CreateEngine(AlvoDescriptor descriptor, SchemaModel schema);

    /// <summary>An authenticated caller acting in <see cref="_tenant"/>.</summary>
    private static AlvoContext Caller() => new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = _tenant,
    };

    /// <summary>The same caller, but with no tenant at all.</summary>
    private static AlvoContext TenantlessCaller() => Caller() with { Tenant = null };

    /// <summary>
    /// A caller carrying the reserved all-zero <see cref="UserId"/> — no identity, exactly what
    /// <see cref="AlvoContext.Anonymous"/> holds. Never a real caller who happens to own the all-zero
    /// rows: the value is reserved.
    /// </summary>
    private static AlvoContext IdentitylessCaller() => Caller() with { User = default };

    /// <summary>No <c>rules</c> block at all denies every operation — secure-by-default, not merely the ones the descriptor happens to omit.</summary>
    [Fact]
    public void An_entity_with_no_rules_block_denies_every_operation()
    {
        var engine = BuildEngine(TenancyMode.Global, rules: null);

        foreach (var operation in AllOperations())
        {
            engine.Resolve("widgets", operation, Caller()).IsDenied.ShouldBeTrue($"{operation} must deny with no rules block.");
        }
    }

    /// <summary>A <see langword="null"/> rule for the specific operation requested denies, even when other operations are configured.</summary>
    [Fact]
    public void A_null_rule_for_the_requested_operation_denies()
    {
        var engine = BuildEngine(TenancyMode.Global, new AccessRules { List = "true" });

        engine.Resolve("widgets", DataOperation.Get, Caller()).IsDenied.ShouldBeTrue();
    }

    /// <summary><c>"true"</c> allows with a real, non-null predicate — never a <see langword="null"/> one, which must not be readable downstream as "no filter".</summary>
    [Fact]
    public void A_true_rule_allows_with_a_non_null_predicate()
    {
        var engine = BuildEngine(TenancyMode.Global, new AccessRules { List = "true" });

        var decision = engine.Resolve("widgets", DataOperation.List, Caller());

        decision.IsDenied.ShouldBeFalse();
        decision.Using.ShouldNotBeNull();
    }

    /// <summary><c>list</c>/<c>get</c>/<c>delete</c> carry a <c>USING</c>-equivalent predicate only; there is no post-image to check.</summary>
    /// <param name="operationName">The <see cref="DataOperation"/> member name under test.</param>
    [Theory]
    [InlineData(nameof(DataOperation.List))]
    [InlineData(nameof(DataOperation.Get))]
    [InlineData(nameof(DataOperation.Delete))]
    public void List_get_and_delete_carry_using_only(string operationName)
    {
        var operation = Enum.Parse<DataOperation>(operationName);
        var engine = BuildEngine(TenancyMode.Global, AllTrueRules());

        var decision = engine.Resolve("widgets", operation, Caller());

        decision.Using.ShouldNotBeNull();
        decision.WithCheck.ShouldBeNull();
    }

    /// <summary><c>create</c> carries a <c>WITH CHECK</c>-equivalent predicate only; there is no stored row to filter.</summary>
    [Fact]
    public void Create_carries_with_check_only()
    {
        var engine = BuildEngine(TenancyMode.Global, AllTrueRules());

        var decision = engine.Resolve("widgets", DataOperation.Create, Caller());

        decision.WithCheck.ShouldNotBeNull();
        decision.Using.ShouldBeNull();
    }

    /// <summary>
    /// <c>update</c> carries both a <c>USING</c> and a <c>WITH CHECK</c> predicate, compiled from the
    /// same rule source — and specifically <c>rules.update</c>'s own source, not
    /// <c>rules.create</c>'s. Deliberately gives <c>create</c> and <c>update</c> two different,
    /// distinctive sources: an engine that (incorrectly) built <c>update</c>'s <c>WITH CHECK</c> from
    /// <c>rules.create</c> — exactly the cross-wiring that would let a caller push a row out of their
    /// own scope on update — would still pass this fact if both sources happened to be <c>"true"</c>.
    /// </summary>
    [Fact]
    public void Update_carries_both_using_and_with_check_from_updates_own_distinctive_source()
    {
        const string UpdateSource = "title == 'update-marker'";
        var rules = new AccessRules { Create = "true", Update = UpdateSource };
        var engine = BuildEngine(TenancyMode.Global, rules);

        var decision = engine.Resolve("widgets", DataOperation.Update, Caller());

        decision.Using.ShouldNotBeNull();
        decision.WithCheck.ShouldNotBeNull();
        decision.Using!.Source.ShouldBe(UpdateSource);
        decision.WithCheck!.Source.ShouldBe(UpdateSource);
    }

    /// <summary>A tenant-scoped entity denies a tenantless caller outright — the tenant guard runs before any rule is consulted.</summary>
    [Fact]
    public void A_scoped_entity_denies_a_tenantless_caller_before_any_rule_is_consulted()
    {
        var engine = BuildEngine(TenancyMode.Scoped, AllTrueRules());

        var decision = engine.Resolve("widgets", DataOperation.List, TenantlessCaller());

        decision.IsDenied.ShouldBeTrue();
        decision.DenyReason.ShouldNotBeNull();
        decision.DenyReason.ShouldContain("tenant");
    }

    /// <summary>
    /// A global entity's tenant guard never fires, so a tenantless caller passes it — but only
    /// because none of this entity's rules reads <c>@tenant.id</c>. The rules here are
    /// <c>"true"</c>, which reads no context at all; the moment a rule does, the required-context
    /// gate below denies instead. Stated that narrowly on purpose: the general claim "a global
    /// entity does not penalize a tenantless caller" is false, and false in precisely the
    /// direction that was a real isolation hole.
    /// </summary>
    [Fact]
    public void A_global_entity_whose_rules_read_no_context_allows_a_tenantless_caller()
    {
        var engine = BuildEngine(TenancyMode.Global, AllTrueRules());

        var decision = engine.Resolve("widgets", DataOperation.List, TenantlessCaller());

        decision.IsDenied.ShouldBeFalse();
    }

    /// <summary>
    /// The required-context gate, on the channel the entity-level tenant guard by definition cannot
    /// speak on: a <b>global</b> entity whose rule reads <c>@tenant.id</c>, resolved for a caller
    /// carrying no tenant. Every operation is asserted, because the gate has to see all three of an
    /// operation's predicate slots — <c>create</c> has no <c>USING</c> and <c>delete</c> no
    /// <c>WITH CHECK</c>, so an engine that only inspected one slot would leak on the other.
    /// </summary>
    /// <remarks>
    /// Without this gate the absent operand simply collapses to <see langword="false"/> in the
    /// backends, and a negated comparison (<c>!(id == @tenant.id)</c>) inverts that into "every
    /// row" with an empty parameter bag. The engine must therefore refuse to hand the predicate
    /// out at all rather than rely on how a backend folds a null operand.
    /// </remarks>
    /// <param name="operationName">The <see cref="DataOperation"/> member name under test.</param>
    [Theory]
    [InlineData(nameof(DataOperation.List))]
    [InlineData(nameof(DataOperation.Get))]
    [InlineData(nameof(DataOperation.Create))]
    [InlineData(nameof(DataOperation.Update))]
    [InlineData(nameof(DataOperation.Delete))]
    public void A_global_entity_whose_rule_reads_the_tenant_denies_a_tenantless_caller(string operationName)
    {
        var operation = Enum.Parse<DataOperation>(operationName);
        var engine = BuildEngine(TenancyMode.Global, AllRules(TenantOwnedRule));

        var decision = engine.Resolve("widgets", operation, TenantlessCaller());

        decision.IsDenied.ShouldBeTrue($"{operation} reads @tenant.id, which this caller does not have.");
    }

    /// <summary>
    /// The same gate one operand over: a rule reading <c>@user.id</c> must deny a caller with no
    /// identity, rather than resolving the reserved all-zero <see cref="UserId"/> against the rows
    /// whose owner column happens to be all-zero — which would silently make an anonymous caller
    /// their owner.
    /// </summary>
    /// <param name="operationName">The <see cref="DataOperation"/> member name under test.</param>
    [Theory]
    [InlineData(nameof(DataOperation.List))]
    [InlineData(nameof(DataOperation.Get))]
    [InlineData(nameof(DataOperation.Create))]
    [InlineData(nameof(DataOperation.Update))]
    [InlineData(nameof(DataOperation.Delete))]
    public void A_rule_reading_the_user_denies_a_caller_with_no_identity(string operationName)
    {
        var operation = Enum.Parse<DataOperation>(operationName);
        var engine = BuildEngine(TenancyMode.Global, AllRules(UserOwnedRule));

        var decision = engine.Resolve("widgets", operation, IdentitylessCaller());

        decision.IsDenied.ShouldBeTrue($"{operation} reads @user.id, which this caller does not have.");
    }

    /// <summary>
    /// The gate must not over-deny: the very same rules that deny the callers above must allow a
    /// caller who does carry both halves of the context. Without this leg, an engine that denied
    /// every operation reading any context value would pass the two facts above.
    /// </summary>
    [Fact]
    public void A_rule_reading_the_tenant_and_the_user_allows_a_caller_who_has_both()
    {
        var engine = BuildEngine(TenancyMode.Global, AllRules($"{TenantOwnedRule} && {UserOwnedRule}"));

        var decision = engine.Resolve("widgets", DataOperation.List, Caller());

        decision.IsDenied.ShouldBeFalse();
    }

    /// <summary>An unknown entity denies rather than throwing — indistinguishable from an unauthorized one at this layer.</summary>
    [Fact]
    public void An_unknown_entity_denies_rather_than_throwing()
    {
        var engine = BuildEngine(TenancyMode.Global, AllTrueRules());

        var decision = engine.Resolve("does-not-exist", DataOperation.List, Caller());

        decision.IsDenied.ShouldBeTrue();
    }

    /// <summary>
    /// The single most important missing fact this suite used to lack: on a <c>Scoped</c> entity with
    /// a tenant present, <see cref="PolicyDecision.TenantScope"/> must be non-null. An engine that
    /// denies a tenantless caller correctly but returns a <see langword="null"/> <c>TenantScope</c> for
    /// every other caller would pass every other fact here — and its data port would then apply no
    /// tenant filter at all, letting one tenant read another's rows (the F7 dynamic-entity risk this
    /// suite exists to guard against, since every tenant there shares one physical table).
    /// </summary>
    [Fact]
    public void A_scoped_entity_with_a_tenant_present_synthesizes_a_non_null_tenant_scope()
    {
        var engine = BuildEngine(TenancyMode.Scoped, AllTrueRules());

        var decision = engine.Resolve("widgets", DataOperation.List, Caller());

        decision.IsDenied.ShouldBeFalse();
        decision.TenantScope.ShouldNotBeNull();
    }

    /// <summary>
    /// A field statically declared <c>hidden: true</c> must actually appear in
    /// <see cref="PolicyDecision.HiddenFields"/> — an engine that always returns an empty mask
    /// currently passes every other fact in this suite.
    /// </summary>
    [Fact]
    public void A_field_declared_statically_hidden_is_in_the_hidden_mask()
    {
        var engine = BuildEngine(TenancyMode.Global, AllTrueRules(), hidden: BoolOrCel.FromBoolean(true));

        var decision = engine.Resolve("widgets", DataOperation.List, Caller());

        decision.HiddenFields.ShouldContain("title");
    }

    /// <summary>The read-only counterpart of <see cref="A_field_declared_statically_hidden_is_in_the_hidden_mask"/>.</summary>
    [Fact]
    public void A_field_declared_statically_read_only_is_in_the_read_only_mask()
    {
        var engine = BuildEngine(TenancyMode.Global, AllTrueRules(), readOnly: BoolOrCel.FromBoolean(true));

        var decision = engine.Resolve("widgets", DataOperation.List, Caller());

        decision.ReadOnlyFields.ShouldContain("title");
    }

    /// <summary>
    /// A denial must be inert: every predicate and both masks come back empty/<see langword="null"/>,
    /// so a data port that forgot to check <see cref="PolicyDecision.IsDenied"/> first cannot
    /// accidentally read a permissive predicate or a populated mask off a denied decision.
    /// </summary>
    [Fact]
    public void A_denied_decision_carries_no_predicate_and_no_mask()
    {
        var engine = BuildEngine(TenancyMode.Global, rules: null, hidden: BoolOrCel.FromBoolean(true), readOnly: BoolOrCel.FromBoolean(true));

        var decision = engine.Resolve("widgets", DataOperation.List, Caller());

        decision.IsDenied.ShouldBeTrue();
        decision.Using.ShouldBeNull();
        decision.WithCheck.ShouldBeNull();
        decision.TenantScope.ShouldBeNull();
        decision.HiddenFields.ShouldBeEmpty();
        decision.ReadOnlyFields.ShouldBeEmpty();
    }

    private const string TenantOwnedRule = "id == @tenant.id";

    private const string UserOwnedRule = "id == @user.id";

    private static DataOperation[] AllOperations() => Enum.GetValues<DataOperation>();

    private static AccessRules AllTrueRules() => AllRules("true");

    /// <summary>The same source on all five operations, so one fact can sweep every slot combination.</summary>
    /// <param name="source">The Rule-profile source every operation is configured with.</param>
    private static AccessRules AllRules(string source) => new()
    {
        List = source,
        Get = source,
        Create = source,
        Update = source,
        Delete = source,
    };

    private IPolicyEngine BuildEngine(TenancyMode tenancy, AccessRules? rules, BoolOrCel? hidden = null, BoolOrCel? readOnly = null)
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["title"] = new() { Type = MMLib.Alvo.Descriptor.FieldType.String, Hidden = hidden, ReadOnly = readOnly },
        };

        var descriptor = new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "contract-tests",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
            {
                ["widgets"] = new() { Fields = fields, Rules = rules },
            },
        };

        var schemaFields = new List<FieldSchema>
        {
            new() { Name = "id", Type = MMLib.Alvo.Schema.FieldType.Uuid, Required = true },
            new() { Name = "title", Type = MMLib.Alvo.Schema.FieldType.String },
        };

        if (tenancy == TenancyMode.Scoped)
        {
            schemaFields.Add(new FieldSchema { Name = "tenant_id", Type = MMLib.Alvo.Schema.FieldType.Uuid, Required = true });
        }

        var schema = new SchemaModel([
            new EntitySchema { Name = "widgets", Tenancy = tenancy, Fields = schemaFields },
        ]);

        return CreateEngine(descriptor, schema);
    }
}
