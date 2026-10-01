using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Identity;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// The simulator answers what production answers.
/// </summary>
/// <remarks>
/// <para>
/// <b>The dashboard's Rules screen shows an operator a verdict, and an operator will act on it.</b>
/// A simulator that drifted from the engine would be worse than no simulator: it would be a
/// confident wrong answer about who reaches what. So this asks both — the same entity, the same
/// operation, the same caller — and holds them to the same answer, over the whole cross product
/// rather than over the examples someone thought of.
/// </para>
/// <para>
/// <b>What "the same answer" means is narrower than it looks, and the verdict's own contract says
/// so.</b> <c>Allowed</c> is <i>"whether the engine resolved a policy at all, rather than refusing
/// outright"</i> — not <i>"this caller will see rows"</i>, because a rule over <c>@user.roles</c>
/// comes back as a predicate rather than a decision. The production side of the equivalence is
/// therefore <b>an outright refusal</b> — <see cref="AlvoAuthorizationException"/> — and
/// deliberately <em>not</em> an empty page or a missing row: a caller whose predicate admits
/// nothing was still admitted by the policy, and the verdict says <see langword="true"/> for them
/// on purpose. A test that read zero rows as "denied" would fail the simulator for telling the
/// truth.
/// </para>
/// <para>
/// <see cref="AlvoRecordNotFoundException"/> is likewise not a refusal: it is what an <em>admitted</em>
/// caller gets for a row their read predicate excludes, which the Data API answers as 404 precisely
/// so the two cannot be told apart from outside.
/// </para>
/// </remarks>
public sealed class PolicySimulatorAgreementTests : IAsyncLifetime
{
    private const string Descriptor = "host-session-tenancy.alvo.json";
    private const string Project = "host-session-tenancy";

    private AlvoHostWorld? _world;
    private IServiceScope? _scope;
    private IAlvoData _data = null!;
    private IAlvoManagement _management = null!;
    private IAlvoContextAccessor _ambient = null!;

    private readonly TenantId _north = TenantId.New();

    private AlvoContext _tenanted = null!;
    private AlvoContext _untenanted = null!;
    private Guid _note;
    private Guid _announcement;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        _world = await AlvoHostWorld.StartAsync(Descriptor);
        _scope = _world.Services.CreateScope();

        _data = _scope.ServiceProvider.GetRequiredService<IAlvoData>();
        _management = _scope.ServiceProvider.GetRequiredService<IAlvoManagement>();
        _ambient = _scope.ServiceProvider.GetRequiredService<IAlvoContextAccessor>();

        var people = _scope.ServiceProvider.GetRequiredKeyedService<IAlvoUserAdministration>(
            AlvoUserAdministration.UnguardedKey);
        var sessions = _scope.ServiceProvider.GetRequiredKeyedService<IAlvoContextResolver>(
            AlvoIdentity.ResolverKey);

        _tenanted = await SessionAsync(people, sessions, "dana@alvo.test", _north);
        _untenanted = await SessionAsync(people, sessions, "elo@alvo.test", tenant: null);

        /* One row of each shape, so the write operations have something real to aim at: an
           operation that refused for want of a row would measure the seeding, not the policy. */
        _note = await CreateAsync("notes", "title", "a note", _tenanted);
        _announcement = await CreateAsync("announcements", "body", "an announcement", _untenanted);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _scope?.Dispose();
        if (_world is not null)
        {
            await _world.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("notes", "list")]
    [InlineData("notes", "create")]
    [InlineData("notes", "update")]
    [InlineData("notes", "delete")]
    [InlineData("announcements", "list")]
    [InlineData("announcements", "create")]
    [InlineData("announcements", "update")]
    [InlineData("announcements", "delete")]
    public async Task The_simulator_agrees_with_the_engine_for_an_operator_holding_a_tenant(
        string entity, string operation)
        => await AgreeAsync(entity, operation, _tenanted);

    [Theory]
    [InlineData("notes", "list")]
    [InlineData("notes", "create")]
    [InlineData("notes", "update")]
    [InlineData("notes", "delete")]
    [InlineData("announcements", "list")]
    [InlineData("announcements", "create")]
    [InlineData("announcements", "update")]
    [InlineData("announcements", "delete")]
    public async Task The_simulator_agrees_with_the_engine_for_an_operator_holding_none(
        string entity, string operation)
        => await AgreeAsync(entity, operation, _untenanted);

