using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// <c>POST {m}/projects/{p}/policy/simulate</c> — the same engine, never a copy.
/// </summary>
/// <remarks>
/// <para>
/// The simulator is held to production's own answer: the same triple, simulated and then actually
/// requested, must agree. That is the acceptance criterion, not a unit test of a mapping.
/// </para>
/// <para>
/// <b>The record-id arm is deliberately absent.</b> Evaluating <c>USING</c> against a stored row needs a
/// read, and a read through the Management API is exactly the data surface deviation D4 refuses to create.
/// A caller who wants to know whether one row passes fetches it through <c>/api</c> under the simulated
/// caller's own credential, which is the production answer by construction.
/// </para>
/// </remarks>
public class ManagementPolicySimulationTests
{
    private static readonly TestApiKey _ops = new("mgmt-ops", ["ops"], ["*:read"]);

    /// <summary>A Data API caller, so a verdict can be checked against what production actually answers.</summary>
    /// <remarks>
    /// It holds both scopes, because the scope gate runs before the policy: a key narrower than the
    /// operation would answer 403 <c>out-of-scope</c> and the comparison would be measuring the credential
    /// rather than the rule.
    /// </remarks>
    private static readonly TestApiKey _dispatcher = new("fleet-dispatcher", ["dispatcher"], ["*:read", "*:write"]);

