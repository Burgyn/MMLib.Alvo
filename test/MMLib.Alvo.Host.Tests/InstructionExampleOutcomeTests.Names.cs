using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Ai.Internal;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;
using MMLib.Alvo.Schema;

using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests;

public sealed partial class InstructionExampleOutcomeTests
{
    private const string ManagedRefusal = "framework-managed column";
    private const string ReservedRefusal = "reserved";

    /// <summary>
    /// The entity names the name rule calls refused: the wrong case, a hyphen, and the reserved auth entity. Each
    /// probe's refusal may land on any rule (the pattern's pointer, the schema's <c>users: false</c>), so none names
    /// a fragment.
    /// </summary>
    private static readonly string[] _refusedEntityNames = ["CustomerAudits", "customer-audits", "users"];

    /// <summary>
    /// Every managed-column bullet the instructions state, with the trait a probe entity declares for the bullet's
    /// columns to be the framework's — each probe adds an entity of its own, so none depends on which traits
    /// <c>bike-workshop</c> happens to declare.
    /// </summary>
    /// <remarks>
    /// The scoped-tenancy bullet is not probed: a scoped entity needs project tenancy, which <c>bike-workshop</c>
    /// does not enable. Its <c>tenant_id</c> is held to <see cref="AlvoManagedColumns"/> by
    /// <c>AssistantInstructionsTests</c>.
    /// </remarks>
    private static readonly (string Label, string? Trait)[] _probedBullets =
    [
        (InstructionClaims.EveryEntity, null),
        (InstructionClaims.AuditedEntity, "audit"),
        (InstructionClaims.SoftDeletedEntity, "softDelete"),
    ];

    [Fact]
    public void The_reserved_fields_the_instructions_list_are_the_data_apis_reserved_query_keys() =>
        InstructionClaims.ReservedFields(AssistantInstructions.Text).ShouldBe(ReservedQueryKeys.All, ignoreOrder: true);

    [Fact]
    public async Task Every_name_the_instructions_call_refused_is_refused_by_the_validator()
    {
        await using var world = await AlvoHostWorld.StartAsync(BikeWorkshop);
        var management = world.Services.GetRequiredService<IAlvoManagement>();
        world.Services.GetRequiredService<IAlvoContextAccessor>().Principal = Administrator();

        foreach (var (path, entity, fragment) in RefusedNameProbes())
        {
            var attempt = await AttemptAsync(management, path, entity);

            attempt.Valid.ShouldBeFalse($"accepted: {path} {entity}");
            if (fragment is not null)
            {
                attempt.Violations.ShouldContain(
                    violation => violation.Message.Contains(fragment, StringComparison.Ordinal),
                    $"{path} {entity}: {string.Join(" | ", attempt.Refusals)}");
            }
        }
    }

    [Fact]
    public async Task The_name_rules_positive_forms_are_accepted()
    {
        await using var world = await AlvoHostWorld.StartAsync(BikeWorkshop);
        var management = world.Services.GetRequiredService<IAlvoManagement>();
        world.Services.GetRequiredService<IAlvoContextAccessor>().Principal = Administrator();

        foreach (var (path, value) in AcceptedNameProbes())
        {
            var attempt = await AttemptAsync(management, path, value);
            attempt.Valid.ShouldBeTrue($"{path} {value}: {string.Join(" | ", attempt.Refusals)}");
        }
    }

    private static IEnumerable<(string Path, string Value, string? Fragment)> RefusedNameProbes()
    {
        var text = AssistantInstructions.Text;
        foreach (var name in _refusedEntityNames)
        {
            yield return ("/entities/" + name, Entity([]), null);
        }

        foreach (var field in InstructionClaims.ReservedFields(text))
        {
            yield return ("/entities/probe_reserved", Entity([field]), ReservedRefusal);
        }

        var columns = InstructionClaims.ManagedColumns(text);
        foreach (var (label, trait) in _probedBullets)
        {
            foreach (var column in columns[label])
            {
                yield return ("/entities/probe_managed", Entity([column], trait), ManagedRefusal);
            }
        }
    }

    /// <summary>
    /// The rule's own example name, and the trait scoping the other way: <c>created_at</c> is an ordinary field on
    /// the non-audited <c>order_lines</c>.
    /// </summary>
    private static IEnumerable<(string Path, string Value)> AcceptedNameProbes() =>
    [
        ("/entities/customer_audits", Entity([])),
        (OrderLines + AlvoManagedColumns.CreatedAt, """{"type": "datetime"}"""),
    ];

    /// <summary>A new entity: a plain <c>label</c> field beside the probed ones, and the trait, if any, set.</summary>
    private static string Entity(IEnumerable<string> probedFields, string? trait = null)
    {
        var fields = new JsonObject { ["label"] = new JsonObject { ["type"] = "string" } };
        foreach (var field in probedFields)
        {
            fields[field] = new JsonObject { ["type"] = "string" };
        }

        var entity = new JsonObject { ["fields"] = fields };
        if (trait is not null)
        {
            entity[trait] = true;
        }

        return entity.ToJsonString();
    }
}