    [Theory]
    [InlineData("notes", "list")]
    [InlineData("notes", "create")]
    [InlineData("notes", "update")]
    [InlineData("notes", "delete")]
    [InlineData("announcements", "list")]
    [InlineData("announcements", "create")]
    [InlineData("announcements", "update")]
    [InlineData("announcements", "delete")]
    public async Task The_simulator_agrees_with_the_engine_for_the_anonymous_caller(
        string entity, string operation)
        => await AgreeAsync(entity, operation, AlvoContext.Anonymous);

    /// <summary>Asks both sides the same question and holds them to the same answer.</summary>
    /// <remarks>
    /// <para>
    /// <b>A denial is absolute in both directions.</b> If the simulator says denied, production must
    /// refuse — a screen that says "no" while the engine says "yes" is the failure that matters most,
    /// because it is the one an operator resolves by widening a rule that was already wide enough.
    /// </para>
    /// <para>
    /// <b>"Allowed" is one-sided for a write, and this is where the two sides legitimately part.</b>
    /// A write is checked twice: the engine resolves a policy, and then <c>WITH CHECK</c> is
    /// evaluated over the <em>post-image</em> — a row that does not exist when the question is
    /// asked. The simulator cannot evaluate it without inventing one, and §6.3-4 forbids a client
    /// evaluating a stored row at all, so it hands the predicate back instead. The fact this suite
    /// therefore holds is that it hands one back: a write the engine refuses while the verdict says
    /// <c>Allowed</c> must carry a <c>WithCheck</c> or a <c>TenantScope</c>, so the screen has
    /// something true to show rather than a bare "permitted".
    /// </para>
    /// </remarks>
    /// <param name="entity">The entity to ask about.</param>
    /// <param name="operation">The operation to ask about.</param>
    /// <param name="caller">The caller to ask for.</param>
    /// <returns>A task that completes when both have answered.</returns>
    private async Task AgreeAsync(string entity, string operation, AlvoContext caller)
    {
        var verdict = await SimulateAsync(entity, operation, caller);
        var refusal = await RefusalAsync(entity, operation, caller);
        var what = $"{operation} on {entity}";

        if (!verdict.Allowed)
        {
            refusal.ShouldNotBeNull(
                $"the simulator denied {what} ({verdict.DenyReason}) and the engine did not refuse it; "
                + "an operator told 'no' by a screen widens a rule that was already wide enough");
            return;
        }

        if (refusal is null)
        {
            return;
        }

        /* The one disagreement this suite tolerates has to be THE documented one, not merely a
           refusal: AlvoAuthorizationException.WriteRejectedByPolicy is a public constant precisely
           so the WITH CHECK refusal is identifiable, and three layers already say it identically. */
        refusal.Message.ShouldBe(
            AlvoAuthorizationException.WriteRejectedByPolicy,
            $"the engine refused {what} while the verdict said allowed, which is legitimate only on "
            + "the WITH CHECK path — any other refusal disagreeing with an allowed verdict is a bug "
            + "in the simulator, and this assertion is what tells the two apart");

        (verdict.WithCheck ?? verdict.TenantScope).ShouldNotBeNull(
            "…and only if the verdict hands that predicate back for the screen to render, since the "
            + "post-image it was refused over is one the simulator may not invent");

        operation.ShouldNotBe(
            "list",
            "a read is refused by the predicate compiled into the WHERE and by nothing else, so there "
            + "is no second gate for a read to disagree about");
    }

