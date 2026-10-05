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

    // Observed apply behaviour that may surprise, pinned here as AGREEMENT between check and apply, not as an
    // endorsement of the outcome: `old.` is accepted in an afterCreate condition (while @user.roles and
    // @tenant.id are refused there). An empty source and one over 2000 characters are refused by the SCHEMA pass
    // (minLength / maxLength), whose paths read `#/entities/…`; for a mutate value that refusal is reported on the
    // action above the slot (a oneOf), which the check reports at the slot.
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
        ("rule", Orders + "/rules/list", new string('a', 2001)),
        ("rule", Orders + "/rules/list", new string('a', 2000)),
        ("rule", Orders + "/rules/list", "normalizePhone(note) == 'x'"),
        ("rule", Orders + "/rules/get", "trim(note) == 'x'"),
        ("beforeHook", BeforeCreate, "new.quantity > 0"),
        ("beforeHook", BeforeCreate, "old.status == 'open'"),
        ("beforeHook", BeforeUpdate, "old.status == 'open' && new.status == 'closed'"),
        ("beforeHook", BeforeCreate, "new.nope > 1"),
        ("beforeHook", BeforeCreate, "new.quantity >"),
        ("beforeHook", BeforeUpdate, "'dispatcher' in @user.roles"),
        ("beforeHook", BeforeCreate, "@tenant.id == 'x'"),
        ("beforeHook", BeforeCreate, "{}"),
        ("beforeHook", BeforeCreate, "normalizePhone(new.note) == '1'"),
        ("beforeHook", BeforeCreate, "size(new.title) > 3"),
        ("beforeHook", BeforeCreate, "trim(old.note) == 'x'"),
        ("beforeHook", BeforeUpdate, "normalisePhone(new.note) == 'x'"),
        ("afterHook", AfterCreate, "new.quantity > 5"),
        ("afterHook", AfterCreate, "@tenant.id == 'x'"),
        ("afterHook", AfterCreate, "'dispatcher' in @user.roles"),
        ("afterHook", AfterCreate, "@user.roles.size() > 0"),
        ("afterHook", AfterCreate, "old.status == 'open'"),
        ("afterHook", AfterCreate, "new.quantity >"),
        ("afterHook", AfterCreate, "null"),
        ("afterHook", AfterCreate, "normalizePhone(new.note) == 'x'"),
        ("afterHook", AfterCreate, "size(new.note) > 3"),
        ("afterHook", AfterCreate, "isMe(@user.id)"),
        ("afterHook", AfterCreate, "isMe(@tenant.id)"),
        ("afterHook", AfterCreate, "yes(isMe(@tenant.id))"),
        ("afterHook", AfterCreate, "yes('owner' in @user.roles)"),
        ("afterHook", AfterCreate, "!yes('owner' in @user.roles)"),
        ("afterHook", AfterCreate, "new.quantity > 1 && yes('owner' in @user.roles)"),
        ("beforeHook", BeforeCreate, "isMe(@user.id)"),
        ("mutate", Mutate + "note", "'closed'"),
        ("mutate", Mutate + "closed_at", "now()"),
        ("mutate", Mutate + "quantity", "'abc'"),
        ("mutate", Mutate + "note", "1"),
        ("mutate", Mutate + "note", "new.nope"),
        ("mutate", Mutate + "quantity", "1 +"),
        ("mutate", Mutate + "quantity", "new.quantity + 1"),
        ("mutate", Mutate + "note", "@user.roles"),
        ("mutate", Mutate + "quantity", ""),
        ("mutate", Mutate + "note", new string('a', 2001)),
        ("mutate", Mutate + "note", "normalizePhone(new.note)"),
        ("mutate", Mutate + "note", "trim(replace(new.title, '-', ' '))"),
        ("mutate", Mutate + "note", "normalizePhone(new.note, new.note)"),
        ("mutate", Mutate + "quantity", "math.abs(new.quantity)"),
        ("mutate", Mutate + "quantity", "math.round(new.price)"),
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
        ("computed", Computed, new string('a', 2001)),
        ("computed", Computed, "normalizePhone(note)"),
        ("computed", Computed, "math.round(price)"),
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
        var current = await WorkingCopyAsync(management);

        var verdict = await management.CheckExpressionAsync(
            Project, new ManagementExpressionCheck(current.DescriptorJson, slot, source), Ct);
        var applied = await ApplyDryRunErrorsAsync(management, current, slot, source);

        verdict.Findings.ShouldAllBe(f => IsAtOrUnder(f.Path, slot), $"{kind} {slot}: a finding outside the slot");
        Errors(verdict.Findings).ShouldBe(applied, $"{kind} {slot} = {source}");
        verdict.IsValid.ShouldBe(applied.Count == 0, $"{kind} {slot} = {source}");
        if (verdict.IsValid)
        {
            await ShouldIntroduceNoErrorAnywhereAsync(management, current, slot, source, $"{kind} {slot} = {source}");
        }
    }

    /// <summary>
    /// A valid verdict is only worth something if the candidate breaks nothing <b>outside</b> the slot either: apply's
    /// whole error set for the spliced document must add no error to apply's set for the working copy as it stands.
    /// </summary>
    private static async Task ShouldIntroduceNoErrorAnywhereAsync(
        IAlvoManagement management, ManagementDescriptor current, string slot, string source, string because)
    {
        var baseline = await ApplyAllErrorsAsync(management, current, current.DescriptorJson);
        var spliced = Splice(JsonNode.Parse(current.DescriptorJson)!, slot, source).ToJsonString();
        var candidate = await ApplyAllErrorsAsync(management, current, spliced);

        candidate.Except(baseline).ShouldBeEmpty($"a valid verdict, yet the candidate adds an error elsewhere: {because}");
    }

    /// <summary>Apply's dry run over <paramref name="descriptorJson"/>: every error it refuses with, or none.</summary>
    private static async Task<List<string>> ApplyAllErrorsAsync(
        IAlvoManagement management, ManagementDescriptor current, string descriptorJson)
    {
        try
        {
            await management.ApplyDescriptorAsync(
                Project, new ManagementApplyRequest(descriptorJson, current.Revision, DryRun: true), Ct);

            return [];
        }
        catch (DescriptorValidationException refused)
        {
            return Errors(refused.Result.Errors);
        }
    }

    /// <summary>
    /// A schema error elsewhere hides the slot's own verdict from apply (its rule pass runs only over a descriptor the
    /// schema accepts), so the check says "not judged" rather than a pass nobody earned.
    /// </summary>
    /// <param name="breakage">What is wrong elsewhere: a schema error, or a descriptor only the mapper refuses.</param>
    /// <param name="source">A source apply would accept or refuse were the descriptor not refused elsewhere.</param>
    /// <returns>A task that completes when both sides have answered.</returns>
    [Theory]
    [InlineData("title", "'dispatcher' in @user.roles")]
    [InlineData("title", "'amdin' in @user.roles")]
    [InlineData("integer-default", "'dispatcher' in @user.roles")]
    [InlineData("integer-default", "'amdin' in @user.roles")]
    [InlineData("enum-default", "'dispatcher' in @user.roles")]
    [InlineData("enum-default", "'amdin' in @user.roles")]
    [InlineData("audit-column", "'dispatcher' in @user.roles")]
    [InlineData("audit-column", "'amdin' in @user.roles")]
    public async Task A_refusal_elsewhere_is_refused_by_apply_and_not_judged_by_the_check(string breakage, string source)
    {
        var management = fixture.Management();
        var current = await WorkingCopyAsync(management);
        var root = JsonNode.Parse(current.DescriptorJson)!;
        BreakElsewhere(root, breakage);
        var broken = current with { DescriptorJson = root.ToJsonString() };

        var verdict = await management.CheckExpressionAsync(
            Project, new ManagementExpressionCheck(broken.DescriptorJson, Orders + "/rules/list", source), Ct);
        var refused = await Should.ThrowAsync<DescriptorValidationException>(() => management.ApplyDescriptorAsync(
            Project,
            new ManagementApplyRequest(Splice(root, Orders + "/rules/list", source).ToJsonString(), current.Revision, DryRun: true),
            Ct));

        refused.Result.Errors.ShouldNotBeEmpty("apply refuses the whole descriptor");
        verdict.IsValid.ShouldBeFalse();
        verdict.Findings.Single().Message.ShouldStartWith("Not checked yet");
    }

    /// <summary>A host function in a rule is refused once, with the recipe that works — not with a misleading operand error.</summary>
    /// <returns>A task that completes when the check has answered.</returns>
    [Fact]
    public async Task A_host_function_in_a_rule_is_refused_once_with_the_mutate_recipe()
    {
        var management = fixture.Management();
        var current = await WorkingCopyAsync(management);

        var verdict = await management.CheckExpressionAsync(
            Project, new ManagementExpressionCheck(current.DescriptorJson, Orders + "/rules/list", "normalizePhone(note) == 'x'"), Ct);

        var finding = verdict.Findings.ShouldHaveSingleItem();
        finding.Message.ShouldStartWith("'normalizePhone(...)' is not available in the Rule profile; it is available in Condition and Mutate.");
        finding.FixSuggestion.ShouldNotBeNull().ShouldContain("before-hook mutate");
    }

    /// <summary>
    /// Agreement is not correctness (both sides share the validator), so pin what an after-hook refuses and why:
    /// a call reads exactly what its arguments read, so the refusal names the one context value inside it.
    /// </summary>
    /// <param name="source">The after-hook condition.</param>
    /// <param name="refusedFor">The one context value the refusal names, or null when the condition is valid.</param>
    /// <returns>A task that completes when the check has answered.</returns>
    [Theory]
    [InlineData("normalizePhone(new.note) == 'x'", null)]
    [InlineData("size(new.note) > 3", null)]
    [InlineData("isMe(@user.id)", null)]
    [InlineData("isMe(@tenant.id)", "@tenant.id")]
    [InlineData("yes(isMe(@tenant.id))", "@tenant.id")]
    [InlineData("yes('owner' in @user.roles)", "@user.roles")]
    [InlineData("!yes('owner' in @user.roles)", "@user.roles")]
    [InlineData("new.quantity > 1 && yes('owner' in @user.roles)", "@user.roles")]
    public async Task An_after_hook_call_reads_only_the_context_its_arguments_name(string source, string? refusedFor)
    {
        var management = fixture.Management();
        var current = await WorkingCopyAsync(management);

        var verdict = await management.CheckExpressionAsync(
            Project, new ManagementExpressionCheck(current.DescriptorJson, AfterCreate, source), Ct);

        var messages = string.Join(" | ", verdict.Findings.Select(f => f.Message));
        verdict.IsValid.ShouldBe(refusedFor is null, messages);
        var reason = verdict.Findings.Where(f => f.Severity == DescriptorValidationSeverity.Error).ToList();
        if (refusedFor is not null)
        {
            reason.ShouldHaveSingleItem(messages).Message.ShouldStartWith($"This after-hook condition reads '{refusedFor}'");
        }
    }

    private static void BreakElsewhere(JsonNode root, string breakage)
    {
        var fields = root["entities"]![Orders.Split('/')[^1]]!["fields"]!;
        switch (breakage)
        {
            case "title": root["title"] = new string('x', 61); break;
            case "integer-default": fields["zz"] = JsonNode.Parse("{\"type\":\"integer\",\"default\":1e30}"); break;
            case "enum-default": fields["zz"] = JsonNode.Parse("{\"type\":\"enum\",\"values\":[\"a\"],\"default\":\"b\"}"); break;
            default:
                root["entities"]!["audited"] = JsonNode.Parse("{\"audit\":true,\"fields\":{\"created_at\":{\"type\":\"string\"}}}");
                break;
        }
    }

    /// <summary>The corpus reaches every kind and both outcomes in each, or the agreement above is vacuous.</summary>
    /// <returns>A task that completes when every case has been judged by apply.</returns>
    [Fact]
    public async Task The_corpus_reaches_both_outcomes_for_every_slot_kind()
    {
        var management = fixture.Management();
        var current = await WorkingCopyAsync(management);
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
            return ErrorsCausedBy(refused.Result.Errors, pointer);
        }
    }

    /// <summary>
    /// The errors at the slot, plus — for a mutate value, a <c>oneOf</c> whose schema failure is reported on the
    /// action <b>above</b> the slot — one marker for a schema error on an ancestor. The working copy has no schema
    /// error of its own, so any ancestor schema error is the candidate's.
    /// </summary>
    private static List<string> ErrorsCausedBy(IEnumerable<DescriptorValidationError> errors, string pointer)
    {
        var all = errors.ToList();
        var result = Errors(all.Where(f => IsAtOrUnder(f.Path, pointer)));
        if (all.Any(f => f.Path.StartsWith('#') && !IsAtOrUnder(f.Path, pointer)))
        {
            result.Add($"{pointer} :: schema refusal above the slot");
        }

        return result;
    }

    /// <summary>
    /// The stored descriptor with two <b>other</b> slots broken, as a dashboard working copy mid-edit is.
    /// </summary>
    /// <remarks>
    /// Every case that does not overwrite them therefore has an apply error <em>elsewhere</em> than its slot, so a
    /// check that returned whole-document findings, or filtered on the wrong prefix, disagrees with apply.
    /// </remarks>
    private static async Task<ManagementDescriptor> WorkingCopyAsync(IAlvoManagement management)
    {
        var stored = await management.GetDescriptorAsync(Project, Ct);
        var root = JsonNode.Parse(stored.DescriptorJson)!;
        Splice(root, Orders + "/rules/delete", "'nobody' in @user.roles");
        Splice(root, Orders + "/rules/get", "quantity > 'x'");

        return stored with { DescriptorJson = root.ToJsonString() };
    }

    /// <summary>
    /// Whether <paramref name="path"/> is the slot or lies under it — this file's own path logic. The schema pass
    /// reports <c>#/entities/…</c> (a URI fragment), every other pass <c>/entities/…</c>; both name the same node.
    /// </summary>
    private static bool IsAtOrUnder(string path, string slot)
    {
        var pointer = path.TrimStart('#');

        return pointer == slot || pointer.StartsWith(slot + "/", StringComparison.Ordinal);
    }

    /// <summary>The sorted (path, message) key of every error given, <b>unfiltered by slot</b>.</summary>
    private static List<string> Errors(IEnumerable<DescriptorValidationError> findings) =>
        [.. findings
            .Where(f => f.Severity == DescriptorValidationSeverity.Error)
            .Select(f => f.Message.StartsWith("The schema refuses this value", StringComparison.Ordinal)
                ? $"{f.Path} :: schema refusal above the slot"
                : $"{f.Path} :: {f.Message}")
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
                new AlvoApiWorldSetup(
                    MapBeforePriming: true,
                    MapManagementApi: true,
                    ConfigureServicesAfterAlvo: services =>
                    {
                        CelFunctionsWorld.Register(services, phone => phone);
                        CelFunctionsWorld.RegisterContextProbes(services);
                    }));

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