    [Fact]
    public async Task The_verdict_carries_the_compiled_predicate_the_engine_resolved()
    {
        await using var world = await ManagedFleet.StartAsync([_ops, _dispatcher]);

        var verdict = await Simulate(world, "vehicles", "list", _dispatcher.Roles);

        verdict["allowed"]!.GetValue<bool>().ShouldBeTrue();
        verdict["denyReason"].ShouldBeNull();
        verdict["using"]!.GetValue<string>().ShouldBe(
            "'dispatcher' in @user.roles",
            "the predicate is the compiled expression's own source, not a rendering of it");
        verdict["hiddenFields"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_triple_no_rule_admits_is_denied_with_the_engine_s_own_reason()
    {
        await using var world = await ManagedFleet.StartAsync([_ops, _dispatcher]);

        var verdict = await Simulate(world, "audits", "delete", _dispatcher.Roles);

        verdict["allowed"]!.GetValue<bool>().ShouldBeFalse();
        verdict["denyReason"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        verdict["using"].ShouldBeNull("a denied decision hands out no predicate at all");
    }

    /// <summary>
    /// The simulator and the Data API answer the same question the same way, in both directions.
    /// </summary>
    /// <remarks>
    /// <b>Both arms, because either alone is satisfiable by an accident.</b> A simulator that answered
    /// "allowed" unconditionally passes the first; one that answered "denied" unconditionally passes the
    /// second. Only the pair says it is calling the engine.
    /// </remarks>
    [Fact]
    public async Task The_simulator_agrees_with_what_the_data_api_actually_answers()
    {
        await using var world = await ManagedFleet.StartAsync([_ops, _dispatcher]);

        var admitted = await Simulate(world, "vehicles", "list", _dispatcher.Roles);
        var refused = await Simulate(world, "audits", "delete", _dispatcher.Roles);

        admitted["allowed"]!.GetValue<bool>().ShouldBeTrue();
        (await world.SendAsync(HttpMethod.Get, "/api/vehicles", _dispatcher)).StatusCode.ShouldBe(
            HttpStatusCode.OK, "the simulator admitted this caller, so production must admit them too");

        refused["allowed"]!.GetValue<bool>().ShouldBeFalse();
        (await world.SendAsync(HttpMethod.Delete, $"/api/audits/{Guid.NewGuid()}", _dispatcher)).StatusCode
            .ShouldBe(
                HttpStatusCode.Forbidden,
                "the simulator said no policy admits it, so production must refuse it for the same reason");
    }

    [Fact]
    public async Task A_role_the_descriptor_does_not_declare_is_422_rather_than_a_silent_deny()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var response = await Ask(world, "vehicles", "list", ["not-a-declared-role"]);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Validation);
        (await response.ReadProblemDetailAsync()).ShouldContain(
            "dispatcher",
            Case.Sensitive,
            "the refusal lists the roles that are declared, so an agent can correct itself");
    }

    [Fact]
    public async Task An_operation_name_the_framework_does_not_know_is_422()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var response = await Ask(world, "vehicles", "purge", ["dispatcher"]);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ReadProblemDetailAsync()).ShouldContain(
            "delete",
            Case.Sensitive,
            "an unusable operation name is answered with the ones that are usable");
    }

    /// <summary>
    /// A caller with roles and no identity is refused rather than quietly simulated as anonymous.
    /// </summary>
    /// <remarks>
    /// Production has no credential that resolves to roles without a user, so answering for one would be
    /// answering a question production cannot be asked — and silently dropping the roles would return the
    /// anonymous caller's verdict under the sender's own role names, which is the worst of the three.
    /// </remarks>
    [Fact]
    public async Task A_caller_with_roles_and_no_identity_is_refused_rather_than_simulated_as_anonymous()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var response = await world.SendAsync(
            HttpMethod.Post,
            $"{ManagedFleet.Routes}/policy/simulate",
            _ops,
            body: new JsonObject
            {
                ["entity"] = "vehicles",
                ["operation"] = "list",
                ["caller"] = new JsonObject { ["roles"] = new JsonArray("dispatcher") },
            });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    /// <summary>
    /// The anonymous caller is simulatable, and <c>allowed</c> means "not refused outright", never "will see
    /// rows".
    /// </summary>
    /// <remarks>
    /// <b>This is the fact that keeps the payload honest.</b> A rule over <c>@user.roles</c> is a predicate
    /// the engine hands back rather than evaluates, so the anonymous caller earns an allow <em>and</em> a
    /// predicate that no row of theirs will satisfy. A dashboard that read <c>allowed</c> as "this caller
    /// can read this entity" would be wrong here, which is why
    /// <see cref="Management.ManagementPolicyVerdict.Allowed"/> says so in its own words.
    /// </remarks>
    [Fact]
    public async Task The_anonymous_caller_is_not_refused_outright_but_earns_a_predicate_that_excludes_them()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var response = await world.SendAsync(
            HttpMethod.Post,
            $"{ManagedFleet.Routes}/policy/simulate",
            _ops,
            body: new JsonObject
            {
                ["entity"] = "vehicles",
                ["operation"] = "list",
                ["caller"] = new JsonObject { ["roles"] = new JsonArray() },
            });
        var verdict = await response.ReadJsonObjectAsync();

        verdict["allowed"]!.GetValue<bool>().ShouldBeTrue(
            "the rule is a predicate over roles, which the engine hands back rather than evaluates");
        verdict["using"]!.GetValue<string>().ShouldBe("'dispatcher' in @user.roles");
    }

    [Fact]
    public async Task A_project_this_instance_does_not_serve_is_404()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var response = await world.SendAsync(
            HttpMethod.Post,
            "/management/projects/not-mine/policy/simulate",
            _ops,
            body: Body("vehicles", "list", ["dispatcher"]));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task<JsonObject> Simulate(
        AlvoApiWorld world, string entity, string operation, IReadOnlyList<string> roles) =>
        await (await Ask(world, entity, operation, roles)).ReadJsonObjectAsync();

    private static Task<HttpResponseMessage> Ask(
        AlvoApiWorld world, string entity, string operation, IReadOnlyList<string> roles) =>
        world.SendAsync(
            HttpMethod.Post,
            $"{ManagedFleet.Routes}/policy/simulate",
            _ops,
            body: Body(entity, operation, roles));

    private static JsonObject Body(string entity, string operation, IReadOnlyList<string> roles) => new()
    {
        ["entity"] = entity,
        ["operation"] = operation,
        ["caller"] = new JsonObject
        {
            ["user"] = Guid.NewGuid().ToString(),
            ["roles"] = new JsonArray([.. roles.Select(role => (JsonNode)role!)]),
        },
    };
}
