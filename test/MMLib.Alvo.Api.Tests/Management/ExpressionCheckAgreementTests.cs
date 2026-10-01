using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Management;
using System.Globalization;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// The expression check answers what apply answers, slot kind by slot kind.
/// </summary>
/// <remarks>
/// <para>
/// <b>The dashboard shows a verdict per keystroke and the operator acts on it.</b> A check that said "fine" for
/// something apply then refuses, or refused what apply accepts, would be a second opinion that drifts. So this asks
/// both the same question over a corpus that reaches every slot kind: the real <c>ApplyDescriptorAsync</c> dry run
/// is the oracle, and the claim is that the <b>set of errors at the slot</b> is identical.
/// </para>
/// <para>
/// <b>The apply side never goes through the check's code.</b> The splice here is this file's own and shares nothing
/// with the production splice, so a bug in one cannot hide in the other; the only thing the two sides share is the
/// validator, which is exactly the thing the check is claimed to be.
/// </para>
/// </remarks>
public sealed class ExpressionCheckAgreementTests(ExpressionCheckAgreementTests.World fixture)
    : IClassFixture<ExpressionCheckAgreementTests.World>
{
    private const string Project = "expression-agreement";
    private const string Orders = "/entities/orders";
    private const string BeforeCreate = Orders + "/hooks/beforeCreate/0/condition";
    private const string BeforeUpdate = Orders + "/hooks/beforeUpdate/0/condition";
    private const string AfterCreate = Orders + "/hooks/afterCreate/0/condition";
    private const string Mutate = Orders + "/hooks/beforeUpdate/0/action/mutate/";
    private const string Computed = Orders + "/fields/line_total/computed";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Every slot kind, with accepted and refused sources and a few odd-but-parseable shapes.</summary>
    /// <returns>(slot kind, pointer, source) rows.</returns>
    public static TheoryData<string, string, string> Corpus()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (kind, pointer, source) in _cases)
        {
            data.Add(kind, pointer, source);
        }

        return data;
    }

    private static readonly (string Kind, string Pointer, string Source)[] _cases =
    [
        ("rule", Orders + "/rules/list", "'dispatcher' in @user.roles"),
        ("rule", Orders + "/rules/list", "'amdin' in @user.roles"),
        ("rule", Orders + "/rules/get", "status == 'open'"),
        ("rule", Orders + "/rules/create", "'dispatcher' in @user.roles ||"),
        ("rule", Orders + "/rules/list", "old.status == 'open'"),
        ("rule", Orders + "/rules/update", "'owner' in @user.roles && quantity > 3"),
        ("rule", Orders + "/rules/delete", "@user.id == owner_id"),
        ("rule", Orders + "/rules/get", "quantity > 'x'"),
        ("rule", Orders + "/rules/list", ""),
        ("rule", Orders + "/rules/list", "   "),
        ("rule", Orders + "/rules/create", "((((((((((true))))))))))"),
        ("rule", Orders + "/rules/delete", "[1, 2, 3]"),
        ("beforeHook", BeforeCreate, "new.quantity > 0"),
        ("beforeHook", BeforeCreate, "old.status == 'open'"),
        ("beforeHook", BeforeUpdate, "old.status == 'open' && new.status == 'closed'"),
        ("beforeHook", BeforeCreate, "new.nope > 1"),
        ("beforeHook", BeforeCreate, "new.quantity >"),
        ("beforeHook", BeforeUpdate, "'dispatcher' in @user.roles"),
        ("beforeHook", BeforeCreate, "@tenant.id == 'x'"),
        ("beforeHook", BeforeCreate, "{}"),
        ("afterHook", AfterCreate, "new.quantity > 5"),
        ("afterHook", AfterCreate, "@tenant.id == 'x'"),
        ("afterHook", AfterCreate, "'dispatcher' in @user.roles"),
        ("afterHook", AfterCreate, "@user.roles.size() > 0"),
        ("afterHook", AfterCreate, "old.status == 'open'"),
        ("afterHook", AfterCreate, "new.quantity >"),
        ("afterHook", AfterCreate, "null"),
        ("mutate", Mutate + "note", "'closed'"),
        ("mutate", Mutate + "closed_at", "now()"),
        ("mutate", Mutate + "quantity", "'abc'"),
        ("mutate", Mutate + "note", "1"),
        ("mutate", Mutate + "note", "new.nope"),
        ("mutate", Mutate + "quantity", "1 +"),
        ("mutate", Mutate + "quantity", "new.quantity + 1"),
        ("mutate", Mutate + "note", "@user.roles"),
        ("mutate", Mutate + "quantity", ""),
        ("computed", Computed, "quantity * price"),
        ("computed", Computed, "quantity * price > 10"),
        ("computed", Computed, "(quantity + 1) * 2 > price"),
        ("computed", Computed, "quantity * 100"),
        ("computed", Computed, "1 + 2"),
        ("computed", Computed, "now()"),
        ("computed", Computed, "quantity *"),
        ("computed", Computed, "nope * 2"),
        ("computed", Computed, "title + note"),
        ("computed", Computed, "quantity == 1 ? price : price * 2"),
        ("computed", Computed, "'x'"),
    ];

    /// <summary>For every case, the check's error set equals apply's, restricted to the slot.</summary>
    /// <param name="kind">The slot kind, for the failure message.</param>
    /// <param name="slot">The slot's RFC 6901 pointer.</param>
    /// <param name="source">The candidate source.</param>
    /// <returns>A task that completes when both sides have answered.</returns>
    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task Check_and_apply_refuse_the_same_expression_the_same_way(string kind, string slot, string source)
    {
        var management = fixture.Management();
        var current = await management.GetDescriptorAsync(Project, Ct);

        var verdict = await management.CheckExpressionAsync(
            Project, new ManagementExpressionCheck(current.DescriptorJson, slot, source), Ct);
        var applied = await ApplyDryRunErrorsAsync(management, current, slot, source);

        Errors(verdict.Findings, slot).ShouldBe(applied, $"{kind} {slot} = {source}");
        verdict.IsValid.ShouldBe(applied.Count == 0, $"{kind} {slot} = {source}");
    }

    /// <summary>The corpus reaches every kind and both outcomes in each, or the agreement above is vacuous.</summary>
    /// <returns>A task that completes when every case has been judged by apply.</returns>
    [Fact]
    public async Task The_corpus_reaches_both_outcomes_for_every_slot_kind()
    {
        var management = fixture.Management();
        var current = await management.GetDescriptorAsync(Project, Ct);
        var outcomes = new List<(string Kind, bool Refused)>();
        foreach (var (kind, pointer, source) in _cases)
        {
            outcomes.Add((kind, (await ApplyDryRunErrorsAsync(management, current, pointer, source)).Count > 0));
        }

        _cases.Length.ShouldBeGreaterThanOrEqualTo(20);
        foreach (var group in outcomes.GroupBy(o => o.Kind))
        {
            group.Count().ShouldBeGreaterThanOrEqualTo(3, group.Key);
            group.Any(o => o.Refused).ShouldBeTrue($"{group.Key} has no refused case");
            group.Any(o => !o.Refused).ShouldBeTrue($"{group.Key} has no accepted case");
        }

        outcomes.Select(o => o.Kind).Distinct().Count().ShouldBe(5);
    }

    /// <summary>Apply's own dry run over the spliced descriptor; the errors it throws at the slot, or none.</summary>
    private static async Task<List<string>> ApplyDryRunErrorsAsync(
        IAlvoManagement management, ManagementDescriptor current, string pointer, string source)
    {
        var spliced = Splice(JsonNode.Parse(current.DescriptorJson)!, pointer, source).ToJsonString();
        try
        {
            await management.ApplyDescriptorAsync(
                Project, new ManagementApplyRequest(spliced, current.Revision, DryRun: true), Ct);

            return [];
        }
        catch (DescriptorValidationException refused)
        {
            return Errors(refused.Result.Errors, pointer);
        }
    }

    /// <summary>The (path, message) of each error at or under the slot, sorted — this file's own restriction.</summary>
    private static List<string> Errors(IEnumerable<DescriptorValidationError> findings, string pointer) =>
        [.. findings
            .Where(f => f.Severity == DescriptorValidationSeverity.Error)
            .Where(f => f.Path == pointer || f.Path.StartsWith(pointer + "/", StringComparison.Ordinal))
            .Select(f => $"{f.Path} :: {f.Message}")
            .Order(StringComparer.Ordinal)];

    /// <summary>This file's own splice: a mutate target holds <c>{"$cel": source}</c>, every other slot a string.</summary>
    private static JsonNode Splice(JsonNode root, string pointer, string source)
    {
        var segments = pointer.TrimStart('/').Split('/')
            .Select(s => s.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))
            .ToArray();
        var node = root;
        foreach (var segment in segments[..^1])
        {
            node = node is JsonArray array ? array[int.Parse(segment, CultureInfo.InvariantCulture)]! : node[segment]!;
        }

        JsonNode value = pointer.Contains("/action/mutate/", StringComparison.Ordinal)
            ? new JsonObject { ["$cel"] = source }
            : JsonValue.Create(source)!;
        if (node is JsonArray target)
        {
            target[int.Parse(segments[^1], CultureInfo.InvariantCulture)] = value;
        }
        else
        {
            node[segments[^1]] = value;
        }

        return root;
    }

    /// <summary>One running project, shared by every case: a dry run appends nothing.</summary>
    public sealed class World : IAsyncLifetime
    {
        private AlvoApiWorld? _world;

        /// <inheritdoc/>
        public async ValueTask InitializeAsync() =>
            _world = await AlvoApiWorld.FromDescriptorAsync(
                "expression-agreement.alvo.json",
                [],
                new AlvoApiWorldSetup(MapBeforePriming: true, MapManagementApi: true));

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            if (_world is not null)
            {
                await _world.DisposeAsync();
            }
        }

        /// <summary>The management contract, called as the project's owner.</summary>
        /// <returns>The registered contract with an owner published.</returns>
        public IAlvoManagement Management() => ManagementInProcessAccessTests.Publish(_world!, "owner");
    }
}
