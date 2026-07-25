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

    /// <summary><c>update</c> carries both a <c>USING</c> and a <c>WITH CHECK</c> predicate, compiled from the same rule source.</summary>
    [Fact]
    public void Update_carries_both_using_and_with_check()
    {
        var engine = BuildEngine(TenancyMode.Global, AllTrueRules());

        var decision = engine.Resolve("widgets", DataOperation.Update, Caller());

        decision.Using.ShouldNotBeNull();
        decision.WithCheck.ShouldNotBeNull();
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

    /// <summary>A global entity ignores tenancy entirely; a tenantless caller is not penalized for it.</summary>
    [Fact]
    public void A_global_entity_ignores_a_tenantless_caller()
    {
        var engine = BuildEngine(TenancyMode.Global, AllTrueRules());

        var decision = engine.Resolve("widgets", DataOperation.List, TenantlessCaller());

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

    private static DataOperation[] AllOperations() => Enum.GetValues<DataOperation>();

    private static AccessRules AllTrueRules() => new()
    {
        List = "true",
        Get = "true",
        Create = "true",
        Update = "true",
        Delete = "true",
    };

    private IPolicyEngine BuildEngine(TenancyMode tenancy, AccessRules? rules)
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["title"] = new() { Type = MMLib.Alvo.Descriptor.FieldType.String },
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
