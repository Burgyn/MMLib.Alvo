# F5 — the schema assistant gets a change right on the first try: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** the assistant's instructions state the name rule and the framework-managed columns (drift-tested against
the schema and `AlvoManagedColumns`), its replies say *proposed* and never *created/applied*, in the operator's
language, and the eval grades all three plus two new cases from the #289 transcript.

**Architecture:** no new runtime type and no public symbol. The instructions (`src/MMLib.Alvo.Ai/Instructions/schema-assistant.md`)
gain facts; a small shared test parser (`test/_shared/ai/InstructionClaims.cs`) reads them for the two suites that
check them — `MMLib.Alvo.Ai.Tests` against the schema and `AlvoManagedColumns`, `MMLib.Alvo.Host.Tests` against the
real validator and `ReservedQueryKeys`. The eval (`eval/MMLib.Alvo.Ai.Eval`, in no ring) gains two pure graders
applied to every turn, two cases, and an `InterferingChatClient` that forces a concurrent apply mid-turn.

**Tech Stack:** .NET 10, `System.Text.Json.Nodes`, Microsoft.Extensions.AI (`DelegatingChatClient`), xUnit v3 on
MTP, Shouldly.

**Spec:** `docs/superpowers/specs/2026-09-29-f5-assistant-first-try-design.md` (#289). It builds on
`docs/superpowers/specs/2026-09-28-f5-assistant-reliability-design.md`, whose tool surface, budget (3), iteration
cap (12), instructions and eval are built and unchanged; deviations D15–D21 are the spec's. Tasks 5–10 build the
spec's §7, *Skills (scope extension, 2026-09-29)*, and its deviations D22–D36.

## Global Constraints

- **Security literals, unchanged:** no tool writes; every tool dry run stays
  `new ManagementApplyRequest(json, revision, AllowDestructive: false, DryRun: true)`. The only real apply this plan
  adds is the eval's own simulated second operator (Task 4), outside the assistant, in no ring.
- **Tool set, caps, budget text: unchanged** (reliability plan's Global Constraints).
- **Instructions version line** becomes `<!-- alvo-schema-assistant v3 -->` (Task 1), in the resource *and*
  `AssistantInstructions.VersionLine`.
- **Public API:** no new `public` symbol; `PublicApi.*.verified.txt` must not move. `MMLib.Alvo.Admin` is not touched
  (D18) — if an implementer finds a reason to touch it, stop and ask; touching it also makes
  `scripts/test-admin-e2e` (whole) a gate.
- **Boundary:** `MMLib.Alvo.Ai.Tests` reaches Abstractions only (`AlvoManagedColumns`, `TenancyMode` are public
  there); anything needing the core (`ReservedQueryKeys`, the validator) lives in `MMLib.Alvo.Host.Tests`.
- **Code style** (`alvo-dotnet-conventions`): `.cs` UTF-8 **with BOM**, **CRLF**; zero inline comments; methods
  ≤ ~25 lines; `///` docs in the surrounding style. A file written by a shell tool is normalised before commit.
- **Windows CI checks out CRLF:** every regex over the Markdown resource ends a line with `\r?$` or excludes `\r`
  from a captured class (`[^\r\n]`); never `.Matches(...).Count` (CA1875 → `Regex.Count`).
- **CI builds tests in Release with analyzers as errors:** interpolate a culture-sensitive value through
  `string.Create(CultureInfo.InvariantCulture, …)` as `CaseRun.Line` does.
- **Gates:** every task ends with `scripts/test-ring1`. Task 4 additionally runs `scripts/test-ring2` and
  `dotnet build MMLib.Alvo.slnx -c Release -warnaserror`.
- **Tasks 5–10** add their own constraints (*Global Constraints, added for Tasks 5–10*, below). These cover:
  - the Release/Windows analyzer traps this branch hit (CA1861, CA1875, CA1859, CA1870, IDE0065);
  - CRLF-safe text;
  - `$$` raw strings that end in a brace run;
  - the per-task `-c Release -warnaserror` build.
- **Commits:** Conventional Commits, one per task, each message ending with the line
  `Claude-Session: https://claude.ai/code/session_01TrcGmun8WAuVxN5FYAtu6H`. Never push; never switch branch
  (`f5/assistant-first-try`); one writer in the worktree.

---

### Task 1: Names and managed columns in the instructions, drift-tested and probed

**Files:**
- Modify: `src/MMLib.Alvo.Ai/Instructions/schema-assistant.md` (version line; §3)
- Modify: `src/MMLib.Alvo.Ai/Internal/AssistantInstructions.cs` (`VersionLine`)
- Create: `test/_shared/ai/InstructionClaims.cs`
- Modify: `test/MMLib.Alvo.Ai.Tests/MMLib.Alvo.Ai.Tests.csproj`, `test/MMLib.Alvo.Host.Tests/MMLib.Alvo.Host.Tests.csproj` (link it)
- Modify: `test/MMLib.Alvo.Ai.Tests/AssistantInstructionsTests.cs`
- Create: `test/MMLib.Alvo.Host.Tests/InstructionNameClaimsTests.cs`
- Modify: `schema/project.schema.json` (the `fields` description only)

**Interfaces:**
```csharp
namespace MMLib.Alvo.Ai.Tests;
internal static partial class InstructionClaims
{
    internal static string NamePattern(string markdown);                                       // the pattern on "- **Names**:"
    internal static IReadOnlyList<string> ReservedFields(string markdown);                     // backticked after "the fields"
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> ManagedColumns(string markdown); // label → columns
}
```

- [ ] **Step 1: Write the failing drift tests (Ai.Tests).** Add to `AssistantInstructionsTests`:

```csharp
[Fact]
public void The_name_rule_is_the_schemas_entity_and_field_name_pattern()
{
    var schema = Schema();

    InstructionClaims.NamePattern(_text).ShouldSatisfyAllConditions(
        stated => stated.ShouldBe(schema["properties"]!["entities"]!["propertyNames"]!["pattern"]!.GetValue<string>()),
        stated => stated.ShouldBe(schema["$defs"]!["entity"]!["properties"]!["fields"]!["propertyNames"]!["pattern"]!.GetValue<string>()));
}

[Fact]
public void The_managed_columns_are_the_ones_the_framework_injects_for_each_trait()
{
    var stated = InstructionClaims.ManagedColumns(_text);
    var always = AlvoManagedColumns.For(null, audit: false, softDelete: false);

    stated.Keys.ShouldBe(_traitLabels);
    stated[_traitLabels[0]].ShouldBe(always, ignoreOrder: true);
    stated[_traitLabels[1]].ShouldBe(Beyond(AlvoManagedColumns.For(TenancyMode.Scoped, audit: false, softDelete: false), always), ignoreOrder: true);
    stated[_traitLabels[2]].ShouldBe(AlvoManagedColumns.Audit);
    stated[_traitLabels[3]].ShouldBe(Beyond(AlvoManagedColumns.For(null, audit: false, softDelete: true), always), ignoreOrder: true);
    stated.Values.SelectMany(columns => columns)
        .ShouldBe(AlvoManagedColumns.For(TenancyMode.Scoped, audit: true, softDelete: true), ignoreOrder: true);
}

private static readonly string[] _traitLabels =
[
    "on every entity", "on an entity whose `tenancy` is `scoped`", "on an entity with `\"audit\": true`",
    "on an entity with `\"softDelete\": true`",
];

private static IEnumerable<string> Beyond(IReadOnlySet<string> columns, IReadOnlySet<string> always) =>
    columns.Where(column => !always.Contains(column));

private static JsonNode Schema() =>
    JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "schema", "project.schema.json")))!;
```

  Also, in the same file: add `"customer_audits"` to `_illustrativeNames`, and in `KnownNames` union in
  `AlvoManagedColumns.For(TenancyMode.Scoped, audit: true, softDelete: true)` (the names the new bullets put in
  code spans). Add `using MMLib.Alvo.Schema;`. Have `The_field_types_are_the_schemas_field_types` use `Schema()`.

- [ ] **Step 2: Create the shared parser** `test/_shared/ai/InstructionClaims.cs` (BOM, CRLF):

```csharp
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>
/// The facts the instructions state about names — the pattern, the reserved names and the managed columns — as data,
/// so one suite can hold them to the schema and the ports, and another to the real validator.
/// </summary>
internal static partial class InstructionClaims
{
    private const string FieldsMarker = "the fields";

    internal static string NamePattern(string markdown) =>
        Pattern().Match(Line(markdown, "- **Names**:")).Groups["pattern"].Value;

