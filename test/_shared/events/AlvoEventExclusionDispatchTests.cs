using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Events;

using Shouldly;

using Xunit;

using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// Spec E7 at dispatch, on a real write through a real dispatcher: a field <c>events.exclude</c> keeps out of both
/// row images is still named by <c>data.changed</c>, so an after-hook's <c>changed(field)</c> measures its move —
/// and so does its negation.
/// </summary>
/// <remarks>
/// <para>
/// Answered from the stripped images, <c>changed(salary)</c> would never hold and <c>!changed(salary)</c> would
/// hold on every update — the absent-operand failure #325 refuses for conditions reading a missing image. Each
/// fact therefore asserts all four hooks, both polarities over an excluded and a carried field, so neither
/// "never fires" nor "always fires" can pass.
/// </para>
/// <para>
/// Linked into both driver test projects rather than shipped in <c>MMLib.Alvo.Testing</c>: the subclass supplies
/// a started database and nothing else, so both engines are asked the same question.
/// </para>
/// </remarks>
public abstract class AlvoEventExclusionDispatchTests
{
    /// <summary>Builds a fresh backend over <paramref name="project"/>.</summary>
    /// <param name="project">The schema and descriptor the backend is started over.</param>
    protected abstract Task<IAlvoEventWorld> WorldAsync(AlvoEventProject project);

    /// <summary>An update moving only the excluded field fires <c>changed(salary)</c> and <c>!changed(status)</c>.</summary>
    [Fact]
    public async Task An_update_moving_only_an_excluded_field_is_measured_by_changed()
    {
        var delivered = await DeliveredAfterAsync(new() { ["salary"] = 5_000 });

        delivered.ShouldBe(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [SalaryMoved] = 1,
            [SalaryStill] = 0,
            [StatusMoved] = 0,
            [StatusStill] = 1,
        });
    }

    /// <summary>An update moving only a carried field fires <c>!changed(salary)</c> and <c>changed(status)</c>.</summary>
    [Fact]
    public async Task An_update_leaving_an_excluded_field_alone_is_measured_by_changed()
    {
        var delivered = await DeliveredAfterAsync(new() { ["status"] = "promoted" });

        delivered.ShouldBe(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [SalaryMoved] = 0,
            [SalaryStill] = 1,
            [StatusMoved] = 1,
            [StatusStill] = 0,
        });
    }

    /// <summary>The project the facts are measured on: one entity excluding <c>salary</c>, and four after-hooks.</summary>
    protected static AlvoEventProject Project => new(Schema, Descriptor, Caller, Employees, "MMLib.Alvo.Events");

    private async Task<Dictionary<string, int>> DeliveredAfterAsync(Dictionary<string, object?> patch)
    {
        await using var world = await WorldAsync(Project);
        var created = await world.Data.CreateAsync(Employees, Hired(), Caller, cancellationToken: Ct);

        await world.Data.UpdateAsync(Employees, (Guid)created[AlvoManagedColumns.Id]!, patch, Caller, cancellationToken: Ct);
        await world.DrainAsync();

        return new[] { SalaryMoved, SalaryStill, StatusMoved, StatusStill }.ToDictionary(
            url => url, url => world.Deliveries.Count(delivery => delivery.Url == url), StringComparer.Ordinal);
    }

    private const string Employees = "employees";
    private const string SalaryMoved = "https://receiver.test/salary-moved";
    private const string SalaryStill = "https://receiver.test/salary-still";
    private const string StatusMoved = "https://receiver.test/status-moved";
    private const string StatusStill = "https://receiver.test/status-still";
    private const string AuthenticatedOnly = "'authenticated' in @user.roles";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AlvoContext Caller { get; } = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
    };

    private static Dictionary<string, object?> Hired() => new(StringComparer.Ordinal)
    {
        ["status"] = "hired",
        ["salary"] = 4_000,
    };

    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "event-exclusion-suite",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Employees] = new()
            {
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["status"] = new() { Type = DescField.String },
                    ["salary"] = new() { Type = DescField.Integer },
                },
                Events = new EntityEvents { Exclude = ["salary"] },
                Rules = new AccessRules
                {
                    List = AuthenticatedOnly,
                    Get = AuthenticatedOnly,
                    Create = AuthenticatedOnly,
                    Update = AuthenticatedOnly,
                    Delete = AuthenticatedOnly,
                },
                Hooks = new EntityHooks
                {
                    AfterUpdate =
                    [
                        Hook("changed(salary)", "salary-moved"),
                        Hook("!changed(salary)", "salary-still"),
                        Hook("changed(status)", "status-moved"),
                        Hook("!changed(status)", "status-still"),
                    ],
                },
            },
        },
        Webhooks = new Webhooks
        {
            Endpoints = new Dictionary<string, WebhookEndpoint>(StringComparer.Ordinal)
            {
                ["salary-moved"] = Endpoint(SalaryMoved),
                ["salary-still"] = Endpoint(SalaryStill),
                ["status-moved"] = Endpoint(StatusMoved),
                ["status-still"] = Endpoint(StatusStill),
            },
        },
    };

    private static AfterHook Hook(string condition, string endpoint) =>
        new() { Condition = condition, Action = new WebhookAction { Endpoint = endpoint } };

    private static WebhookEndpoint Endpoint(string url) => new() { Url = url, SecretRef = "exclusion-secret" };

    private static SchemaModel Schema => new([
        new EntitySchema
        {
            Name = Employees,
            Fields =
            [
                new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "status", Type = SchemaField.String, Nullable = true },
                new FieldSchema { Name = "salary", Type = SchemaField.Integer, Nullable = true },
            ],
            EventImageExclusions = ["salary"],
        },
    ]);
}
