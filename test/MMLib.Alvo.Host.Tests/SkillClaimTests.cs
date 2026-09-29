using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// What the descriptor skills state about the core, held to the core: the reserved-fields region to
/// <see cref="ReservedQueryKeys"/> (D25), and each refusal or acceptance a skill states in prose to the real dry run
/// on <c>bike-workshop</c>.
/// </summary>
public sealed class SkillClaimTests
{
    private const string OrderLines = "/entities/order_lines/fields/";
    private const string Technicians = "/entities/technicians/fields/";
    private const string Rentals = "/entities/rentals/fields/";
    private const string AfterCreate = "/entities/rentals/hooks/afterCreate/-";

    /// <summary>Each probe, the field it adds, and whether the skill says the dry run accepts it.</summary>
    private static readonly (string Path, string Field, bool Accepted)[] _probes =
    [
        (OrderLines + "probe_computed_read_only", """{"type": "decimal", "precision": 10, "scale": 2, "required": true, "readOnly": true, "computed": "quantity * unit_price"}""", false),
        (OrderLines + "probe_computed", """{"type": "decimal", "precision": 10, "scale": 2, "required": true, "computed": "quantity * unit_price"}""", true),
        (Technicians + "probe_read_only_default", """{"type": "integer", "required": true, "readOnly": true, "default": 0}""", true),
        (Technicians + "probe_default_of_another_type", """{"type": "integer", "default": "0"}""", false),
        (Rentals + "probe_user_ref", """{"type": "ref", "entity": "users", "index": true}""", true),
        (Technicians + "probe_cel_default", """{"type": "datetime", "default": {"$cel": "now()"}}""", false),
        (Technicians + "probe_hidden_by_row", """{"type": "string", "hidden": "active == true"}""", false),
        (Technicians + "probe_hidden_by_caller", """{"type": "string", "hidden": "!('admin' in @user.roles)"}""", true),
        (Technicians + "probe_facet_on_another_type", """{"type": "text", "maxLength": 10}""", false),
        (Technicians + "id", """{"type": "uuid"}""", false),
        (Technicians + "created_at", """{"type": "datetime"}""", false),
        ("/entities/users", """{"fields": {"nickname": {"type": "string"}}}""", false),
        (AfterCreate, """{"action": {"type": "function", "name": "invoice"}}""", false),
        (AfterCreate, """{"action": {"type": "http.call", "url": "https://erp.example.com/rentals"}}""", false),
        (AfterCreate, """{"action": {"type": "entity.update", "entity": "customers", "payload": "{}"}}""", false),
    ];

    public static TheoryData<int> Probes() => [.. Enumerable.Range(0, _probes.Length)];

    [Fact]
    public void The_reserved_fields_are_the_data_apis_reserved_query_keys()
    {
        var regions = SkillCatalogue.Regions(SkillCatalogue.Named("entities-and-fields").Body);

        regions.ShouldContainKey("reserved-fields");
        SkillCatalogue.Tokens(regions["reserved-fields"])
            .ShouldBe(ReservedQueryKeys.All, $"<!-- gen:reserved-fields --> should read:\n{SkillCatalogue.Expected(ReservedQueryKeys.All)}");
    }

    [Theory]
    [MemberData(nameof(Probes))]
    public async Task A_field_the_skills_accept_or_refuse_is_accepted_or_refused_by_the_dry_run(int probe)
    {
        var (path, field, accepted) = _probes[probe];
        await using var world = await AlvoHostWorld.StartAsync(InstructionExampleOutcomeTests.BikeWorkshop);
        var management = world.Services.GetRequiredService<IAlvoManagement>();
        world.Services.GetRequiredService<IAlvoContextAccessor>().Principal = InstructionExampleOutcomeTests.Administrator();

        var attempt = await InstructionExampleOutcomeTests.AttemptAsync(management, path, field);

        attempt.Valid.ShouldBe(accepted, $"{path}: {string.Join(" | ", attempt.Refusals)}");
    }
}