    internal static IReadOnlyList<string> ReservedFields(string markdown)
    {
        var line = Line(markdown, "- Reserved names:");
        return [.. Token().Matches(line[line.IndexOf(FieldsMarker, StringComparison.Ordinal)..]).Select(match => match.Groups["token"].Value)];
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> ManagedColumns(string markdown) =>
        TraitBullet().Matches(markdown).ToDictionary(
            match => match.Groups["label"].Value,
            match => (IReadOnlyList<string>)[.. Token().Matches(match.Groups["columns"].Value).Select(token => token.Groups["token"].Value)],
            StringComparer.Ordinal);

    private static string Line(string markdown, string prefix) =>
        markdown.Split('\n').Single(line => line.StartsWith(prefix, StringComparison.Ordinal)).TrimEnd('\r');

    [GeneratedRegex(@"`(?<pattern>\^[^`]+\$)`", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    [GeneratedRegex("`(?<token>[a-z][a-z_]*)`", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex(@"^  - (?<label>on [^\r\n]+?) — (?<columns>[^\r\n]+)\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TraitBullet();
}
```

  Link it beside `InstructionExamples.cs` in both csprojs:
  `<Compile Include="$(MSBuildThisFileDirectory)../_shared/ai/InstructionClaims.cs" Link="_shared/InstructionClaims.cs" />`.

- [ ] **Step 3: Run to see them fail.** `dotnet test --project test/MMLib.Alvo.Ai.Tests` → the two new facts fail
  (`Single` finds no `- **Names**:` line; the dictionary is empty).

- [ ] **Step 4: Write the instructions.** Line 1 → `<!-- alvo-schema-assistant v3 -->`; `VersionLine` likewise. In §3,
  directly after the `- Entities live at …` bullet, insert:

```markdown
- **Names**: every entity and field name matches `^[a-z][a-z0-9_]{0,62}$` — lower-case snake_case that starts with a
  letter: `customer_audits`, never `CustomerAudits` or `customer-audits`. Turn the operator's words into such a name
  yourself; do not ask.
- Reserved names: the entity `users` (the built-in auth entity; a `ref` may still point at it), and the fields `order`, `limit`, `offset`, `after`, `select`, `or`, `and`, `not`, which the Data API's query string uses.
- **Framework-managed columns** — never declare them: the framework adds them from the entity's traits, and a
  declaration is refused. To give an entity these columns, set its trait.
  - on every entity — `id`
  - on an entity whose `tenancy` is `scoped` — `tenant_id`
  - on an entity with `"audit": true` — `created_at`, `created_by`, `updated_at`, `updated_by`
  - on an entity with `"softDelete": true` — `deleted_at`
```

  The *Reserved names* bullet stays on **one line**: `InstructionClaims.Line` reads a single line.

- [ ] **Step 5: Correct the schema's `fields` description** (`schema/project.schema.json`, `$defs.entity.properties.fields.description`):
  replace *"The `id` field (uuid, PK) is added automatically when not defined."* with *"The `id` field (uuid, PK) is
  always added by the framework and cannot be declared, nor can the columns the entity's traits add (`tenant_id`,
  the audit columns, `deleted_at`)."* Prose only — no keyword moves (spec §4).

- [ ] **Step 6: Run Ai.Tests** → green, including the existing snake-case, tool-name and fence facts over the new text.

- [ ] **Step 7: Write the probes (Host.Tests)** — `test/MMLib.Alvo.Host.Tests/InstructionNameClaimsTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Ai.Internal;
using MMLib.Alvo.Ai.Tests;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;
using MMLib.Alvo.Schema;

using System.Text.Json;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// Every name the instructions call refused is one the real validator refuses, and the trait scoping they state holds
/// the other way too — the name half of the drift the Computed quotes already have.
/// </summary>
/// <remarks>
/// Each probe adds an entity of its own, so no probe depends on which traits <c>bike-workshop</c> happens to declare.
/// <c>tenant_id</c> is not probed: a scoped entity needs project tenancy, which the example does not enable; its
/// membership is held to <see cref="AlvoManagedColumns"/> by <c>AssistantInstructionsTests</c>.
/// </remarks>
public sealed class InstructionNameClaimsTests
{
    private const string Project = "bike-workshop";
    private const string ManagedRefusal = "framework-managed column";
    private const string ReservedRefusal = "reserved";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_reserved_fields_the_instructions_list_are_the_data_apis_reserved_query_keys() =>
        InstructionClaims.ReservedFields(AssistantInstructions.Text).ShouldBe(ReservedQueryKeys.All, ignoreOrder: true);

    [Fact]
    public async Task Every_name_the_instructions_call_refused_is_refused_by_the_validator()
    {
        await using var world = await AlvoHostWorld.StartAsync(BikeWorkshop);
        var management = AsAdministrator(world);

        foreach (var (operations, fragment) in RefusedProbes())
        {
            var attempt = await AttemptAsync(management, operations);

            attempt.Valid.ShouldBeFalse($"accepted: {operations}");
            if (fragment is not null)
            {
                attempt.Violations.ShouldContain(violation => violation.Message.Contains(fragment, StringComparison.Ordinal), operations);
            }
        }
    }

    [Fact]
    public async Task The_rules_positive_forms_are_accepted()
    {
        await using var world = await AlvoHostWorld.StartAsync(BikeWorkshop);
        var management = AsAdministrator(world);

        foreach (var operations in new[] { Entity("customer_audits", "{}"), Entity("probe_plain", """{"created_at": {"type": "datetime"}}""") })
        {
            var attempt = await AttemptAsync(management, operations);
            attempt.Valid.ShouldBeTrue($"{operations}: {string.Join(" | ", attempt.Violations.Select(violation => violation.Message))}");
        }
    }

    private static IEnumerable<(string Operations, string? Fragment)> RefusedProbes()
    {
        var text = AssistantInstructions.Text;
        var columns = InstructionClaims.ManagedColumns(text);
        yield return (Entity("CustomerAudits", "{}"), null);
        yield return (Entity("customer-audits", "{}"), null);
        yield return (Entity("users", "{}"), null);
        foreach (var field in InstructionClaims.ReservedFields(text))
        {
            yield return (Entity("probe_reserved", $$"""{"{{field}}": {"type": "string"}}"""), ReservedRefusal);
        }

        yield return (Entity("probe_id", """{"id": {"type": "uuid"}}"""), ManagedRefusal);
        foreach (var column in columns["on an entity with `\"audit\": true`"])
        {
            yield return (Entity("probe_audit", $$"""{"{{column}}": {"type": "datetime"}}""", """ "audit": true, """), ManagedRefusal);
        }

        yield return (Entity("probe_soft", """{"deleted_at": {"type": "datetime"}}""", """ "softDelete": true, """), ManagedRefusal);
    }

    /// <summary>One <c>add</c> of a new entity: a plain <c>label</c> field beside the probed ones, and any traits.</summary>
    private static string Entity(string name, string fields, string traits = "") =>
        $$"""[{"op": "add", "path": "/entities/{{name}}", "value": {{{traits}} "fields": {{MergedFields(fields)}}}}]""";

    private static string MergedFields(string fields) =>
        fields == "{}" ? """{"label": {"type": "string"}}""" : fields.Insert(1, """ "label": {"type": "string"}, """);

    private static IAlvoManagement AsAdministrator(AlvoHostWorld world)
    {
        world.Services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = new AlvoContext { User = UserId.New(), Roles = new HashSet<Role> { Role.Admin } },
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "instruction-name-claims",
        };
        return world.Services.GetRequiredService<IAlvoManagement>();
    }

    private static string BikeWorkshop { get; } =
        Path.Combine(RepositoryRoot.Find(), "examples", "bike-workshop", "bike-workshop.alvo.json");

    private static async Task<DraftAttempt> AttemptAsync(IAlvoManagement management, string operations)
    {
        var current = await management.GetDescriptorAsync(Project, Ct);
        using var document = JsonDocument.Parse(operations);
        return await DescriptorDraft.BuildAsync(management, Project, current.Revision, document.RootElement.Clone(), Ct);
    }
}
```

  The principal is published on the test's own async flow before the first call, as `InstructionExampleOutcomeTests`
  does; the world is disposed by each fact's `await using`, never returned out of a helper.

- [ ] **Step 8: Run Host.Tests** → green. If a probe's refusal lands only on another rule (e.g. the pattern probe's
  pointer), the `null` fragment already accepts any refusal; do **not** weaken a fragment to make a probe pass —
  a missing fragment means the instructions claim a refusal the validator does not give, which is the defect.
- [ ] **Step 9:** `scripts/test-ring1` → green. Commit `feat(ai): the instructions state the name rule and the managed columns, drift-tested`.

---

### Task 2: "Proposed, never done" — the rule, the examples' replies, and a grader they must pass

**Files:**
- Modify: `src/MMLib.Alvo.Ai/Instructions/schema-assistant.md` (§6 replies, §7)
- Create: `eval/MMLib.Alvo.Ai.Eval/ProposalWording.cs`
- Create: `test/MMLib.Alvo.Ai.Eval.Tests/ProposalWordingTests.cs`
- Create: `test/MMLib.Alvo.Ai.Eval.Tests/InstructionRepliesTests.cs`

**Interfaces:**
```csharp
namespace MMLib.Alvo.Ai.Eval;
internal static partial class ProposalWording
{
    internal static bool SaysProposed(string prose);
    internal static bool ClaimsDone(string prose);
    internal static Verdict Judge(TurnRecord turn);   // over ReplyText.OwnProse; SaysProposed required only with a valid proposal
}
```

- [ ] **Step 1: Failing grader tests** — `ProposalWordingTests.cs`:

```csharp
namespace MMLib.Alvo.Ai.Eval.Tests;

using static MMLib.Alvo.Ai.Eval.Tests.Turns;

/// <summary>The wording grader passes "proposed, apply it from Preview" and fails the transcript's "is created".</summary>
public sealed class ProposalWordingTests
{
    [Theory]
    [InlineData("The entity customers_audit is created.")]
    [InlineData("Entita customers_audit je vytvorená.")]
    [InlineData("Pojďme dál: entita je vytvořena.")]
    [InlineData("The change has been applied.")]
    [InlineData("I've applied the change.")]
    [InlineData("Zmenu som aplikoval.")]
    public void A_reply_that_says_the_change_happened_claims_done(string prose) =>
        ProposalWording.ClaimsDone(prose).ShouldBeTrue();

    [Theory]
    [InlineData("I proposed customers_audit; nothing is applied until you apply it from Preview.")]
    [InlineData("Navrhol som entitu customers_audit. Aplikujte ju v Preview.")]
    [InlineData("Nothing has been applied.")]
    [InlineData("It will be created once you apply it from Preview.")]
    [InlineData("Nič ešte nie je aplikované.")]
    public void A_reply_that_leaves_the_apply_to_the_operator_does_not(string prose) =>
        ProposalWording.ClaimsDone(prose).ShouldBeFalse();

    [Theory]
    [InlineData("I proposed a notes field.", true)]
    [InlineData("Navrhol som pole notes.", true)]
    [InlineData("Návrh je pripravený v Preview.", true)]
    [InlineData("The notes field is ready.", false)]
    public void Saying_proposed_is_read_in_both_languages(string prose, bool says) =>
        ProposalWording.SaysProposed(prose).ShouldBe(says);

    [Fact]
    public void A_valid_proposal_answered_with_done_fails() =>
        ProposalWording.Judge(Turn(Original, "Done.", calls: Propose(Valid()))).Passed.ShouldBeFalse();

    [Fact]
    public void A_turn_with_no_proposal_need_not_say_proposed() =>
        ProposalWording.Judge(Turn(answer: "This build cannot run automation.")).Passed.ShouldBeTrue();

    [Fact]
    public void A_done_claim_inside_a_quoted_refusal_is_not_the_models() =>
        ProposalWording.Judge(Turn(answer: "> The change was applied elsewhere.\nI proposed nothing.",
            calls: Propose(Refused("validation", "The change was applied elsewhere.")))).Passed.ShouldBeTrue();
}
```

- [ ] **Step 2: Run** `dotnet test --project test/MMLib.Alvo.Ai.Eval.Tests` → does not compile (no `ProposalWording`).

- [ ] **Step 3: Implement** `eval/MMLib.Alvo.Ai.Eval/ProposalWording.cs`:

```csharp
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>Whether a reply keeps to "proposed, never done": the assistant files a proposal, the operator applies it.</summary>
/// <remarks>
/// A tripwire over the forms the #289 transcript and its languages use — a state or completion verb after a copula or a
/// first person — not an understanding of the sentence. A negation just before it ("nothing is applied", "nie je
/// aplikované") is the operator-applies wording the rule asks for, so it is excluded. Future tense ("will be created
/// once you apply it") never matches: it is the right thing to say.
/// </remarks>
internal static partial class ProposalWording
{
    private static readonly string[] _proposalStems = ["propos", "návrh", "navrh"];

    internal static bool SaysProposed(string prose) =>
        _proposalStems.Any(stem => prose.Contains(stem, StringComparison.OrdinalIgnoreCase));

    internal static bool ClaimsDone(string prose) => Done().IsMatch(prose);

    internal static Verdict Judge(TurnRecord turn)
    {
        var prose = ReplyText.OwnProse(turn.Answer, turn.FrameworkTexts);
        var claimsDone = ClaimsDone(prose);
        var saysProposed = SaysProposed(prose);
        return Verdict.When(
            !claimsDone && (saysProposed || !turn.HasValidProposal),
            $"claimsDone={claimsDone} saysProposed={saysProposed}");
    }

    [GeneratedRegex(
        @"(?<!\b(?:nothing|not|never|nič|nie|nic|ne)\s+)\b(?:"
        + @"(?:is|are|was|were|has\s+been|have\s+been)\s+(?:now\s+|already\s+)?(?:created|applied|live|in\s+place)\b"
        + @"|I(?:'|’)?ve\s+applied\b|I\s+applied\b"
        + @"|(?:je|sú|bol|bola|bolo|boli)\s+(?:teraz\s+|už\s+)?(?:vytvoren|aplikovan|nasaden)\w*"
        + @"|(?:aplikoval|nasadil)a?\s+som\b|som\s+(?:aplikoval|nasadil)"
        + @"|(?:je|jsou|byl|byla|bylo|byly)\s+(?:nyní\s+|už\s+)?(?:vytvořen|aplikován|nasazen)\w*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Done();
}
```

  Note the negative look-behind covers *"Nič ešte nie je aplikované"* only through `nie` directly before `je`; keep
  that fixture and extend the look-behind (not the fixture) if it fails.

- [ ] **Step 4: Run** → the grader tests pass.

- [ ] **Step 5: Failing reply test** — `InstructionRepliesTests.cs`:

```csharp
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>The worked examples' replies are what models copy, so they must pass the grader the eval applies to models.</summary>
public sealed partial class InstructionRepliesTests
{
    private const int WorkedReplies = 8;

    private static readonly string _instructions = File.ReadAllText(
        Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo.Ai", "Instructions", "schema-assistant.md"));

    [Fact]
    public void Every_worked_example_reply_says_proposed_and_never_claims_done()
    {
        var replies = Reply().Matches(_instructions).Select(match => match.Groups["reply"].Value).ToList();

        replies.Count.ShouldBeGreaterThanOrEqualTo(WorkedReplies);
        replies.ShouldAllBe(reply => ProposalWording.SaysProposed(reply) && !ProposalWording.ClaimsDone(reply));
    }

    [GeneratedRegex(@"Reply:[^*]*\*(?<reply>[^*]+)\*", RegexOptions.CultureInvariant)]
    private static partial Regex Reply();
}
```

  Run → fails on (a), (b), (c), (d), (e retry), (f multiplier), (g): none says *proposed*.

- [ ] **Step 6: Rewrite the replies** (the italic text after each `Reply:`, nothing else in the example):
  - (a) *I proposed an optional `notes` text field on bikes; nothing changes until you apply it from Preview. Once
    applied, a caller may send `notes` on create and update, nothing accepted before is rejected, and existing bikes
    start with no notes.*
  - (b) *I proposed renaming `phone` to `phone_number`. Once you apply it from Preview, a caller that still sends
    `phone` is refused — it must send `phone_number` — and every existing number is kept: the column is renamed, not
    dropped.*
  - (c) *I proposed that only a caller with the `technician` role may delete parts. Once you apply it from Preview,
    admins and managers can no longer delete them; nothing else about parts changes.*
  - (d) *I proposed `full_name` on customers. Once you apply it from Preview, the database maintains it for every
    existing and future customer from `first_name` and `last_name`, and a caller cannot write it.*
  - (e, retry) *I proposed an optional `middle_name` and a `full_name` the database maintains; the middle name and its
    space appear only when there is one. Nothing changes until you apply it from Preview, and a caller cannot write
    `full_name`.*
  - (f, refused) unchanged — it already says *I proposed nothing*.
  - (f, yes) *I proposed `vat_multiplier` on order lines, 1.20 unless a caller sends another, and `total_with_vat`,
    which the database maintains. Once you apply it from Preview, existing lines get 1.20.*
  - (g) *I proposed `is_vip` on customers, set on every create and update from the loyalty tier. Once you apply it
    from Preview, existing customers start as `false` until they are next updated.*

- [ ] **Step 7: §7 rules.** Replace the *Answer in the operator's language* bullet and add three, in this order after
  *Act, don't ask*:

```markdown
- **Proposed, never done.** Nothing you do changes the project: you file a proposal, and the operator reviews and
  applies it from Preview. Say that you *proposed* the change and what happens once it is applied; never say it is
  created, added, applied or in place.
- **Retry yourself.** Every refusal is in the tool's answer. Fix it and retry in the same turn; never ask the operator
  to paste a refusal back or to tell you to try again.
- Answer in the operator's language — and Slovak is not Czech: to a Slovak question, not one Czech word. Quote the
  framework's refusals and their fixes verbatim — they are English — in a quote block, then explain them in the
  operator's language.
- What this build cannot do comes from `get_capabilities` and section 2 only: quote it. Never describe from memory
  what a hook or a hook action does.
```

- [ ] **Step 8: Run** Eval.Tests, Ai.Tests (snake-case/tool-name facts over the new text) and Host.Tests
  (`InstructionExampleOutcomeTests` — the replies are outside the fences, so outcomes are unchanged) → green.
- [ ] **Step 9:** `scripts/test-ring1`. Commit `feat(ai): the assistant says it proposed a change, and the eval holds its replies to that`.

---

### Task 3: The reply-language grader, and both behaviour graders on every turn

**Files:**
- Create: `eval/MMLib.Alvo.Ai.Eval/ReplyLanguage.cs`
- Create: `test/MMLib.Alvo.Ai.Eval.Tests/ReplyLanguageTests.cs`
- Modify: `eval/MMLib.Alvo.Ai.Eval/EvalRunner.cs` (`Graded` takes the language)
- Modify: `test/MMLib.Alvo.Ai.Eval.Tests/EvalCasesTests.cs` (the two `Graded` call sites)

**Interfaces:**
```csharp
internal static partial class ReplyLanguage
{
    internal const string English = "en", Slovak = "sk", Czech = "cs", Unknown = "unknown";
    internal static string Of(string prose);                        // code spans and fences ignored
    internal static Verdict Judge(TurnRecord turn, string asked);  // over ReplyText.OwnProse
}
// EvalRunner
internal static Verdict Graded(EvalCase evalCase, string language, TurnRecord turn);
```

- [ ] **Step 1: Failing tests** — `ReplyLanguageTests.cs`:

```csharp
using static MMLib.Alvo.Ai.Eval.Tests.Turns;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>The language grader tells Slovak from English and from Czech, on the model's own prose.</summary>
public sealed class ReplyLanguageTests
{
    [Theory]
    [InlineData("I proposed the entity `customers_audit`; nothing changes until you apply it from Preview.", "en")]
    [InlineData("Navrhol som entitu `customers_audit`. Kým ju neaplikujete v Preview, nič sa nezmení.", "sk")]
    [InlineData("Pojďme na to: entita je připravena.", "cs")]
    [InlineData("Entita je pripravená, pojďme ďalej.", "cs")]
    [InlineData("Navrhol som to pro vás.", "cs")]
    [InlineData("`customers_audit`", "unknown")]
    public void The_language_of_a_reply_is_read_from_its_words(string prose, string language) =>
        ReplyLanguage.Of(prose).ShouldBe(language);

    [Fact]
    public void A_slovak_reply_after_an_english_quote_block_is_slovak()
    {
        const string Refusal = "Field 'id' is a framework-managed column and cannot be declared.";
        var turn = Turn(answer: $"> {Refusal}\n\nPole `id` pridáva framework sám, preto som ho z návrhu vynechal.",
            calls: Propose(Refused("validation", Refusal)));

        ReplyLanguage.Judge(turn, "sk").Passed.ShouldBeTrue();
    }

    [Fact]
    public void An_english_reply_to_a_slovak_question_fails() =>
        ReplyLanguage.Judge(Turn(answer: "I proposed the notes field; apply it from Preview."), "sk").Passed.ShouldBeFalse();
}
```

- [ ] **Step 2: Run** → does not compile.

- [ ] **Step 3: Implement** `ReplyLanguage.cs`:

```csharp
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>Which language a reply's own prose is in — English, Slovak, or Czech where Slovak was asked (D17).</summary>
/// <remarks>
/// <para>
/// Stop words decide English against Slovak; words the two share (<c>a</c>, <c>to</c>, <c>do</c>, <c>so</c>) are in
/// neither list. <b>Czech is decided by markers alone</b>, because it is the confusion the #289 transcript shows and
/// the one a count cannot see: the letters <c>ě ř ů</c>, which Slovak does not have, and the function words Slovak
/// spells differently (<c>se</c>/<c>sa</c>, <c>pro</c>/<c>pre</c>, <c>jsem</c>/<c>som</c>, <c>pojďme</c>/<c>poďme</c>).
/// One marker makes the reply Czech, since a Slovak reply with one Czech word is the defect.
/// </para>
/// <para>Code spans and fences are identifiers, not language, so they are taken out first.</para>
/// </remarks>
internal static partial class ReplyLanguage
{
    internal const string English = "en";
    internal const string Slovak = "sk";
    internal const string Czech = "cs";
    internal const string Unknown = "unknown";

    private static readonly char[] _czechLetters = ['ě', 'ř', 'ů', 'Ě', 'Ř', 'Ů'];

    private static readonly HashSet<string> _english = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "is", "and", "of", "it", "for", "can", "will", "this", "that", "are", "not", "with", "you", "your", "now",
        "be", "until", "once", "from", "nothing", "there",
    };

    private static readonly HashSet<string> _slovak = new(StringComparer.OrdinalIgnoreCase)
    {
        "je", "sa", "na", "v", "pre", "sú", "nie", "ako", "že", "bude", "môže", "alebo", "ktorý", "ktorá", "ktoré",
        "pri", "po", "aby", "ak", "už", "iba", "som", "ho", "ju", "kým", "nič", "teraz", "preto",
    };

    private static readonly HashSet<string> _czechWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "se", "pro", "jsem", "jsi", "jsou", "jste", "pojďme", "který", "která", "které", "nebo", "protože",
    };

    internal static string Of(string prose)
    {
        var words = Word().Matches(CodeSpan().Replace(Fence().Replace(prose, " "), " ")).Select(match => match.Value).ToList();
        if (words.Any(IsCzech))
        {
            return Czech;
        }

        var english = words.Count(_english.Contains);
        var slovak = words.Count(_slovak.Contains);
        return english == slovak ? Unknown : english > slovak ? English : Slovak;
    }

    internal static Verdict Judge(TurnRecord turn, string asked)
    {
        var said = Of(ReplyText.OwnProse(turn.Answer, turn.FrameworkTexts));
        return Verdict.When(said == asked, $"language={said} asked={asked}");
    }

    private static bool IsCzech(string word) => word.IndexOfAny(_czechLetters) >= 0 || _czechWords.Contains(word);

    [GeneratedRegex(@"\p{L}+", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    [GeneratedRegex("```.*?```", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Fence();

    [GeneratedRegex("`[^`]*`", RegexOptions.CultureInvariant)]
    private static partial Regex CodeSpan();
}
```

- [ ] **Step 4: Run** → green. A fixture that fails is fixed in the word lists, and every word added must not be a
  word of the other language (the shared words are the reason `a`, `to`, `do`, `so` are in neither).

- [ ] **Step 5: Apply both graders to every turn.** In `EvalRunner`:

```csharp
/// <summary>The invariants first; a turn that holds them is graded by its case and by the two behaviour rules.</summary>
/// <remarks>
/// The wording and language graders apply to every case (D21): "Done." or a Czech reply to a Slovak question is a
/// failure the operator sees whatever was asked. Every diagnostic is kept, pass or fail.
/// </remarks>
internal static Verdict Graded(EvalCase evalCase, string language, TurnRecord turn)
{
    var invariants = Invariants(turn);
    if (!invariants.Passed)
    {
        return invariants;
    }

    Verdict[] verdicts = [evalCase.Grade(turn), ProposalWording.Judge(turn), ReplyLanguage.Judge(turn, language), invariants];
    return new Verdict(verdicts.All(verdict => verdict.Passed), string.Join(" | ", verdicts.Select(verdict => verdict.Why)));
}
```

  and `RunOnceAsync` passes `language`. In `EvalCasesTests`: `EvalRunner.Graded(Case("automation_refused"), "en", turn)`
  in both facts; `A_pass_keeps_both_diagnostics_so_it_can_be_audited` answers
  `"This build does not run automation, so there is nothing to propose."` and also asserts
  `verdict.Why.ShouldContain("language=en")`.

- [ ] **Step 6:** `scripts/test-ring1`. Commit `feat(eval): every turn is graded on its wording and its language`.

---

### Task 4: The two #289 cases — the audit entity on the first try, and a refusal recovered in the turn

**Files:**
- Create: `eval/MMLib.Alvo.Ai.Eval/InterferingChatClient.cs`
- Modify: `eval/MMLib.Alvo.Ai.Eval/EvalWorld.cs` (`EditAsAnotherOperatorAsync`, `OtherOperatorsEdit`, remark)
- Modify: `eval/MMLib.Alvo.Ai.Eval/EvalCases.cs` (`ForcesStaleRevision`, two cases, `partial`)
- Modify: `eval/MMLib.Alvo.Ai.Eval/EvalRunner.cs` (the dial per case)
- Modify: `test/MMLib.Alvo.Ai.Eval.Tests/Turns.cs` (`technicians` in `Original`)
- Modify: `test/MMLib.Alvo.Ai.Eval.Tests/EvalCasesTests.cs`
- Create: `test/MMLib.Alvo.Ai.Eval.Tests/InterferingChatClientTests.cs`
- Modify: `CLAUDE.md` (the eval paragraph), `scripts/eval-assistant` (header comment)

**Interfaces:**
```csharp
internal sealed record EvalCase(string Name, string English, string Slovak, Func<TurnRecord, Verdict> Grade, bool ForcesStaleRevision = false);
internal sealed class InterferingChatClient(IChatClient inner, Func<CancellationToken, Task> interfere) : DelegatingChatClient(inner)
{
    internal static bool IsDue(IEnumerable<ChatMessage> messages);   // a get_descriptor call is in the history
}
// EvalWorld
internal const string OtherOperatorsEdit = "/entities/bikes/description";
internal Task EditAsAnotherOperatorAsync(CancellationToken ct);
```

- [ ] **Step 1: Failing interference test** — `InterferingChatClientTests.cs`:

```csharp
using Microsoft.Extensions.AI;

using System.Runtime.CompilerServices;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>The second operator applies once, after the model has read the descriptor and before its next request.</summary>
public sealed class InterferingChatClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task It_interferes_once_before_the_first_request_that_follows_a_descriptor_read()
    {
        var model = new CountingModel();
        var requestsSeenAtInterference = new List<int>();
        using var client = new InterferingChatClient(model, _ =>
        {
            requestsSeenAtInterference.Add(model.Requests);
            return Task.CompletedTask;
        });
        ChatMessage[] afterRead =
        [
            new(ChatRole.User, "hi"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "get_descriptor")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "{}")]),
        ];

        await DrainAsync(client, [new ChatMessage(ChatRole.User, "hi")]);
        await DrainAsync(client, afterRead);
        await DrainAsync(client, afterRead);

        requestsSeenAtInterference.ShouldBe([1]);
    }

    [Fact]
    public void A_history_with_no_descriptor_read_is_not_due() =>
        InterferingChatClient.IsDue([new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "get_schema")])]).ShouldBeFalse();

    private static async Task DrainAsync(IChatClient client, ChatMessage[] messages)
    {
        await foreach (var _ in client.GetStreamingResponseAsync(messages, cancellationToken: Ct))
        {
        }
    }

    /// <summary>A model that counts the requests it was sent and answers each with one text update.</summary>
    private sealed class CountingModel : IChatClient
    {
        internal int Requests { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests++;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
```

- [ ] **Step 2: Implement** `InterferingChatClient.cs`:

```csharp
using Microsoft.Extensions.AI;

using System.Runtime.CompilerServices;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>
/// Plays a second operator who applies a change while the turn is running: once, after the model has read the
/// descriptor and before its next request, so the change the model then proposes is written against a stale revision.
/// </summary>
/// <remarks>
/// The forced refusal of D15. It is forced by the world rather than by the prompt: every refusal a prompt can reliably
/// provoke is one the instructions exist to prevent, whereas a concurrent apply is refused whatever the model knows.
/// It sits under the recorder, so the request it delays is still counted as one round-trip.
/// </remarks>
internal sealed class InterferingChatClient(IChatClient inner, Func<CancellationToken, Task> interfere) : DelegatingChatClient(inner)
{
    private const string DescriptorRead = "get_descriptor";
    private bool _interfered;

    internal static bool IsDue(IEnumerable<ChatMessage> messages) =>
        messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Any(call => call.Name == DescriptorRead);

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sent = messages.ToList();
        await InterfereOnceAsync(sent, cancellationToken).ConfigureAwait(false);
        return await base.GetResponseAsync(sent, options, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sent = messages.ToList();
        await InterfereOnceAsync(sent, cancellationToken).ConfigureAwait(false);
        await foreach (var update in base.GetStreamingResponseAsync(sent, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private async Task InterfereOnceAsync(List<ChatMessage> sent, CancellationToken ct)
    {
        if (_interfered || !IsDue(sent))
        {
            return;
        }

        _interfered = true;
        await interfere(ct).ConfigureAwait(false);
    }
}
```

- [ ] **Step 3: The second operator's edit** in `EvalWorld`:

```csharp
/// <summary>Where <see cref="EditAsAnotherOperatorAsync"/> edits — a member no case grades.</summary>
internal const string OtherOperatorsEdit = "/entities/bikes/description";

/// <summary>Applies a real, harmless edit as another operator would, moving the project to a new revision.</summary>
/// <remarks>
/// A unique text each time, so every run moves the revision. The edit stays in the world: every turn reads its own
/// starting descriptor, so the cases after it grade against a descriptor that already has it.
/// </remarks>
internal async Task EditAsAnotherOperatorAsync(CancellationToken ct)
{
    var current = await Management.GetDescriptorAsync(Project, ct).ConfigureAwait(false);
    var document = JsonNode.Parse(current.DescriptorJson)!;
    document["entities"]!["bikes"]!["description"] = string.Create(
        CultureInfo.InvariantCulture, $"A customer's bicycle, edited by another operator ({Guid.NewGuid():N}).");
    var applied = await Management.ApplyDescriptorAsync(
        Project,
        new ManagementApplyRequest(document.ToJsonString(), current.Revision, Author: CallerKeyId, Reason: "Another operator applies mid-turn (D15)."),
        ct).ConfigureAwait(false);
    if (applied.Revision == current.Revision)
    {
        throw new InvalidOperationException("The second operator's edit did not move the revision, so the forced case forces nothing.");
    }
}
```

  A description-only edit is a revision with an empty plan (`ManagementPlanSummary.IsEmpty` remarks: a rules-only edit
  is still applied); the guard makes a build where that stops being true fail loudly instead of grading every forced
  turn `forced=False`.

  and amend the class remark: *"One world serves the whole suite. The assistant only ever dry-runs; the one write is
  the forced case's second operator (D15), a description no case grades, and every turn reads its own starting
  descriptor."*

- [ ] **Step 4: Dial per case** in `EvalRunner`: the private `AskAsync` takes the `EvalCase`, and

```csharp
/// <summary>The provider, with the second operator between it and the recorder when the case forces a stale revision.</summary>
private Func<AlvoAiConnection, IChatClient> DialFor(EvalCase evalCase) =>
    evalCase.ForcesStaleRevision
        ? connection => new InterferingChatClient(ChatClientFactory.For(connection), world.EditAsAnotherOperatorAsync)
        : ChatClientFactory.For;
```

  is passed as `dial` to the unchanged static `AskAsync` (its signature — used by `ProviderTimeoutTests` — does not move).

- [ ] **Step 5: Failing case tests.** In `Turns.Original` add `"technicians": { "fields": { "full_name": { "type": "string" } } }`
  under `entities`. Add to `EvalCasesTests`:

```csharp
[Fact]
public void Audit_passes_one_new_entity_proposed_on_the_first_attempt() =>
    Grade("audit_entity", Turn(WithEntity("customer_audits", AuditFields()), "I proposed customer_audits.", calls: Propose(Valid())))
        .Passed.ShouldBeTrue();

[Fact]
public void Audit_fails_the_transcripts_turn_that_got_there_on_the_third_attempt()
{
    var turn = Turn(WithEntity("customer_audits", AuditFields()), "I proposed customer_audits.", calls:
    [
        Propose(Refused("validation", "The string value does not match the pattern."), round: 1),
        Propose(Refused("validation", "Field 'id' is a framework-managed column and cannot be declared."), round: 2),
        Propose(Valid(), round: 3),
    ]);

    Grade("audit_entity", turn).Passed.ShouldBeFalse();
}

[Fact]
public void Audit_fails_a_proposal_that_also_hooks_customers()
{
    var proposed = Edited(document =>
    {
        document["entities"]!.AsObject()["customer_audits"] = new JsonObject { ["fields"] = AuditFields() };
        document["entities"]!["customers"]!["hooks"] = new JsonObject { ["afterUpdate"] = new JsonArray() };
    });

    Grade("audit_entity", Turn(proposed, "I proposed it.", calls: Propose(Valid()))).Passed.ShouldBeFalse();
}

[Fact]
public void Audit_fails_a_declared_managed_column_even_when_the_dry_run_passed_it()
{
    var fields = AuditFields();
    fields["id"] = new JsonObject { ["type"] = "uuid" };

    Grade("audit_entity", Turn(WithEntity("customer_audits", fields), "I proposed it.", calls: Propose(Valid()))).Passed.ShouldBeFalse();
}

[Fact]
public void The_forced_case_passes_a_turn_that_rebased_after_the_stale_refusal()
{
    var turn = Turn(WithNicknameAfterTheOtherOperator(rebased: true), "I proposed nickname.", calls:
    [
        Read("get_descriptor", round: 1),
        Propose(Refused("concurrency", "The descriptor is at revision 2.", code: "stale-revision"), round: 2),
        Read("get_descriptor", round: 3),
        Propose(Valid(), round: 4),
    ]);

    Grade("stale_revision_recovered", turn).Passed.ShouldBeTrue();
}

[Fact]
public void The_forced_case_fails_when_the_force_did_not_land() =>
    Grade("stale_revision_recovered", Turn(WithNicknameAfterTheOtherOperator(rebased: true), "I proposed it.", calls: Propose(Valid())))
        .Passed.ShouldBeFalse();

[Fact]
public void The_forced_case_fails_a_proposal_without_the_other_operators_edit()
{
    var turn = Turn(WithNicknameAfterTheOtherOperator(rebased: false), "I proposed it.", calls:
    [
        Propose(Refused("concurrency", "The descriptor is at revision 2.", code: "stale-revision"), round: 1),
        Propose(Valid(), round: 2),
    ]);

    Grade("stale_revision_recovered", turn).Passed.ShouldBeFalse();
}

private static JsonObject AuditFields() => new()
{
    ["customer_id"] = new JsonObject { ["type"] = "ref", ["entity"] = "customers", ["onDelete"] = "cascade" },
    ["previous_version"] = new JsonObject { ["type"] = "json", ["required"] = true },
};

private static string WithEntity(string name, JsonObject fields) =>
    Edited(document => document["entities"]!.AsObject()[name] = new JsonObject { ["fields"] = fields });

private static string WithNicknameAfterTheOtherOperator(bool rebased) => Edited(document =>
{
    document.Fields("technicians")["nickname"] = new JsonObject { ["type"] = "string" };
    if (rebased)
    {
        document["entities"]!["bikes"]!["description"] = "edited by another operator";
    }
});
```

- [ ] **Step 6: Run** → the seven new facts fail (`Single` finds no such case).

- [ ] **Step 7: The cases.** Make `EvalCases` `internal static partial class`; add to `EvalCase` the trailing
  `bool ForcesStaleRevision = false` (document it: *"the turn runs under <see cref="InterferingChatClient"/> (D15)"*);
  append to `All`:

```csharp
new("audit_entity",
    "I want an audit of customers: when a customer changes, keep its previous version in an audit entity. For now, at least create the schema.",
    "Chcem audit zákazníkov: keď sa zákazník zmení, ulož jeho predchádzajúcu verziu do audit entity. Zatiaľ sprav aspoň schému.",
    AuditEntity),
new("stale_revision_recovered",
    "Add an optional nickname field to technicians.",
    "Pridaj technikom (technicians) nepovinné pole nickname.",
    StaleRevisionRecovered,
    ForcesStaleRevision: true),
```

  and the graders:

```csharp
/// <summary>The #289 transcript: one new entity under a schema-valid name, right on the first attempt (D16).</summary>
/// <remarks>
/// Schema only, as asked: exactly one changed path, so a proposal that also adds an after-hook — the unhonoured
/// <c>entity.update</c> the transcript confused — fails. "Declares no managed column" is implied by a valid dry run and
/// graded anyway, so a validator that stopped refusing <c>id</c> still fails the case. Whether the entity refers to
/// customers is printed, not graded: the mechanism that fills it is #288's.
/// </remarks>
private static Verdict AuditEntity(TurnRecord turn)
{
    var entity = NewEntity(turn);
    var declared = entity is null ? null : turn.Proposed(entity);
    var managed = DeclaredManagedColumns(declared);
    return Verdict.When(
        turn.HasValidProposal && turn.RefusedAttempts == 0 && turn.ProposeCalls == 1 && entity is not null && managed.Count == 0,
        $"valid={turn.HasValidProposal} refused={turn.RefusedAttempts} propose_change={turn.ProposeCalls} "
        + $"newEntity={entity ?? "none"} managedDeclared=[{Joined(managed)}] refersToCustomers={RefersTo(declared, "customers")} "
        + $"changed=[{Joined(turn.ChangedPaths)}]");
}

/// <summary>A stale-revision refusal the world forced, recovered in the same turn by re-reading and re-basing (D15).</summary>
private static Verdict StaleRevisionRecovered(TurnRecord turn)
{
    var forced = turn.HasViolation("code", StaleRevision);
    var changed = turn.ChangedPaths.Order(StringComparer.Ordinal).ToList();
    return Verdict.When(
        forced && turn.HasValidProposal && turn.ProposeCalls <= 2
            && changed.SequenceEqual([EvalWorld.OtherOperatorsEdit, TechniciansNickname])
            && turn.Proposed($"{TechniciansNickname}/required")?.ToJsonString() != "true",
        $"forced={forced} valid={turn.HasValidProposal} propose_change={turn.ProposeCalls} changed=[{Joined(changed)}]");
}

private static string? NewEntity(TurnRecord turn) =>
    turn.ChangedPaths is [var only] && EntityPointer().IsMatch(only)
        && DescriptorDiff.At(JsonNode.Parse(turn.OriginalDescriptor), only) is null ? only : null;

private static List<string> DeclaredManagedColumns(JsonNode? entity)
{
    var owned = AlvoManagedColumns.For(
        entity?["tenancy"]?.GetValue<string>() == "scoped" ? TenancyMode.Scoped : null, Flag(entity, "audit"), Flag(entity, "softDelete"));
    return [.. (entity?["fields"] as JsonObject ?? new JsonObject()).Select(field => field.Key).Where(owned.Contains)];
}

private static bool Flag(JsonNode? entity, string trait) => entity?[trait] is JsonValue value && value.TryGetValue<bool>(out var on) && on;

private static bool RefersTo(JsonNode? entity, string target) =>
    (entity?["fields"] as JsonObject ?? new JsonObject()).Any(field => field.Value?["entity"]?.GetValue<string>() == target);

[GeneratedRegex("^/entities/[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
private static partial Regex EntityPointer();
```

  with constants `StaleRevision = "stale-revision"`, `TechniciansNickname = "/entities/technicians/fields/nickname"`,
  and usings `MMLib.Alvo.Schema`, `System.Text.Json.Nodes`, `System.Text.RegularExpressions`.
  Keep each grader ≤ ~25 lines; if the `AuditEntity` diagnostic line pushes past it, extract `AuditWhy(turn, entity, managed)`.
  Update the class remark: the suite is §4.2's seven cases plus the first-try design's two (D15, D16).

- [ ] **Step 8: Run** Eval.Tests → green, including every existing case test over the widened `Turns.Original`.

- [ ] **Step 9: Docs.** `CLAUDE.md` eval paragraph: *"…asks a real model the nine cases of
  `…-reliability-design.md` §4.2 and `docs/superpowers/specs/2026-09-29-f5-assistant-first-try-design.md` §3 over the
  real host and grades outcomes, plus the reply's wording and language…"*. `scripts/eval-assistant` header: *"asks the
  reliability and first-try designs' nine cases in English and Slovak, and grades outcomes (the proposal, the dry-run
  violations, the calls) and the reply's wording and language."* Sweep for the claim, not the file:
  `git grep -n -i "seven cases\|grades outcomes"` and correct every live hit (the reliability plan is history — leave it).

- [ ] **Step 10: Final gates.**
  - `scripts/test-ring1` → green.
  - `scripts/test-ring2` → green (the two known `PagingPerformanceTests` Npgsql *connect* timeouts are infrastructure;
    read the trace before calling either a regression).
  - `dotnet build MMLib.Alvo.slnx -c Release -warnaserror` → 0 warnings, 0 errors (CI builds tests in Release; the
    eval project and both test projects are in the solution).
  - `git diff --stat main -- src/MMLib.Alvo.Admin '*.verified.*'` → empty (no Admin change, no baseline moved), so
    `scripts/test-admin-e2e` is not a gate for this PR.
  - Commit `feat(eval): the #289 cases — an audit entity right on the first try, and a stale revision recovered in the turn`.

## Skills extension — Tasks 5–10 (spec §7, D22–D36)

**Goal:** the assistant loads the rest of the descriptor — rules, hooks, rollups, indexes, access, capabilities — as
Agent Skills, and the always-in-context prompt does not grow. The same `SKILL.md` files serve Claude Code in this
repository (D33).

**Architecture:** there are nine directories, `.claude/skills/alvo-descriptor-<area>/SKILL.md`, embedded into
`MMLib.Alvo.Ai` by an `EmbeddedResource` glob, never copied.
- An internal `EmbeddedSkills` turns each file into an `AgentInlineSkill`. Each skill's resources are the
  `schema/project.schema.json#<pointer>` slices its body cites (D26), served from the linked schema.
- One static `AgentSkillsProvider`, with approvals off, is wrapped in an internal `ReadOnlySkillsProvider` that
  drops `run_skill_script` (D28). The agent receives it through `ChatClientAgentOptions.AIContextProviders`.
- Regions with a source of truth are drift-tested:
  - schema-derived regions in `MMLib.Alvo.Ai.Tests`;
  - core-derived regions in `MMLib.Alvo.Host.Tests`, by probing `CelCompiler` (D35) and reading the capability tables.
- The eval gains a `SkillsRead` grader on every turn (D31), a split tool-call invariant (D34) and seven cases (D36).

**Framework API used — verified against `Microsoft.Agents.AI` 1.22.0** (reflection over
`~/.nuget/packages/microsoft.agents.ai/1.22.0/lib/net10.0/Microsoft.Agents.AI.dll`, its XML docs, and a scripted
`ChatClientAgent` probe; none of these types or members is `[Experimental]`):

| Member | Signature as shipped |
|---|---|
| `AgentInlineSkill` ctor | `(AgentSkillFrontmatter frontmatter, string instructions, JsonSerializerOptions? serializerOptions = null, Func<JsonElement?, AIFunctionArguments>? argumentMarshaler = null)` |
| `AgentInlineSkill.AddResource` | `(string name, object value, string? description = null)` → `AgentInlineSkill` |
| `AgentSkill.GetResourceAsync` / `GetContentAsync` | `ValueTask<AgentSkillResource?> (string name, CancellationToken)` / `ValueTask<string> (CancellationToken)` |
| `AgentSkillResource.ReadAsync` | `Task<object?> (IServiceProvider? serviceProvider = null, CancellationToken cancellationToken = default)` |
| `AgentSkillFrontmatter` ctor | `(string name, string description, string? compatibility = null)`; throws `ArgumentException` on a spec violation |
| `AgentSkillFrontmatter.ValidateName` / `ValidateDescription` | `static bool (string? value, out string? reason)` |
| `AgentSkillsProvider` ctor | `(IEnumerable<AgentSkill> skills, AgentSkillsProviderOptions? options = null, ILoggerFactory? loggerFactory = null)` |
| `AgentSkillsProvider` constants | `LoadSkillToolName = "load_skill"`, `ReadSkillResourceToolName = "read_skill_resource"`, `RunSkillScriptToolName = "run_skill_script"` |
| `AgentSkillsProviderOptions` | `SkillsInstructionPrompt` (must contain `{skills}`), `DisableLoadSkillApproval`, `DisableReadSkillResourceApproval` |
| `AIContextProvider` | `public ValueTask<AIContext> InvokingAsync(InvokingContext, CancellationToken)`; `protected virtual ValueTask<AIContext> InvokingCoreAsync(InvokingContext, CancellationToken)`; protected ctor with three optional filters |
| `AIContext.Tools` | `IEnumerable<AITool>? { get; set; }` |
| `ChatClientAgent` ctor | `(IChatClient chatClient, ChatClientAgentOptions? options, ILoggerFactory? loggerFactory = null, IServiceProvider? services = null)` |
| `ChatClientAgentOptions` | `Name`, `ChatOptions` (`Instructions`, `Tools`), `AIContextProviders` (`IEnumerable<AIContextProvider>?`) |
| M.E.AI 10.10 | `ApprovalRequiredAIFunction` (not experimental); an approval halt is `ToolApprovalRequestContent` (there is no `FunctionApprovalRequestContent` in 10.10) |

These behaviours were measured:
- `load_skill` has the schema `{"skillName": string}` and `read_skill_resource` has `{"skillName", "resourceName"}`.
- `load_skill` answers `<name>…</name><description>…</description><instructions>…</instructions><available_resources>…</available_resources><available_scripts />`.
- An unknown name answers `Error: Skill 'x' not found.` or `Error: Resource 'x' not found in skill 'y'.`
- **`run_skill_script` is always advertised, as an `ApprovalRequiredAIFunction`** (1.22.0's `BuildTools` adds it
  unconditionally; the docs say otherwise).
- The provider's instructions are appended to `ChatOptions.Instructions` after a `\n`.
- The agent's `FunctionInvokingChatClient` caps provider tools too.
- MSBuild's `%(RecursiveDir)` for `…/alvo-descriptor-*/**/*` starts at the skill directory, and it uses `\` on
  Windows.

### Global Constraints, added for Tasks 5–10

Everything above still holds, with two amendments:
- The tool set now grows by exactly `load_skill` and `read_skill_resource` (D30).
- The instructions move to **v4** (Task 7).

These lessons from this branch apply to every step:

- **CI builds tests in Release with `-warnaserror` on Windows (CRLF checkout).** Rings are Debug and do not
  reproduce it.
  - **CA1861**: a constant array argument becomes a `static readonly` field.
  - **CA1875**: `Regex.Count(text)`, never `.Matches(text).Count`.
  - **CA1859**: a private helper returns its concrete type (`List<T>`, `Dictionary<,>`), not an interface.
  - **CA1870**: an `IndexOfAny`/`Split` over constant chars uses a `static readonly SearchValues<char>`, or a `string`
    overload.
  - **IDE0065**: `using` directives go at the top of the file, above `namespace`, never inside it. The ordering is
    `Microsoft.*` / `MMLib.*`, a blank line, then `System.*`, as the files above.
- **CRLF-safe text.**
  - Every skill and instruction text is normalised with `.ReplaceLineEndings("\n")` before it is parsed, measured
    or served.
  - Every regex over raw Markdown ends a line with `\r?$` or captures `[^\r\n]`.
  - A logical resource name is normalised with `.Replace('\\', '/')`, because `%(RecursiveDir)` is backslashed on
    Windows.
- **`$$"""…"""` raw strings:** a literal run of `{` or `}` as long as the interpolation delimiter (e.g. a JSON value
  ending `}}` right before `"""`) does not compile. End with a space or newline before the closing quotes, or build
  the JSON with `JsonObject`.
- **Culture:** interpolate a number or date through `string.Create(CultureInfo.InvariantCulture, …)`.
- **Everything new is `internal`.** No `PublicApi.*.verified.txt` may move. If one does, the change is wrong: it is
  not a baseline to accept. `MMLib.Alvo.Abstractions` is not touched. `MMLib.Alvo.Admin` is not touched: the drawer
  shows `load_skill` as a tool chip unchanged. If an implementer finds a reason to touch Admin, stop and ask, because
  `scripts/test-admin-e2e` (whole) then becomes a gate.
- **Skill files** (`.claude/skills/alvo-descriptor-*/SKILL.md`):
  - UTF-8 **without** BOM, LF.
  - The frontmatter is exactly `---`, `name: …`, `description: …`, `---`, one line each, no quotes.
  - The body is tool-neutral as D33 states.
  - `.cs` files keep BOM and CRLF (the existing constraint).
- **Every task ends with**, all green:
  - `scripts/test-ring1`;
  - `dotnet build MMLib.Alvo.slnx -c Release -warnaserror` → 0 warnings, 0 errors.
- **The final task also runs**:
  - `scripts/test-ring2`;
  - `docker build -f src/MMLib.Alvo.Host/Dockerfile .`;
  - the pack check;
  - `scripts/test-admin-e2e` whole, if and only if `git diff --stat main -- src/MMLib.Alvo.Admin` is non-empty.
- **Commits:** one per task, Conventional Commits, ending with
  `Claude-Session: https://claude.ai/code/session_01TrcGmun8WAuVxN5FYAtu6H`.

---

### Task 5: Skill loading and wiring — the provider, the embed, the image

**Files:**
- Create: `.claude/skills/alvo-descriptor-indexes/SKILL.md` (the first skill; the build requires at least one)
- Modify: `src/MMLib.Alvo.Ai/MMLib.Alvo.Ai.csproj` (the glob, the linked schema, the guard target)
- Create: `src/MMLib.Alvo.Ai/Internal/SkillMarkdown.cs`, `src/MMLib.Alvo.Ai/Internal/EmbeddedSkills.cs`,
  `src/MMLib.Alvo.Ai/Internal/ReadOnlySkillsProvider.cs`
- Modify: `src/MMLib.Alvo.Ai/AlvoAssistant.cs` (`AgentFor`)
- Modify: `src/MMLib.Alvo.Host/Dockerfile`, `.dockerignore`
- Modify: `test/MMLib.Alvo.Ai.Tests/ScriptedChatClient.cs` (record each request's options)
- Create: `test/MMLib.Alvo.Ai.Tests/EmbeddedSkillsTests.cs`

**Interfaces:**
```csharp
namespace MMLib.Alvo.Ai.Internal;
internal static partial class SkillMarkdown
{
    internal static SkillParts Parse(string markdown);                       // LF text in; throws on a malformed frontmatter
}
internal sealed record SkillParts(string Name, string Description, string Body);
internal static partial class EmbeddedSkills
{
    internal const string SkillsPrefix = "MMLib.Alvo.Ai.Skills/";
    internal const string SchemaResourceName = "MMLib.Alvo.Ai.Schema/project.schema.json";
    internal const string SchemaReference = "schema/project.schema.json#";
    internal static IReadOnlyDictionary<string, string> Files { get; }        // "alvo-descriptor-x/SKILL.md" → LF text
    internal static IReadOnlyList<AgentInlineSkill> All { get; }
    internal static AIContextProvider Provider { get; }
    internal static IReadOnlyList<string> SchemaPointers(string body);         // "/$defs/…" cited after SchemaReference
    internal static string Slice(string pointer);                              // that slice of the embedded schema, as JSON
}
internal sealed class ReadOnlySkillsProvider(AgentSkillsProvider skills) : AIContextProvider;
```

- [ ] **Step 1: Failing tests.** `ScriptedChatClient` records every request's options. Add
  `internal List<ChatOptions?> Options { get; } = [];`, and `Options.Add(options?.Clone());` beside each
  `Sent.AddRange(messages);`. Then `EmbeddedSkillsTests.cs`:

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MMLib.Alvo.Ai.Internal;
using MMLib.Alvo.Management;

using NSubstitute;

using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>The descriptor skills as the package embeds and serves them (D23, D26, D28, D33).</summary>
public sealed class EmbeddedSkillsTests
{
    private const string Indexes = "alvo-descriptor-indexes";
    private const string IndexesPointer = "/$defs/entity/properties/indexes";

    private static readonly string[] _readTools = [AgentSkillsProvider.LoadSkillToolName, AgentSkillsProvider.ReadSkillResourceToolName];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Every_embedded_file_sits_under_a_descriptor_skill_directory_with_forward_slashes() =>
        EmbeddedSkills.Files.Keys.ShouldAllBe(path =>
            path.StartsWith("alvo-descriptor-", StringComparison.Ordinal) && !path.Contains('\\') && !path.Contains('\r'));

    [Fact]
    public void Every_skill_file_is_served_as_a_skill_of_its_directorys_name() =>
        EmbeddedSkills.All.Select(skill => skill.Frontmatter.Name + "/SKILL.md")
            .ShouldBe(EmbeddedSkills.Files.Keys.Where(path => path.EndsWith("/SKILL.md", StringComparison.Ordinal)).Order(StringComparer.Ordinal));

    [Fact]
    public async Task A_cited_schema_pointer_is_served_under_its_citation_as_that_slice_of_the_schema()
    {
        var skill = EmbeddedSkills.All.Single(candidate => candidate.Frontmatter.Name == Indexes);

        var resource = (await skill.GetResourceAsync(EmbeddedSkills.SchemaReference + IndexesPointer, Ct)).ShouldNotBeNull();
        var served = (string)(await resource.ReadAsync(cancellationToken: Ct))!;

        JsonNode.DeepEquals(JsonNode.Parse(served), Schema()["$defs"]!["entity"]!["properties"]!["indexes"]).ShouldBeTrue();
    }

    [Fact]
    public async Task The_model_sees_both_read_tools_without_approval_and_never_the_script_tool()
    {
        var model = new ScriptedChatClient(Scripted.Says("ok"));

        await DrainAsync(model);

        var tools = model.Options[0].ShouldNotBeNull().Tools.ShouldNotBeNull();
        tools.Select(tool => tool.Name).ShouldBeSupersetOf(_readTools);
        tools.Select(tool => tool.Name).ShouldNotContain(AgentSkillsProvider.RunSkillScriptToolName);
        tools.ShouldAllBe(tool => !(tool is ApprovalRequiredAIFunction));
    }

    [Fact]
    public async Task The_instructions_the_model_receives_end_with_the_skill_list()
    {
        var model = new ScriptedChatClient(Scripted.Says("ok"));

        await DrainAsync(model);

        var instructions = model.Options[0].ShouldNotBeNull().Instructions.ShouldNotBeNull();
        instructions.ShouldStartWith(AssistantInstructions.Text);
        EmbeddedSkills.All.ShouldAllBe(skill => instructions.Contains($"<name>{skill.Frontmatter.Name}</name>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_turn_that_loads_a_skill_goes_on_without_an_approval_halt()
    {
        var model = new ScriptedChatClient(
            Scripted.Calls(AgentSkillsProvider.LoadSkillToolName, new() { ["skillName"] = Indexes }),
            Scripted.Says("Loaded."));

        var updates = await DrainAsync(model);

        string.Concat(updates.SelectMany(update => update.Contents).OfType<TextContent>().Select(text => text.Text))
            .ShouldContain("Loaded.");
        updates.SelectMany(update => update.Contents).OfType<ToolApprovalRequestContent>().ShouldBeEmpty();
        model.Sent.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            .ShouldContain(result => result.Result!.ToString()!.Contains("<instructions>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_model_that_loops_on_load_skill_is_cut_off_at_the_iteration_cap()
    {
        var looping = Enumerable.Range(0, 30)
            .Select(_ => Scripted.Calls(AgentSkillsProvider.LoadSkillToolName, new() { ["skillName"] = Indexes })).ToArray();
        var model = new ScriptedChatClient(looping);

        await DrainAsync(model);

        model.Options.Count.ShouldBe(AlvoAssistant.MaximumIterations + 1);
    }

    private static async Task<List<AgentResponseUpdate>> DrainAsync(ScriptedChatClient model)
    {
        var agent = AlvoAssistant.AgentFor(model, ManagementTools.For(Substitute.For<IAlvoManagement>(), "p"));
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "hi")], session: null, options: null, Ct))
        {
            updates.Add(update);
        }

        return updates;
    }

    private static JsonNode Schema() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "schema", "project.schema.json")))!;
}
```

  The cap fact asserts `MaximumIterations + 1` requests. A cap-3 probe made 4: the invoker asks once more after
  the last permitted round, and that request has no tools. If the count is off by one, read the probe again before
  changing the expectation.

- [ ] **Step 2: Run** `dotnet test --project test/MMLib.Alvo.Ai.Tests` → it does not compile (`EmbeddedSkills` does not
  exist).

- [ ] **Step 3: The first skill** — `.claude/skills/alvo-descriptor-indexes/SKILL.md` (LF, no BOM). It is the pattern
  every later skill follows:

```markdown
---
name: alvo-descriptor-indexes
description: Use when an Alvo descriptor change should speed up a query or make a combination of fields unique — composite and unique-per-group indexes, and the indexes Alvo already creates by itself.
---

# Indexes in an Alvo descriptor

Alvo already indexes, without being asked, the primary key `id`, every field with `"unique": true`, and every `ref`
field. Declare an index only beyond those:

- A field's `"index": true` is the one-field form. Use it for a single field.
- The entity's `indexes` list is for two or more fields, in query order: filter on the first, then the next.
  `{"fields": ["technician_id", "status"]}` speeds up "this technician's open orders".
- `{"fields": [...], "unique": true}` makes the **combination** unique — one row per pair. A field's own
  `"unique": true` is uniqueness across the whole table, which is a different rule.
- A new `unique` index cannot be created while duplicate rows exist, so say that when you propose one. A plain index
  never rejects a write.

The shape: `schema/project.schema.json#/$defs/entity/properties/indexes`.

In the dashboard: read with `get_descriptor`, then `propose_change` an `add` at `/entities/<entity>/indexes/-`.
In this repo: edit the descriptor file and run the validator (`dotnet test`), or the Management API.
```

- [ ] **Step 4: Embed** — in `MMLib.Alvo.Ai.csproj`, after the instructions `ItemGroup`:

```xml
  <!-- The descriptor skills (D33): one source, .claude/skills/alvo-descriptor-*, which Claude Code discovers in this
       repository and this package embeds; never a copy. The schema is linked for the slices the skills cite (D26):
       a linked file, not an assembly reference, so the boundary test still holds. %(RecursiveDir) starts at the skill
       directory and is backslashed on Windows; EmbeddedSkills normalises it. -->
  <ItemGroup>
    <AlvoDescriptorSkill Include="$(MSBuildThisFileDirectory)../../.claude/skills/alvo-descriptor-*/**/*" />
    <EmbeddedResource Include="@(AlvoDescriptorSkill)" LogicalName="MMLib.Alvo.Ai.Skills/%(RecursiveDir)%(Filename)%(Extension)" />
    <EmbeddedResource Include="$(MSBuildThisFileDirectory)../../schema/project.schema.json" LogicalName="MMLib.Alvo.Ai.Schema/project.schema.json" />
  </ItemGroup>

  <!-- A context without the skills (a Docker context that forgot them, a sparse checkout) would build an assistant that
       silently knows nothing; this makes it a build error instead. -->
  <Target Name="AlvoRequireDescriptorSkills" BeforeTargets="BeforeBuild">
    <Error Condition="'@(AlvoDescriptorSkill)' == ''"
           Text="No .claude/skills/alvo-descriptor-*/SKILL.md was found next to the repository root. MMLib.Alvo.Ai embeds them (D33); a Docker build must copy .claude/skills/ into its context." />
  </Target>
```

- [ ] **Step 5: The loader.** `SkillMarkdown.cs`:

```csharp
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>A <c>SKILL.md</c> split into its frontmatter and body, as the Agent Skills standard lays it out.</summary>
/// <remarks>
/// Deliberately not a YAML parser. D24 limits the frontmatter to one-line <c>name</c> and <c>description</c>, in that
/// order and without quotes, and <c>SkillConformanceTests</c> holds every skill to it. The text arrives LF-normalised.
/// </remarks>
internal static partial class SkillMarkdown
{
    internal static SkillParts Parse(string markdown)
    {
        var match = Frontmatter().Match(markdown);
        if (!match.Success)
        {
            throw new InvalidOperationException("A SKILL.md must open with '---', 'name: …', 'description: …', '---'.");
        }

        return new SkillParts(match.Groups["name"].Value, match.Groups["description"].Value, markdown[match.Length..].TrimStart('\n'));
    }

    [GeneratedRegex(@"\A---\nname: (?<name>[^\n]+)\ndescription: (?<description>[^\n]+)\n---\n", RegexOptions.CultureInvariant)]
    private static partial Regex Frontmatter();
}

/// <summary>A skill's frontmatter values and its body.</summary>
internal sealed record SkillParts(string Name, string Description, string Body);
```

  `EmbeddedSkills.cs`:

```csharp
using Microsoft.Agents.AI;

using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>The descriptor skills (D33), embedded from <c>.claude/skills/alvo-descriptor-*</c> and fixed.</summary>
/// <remarks>
/// <para>
/// <b>Embedded rather than read from disk, for #29 §5's reason</b> (D23): a deployment must not be able to edit what
/// the assistant believes. They are the same files Claude Code reads in this repository, so a rule the assistant
/// learns is one a developer's agent learns too.
/// </para>
/// <para>
/// <b>A skill's resources are the schema slices its body cites</b> (D26). <c>schema/project.schema.json#/…</c> is a
/// repository path Claude Code opens, and the name the assistant passes to <c>read_skill_resource</c>. Both resolve to
/// one file, linked into this assembly, never copied.
/// </para>
/// </remarks>
internal static partial class EmbeddedSkills
{
    internal const string SkillsPrefix = "MMLib.Alvo.Ai.Skills/";
    internal const string SchemaResourceName = "MMLib.Alvo.Ai.Schema/project.schema.json";
    internal const string SchemaReference = "schema/project.schema.json#";

    private const string SkillFile = "/SKILL.md";
    private const string SliceDescription = "A slice of the descriptor's JSON Schema, schema/project.schema.json.";

    /// <summary>The skill list's frame in the instructions; the framework's default also advertises scripts (D28).</summary>
    private const string Catalogue =
        "## Skills\n\nEach skill below holds the rules of one area of the descriptor. Before `check_change` or "
        + "`propose_change` in an area, load its skill with `load_skill`; read a resource it lists with "
        + "`read_skill_resource`, using the name exactly as listed.\n\n<available_skills>\n{skills}\n</available_skills>";

    private static readonly Lazy<JsonNode> _schema = new(() => JsonNode.Parse(Read(SchemaResourceName))!);

    internal static IReadOnlyDictionary<string, string> Files { get; } = LoadFiles();

    internal static IReadOnlyList<AgentInlineSkill> All { get; } =
        [.. Files.Keys.Where(path => path.EndsWith(SkillFile, StringComparison.Ordinal)).Order(StringComparer.Ordinal).Select(Build)];

    internal static AIContextProvider Provider { get; } = new ReadOnlySkillsProvider(new AgentSkillsProvider(All, new AgentSkillsProviderOptions
    {
        DisableLoadSkillApproval = true,
        DisableReadSkillResourceApproval = true,
        SkillsInstructionPrompt = Catalogue,
    }));

    internal static IReadOnlyList<string> SchemaPointers(string body) =>
        [.. Citation().Matches(body).Select(match => match.Groups["pointer"].Value).Distinct(StringComparer.Ordinal)];

    internal static string Slice(string pointer) =>
        (JsonPointer.TryParse(pointer, out var parsed) ? parsed.Tokens.Aggregate<string, JsonNode?>(_schema.Value, (node, token) => node?[token]) : null)?.ToJsonString()
        ?? throw new InvalidOperationException($"'{SchemaReference}{pointer}' names nothing in the schema.");

    private static AgentInlineSkill Build(string path)
    {
        var parts = SkillMarkdown.Parse(Files[path]);
        var skill = new AgentInlineSkill(new AgentSkillFrontmatter(parts.Name, parts.Description), parts.Body);
        foreach (var pointer in SchemaPointers(parts.Body))
        {
            skill.AddResource(SchemaReference + pointer, Slice(pointer), SliceDescription);
        }

        return skill;
    }

    private static Dictionary<string, string> LoadFiles()
    {
        var assembly = typeof(EmbeddedSkills).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(SkillsPrefix, StringComparison.Ordinal))
            .ToDictionary(name => name[SkillsPrefix.Length..].Replace('\\', '/'), name => Read(name), StringComparer.Ordinal);
    }

    private static string Read(string resource)
    {
        var assembly = typeof(EmbeddedSkills).Assembly;
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The embedded resource '{resource}' is missing from {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    [GeneratedRegex(@"schema/project\.schema\.json#(?<pointer>/[^\s`)]+)", RegexOptions.CultureInvariant)]
    private static partial Regex Citation();
}
```

  Two rules on these statics:
  - **Declaration order is initialisation order.** `_schema`, then `Files`, then `All`, then `Provider`: `All`'s
    initialiser already slices the schema. A field declared below its first reader is still `null` when the reader
    runs, and `Lazy` does not help, because the `Lazy` itself is that field (the same trap `UnhonouredFeatures`
    documents).
  - **Nesting.** If `Slice`'s single line trips a length or nesting analyzer, extract `Walk(JsonPointer)`, and keep
    both under ~25 lines.

  `JsonPointer` is the package's own RFC 6901 type (`Internal/JsonPointer.cs`), so `~0`/`~1` unescape as its tokens
  already do.

  `ReadOnlySkillsProvider.cs`:

```csharp
using Microsoft.Agents.AI;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>The framework's skills provider with <c>run_skill_script</c> taken out (D28).</summary>
/// <remarks>
/// <b>1.22.0 advertises the script tool whether or not any skill has a script</b>, and advertises it as
/// approval-required, so a model that called it would end the turn on an approval request the drawer cannot draw. No
/// descriptor skill has a script. The inner provider's instructions and its two read tools pass through unchanged,
/// and the agent's own invoker, cap and all, runs them.
/// </remarks>
internal sealed class ReadOnlySkillsProvider(AgentSkillsProvider skills) : AIContextProvider
{
    protected override async ValueTask<AIContext> InvokingCoreAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var provided = await skills.InvokingAsync(context, cancellationToken).ConfigureAwait(false);
        provided.Tools = provided.Tools?.Where(tool => tool.Name != AgentSkillsProvider.RunSkillScriptToolName).ToList();
        return provided;
    }
}
```

  `skills.InvokingAsync` already merges the agent's own instructions and tools with the provider's
  (`AIContextProvider.InvokingCoreAsync`). Returning its result is therefore the whole context: do not merge a
  second time.

- [ ] **Step 6: Wire the agent.** In `AlvoAssistant.AgentFor`:

```csharp
internal static ChatClientAgent AgentFor(IChatClient client, ManagementTools tools)
{
    var agent = new ChatClientAgent(
        client,
        new ChatClientAgentOptions
        {
            Name = AgentName,
            ChatOptions = new ChatOptions { Instructions = AssistantInstructions.Text, Tools = [.. tools.Functions] },
            AIContextProviders = [EmbeddedSkills.Provider],
        },
        loggerFactory: null,
        services: null);
    Constrain(agent);

    return agent;
}
```

  Update its summary: *"the fixed instructions, the tools, the descriptor skills, and a capped, sequential invoker"*.
  The class remark gains one bullet: *"The skills are read-only too (D28): `load_skill` and `read_skill_resource`
  read fixed embedded text."*

- [ ] **Step 7: Run Ai.Tests** → green, including `BoundaryArchitectureTests` (no new assembly reference) and the
  existing `The_agents_invoker_is_capped_and_sequential`.

- [ ] **Step 8: The image.** In `src/MMLib.Alvo.Host/Dockerfile`, directly after `COPY schema/ schema/`:

```dockerfile
# .claude/skills/alvo-descriptor-* is not tooling here: MMLib.Alvo.Ai embeds those skills (D33) and its build fails
# without them. .dockerignore admits only those directories of .claude/.
COPY .claude/skills/ .claude/skills/
```

  and append to `.dockerignore`:

```
# Only the descriptor skills of .claude/ enter the context: MMLib.Alvo.Ai embeds them (D33).
.claude/*
!.claude/skills
.claude/skills/*
!.claude/skills/alvo-descriptor-*
```

  Prove it:
  - `docker build -f src/MMLib.Alvo.Host/Dockerfile -t alvo:skills .` → succeeds. The guard target makes a missing
    skill a build error, not an assistant that knows nothing.
  - `docker build -f src/MMLib.Alvo.Host/Dockerfile --target build -t alvo:skills-build .` then
    `docker run --rm alvo:skills-build ls /source/.claude/skills` → only `alvo-descriptor-*` directories.

- [ ] **Step 9:** Run the gates:
  - `scripts/test-ring1`;
  - `dotnet build MMLib.Alvo.slnx -c Release -warnaserror`;
  - `git diff --stat main -- '*.verified.*'` → empty.

  Commit `feat(ai): the assistant loads descriptor skills through the Agent Framework's skills provider`.

---

### Task 6: The skill catalogue's conformance, and the four schema-sourced skills

**Files:**
- Create: `test/_shared/ai/SkillCatalogue.cs`, linked into both `MMLib.Alvo.Ai.Tests` and `MMLib.Alvo.Host.Tests`
  csprojs beside `InstructionClaims.cs`
- Create: `test/MMLib.Alvo.Ai.Tests/SkillConformanceTests.cs`, `test/MMLib.Alvo.Ai.Tests/SkillRegionTests.cs`
- Create: `test/MMLib.Alvo.Host.Tests/SkillExampleOutcomeTests.cs`
- Modify: `test/MMLib.Alvo.Ai.Tests/AssistantInstructionsTests.cs` (`KnownNames` becomes `internal static`, reused)
- Create: `.claude/skills/alvo-descriptor-entities-and-fields/SKILL.md`,
  `.claude/skills/alvo-descriptor-field-types-and-formats/SKILL.md`,
  `.claude/skills/alvo-descriptor-traits-and-tenancy/SKILL.md`
- Modify: `.claude/skills/alvo-descriptor-indexes/SKILL.md` (a worked example)
- Modify: `CLAUDE.md` ("Skills & guard")

**Interfaces:**
```csharp
namespace MMLib.Alvo.Ai.Tests;
internal static partial class SkillCatalogue
{
    internal const string Prefix = "alvo-descriptor-";
    internal static string Root { get; }                                        // <repo>/.claude/skills
    internal static IReadOnlyList<SkillOnDisk> All { get; }
    internal static SkillOnDisk Named(string area);
    internal static IReadOnlyDictionary<string, string> Regions(string body);    // gen id → region text
    internal static IReadOnlyList<string> Tokens(string text);                   // every `backticked` token, in order
    internal static string Expected(IEnumerable<string> tokens);                 // what a failing region should say (D25)
}
internal sealed record SkillOnDisk(string Directory, string Name, string Description, string Body, string Text, IReadOnlyList<string> FrontmatterKeys);
```

- [ ] **Step 1: The shared catalogue** — `test/_shared/ai/SkillCatalogue.cs` (BOM, CRLF):

```csharp
using MMLib.Alvo.Ai.Internal;

using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>
/// The descriptor skills as they sit in the repository: the directories Claude Code discovers and the package embeds
/// (D33). The tests run over these, not over a copy.
/// </summary>
internal static partial class SkillCatalogue
{
    internal const string Prefix = "alvo-descriptor-";

    internal static string Root { get; } = Path.Combine(RepositoryRoot.Find(), ".claude", "skills");

    internal static IReadOnlyList<SkillOnDisk> All { get; } =
        [.. Directory.GetDirectories(Root, Prefix + "*").Order(StringComparer.Ordinal).Select(Read)];

    internal static SkillOnDisk Named(string area) => All.Single(skill => skill.Name == Prefix + area);

    internal static IReadOnlyDictionary<string, string> Regions(string body) =>
        Region().Matches(body).ToDictionary(match => match.Groups["id"].Value, match => match.Groups["text"].Value, StringComparer.Ordinal);

    internal static IReadOnlyList<string> Tokens(string text) =>
        [.. Token().Matches(text).Select(match => match.Groups["token"].Value)];

    internal static string Expected(IEnumerable<string> tokens) => string.Join(" ", tokens.Select(token => $"`{token}`"));

    private static SkillOnDisk Read(string directory)
    {
        var text = File.ReadAllText(Path.Combine(directory, "SKILL.md")).ReplaceLineEndings("\n");
        var parts = SkillMarkdown.Parse(text);
        var keys = FrontmatterKey().Matches(text[..(text.IndexOf("\n---\n", 3, StringComparison.Ordinal) + 1)]).Select(match => match.Groups["key"].Value);
        return new SkillOnDisk(directory, parts.Name, parts.Description, parts.Body, text, [.. keys]);
    }

    [GeneratedRegex(@"<!-- gen:(?<id>[a-z-]+) -->\n(?<text>.*?)\n<!-- /gen:\k<id> -->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Region();

    [GeneratedRegex("`(?<token>[^`\n]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex(@"^(?<key>[a-z][a-z-]*):", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FrontmatterKey();
}

/// <summary>One skill directory: its frontmatter, its body and its whole text, LF-normalised.</summary>
internal sealed record SkillOnDisk(
    string Directory, string Name, string Description, string Body, string Text, IReadOnlyList<string> FrontmatterKeys);
```

  Link it in both csprojs:
  `<Compile Include="$(MSBuildThisFileDirectory)../_shared/ai/SkillCatalogue.cs" Link="_shared/SkillCatalogue.cs" />`.
  Extend the csproj comment: *"The skill catalogue is shared the same way: this suite holds the skills to the
  standard and the schema, that one to the core."*

- [ ] **Step 2: Failing conformance tests** — `SkillConformanceTests.cs`:

```csharp
using Microsoft.Agents.AI;
using MMLib.Alvo.Ai.Internal;

using System.Text;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>The descriptor skills against the Agent Skills standard, their size caps (D32), and what they claim.</summary>
public sealed class SkillConformanceTests
{
    private const int MaximumSkillBytes = 6_144;
    private const int MaximumSkillLines = 200;
    private const int MaximumDescription = 300;
    private const int MaximumResourceBytes = 16_384;
    private const int MaximumSkills = 10;

    /// <summary>The areas, in order. Task 7 adds the five core-sourced ones.</summary>
    private static readonly string[] _areas = ["entities-and-fields", "field-types-and-formats", "indexes", "traits-and-tenancy"];

    private static readonly string[] _frontmatterKeys = ["name", "description"];

    public static TheoryData<string> SkillNames() => [.. SkillCatalogue.All.Select(skill => skill.Name)];

    public static TheoryData<string, string> SkillExamples()
    {
        var data = new TheoryData<string, string>();
        foreach (var skill in SkillCatalogue.All)
        {
            foreach (var example in InstructionExamples.Parse(skill.Body))
            {
                data.Add(skill.Name, example.Name);
            }
        }

        return data;
    }

    [Fact]
    public void The_catalogue_is_exactly_the_descriptor_areas()
    {
        SkillCatalogue.All.Select(skill => skill.Name).ShouldBe(_areas.Select(area => SkillCatalogue.Prefix + area));
        SkillCatalogue.All.Count.ShouldBeLessThanOrEqualTo(MaximumSkills);
    }

    [Theory]
    [MemberData(nameof(SkillNames))]
    public void A_skill_follows_the_standard(string name)
    {
        var skill = SkillCatalogue.All.Single(candidate => candidate.Name == name);

        Path.GetFileName(skill.Directory).ShouldBe(skill.Name);
        skill.FrontmatterKeys.ShouldBe(_frontmatterKeys);
        AgentSkillFrontmatter.ValidateName(skill.Name, out var nameReason).ShouldBeTrue(nameReason);
        AgentSkillFrontmatter.ValidateDescription(skill.Description, out var descriptionReason).ShouldBeTrue(descriptionReason);
        skill.Description.ShouldStartWith("Use when ");
    }

    [Theory]
    [MemberData(nameof(SkillNames))]
    public void A_skill_keeps_its_size_caps(string name)
    {
        var skill = SkillCatalogue.All.Single(candidate => candidate.Name == name);

        Encoding.UTF8.GetByteCount(skill.Text).ShouldBeLessThanOrEqualTo(MaximumSkillBytes);
        skill.Text.Count(character => character == '\n').ShouldBeLessThanOrEqualTo(MaximumSkillLines);
        skill.Description.Length.ShouldBeLessThanOrEqualTo(MaximumDescription);
        EmbeddedSkills.SchemaPointers(skill.Body)
            .ShouldAllBe(pointer => Encoding.UTF8.GetByteCount(EmbeddedSkills.Slice(pointer)) <= MaximumResourceBytes);
    }

    [Theory]
    [MemberData(nameof(SkillNames))]
    public void A_skill_says_how_to_act_in_the_dashboard_and_in_this_repository(string name)
    {
        var body = SkillCatalogue.All.Single(candidate => candidate.Name == name).Body;

        body.ShouldContain("In the dashboard:");
        body.ShouldContain("In this repo:");
    }

    [Fact]
    public void The_embedded_catalogue_is_the_directories_on_disk()
    {
        var onDisk = Directory.GetFiles(SkillCatalogue.Root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(SkillCatalogue.Root, path).Replace('\\', '/'))
            .Where(path => path.StartsWith(SkillCatalogue.Prefix, StringComparison.Ordinal))
            .ToDictionary(path => path, path => File.ReadAllText(Path.Combine(SkillCatalogue.Root, path)).ReplaceLineEndings("\n"), StringComparer.Ordinal);

        EmbeddedSkills.Files.Keys.Order(StringComparer.Ordinal).ShouldBe(onDisk.Keys.Order(StringComparer.Ordinal));
        onDisk.ShouldAllBe(file => EmbeddedSkills.Files[file.Key] == file.Value);
    }

    [Theory]
    [MemberData(nameof(SkillNames))]
    public void Every_json_fence_in_a_skill_belongs_to_a_worked_example(string name)
    {
        var body = SkillCatalogue.All.Single(candidate => candidate.Name == name).Body;

        AssistantInstructionsTests.JsonFence().Count(body).ShouldBe(2 * InstructionExamples.Parse(body).Count);
    }

    [Theory]
    [MemberData(nameof(SkillExamples))]
    public void A_skill_example_patches_the_bike_workshop_and_touches_what_it_claims(string skill, string example)
    {
        var worked = InstructionExamples.Parse(SkillCatalogue.All.Single(candidate => candidate.Name == skill).Body)
            .Single(candidate => candidate.Name == example);
        var descriptor = JsonNode.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "examples", "bike-workshop", "bike-workshop.alvo.json")));

        var result = JsonPatch.Apply(descriptor, worked.Operations);

        result.Succeeded.ShouldBeTrue(result.Error?.ToString());
        PatchAdmission.Check(worked.Operations).ShouldBeNull();
        result.ChangedPaths.ShouldBe(worked.ChangedPaths);
    }

    [Theory]
    [MemberData(nameof(SkillNames))]
    public void Every_snake_case_name_in_a_skills_code_is_a_known_name(string name)
    {
        var body = SkillCatalogue.All.Single(candidate => candidate.Name == name).Body;

        AssistantInstructionsTests.SnakeCaseInCode(body).ShouldAllBe(token => AssistantInstructionsTests.KnownNames(body).Contains(token));
    }
}
```

  `SkillExamples()` is the same builder as Host.Tests' (Step 4). If the two stay identical, move it into
  `SkillCatalogue` as `internal static TheoryData<string, string> Examples()`, and have both suites use that one.

  In `AssistantInstructionsTests`, make four members `internal static` and parameterise the two that read `_text`:
  - `JsonFence()`;
  - `KnownNames(string text)`, which unions `InstructionExamples.Parse(text).SelectMany(ExampleTokens)` for the text it
    is given;
  - `SnakeCaseInCode(string text)` =
    `CodeSpan().Matches(Fence().Replace(text, string.Empty)).SelectMany(span => SnakeCase().Matches(span.Value).Select(match => match.Value)).Distinct()`;
  - `Registered`.

  Have the existing snake-case fact call `SnakeCaseInCode(_text)` and `KnownNames(_text)`. `KnownNames` also unions
  `AgentSkillsProvider.LoadSkillToolName` and `ReadSkillResourceToolName`.

- [ ] **Step 3: Failing region tests** — `SkillRegionTests.cs` (the schema-sourced regions, D25):

```csharp
using MMLib.Alvo.Schema;

using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>
/// Every marked region whose source is the schema or the ports equals it (D25). A failure prints the region as it
/// should read; the test never rewrites the file.
/// </summary>
public sealed class SkillRegionTests
{
    [Fact]
    public void The_entity_keys_are_the_schemas() =>
        Holds("entities-and-fields", "entity-keys", Keys(Schema()["$defs"]!["entity"]!["properties"]!));

    [Fact]
    public void The_field_keys_are_the_schemas() =>
        Holds("entities-and-fields", "field-keys", Keys(Schema()["$defs"]!["field"]!["properties"]!));

    [Fact]
    public void The_field_types_are_the_schemas() =>
        Holds("field-types-and-formats", "field-types", Enum(Schema()["$defs"]!["fieldType"]!["enum"]!));

    [Fact]
    public void The_on_delete_behaviours_are_the_schemas() =>
        Holds("field-types-and-formats", "on-delete", Enum(Schema()["$defs"]!["field"]!["properties"]!["onDelete"]!["enum"]!));

    [Fact]
    public void The_managed_columns_per_trait_are_the_ones_the_framework_injects()
    {
        var region = SkillCatalogue.Regions(SkillCatalogue.Named("traits-and-tenancy").Body)["managed-columns"];
        var stated = InstructionClaims.ManagedColumns(region);
        var always = AlvoManagedColumns.For(null, audit: false, softDelete: false);

        stated.Keys.ShouldBe(InstructionClaims.TraitLabels);
        stated[InstructionClaims.EveryEntity].ShouldBe(always, ignoreOrder: true);
        stated[InstructionClaims.AuditedEntity].ShouldBe(AlvoManagedColumns.Audit);
        stated.Values.SelectMany(columns => columns)
            .ShouldBe(AlvoManagedColumns.For(TenancyMode.Scoped, audit: true, softDelete: true), ignoreOrder: true);
    }

    private static void Holds(string area, string region, IReadOnlyList<string> source)
    {
        var regions = SkillCatalogue.Regions(SkillCatalogue.Named(area).Body);

        regions.ShouldContainKey(region, $"alvo-descriptor-{area} has no <!-- gen:{region} --> region");
        SkillCatalogue.Tokens(regions[region]).ShouldBe(source, $"<!-- gen:{region} --> should read:\n{SkillCatalogue.Expected(source)}");
    }

    private static List<string> Keys(JsonNode members) => [.. members.AsObject().Select(member => member.Key)];

    private static List<string> Enum(JsonNode values) => [.. values.AsArray().Select(value => value!.GetValue<string>())];

    private static JsonNode Schema() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "schema", "project.schema.json")))!;
}
```

  The managed-columns region reuses the base prompt's bullet form and `InstructionClaims.ManagedColumns`, so one
  parser reads both (`^  - on … — …\r?$`).

- [ ] **Step 4: The outcome check, Host.Tests** — `SkillExampleOutcomeTests.cs` runs every skill example through the
  real validator. It reuses the machinery of `InstructionExampleOutcomeTests`: `BikeWorkshop`, `InvokeAsync`,
  `Administrator`, `Violations` and `Matches` become `internal static` there. Nothing is duplicated.

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Ai.Tests;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;

using System.Text.Json;

namespace MMLib.Alvo.Host.Tests;

/// <summary>A skill's worked example gets the outcome it claims from the real validator on <c>bike-workshop</c>.</summary>
public sealed class SkillExampleOutcomeTests
{
    public static TheoryData<string, string> SkillExamples()
    {
        var data = new TheoryData<string, string>();
        foreach (var skill in SkillCatalogue.All)
        {
            foreach (var example in InstructionExamples.Parse(skill.Body))
            {
                data.Add(skill.Name, example.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SkillExamples))]
    public async Task A_skill_example_gets_the_outcome_it_claims(string skill, string name)
    {
        var example = InstructionExamples.Parse(SkillCatalogue.All.Single(candidate => candidate.Name == skill).Body)
            .Single(candidate => candidate.Name == name);
        await using var world = await AlvoHostWorld.StartAsync(InstructionExampleOutcomeTests.BikeWorkshop);
        var management = world.Services.GetRequiredService<IAlvoManagement>();
        world.Services.GetRequiredService<IAlvoContextAccessor>().Principal = InstructionExampleOutcomeTests.Administrator();

        var outcome = await InstructionExampleOutcomeTests.InvokeAsync(management, example);

        InstructionExampleOutcomeTests.AssertClaims(example, outcome);
    }
}
```

  Extract the two `foreach` loops of `A_worked_example_gets_the_outcome_the_instructions_claim` into
  `internal static void AssertClaims(InstructionExample example, JsonElement outcome)`, and call it from both. The
  principal is published on the fact's own async flow before the first call, as the existing fact does.

- [ ] **Step 5: Run** Ai.Tests and Host.Tests → they fail: the catalogue has one skill, not four, and it has no
  regions.

- [ ] **Step 6: Author the three skills, and the indexes example.** Each follows Task 5's pattern:
  - frontmatter `description` ≤ 300 characters, starting *"Use when …"*;
  - a body ≤ 6 KB;
  - tool-neutral closing lines;
  - worked examples in the `<!-- example: … -->` + two-fence format, patching `bike-workshop`, with the outcome
    abbreviated to `valid` and `changedPaths` (plus violations as fragments for a refusal).

  Take `changedPaths` from what `A_skill_example_patches_the_bike_workshop_and_touches_what_it_claims` reports
  (for an append at `/indexes/-` it is the path `JsonPatch` gives). Never guess it.
  - **`alvo-descriptor-entities-and-fields`**:
    - pointer paths;
    - rename through `move` + `renamedFrom`, never remove-and-add;
    - a removal is destructive and the operator applies it;
    - `default` is a literal or `{"$cel": …}`;
    - `hidden` / `readOnly` / `nullable`.

    Regions: `<!-- gen:entity-keys -->` and `<!-- gen:field-keys -->` (each a line of backticked keys, in schema
    order). Example `rename-technician-phone`: rename `technicians.phone` to `phone_number`.
  - **`alvo-descriptor-field-types-and-formats`**:
    - facets per type;
    - `precision` counts all digits;
    - `text` vs `string`;
    - `ref` → `entity` + `onDelete`, and a `ref` may point at `users`;
    - the built-in formats `email` / `uri` / `phone`, and a named format under `/formats`.

    Regions: `field-types` and `on-delete`. Example `add-rental-deposit-note`: an optional `text` field.
  - **`alvo-descriptor-traits-and-tenancy`**:
    - how project and entity tenancy combine (the base prompt's sentence, restated);
    - a scoped create echoes `tenant_id`;
    - `softDelete` + a field `default` is refused at apply;
    - `storage` and `realtime` are warned blocks: point to `alvo-descriptor-capabilities-and-limits`.

    Region: `managed-columns`, the four bullets of the base prompt, verbatim. Example `audit-rentals`: add
    `"audit": true` to an entity that lacks it. Choose one with `jq` over `bike-workshop`, such as `order_lines` or
    `rental_fleet`.
  - **`alvo-descriptor-indexes`**: add example `index-technician-status` (the composite index of Task 5's text) on
    `service_orders`.

- [ ] **Step 7: Run** Ai.Tests and Host.Tests → green. A region that fails prints what it should read. Fix the
  **skill**, never the source: the schema and the ports are the truth (D25).

- [ ] **Step 8: CLAUDE.md** — "Skills & guard", after its first sentence:

  *"The `alvo-descriptor-*` skills are different in kind: they teach the descriptor itself (entities, rules, hooks,
  rollups, indexes, access, capabilities), and they are **shared with the admin assistant**, which embeds these same
  directories (`MMLib.Alvo.Ai`, D33 of `docs/superpowers/specs/2026-09-29-f5-assistant-first-try-design.md`). Edit
  them as product text: their regions are drift-tested, and their size is capped."*

  Also add `.claude/skills/alvo-descriptor-*` to the repo map's `.claude/skills/` line.

- [ ] **Step 9:** Run the gates:
  - `scripts/test-ring1`;
  - `dotnet build MMLib.Alvo.slnx -c Release -warnaserror`.

  Commit `feat(ai): the descriptor skills for entities, field types, traits and indexes, held to the standard and the schema`.

---

### Task 7: The core-sourced skills, and instructions v4

**Files:**
- Create:
  - `.claude/skills/alvo-descriptor-rules-and-cel/SKILL.md`
  - `…-hooks/SKILL.md`
  - `…-computed-and-rollups/SKILL.md`
  - `…-project-access/SKILL.md`
  - `…-capabilities-and-limits/SKILL.md`
- Modify: `src/MMLib.Alvo.Ai/Instructions/schema-assistant.md` (v4: §2 tools and rule, "You cannot", §4 removed, §7),
  `src/MMLib.Alvo.Ai/Internal/AssistantInstructions.cs` (`VersionLine`)
- Modify: `test/MMLib.Alvo.Ai.Tests/SkillConformanceTests.cs` (`_areas` → nine), `SkillRegionTests.cs` (rollup ops),
  `AssistantInstructionsTests.cs` (registered tools, computed rules retargeted), `AlvoAssistantTests.cs` (budget)
- Create: `test/MMLib.Alvo.Host.Tests/SkillCoreClaimsTests.cs`
- Modify: `test/MMLib.Alvo.Host.Tests/InstructionExampleOutcomeTests.cs` (`ComputedSection()` reads the skill)

**Region formats** (the parser is `SkillCatalogue.Tokens` over each line):
```markdown
<!-- gen:cel-rule -->
- allowed: `'admin' in @user.roles` `assigned_user_id == @user.id` `status != 'collected'` `has(promised_on)`
- refused: `labour_hours * 2 > 1` `changed(status)` `now()` `old.status == 'ready'`
<!-- /gen:cel-rule -->
```
The regions are:
- `cel-rule`, `cel-condition`, `cel-mutate` and `cel-access`: one `- allowed:` line and one `- refused:` line each.
- `cel-computed`: the computed skill's own region.
- `mutate-functions`: the two names.
- `rollup-ops`: the schema enum.
- `honoured`, `warned` and `refused-actions`: each one line of backticked names.

- [ ] **Step 1: Failing core-claims tests, Host.Tests** — `SkillCoreClaimsTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Ai.Tests;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Management.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// The skill regions whose source is the core: each CEL profile's stated examples compile, or are refused, under that
/// profile (D35); the Mutate functions, and the capability names (D27), equal the tables that define them.
/// </summary>
/// <remarks>
/// The CEL table (<c>CelTypeChecker._allowedProfiles</c>) and its key are private, so the regions are held by probing
/// <see cref="CelCompiler"/>, as the Computed quotes are. That proves every claim; it does not prove the list complete,
/// and it is not meant to — a skill teaches the common constructs, and a construct it omits is refused by the tool.
/// </remarks>
public sealed class SkillCoreClaimsTests
{
    private static readonly (string Area, string Region, CelProfile Profile, string? Entity)[] _profiles =
    [
        ("rules-and-cel", "cel-rule", CelProfile.Rule, "service_orders"),
        ("hooks", "cel-condition", CelProfile.Condition, "service_orders"),
        ("hooks", "cel-mutate", CelProfile.Mutate, "service_orders"),
        ("computed-and-rollups", "cel-computed", CelProfile.Computed, "order_lines"),
        ("project-access", "cel-access", CelProfile.Access, null),
    ];

    private static readonly string[] _mutateFunctions = [CelCall.LowerAscii, CelCall.Now];

    [Fact]
    public async Task Every_stated_cel_example_compiles_exactly_where_its_skill_says()
    {
        await using var world = await AlvoHostWorld.StartAsync(InstructionExampleOutcomeTests.BikeWorkshop);
        var entities = world.Services.GetRequiredService<ISchemaRegistry>().GetSchema().Entities;

        foreach (var (area, region, profile, entity) in _profiles)
        {
            var schema = entity is null ? new EntitySchema { Name = "<project>", Fields = [] } : entities.Single(candidate => candidate.Name == entity);
            var lines = SkillCatalogue.Regions(SkillCatalogue.Named(area).Body)[region].Split('\n');
            Claims(lines, "- allowed:").ShouldAllBe(source => new CelCompiler().Compile(source, profile, schema).IsSuccess, $"{region}: allowed");
            Claims(lines, "- refused:").ShouldAllBe(source => !new CelCompiler().Compile(source, profile, schema).IsSuccess, $"{region}: refused");
        }
    }

    [Fact]
    public void The_mutate_functions_are_the_ones_the_profile_allow_lists() =>
        SkillCatalogue.Tokens(Region("hooks", "mutate-functions")).ShouldBe(_mutateFunctions);

    [Fact]
    public void The_honoured_blocks_are_the_capability_reports() =>
        SkillCatalogue.Tokens(Region("capabilities-and-limits", "honoured")).ShouldBe(CapabilityReport.Honoured);

    [Fact]
    public void The_warned_blocks_are_the_unhonoured_subsystems() =>
        SkillCatalogue.Tokens(Region("capabilities-and-limits", "warned")).ShouldBe(
            UnhonouredSubsystems.All.Concat(UnhonouredSubsystems.WithinBlocks).Concat(UnhonouredSubsystems.ReportedOnly).Select(block => block.Block));

    [Fact]
    public void The_refused_action_types_are_the_unhonoured_features() =>
        SkillCatalogue.Tokens(Region("capabilities-and-limits", "refused-actions")).ShouldBe(UnhonouredFeatures.EveryActionType);

    [Fact]
    public void The_reserved_fields_are_the_data_apis_reserved_query_keys() =>
        SkillCatalogue.Tokens(Region("entities-and-fields", "reserved-fields")).ShouldBe(ReservedQueryKeys.All, ignoreOrder: true);

    private static string Region(string area, string region) => SkillCatalogue.Regions(SkillCatalogue.Named(area).Body)[region];

    private static List<string> Claims(string[] lines, string prefix) =>
        [.. SkillCatalogue.Tokens(lines.Single(line => line.StartsWith(prefix, StringComparison.Ordinal)))];
}
```

  A few points about this code:
  - `ReservedQueryKeys` lives in `MMLib.Alvo.Api.Internal`, so add that `using`.
  - The `reserved-fields` region belongs to the entities skill, but it is tested here because it needs the core. Add
    the region to that skill in this task.
  - `CapabilityReport.Honoured` and `UnhonouredFeatures.EveryActionType` are `IReadOnlyList<string>`, so Shouldly's
    `ShouldBe(IEnumerable<string>)` compares them in order.
  - A region that fails prints its tokens. The core is the truth: fix the skill.

- [ ] **Step 2: Failing Ai.Tests changes.**
  - `SkillConformanceTests._areas` becomes the nine areas, in ordinal order.
  - `SkillRegionTests` gains:

```csharp
[Fact]
public void The_rollup_operations_are_the_schemas() =>
    Holds("computed-and-rollups", "rollup-ops", Enum(Schema()["$defs"]!["field"]!["properties"]!["rollup"]!["properties"]!["op"]!["enum"]!));
```

  `AssistantInstructionsTests`:
  - `Registered` becomes the management tools plus `AgentSkillsProvider.LoadSkillToolName` and
    `ReadSkillResourceToolName`, sorted.
  - `ToolName()` gains the verbs `load|read|run`, so a stray `run_skill_script` in the text fails
    `Every_tool_the_instructions_name_is_registered`.
  - `The_computed_section_states_every_rule_it_owes` reads `SkillCatalogue.Named("computed-and-rollups").Body`
    instead of `_text`. Rename it to `The_computed_skill_states_every_rule_it_owes`.
  - Add `The_base_prompt_no_longer_carries_the_computed_section`:
    `_text.ShouldNotContain("What Computed allows")`.

  `AlvoAssistantTests` gains the budget (spec §7.4 AC 3):

```csharp
/// <summary>
/// What the model always reads — the base prompt and the skill list — is no larger than the v3 base prompt alone
/// (spec §7.4 AC 3). Measured LF-normalised, so a CRLF checkout measures the same text.
/// </summary>
[Fact]
public async Task The_always_in_context_instructions_do_not_outgrow_the_v3_base_prompt()
{
    var model = new ScriptedChatClient(Scripted.Says("ok"));

    await RunAsync(Describing(revision: 1), Configured(), model);

    Encoding.UTF8.GetByteCount(model.Options[0].ShouldNotBeNull().Instructions.ShouldNotBeNull().ReplaceLineEndings("\n"))
        .ShouldBeLessThanOrEqualTo(AlwaysInContextBudget);
}

/// <summary>The v3 base prompt's size in UTF-8 bytes, before skills (spec §7.4 AC 3).</summary>
private const int AlwaysInContextBudget = 22_758;
```

  In `InstructionExampleOutcomeTests.ComputedSection()`, slice the skill instead:
  `SkillCatalogue.Named("computed-and-rollups").Body` from `"## What Computed allows"` to the next `"\n## "`, or to
  the end.

- [ ] **Step 3: Run** → it fails: the five skills are missing, the base prompt still has §4, and the tool list lacks
  the two tools.

- [ ] **Step 4: Author the five skills** (Task 6's pattern and caps):
  - **`alvo-descriptor-rules-and-cel`**:
    - a missing operation denies;
    - `list`/`get` filter rows, and `create`/`update` check the written row (Postgres RLS `USING` / `WITH CHECK`,
      per `docs/architecture/cel.md`);
    - role literals are validated at apply;
    - `@user.id` / `@user.roles` / `@tenant`.

    Region: `cel-rule`. Example `technician-updates-own-orders`: `rules.update` on `service_orders`, keeping the
    admin/manager grant. It is **not** the eval's `own_orders_only` list/get case, which must stay a measurement.
  - **`alvo-descriptor-hooks`**:
    - `beforeCreate`/`beforeUpdate`/`beforeDelete` with `condition` + `action` `reject` or `mutate`;
    - `old.` / `new.`, `changed(field)`;
    - hooks run in the write's transaction, with no network;
    - after-hooks and their actions are for `alvo-descriptor-capabilities-and-limits` and `get_capabilities`.

    Regions: `cel-condition`, `cel-mutate`, `mutate-functions`. Cite
    `schema/project.schema.json#/$defs/beforeHookList`. Example `order-lines-reject-zero-price`: a `reject` on
    `order_lines`. It is **not** the eval's `parts` / `rentals` cases.
  - **`alvo-descriptor-computed-and-rollups`**:
    - the ladder: computed → rollup → hook → action → csx (from `alvo-architecture-rules`);
    - base §4, moved **verbatim** under `## What Computed allows`, every `**…**` rule label intact
      (`_computedRules`);
    - rollup `from` / `op` / `field`, and a `where` is refused (quote `get_capabilities`);
    - a computed field may read a rollup and may not read another computed field.

    Regions: `cel-computed`, `rollup-ops`. Its examples are the base prompt's computed examples. They **stay** in
    the base prompt (§6), and the skill adds none of its own, so the fence count stays exact in both.
  - **`alvo-descriptor-project-access`**:
    - access levels are CEL over `@user.id` / `@user.roles` only;
    - only an administrator may change `access`, and the tool answers with the `access` violation otherwise.

    Region: `cel-access`. Cite `schema/project.schema.json#/properties/access`.
  - **`alvo-descriptor-capabilities-and-limits`**:
    - `get_capabilities` is the authority: quote it;
    - never propose an unhonoured block or action;
    - offer the lowest honoured rung;
    - never promise dates;
    - the assistant cannot read or write data rows, or apply;
    - one line each on what automation (ECA over the post-commit outbox), webhooks (Standard Webhooks, signed) and
      functions (csx) *are*, so it can explain them without offering them.

    Regions: `honoured`, `warned`, `refused-actions`. No example.

  Add the `reserved-fields` region to `alvo-descriptor-entities-and-fields`: the eight names, one line.

- [ ] **Step 5: Instructions v4.**
  - **Version.** Line 1 and `AssistantInstructions.VersionLine` → `<!-- alvo-schema-assistant v4 -->`.
  - **§2 *Your tools*.** Append:

```markdown
- `load_skill` — loads one skill from the list at the end: the rules of one area of the descriptor.
- `read_skill_resource` — reads a resource a loaded skill lists, such as a slice of the schema.
```

  - **§2, new subsection** `### Skills`, before *You can change*:

```markdown
### Skills

Before `check_change` or `propose_change` in an area, load that area's skill — in the same step as `get_descriptor`,
so it costs no extra round. For "can Alvo …?", load `alvo-descriptor-capabilities-and-limits` and call
`get_capabilities`. What a skill says outranks what you remember about Alvo or about other frameworks.
```

  - **§2 *You cannot*.** The automation/functions bullet becomes *"Author a block or an action this build does not
    honour — `alvo-descriptor-capabilities-and-limits` says which, and `get_capabilities` says it in the framework's
    words."* The data-rows and `access` bullets stay.
  - **§4.** Delete it and renumber §5–§7 to §4–§6. Update every "section N" cross-reference inside the text. In §7
    (now §6), *"comes from `get_capabilities` and section 2 only"* becomes *"comes from `get_capabilities`, section 2
    and `alvo-descriptor-capabilities-and-limits` only"*.
  - **Probe ranges.** `ComputedSection()` and `InstructionClaims` read headings, not numbers. Grep for `"## 5."`,
    `"## 6."` and `"## 7."` in `test/` and retarget each hit to the new numbers.

- [ ] **Step 6: Run** Ai.Tests, Host.Tests and Eval.Tests → green. This includes:
  - the budget. It is tight by design. The base loses §4's 4,563 bytes and gains about 0.6 KB of tool and skill
    text, and the list adds about 9 × 330 bytes plus its frame: ≈ 22.1 KB of 22,758. If it fails, trim
    descriptions first (they are read by both consumers, so shorter is better anyway), then base §6 replies. Never
    raise the budget;
  - `InstructionRepliesTests` (the base replies are unchanged);
  - the Computed probes, now over the skill.

- [ ] **Step 7:** Run the gates:
  - `scripts/test-ring1`;
  - `dotnet build MMLib.Alvo.slnx -c Release -warnaserror`.

  Commit `feat(ai): skills for rules, hooks, computed and rollups, access and capabilities, and instructions v4`.

---

### Task 8: The `SkillsRead` grader on every turn, and the split tool-call invariant

**Files:**
- Create: `eval/MMLib.Alvo.Ai.Eval/SkillsRead.cs`, `test/MMLib.Alvo.Ai.Eval.Tests/SkillsReadTests.cs`
- Modify: `eval/MMLib.Alvo.Ai.Eval/EvalRunner.cs` (`Invariants`, `Graded`)
- Modify: `eval/MMLib.Alvo.Ai.Eval/TurnRecord.cs` (`ManagementCalls`, `SkillReads`)
- Modify: `test/MMLib.Alvo.Ai.Eval.Tests/Turns.cs` (`Loads`), `EvalCasesTests.cs`

**Interfaces:**
```csharp
namespace MMLib.Alvo.Ai.Eval;
internal static partial class SkillsRead
{
    internal static IReadOnlySet<string> SkillTools { get; }                 // load_skill, read_skill_resource
    internal static string? AreaOf(string path, JsonNode? proposed);       // "hooks", "rules-and-cel", …
    internal static IReadOnlyList<string> Needed(TurnRecord turn);         // alvo-descriptor-<area>, ordinal
    internal static IReadOnlyCollection<string> Catalogue { get; }         // EmbeddedSkills.All names
    internal static Verdict Judge(TurnRecord turn);
}
// TurnRecord
internal int ManagementCalls { get; }   // ToolCalls not in SkillsRead.SkillTools
internal int SkillReads { get; }
// EvalRunner
internal const int MaximumSkillReads = 4;   // D34
```

- [ ] **Step 1: Failing tests** — `SkillsReadTests.cs`:

```csharp
using System.Text.Json.Nodes;

using static MMLib.Alvo.Ai.Eval.Tests.Turns;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>A proposal touching an area passes only when that area's skill was loaded before the first dry run (D31).</summary>
public sealed class SkillsReadTests
{
    [Theory]
    [InlineData("/entities/parts/rules/delete", "rules-and-cel")]
    [InlineData("/entities/rentals/hooks/beforeUpdate", "hooks")]
    [InlineData("/entities/order_lines/indexes", "indexes")]
    [InlineData("/entities/rentals/audit", "traits-and-tenancy")]
    [InlineData("/access", "project-access")]
    [InlineData("/entities/bikes/fields/notes", "entities-and-fields")]
    public void A_changed_path_belongs_to_its_area(string path, string area) =>
        SkillsRead.AreaOf(path, proposed: null).ShouldBe(area);

    [Fact]
    public void A_new_field_that_is_computed_or_a_rollup_belongs_to_computed_and_rollups()
    {
        SkillsRead.AreaOf("/entities/customers/fields/full_name", new JsonObject { ["computed"] = "first_name" }).ShouldBe("computed-and-rollups");
        SkillsRead.AreaOf("/entities/customers/fields/rentals_count", new JsonObject { ["rollup"] = new JsonObject() }).ShouldBe("computed-and-rollups");
    }

    [Fact]
    public void Every_area_the_grader_can_name_is_a_skill_in_the_catalogue() =>
        SkillsRead.Areas.Select(area => "alvo-descriptor-" + area).ShouldAllBe(skill => SkillsRead.Catalogue.Contains(skill));

    [Fact]
    public void A_proposal_after_loading_its_skill_passes() =>
        SkillsRead.Judge(Turn(Edited(document => document.Fields("bikes")["notes"] = new JsonObject { ["type"] = "text" }),
            "I proposed notes.", calls: [Loads("alvo-descriptor-entities-and-fields", round: 1), Propose(Valid(), round: 2)]))
            .Passed.ShouldBeTrue();

    [Fact]
    public void A_proposal_whose_skill_was_loaded_in_the_same_round_fails() =>
        SkillsRead.Judge(Turn(Edited(document => document.Fields("bikes")["notes"] = new JsonObject { ["type"] = "text" }),
            "I proposed notes.", calls: [Loads("alvo-descriptor-entities-and-fields", round: 1), Propose(Valid(), round: 1)]))
            .Passed.ShouldBeFalse();

    [Fact]
    public void A_proposal_with_no_skill_loaded_fails_and_says_which_was_needed()
    {
        var verdict = SkillsRead.Judge(Turn(
            Edited(document => document["entities"]!["parts"]!["rules"]!["delete"] = "'technician' in @user.roles"),
            "I proposed it.", calls: Propose(Valid())));

        verdict.Passed.ShouldBeFalse();
        verdict.Why.ShouldContain("alvo-descriptor-rules-and-cel");
    }

    [Fact]
    public void A_turn_with_no_proposal_needs_no_skill() =>
        SkillsRead.Judge(Turn(answer: "This build does not run automation.")).Passed.ShouldBeTrue();

    [Fact]
    public void Skill_reads_do_not_count_against_the_six_management_calls()
    {
        RecordedCall[] calls =
        [
            Read("get_descriptor", 1), Loads("alvo-descriptor-entities-and-fields", 1), Loads("alvo-descriptor-hooks", 1),
            Loads("alvo-descriptor-indexes", 1), Propose(Valid(), 2), Read("get_capabilities", 2),
        ];

        EvalRunner.Invariants(Turn(answer: "ok", calls: calls)).Passed.ShouldBeTrue();
    }

    [Fact]
    public void More_than_four_skill_reads_fail_the_invariants()
    {
        var calls = Enumerable.Range(1, 5).Select(round => Loads("alvo-descriptor-hooks", round)).ToArray();

        EvalRunner.Invariants(Turn(answer: "ok", calls: calls)).Passed.ShouldBeFalse();
    }
}
```

  In `Turns`:

```csharp
/// <summary>A <c>load_skill</c> call, answered with the skill's body as the provider wraps it.</summary>
internal static RecordedCall Loads(string skill, int round = 1) =>
    new(round, $"load_{skill}_{round}", "load_skill", new Dictionary<string, object?> { ["skillName"] = skill }, "<instructions>…</instructions>");
```

  `SkillsRead` also exposes `internal static IReadOnlyList<string> Areas` (the values `AreaOf` can return), which is
  what the catalogue fact reads.

- [ ] **Step 2: Run** → it does not compile.

- [ ] **Step 3: Implement** `SkillsRead.cs`:

```csharp
using Microsoft.Agents.AI;
using MMLib.Alvo.Ai.Internal;

using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>Whether a turn loaded the skill of every area its proposal touches, before its first dry run (D31).</summary>
/// <remarks>
/// <para>
/// "Before" means an earlier round. A <c>load_skill</c> in the same model response as the first dry run was chosen
/// before the model had read a word of it.
/// </para>
/// <para>
/// The area is read from the changed path, and for a new field from its value too: <c>/entities/x/fields/y</c> is a
/// computed field or a rollup only by what it declares. <c>field-types-and-formats</c> is never required. A field's type
/// is in every proposal, and the base prompt states the types.
/// </para>
/// </remarks>
internal static class SkillsRead
{
    private const string Prefix = "alvo-descriptor-";
    private const string SkillName = "skillName";
    private static readonly HashSet<string> _dryRuns = new(StringComparer.Ordinal) { "check_change", "propose_change" };
    private static readonly string[] _traits = ["tenancy", "audit", "softDelete"];

    internal static IReadOnlySet<string> SkillTools { get; } =
        new HashSet<string>(StringComparer.Ordinal) { AgentSkillsProvider.LoadSkillToolName, AgentSkillsProvider.ReadSkillResourceToolName };

    internal static IReadOnlyList<string> Areas { get; } =
        ["project-access", "rules-and-cel", "hooks", "indexes", "traits-and-tenancy", "computed-and-rollups", "entities-and-fields"];

    internal static IReadOnlyCollection<string> Catalogue { get; } = [.. EmbeddedSkills.All.Select(skill => skill.Frontmatter.Name)];

    internal static string? AreaOf(string path, JsonNode? proposed)
    {
        var segments = path.Split('/');
        return path switch
        {
            _ when path.StartsWith("/access", StringComparison.Ordinal) => Areas[0],
            _ when segments.Contains("rules") => Areas[1],
            _ when segments.Contains("hooks") => Areas[2],
            _ when segments.Contains("indexes") => Areas[3],
            _ when segments.Intersect(_traits).Any() || path == "/tenancy" => Areas[4],
            _ when segments.Contains("computed") || segments.Contains("rollup") || proposed?["computed"] is not null || proposed?["rollup"] is not null => Areas[5],
            _ when path.StartsWith("/entities/", StringComparison.Ordinal) => Areas[6],
            _ => null,
        };
    }

    internal static IReadOnlyList<string> Needed(TurnRecord turn) =>
        [.. turn.ChangedPaths.Select(path => AreaOf(path, turn.Proposed(path))).OfType<string>().Distinct().Select(area => Prefix + area).Order(StringComparer.Ordinal)];

    internal static Verdict Judge(TurnRecord turn)
    {
        var firstDryRun = turn.Calls.Where(call => _dryRuns.Contains(call.Tool)).Select(call => call.Round).DefaultIfEmpty(int.MaxValue).Min();
        var loaded = turn.Calls.Where(call => call.Tool == AgentSkillsProvider.LoadSkillToolName && call.Round < firstDryRun)
            .Select(Skill).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var needed = Needed(turn);
        return Verdict.When(
            needed.All(loaded.Contains),
            $"skillsNeeded=[{string.Join(",", needed)}] skillsLoaded=[{string.Join(",", loaded.Order(StringComparer.Ordinal))}]");
    }

    private static string? Skill(RecordedCall call) =>
        call.Arguments?.TryGetValue(SkillName, out var value) == true
            ? value is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : value?.ToString()
            : null;
}
```

  `Areas` is indexed by position so that `AreaOf` and the catalogue fact share one list. If the analyzer or a
  reviewer prefers names, use `const string` fields and build `Areas` from them. A real provider hands `Arguments`
  values over as `JsonElement`, and the canned turns hand them over as `string`; `Skill` reads both.

- [ ] **Step 4: The invariant split and the grader wiring.** `TurnRecord`:

```csharp
internal int ManagementCalls => ToolCalls.Count(tool => !SkillsRead.SkillTools.Contains(tool));

internal int SkillReads => ToolCalls.Count(SkillsRead.SkillTools.Contains);
```

  `EvalRunner.Invariants`:
  - The condition becomes `turn.ToolRounds <= AlvoAssistant.MaximumIterations && turn.ManagementCalls <= MaximumToolCalls && turn.SkillReads <= MaximumSkillReads && !wholeDocument`.
  - The diagnostic becomes `toolCalls={turn.ManagementCalls} skillReads={turn.SkillReads}`.
  - Add a remark: *"Skill reads are bounded apart (D34): the ≤ 6 bar predates skills, and a turn that loads what the
    instructions ask would otherwise fail for obeying them."*

  `Graded` adds `SkillsRead.Judge(turn)` to `verdicts`, after `ReplyLanguage`. Its summary gains *"and whether it
  loaded the skills its proposal needed (D31)"*. `CaseRun.Line`'s `{Turn.ToolCalls.Count} calls` stays: it is the raw
  count, and the diagnostic splits it.

- [ ] **Step 5: Run** Eval.Tests → green. The existing `A_turn_over_six_tool_calls_fails_the_invariants` still holds:
  seven `get_descriptor` calls are management calls.
- [ ] **Step 6:** Run the gates:
  - `scripts/test-ring1`;
  - `dotnet build MMLib.Alvo.slnx -c Release -warnaserror`.

  Commit `feat(eval): every turn is graded on whether it loaded the skills its proposal needed`.

---

### Task 9: The seven skill cases

**Files:**
- Modify: `eval/MMLib.Alvo.Ai.Eval/EvalCases.cs` (seven cases and their graders; the class remark)
- Modify: `test/MMLib.Alvo.Ai.Eval.Tests/Turns.cs` (`Original` gains the members the cases touch)
- Modify: `test/MMLib.Alvo.Ai.Eval.Tests/EvalCasesTests.cs`

- [ ] **Step 1: Widen `Turns.Original`.** Add these, in the shape of `bike-workshop`, reduced to the fields the
  graders read:
  - `rentals` (`status` enum, `returned_at`, `customer_id` ref);
  - `service_orders` (`status`, `assigned_user_id`, and `rules.list` / `rules.get` = `"'authenticated' in @user.roles"`);
  - `order_lines` (`order_id`, `part_id`);
  - `parts.fields.unit_price`.

  Run Eval.Tests → the existing case tests stay green over the wider descriptor.

- [ ] **Step 2: Failing case tests** — one passing and one failing canned turn per case, in `EvalCasesTests`:

```csharp
[Fact]
public void Returned_at_passes_a_mutate_of_now_when_status_becomes_returned() =>
    Grade("hook_returned_at", Turn(WithHook("rentals", "beforeUpdate", ReturnedAtHook()), "I proposed it.", calls: Propose(Valid())))
        .Passed.ShouldBeTrue();

[Fact]
public void Returned_at_fails_a_hook_that_fires_on_every_update()
{
    var hook = ReturnedAtHook();
    hook.Remove("condition");

    Grade("hook_returned_at", Turn(WithHook("rentals", "beforeUpdate", hook), "I proposed it.", calls: Propose(Valid()))).Passed.ShouldBeFalse();
}

[Fact]
public void Negative_price_passes_a_reject_hook_over_unit_price() =>
    Grade("reject_negative_price", Turn(WithHook("parts", "beforeUpdate", Reject("new.unit_price < 0.0")), "I proposed it.", calls: Propose(Valid())))
        .Passed.ShouldBeTrue();

[Fact]
public void Negative_price_fails_a_validation_facet() =>
    Grade("reject_negative_price", Turn(Edited(document => document.Fields("parts")["unit_price"]!["validation"] = "unit_price >= 0"),
        "I proposed it.", calls: Propose(Valid()))).Passed.ShouldBeFalse();

[Fact]
public void Rentals_count_passes_a_count_rollup_from_rentals() =>
    Grade("rollup_rentals_count", Turn(Edited(document => document.Fields("customers")["rentals_count"] = new JsonObject
    {
        ["type"] = "integer", ["rollup"] = new JsonObject { ["from"] = "rentals", ["op"] = "count" },
    }), "I proposed it.", calls: Propose(Valid()))).Passed.ShouldBeTrue();

[Fact]
public void Rentals_count_fails_a_hook_that_maintains_a_counter() =>
    Grade("rollup_rentals_count", Turn(WithHook("rentals", "beforeCreate", Reject("false")), "I proposed it.", calls: Propose(Valid())))
        .Passed.ShouldBeFalse();

[Fact]
public void Unique_part_passes_a_composite_unique_index() =>
    Grade("unique_part_per_order", Turn(Edited(document => document["entities"]!["order_lines"]!["indexes"] = new JsonArray(
        new JsonObject { ["fields"] = new JsonArray("order_id", "part_id"), ["unique"] = true })), "I proposed it.", calls: Propose(Valid())))
        .Passed.ShouldBeTrue();

[Fact]
public void Unique_part_fails_a_unique_part_id_across_the_table() =>
    Grade("unique_part_per_order", Turn(Edited(document => document.Fields("order_lines")["part_id"]!["unique"] = true),
        "I proposed it.", calls: Propose(Valid()))).Passed.ShouldBeFalse();

[Fact]
public void Own_orders_passes_list_and_get_that_compare_the_assignee_and_keep_the_grant() =>
    Grade("own_orders_only", Turn(WithReadRules("'admin' in @user.roles || 'manager' in @user.roles || assigned_user_id == @user.id"),
        "I proposed it.", calls: Propose(Valid()))).Passed.ShouldBeTrue();

[Fact]
public void Own_orders_fails_a_rule_that_drops_the_managers() =>
    Grade("own_orders_only", Turn(WithReadRules("assigned_user_id == @user.id"), "I proposed it.", calls: Propose(Valid())))
        .Passed.ShouldBeFalse();

[Fact]
public void Function_action_passes_no_proposal_and_a_capabilities_read() =>
    Grade("function_action_refused", Turn(answer: "> This build does not run functions.", calls: Read("get_capabilities")))
        .Passed.ShouldBeTrue();

[Fact]
public void Function_action_fails_a_proposed_function_hook() =>
    Grade("function_action_refused", Turn(WithHook("service_orders", "afterUpdate", new JsonObject
    {
        ["action"] = new JsonObject { ["type"] = "function", ["name"] = "invoice" },
    }), "I proposed it.", calls: [Read("get_capabilities"), Propose(Valid(), round: 2)])).Passed.ShouldBeFalse();

[Fact]
public void Http_question_passes_an_answer_that_read_the_capabilities() =>
    Grade("can_alvo_call_http", Turn(answer: "No: this build makes no http.call request.", calls: Read("get_capabilities"))).Passed.ShouldBeTrue();

[Fact]
public void Http_question_fails_an_answer_that_never_read_the_capabilities() =>
    Grade("can_alvo_call_http", Turn(answer: "No, it cannot.")).Passed.ShouldBeFalse();

private static JsonObject ReturnedAtHook() => new()
{
    ["condition"] = "new.status == 'returned' && old.status != 'returned'",
    ["action"] = new JsonObject { ["mutate"] = new JsonObject { ["returned_at"] = new JsonObject { ["$cel"] = "now()" } } },
};

private static JsonObject Reject(string condition) => new()
{
    ["condition"] = condition, ["action"] = new JsonObject { ["reject"] = "A part's price cannot be negative." },
};

private static string WithHook(string entity, string slot, JsonObject hook) =>
    Edited(document => document["entities"]![entity]!.AsObject()["hooks"] = new JsonObject { [slot] = new JsonArray(hook) });

private static string WithReadRules(string rule) => Edited(document =>
{
    document["entities"]!["service_orders"]!["rules"]!["list"] = rule;
    document["entities"]!["service_orders"]!["rules"]!["get"] = rule;
});
```

  Fixed arrays and objects used as test data go into `static readonly` fields where CA1861 asks.

- [ ] **Step 3: Run** → the fourteen new facts fail (`Single` finds no such case).

- [ ] **Step 4: The cases.** Append to `EvalCases.All`, with the prompts of spec §7.5 in English and these in Slovak:
  - `hook_returned_at`: *"Keď sa výpožička (rentals) vráti, nastav returned_at na aktuálny čas."*
  - `reject_negative_price`: *"Predajná cena dielu (parts) nesmie byť nikdy záporná."*
  - `rollup_rentals_count`: *"Pri zákazníkoch (customers) chcem vidieť, koľko majú výpožičiek."*
  - `unique_part_per_order`: *"Jeden diel sa smie na jednej zákazke objaviť iba raz."*
  - `own_orders_only`: *"Technici môžu vidieť a čítať iba zákazky (service_orders), ktoré sú pridelené im; admini a manažéri naďalej všetky."*
  - `function_action_refused`: *"Keď je zákazka hotová (ready), spusti našu fakturačnú funkciu."*
  - `can_alvo_call_http`: *"Vie Alvo zavolať HTTP API nášho ERP, keď sa zmení sklad dielu?"*

  The graders, each ≤ ~25 lines, reading outcomes as the existing ones do:
  - **`HookReturnedAt`**: valid and exactly one changed path under `/entities/rentals/hooks/beforeUpdate`. The hook's
    `condition` (collapsed with `ReplyText`'s whitespace rule) contains `new.status=='returned'` or `changed(status)`.
    Its `action.mutate.returned_at.$cel` is `now()`.
  - **`RejectNegativePrice`**: valid; every changed path is under `/entities/parts/hooks/`; some hook's `condition`
    mentions `unit_price` and has a `reject`; no changed path ends `/validation`.
  - **`RollupRentalsCount`**: valid, and exactly one changed path, `/entities/customers/fields/<name>`, whose value
    has `rollup.from == "rentals"` and `rollup.op == "count"`. No path contains `/hooks`.
  - **`UniquePartPerOrder`**: valid, and some `order_lines.indexes[]` has `unique: true` and exactly the fields
    `order_id` and `part_id`, in any order. `part_id.unique` is not `true`.
  - **`OwnOrdersOnly`**: valid, and the changed paths are exactly `…/service_orders/rules/get` and `…/rules/list`.
    Each (collapsed) contains `assigned_user_id==@user.id`, `'admin'in@user.roles` and `'manager'in@user.roles`.
  - **`FunctionActionRefused`**: no valid proposal carries an action `type` of `function` (walk every
    `hooks.after*[]`), `get_capabilities` is among `turn.ToolCalls`, and the answer is non-empty.
  - **`CanAlvoCallHttp`**: `turn.Proposal is null`, `get_capabilities` is among the calls, and the answer does not
    claim the capability. The claim check reuses `_affirmatives` (a new `static readonly string[]`: `"yes, "`,
    `"áno, "`, `"it can "`, `"dokáže "`) at the reply's start, over `ReplyText.OwnProse`.

  Update the class remark to *"…plus the first-try design's two (D15, D16) and its seven skill cases (§7.5, D36)"*.

- [ ] **Step 5: Run** Eval.Tests → green. A grader that fails its own passing turn is fixed in the grader. A passing
  turn that is wrong about Alvo is fixed in the turn, after checking the claim against `bike-workshop` and the
  validator: the case must be answerable right.
- [ ] **Step 6: Prove each new case is right-answerable against the real host** (in no ring, and no model is
  needed). Add a Host.Tests theory: the passing canned proposal's operations for each proposing case, dry-run through
  `DescriptorDraft.BuildAsync` as `InstructionNameClaimsTests.AttemptAsync` does, is `Valid`. A case whose right
  answer the validator refuses would grade every model a failure.

  Write the five proposals as JSON Patch in the theory's data:
  - `hook_returned_at`;
  - `reject_negative_price`;
  - `rollup_rentals_count`;
  - `unique_part_per_order`;
  - `own_orders_only`.

  `function_action_refused`'s wrong answer must be **refused** (`function` is in `UnhonouredFeatures.EveryActionType`):
  assert that too.
- [ ] **Step 7:** Run the gates:
  - `scripts/test-ring1`;
  - `dotnet build MMLib.Alvo.slnx -c Release -warnaserror`.

  Commit `feat(eval): seven skill cases — hooks, a rollup, an index, row rules and two capability answers`.

---

### Task 10: Runnable, documented, and gated

**Files:**
- Modify: `CLAUDE.md` (the eval paragraph's case count), `scripts/eval-assistant` (header comment)
- Modify: `test/MMLib.Alvo.Ai.Eval.Tests/EvalCasesTests.cs` (the suite size and `--case` parsing)

- [ ] **Step 1: The cases are runnable.** Add these facts:

```csharp
[Fact]
public void The_suite_has_the_reliability_first_try_and_skill_cases() => EvalCases.All.Count.ShouldBe(16);

[Theory]
[InlineData("hook_returned_at")]
[InlineData("reject_negative_price")]
[InlineData("rollup_rentals_count")]
[InlineData("unique_part_per_order")]
[InlineData("own_orders_only")]
[InlineData("function_action_refused")]
[InlineData("can_alvo_call_http")]
public void A_skill_case_can_be_asked_for_by_name(string name) =>
    EvalOptions.Parse(["--repository", ".", "--endpoint", "http://localhost:11434/v1", "--model", "m", "--case", name]).Case
        .ShouldNotBeNull().Name.ShouldBe(name);
```

  Adjust the argument list to what `EvalOptions.Parse` requires (read `Required(...)` first). Then:
  - `dotnet build eval/MMLib.Alvo.Ai.Eval -c Release -warnaserror` → green;
  - `scripts/eval-assistant --help` → the usage prints.

  Do **not** run the eval against a model. The real-model run is the maintainer's step (D8): it needs his endpoint
  and key. No `docs/assistant-evals.md` is created here.

- [ ] **Step 2: Document the command.**
  - `CLAUDE.md`, the eval paragraph: *"…asks a real model the sixteen cases of `…-reliability-design.md` §4.2 and
    `docs/superpowers/specs/2026-09-29-f5-assistant-first-try-design.md` §3 and §7.5 over the real host and grades
    outcomes, plus the reply's wording, its language, and whether it loaded the skills its proposal needed…"*
  - `scripts/eval-assistant` header: *"asks the reliability and first-try designs' sixteen cases in English and
    Slovak, and grades outcomes (the proposal, the dry-run violations, the calls), the reply's wording and language,
    and the skills each turn loaded."*
  - Add a usage line: `scripts/eval-assistant --endpoint … --model … --case hook_returned_at` runs one skill case.

  Sweep for the claim, not the file: `git grep -n -i "nine cases\|seven cases\|grades outcomes"`, and correct every
  live hit. The reliability plan is history: leave it.

- [ ] **Step 3: The pack carries the skills.**

  Run
  `dotnet pack src/MMLib.Alvo.Ai -c Release -o "$SCRATCH/pack"`
  then
  `unzip -p "$SCRATCH"/pack/MMLib.Alvo.Ai.*.nupkg lib/net10.0/MMLib.Alvo.Ai.dll | strings | grep -c 'MMLib.Alvo.Ai.Skills/alvo-descriptor-[a-z-]*/SKILL.md'`
  → `9`. `grep -c 'MMLib.Alvo.Ai.Schema/project.schema.json'` → `1`.

  `$SCRATCH` is the session scratchpad. Do not write artifacts into the repository.

- [ ] **Step 4: Final gates**, all green:
  - `scripts/test-ring1`.
  - `scripts/test-ring2`. The two known `PagingPerformanceTests` Npgsql *connect* timeouts are infrastructure: read
    the trace before calling either one a regression.
  - `dotnet build MMLib.Alvo.slnx -c Release -warnaserror` → 0 warnings, 0 errors.
  - `docker build -f src/MMLib.Alvo.Host/Dockerfile .` → succeeds (the guard target proves the skills were in the
    context).
  - `git diff --stat main -- '*.verified.*'` → empty (no PublicApi baseline moved).
  - `git diff --stat main -- src/MMLib.Alvo.Admin`:
    - empty → `scripts/test-admin-e2e` is not a gate;
    - non-empty → run `scripts/test-admin-e2e` **whole** and have it green before going on.

- [ ] **Step 5:** Commit `docs(eval): the sixteen cases, and how to run a skill case`.

The controller runs `alvo-plan-guard` and builds the PR report (`alvo-pr-report`) after this task. Neither is a task
step.

---

## Self-review

- **Spec coverage:** scope 1 → Task 1 (pattern, reserved, managed columns, probes, schema description); scope 2 →
  Task 2; scope 3 → Task 3; scope 4 → Task 4; scope 5 (drawer) → no task, D18, checked by Task 4 step 10's diff.
- **AC map:** AC1–2 Task 1; AC3 Task 2 step 5; AC4 Tasks 2–3 fixtures (*je vytvorená*, *is created*, *Pojďme*,
  English-to-Slovak, quote-then-Slovak); AC5 Task 4; AC6 unchanged `EvalReport.SuitePasses`; AC7 Task 4 step 10.
- **Windows/Release traps:** every new Markdown regex tolerates `\r`; no `.Matches(…).Count`; the one culture-sensitive
  interpolation goes through `string.Create(CultureInfo.InvariantCulture, …)`.
- **Skills extension (spec §7):**
  - D22, D23, D26 and D28 → Task 5; D24, D25, D32 and D33 → Tasks 5–6; D27, D29, D30 and D35 → Task 7; D31 and
    D34 → Task 8; D36 → Task 9.
  - AC 1–2 and 5–6 → Tasks 5–7 (conformance, sizes, regions, examples, the embed equality and the image); AC 3
    (budget) → Task 7; AC 4 → Task 5; AC 7 → Task 7; AC 8 → Tasks 8–10; AC 9 → Task 10's gates.
  - The real-model run is the maintainer's (D8). No task creates `docs/assistant-evals.md`.
- **Assumptions the draft design made that the 1.22.0 assembly contradicted:**
  - `run_skill_script` is advertised even with no scripts → `ReadOnlySkillsProvider`.
  - An approval halt is `ToolApprovalRequestContent` in M.E.AI 10.10, not `FunctionApprovalRequestContent`.
  - `CelTypeChecker._allowedProfiles` and `CelConstructKind` are `private` → probes (D35).
  - `%(RecursiveDir)` is backslashed on Windows → the loader normalises it.