    /// <summary>What the simulator answers, asked as an administrator.</summary>
    /// <remarks>
    /// The simulation route is itself gated, so the question is asked by a caller the project
    /// admits — which is the tenanted operator, since this descriptor's access block resolves
    /// <c>admin</c> from the <c>operator</c> role. Who <em>asks</em> is not who is simulated.
    /// </remarks>
    /// <param name="entity">The entity to ask about.</param>
    /// <param name="operation">The operation to ask about.</param>
    /// <param name="caller">The caller to ask for.</param>
    /// <returns>The verdict.</returns>
    private async Task<ManagementPolicyVerdict> SimulateAsync(
        string entity, string operation, AlvoContext caller)
    {
        var anonymous = caller.User == AlvoContext.Anonymous.User;
        var simulated = new ManagementSimulatedCaller(
            anonymous ? null : caller.User.Value,
            anonymous ? [] : [.. caller.Roles.Select(role => role.Name)],
            caller.Tenant?.Value);

        var previous = _ambient.Principal;
        _ambient.Principal = new AlvoPrincipal
        {
            Context = _tenanted,
            Scopes = new HashSet<ApiKeyScope>
            {
                new() { Entity = "*", Access = ScopeAccess.Read },
                new() { Entity = "*", Access = ScopeAccess.Write },
            },
            KeyId = "simulator-agreement",
        };

        try
        {
            return await _management.SimulatePolicyAsync(
                Project,
                new ManagementPolicySimulation(entity, operation, simulated),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            _ambient.Principal = previous;
        }
    }

    /// <summary>The engine's outright refusal for this caller, or nothing.</summary>
    /// <remarks>
    /// The exception itself rather than a bool, because <em>which</em> refusal it is decides whether
    /// a disagreement with the verdict is the documented one or a defect.
    /// </remarks>
    /// <param name="entity">The entity to act on.</param>
    /// <param name="operation">The operation to attempt.</param>
    /// <param name="caller">The caller to act as.</param>
    /// <returns>The refusal, or <see langword="null"/> when the engine did not refuse outright.</returns>
    private async Task<AlvoAuthorizationException?> RefusalAsync(
        string entity, string operation, AlvoContext caller)
    {
        try
        {
            await AttemptAsync(entity, operation, caller);
            return null;
        }
        catch (AlvoAuthorizationException refusal)
        {
            return refusal;
        }
        catch (AlvoRecordNotFoundException)
        {
            /* An admitted caller whose read predicate excludes the row: 404, deliberately
               indistinguishable from absence and deliberately not a refusal. */
            return null;
        }
    }

    /// <summary>Runs one operation through the data port.</summary>
    /// <param name="entity">The entity to act on.</param>
    /// <param name="operation">The operation to attempt.</param>
    /// <param name="caller">The caller to act as.</param>
    /// <returns>A task that completes when the attempt has been made.</returns>
    private Task AttemptAsync(string entity, string operation, AlvoContext caller)
    {
        var ct = TestContext.Current.CancellationToken;
        var row = entity == "notes" ? _note : _announcement;

        return operation switch
        {
            "list" => _data.QueryAsync(new AlvoQuery { Entity = entity, Limit = 1 }, caller, ct),
            "create" => _data.CreateAsync(entity, Body(entity, "simulated", caller), caller, cancellationToken: ct),
            "update" => _data.UpdateAsync(
                entity, row, Body(entity, "simulated", caller: null), caller, cancellationToken: ct),
            "delete" => _data.DeleteAsync(entity, row, caller, cancellationToken: ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation."),
        };
    }

    /// <summary>The body one of the two entities takes.</summary>
    /// <remarks>
    /// <paramref name="caller"/> is non-null only for a create on a tenant-scoped entity, where the
    /// post-image's tenant is compared against the caller's own — so the row has to name it, exactly
    /// as the Data API's body does.
    /// </remarks>
    /// <param name="entity">The entity the body is for.</param>
    /// <param name="value">The value of its one required field.</param>
    /// <param name="caller">The caller whose tenant to stamp, on a create.</param>
    /// <returns>The values to send.</returns>
    private static Dictionary<string, object?> Body(string entity, string value, AlvoContext? caller)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [entity == "notes" ? "title" : "body"] = value,
        };

        if (caller is not null && entity == "notes")
        {
            body["tenant_id"] = caller.Tenant?.Value;
        }

        return body;
    }

    private static async Task<AlvoContext> SessionAsync(
        IAlvoUserAdministration people, IAlvoContextResolver sessions, string email, TenantId? tenant)
    {
        var created = await people.CreateAsync(
            new AlvoUserCreation(email, ["operator"], tenant), TestContext.Current.CancellationToken);

        var principal = await sessions.ResolveAsync(
            created.Id.ToString(), null, TestContext.Current.CancellationToken);

        return principal.ShouldNotBeNull().Context;
    }

    private async Task<Guid> CreateAsync(string entity, string field, string value, AlvoContext caller)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal) { [field] = value };
        if (entity == "notes")
        {
            body["tenant_id"] = caller.Tenant?.Value;
        }

        var created = await _data.CreateAsync(
            entity, body, caller, cancellationToken: TestContext.Current.CancellationToken);

        return (Guid)created["id"]!;
    }
}
