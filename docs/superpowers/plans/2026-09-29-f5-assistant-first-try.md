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
spec's §7, *Skills (scope extension, 2026-09-29)*, and its deviations D22–D36. Tasks 11–14 build §8, *RCA of the
task-management turn (2026-09-30)*, and its decisions D41–D46. Tasks 15–17 build §9, *RCA 2: the turn that stopped
with attempts left (2026-09-30)*, and its decisions D47–D53.

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

---

## RCA extension — Tasks 11–14 (spec §8, D41–D46)

**Goal:** fix what the RCA of the task-management turn (spec §8) proved and the maintainer approved in this PR.
- **S1**, the refusal budget measures progress and never answers "refused" for an attempt it did not check (T11).
- **S3**, the CEL compiler names a whole expression wrapped in quotes and echoes the source (T12).
- **S2**, the instructions and the rules skill teach a rule's JSON shape, a whole new entity, and `<x>_id` ref naming
  (T13).
- **S7**, every turn leaves a trace: in the log, and in the dashboard for the operator who asked (T14).

S4, S5, S6 and S8 are #292 and out of scope.

**Architecture:**
- T11 is `ManagementTools` state plus one outcome flag.
- T12 is one refusal branch in the core's `CelCompiler`, which is **security core**.
- T13 is Markdown with drift and outcome tests.
- T14 adds an internal `TurnRecorder`, `TurnTrace` and `SecretScrub` to `MMLib.Alvo.Ai`, and two log events. Two
  internal members of Abstractions carry the trace to `MMLib.Alvo.Admin`, whose drawer shows it (D45, D46).

**Evidence base:** `…/scratchpad/rca/analysis.md` and `probe-out.txt` (the RCA's probe over real
`AlvoHostWorld`/`DescriptorDraft`/`ManagementTools`/validator/`CelCompiler`). Every line number below was re-read on
this branch at `c440388`.

### Global Constraints, added for Tasks 11–14

Everything above still holds, with these amendments:
- **The budget text changes (D41).** It supersedes the reliability plan's "tool set, caps, budget text:
  unchanged" for the budget only. The tool set and the 12-iteration cap are unchanged.
- **Instructions v5.** The first task that edits `schema-assistant.md` (T11) moves both the resource's first line and
  `AssistantInstructions.VersionLine` to `<!-- alvo-schema-assistant v5 -->`. T13 edits stay under v5.
- **`MMLib.Alvo.Admin` is touched in T14 (D46), so `scripts/test-admin-e2e` whole is a gate for T14.** This lifts
  the earlier "do not touch Admin" constraint for T14 only.
- **Public API (D45):** no public type or member is added anywhere.
  - The only baseline allowed to move is `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`,
    and only by the four `InternalsVisibleTo` lines T14 adds.
  - The turn-review gate will stop on that growth. Answer it against `alvo-architecture-rules` with D45's text:
    grants only, no symbol.
  - Any other `*.verified.*` diff means the change is wrong.
- **Security core (T12):** `CelCompiler` is the CEL compile boundary.
  - Apply the `alvo-security-core-review` checklist.
  - The PR report marks T12 **needs-deep-review**.
  - `/security-review` is user-only in this environment, so the controller dispatches a reviewer subagent and labels
    it a substitute.
- **These lessons from this branch still apply to every step:**
  - Release `-warnaserror` catches CA1861 (a constant array argument becomes a `static readonly` field), CA1875
    (`Regex.Count`), CA1859 (return the concrete type), CA1870 (`SearchValues`/`string` overloads) and IDE0065
    (`using`s above `namespace`).
  - Windows CI checks out CRLF: every regex over Markdown ends a line with `\r?$` or captures `[^\r\n]`, and text is
    `.ReplaceLineEndings("\n")` before it is parsed or measured.
  - A `$$"""…"""` raw string must not contain a literal `}}` or `{{`. A JSON object nested to its end is exactly
    that, so build it with `JsonObject`/`JsonArray` or concatenate.
  - Culture-sensitive interpolation goes through `string.Create(CultureInfo.InvariantCulture, …)`.
  - `.cs` files are UTF-8 with BOM and CRLF, and a file written by a shell tool is normalised before commit.
  - Skill files are UTF-8 without BOM, LF.
- **Write the failing facts first**, run them red, then implement.
- **Every task ends with**, all green:
  - `scripts/test-ring1`;
  - `dotnet build MMLib.Alvo.slnx -c Release -warnaserror`, with 0 warnings and 0 errors.
- **T14 also runs**:
  - `scripts/test-ring2` (the two known `PagingPerformanceTests` Npgsql *connect* timeouts are infrastructure: read
    the trace first);
  - `scripts/test-admin-e2e` **whole**;
  - `docker build -f src/MMLib.Alvo.Host/Dockerfile .`.
- **Commits:** one per task, Conventional Commits, ending with
  `Claude-Session: https://claude.ai/code/session_01TrcGmun8WAuVxN5FYAtu6H`. Never push. Never switch branch. One
  writer in the worktree.

---

### Task 11: The refusal budget measures progress, and never calls an unchecked attempt refused (S1, D41)

**Files:**
- Modify: `src/MMLib.Alvo.Ai/Internal/ManagementTools.cs` (the constants, the state, `AttemptAsync`, `Record`, and
  the class remarks)
- Modify: `src/MMLib.Alvo.Ai/Internal/ChangeOutcome.cs` (`Unchecked`, `BudgetSpent`, `ToolViolation.Key`)
- Modify: `src/MMLib.Alvo.Ai/Internal/ViolationMapping.cs` (`BudgetSpent(lastRefusals)`, `UncheckedLead`)
- Modify: `src/MMLib.Alvo.Ai/AlvoAssistant.cs` (the "bounded twice" remark only)
- Modify: `src/MMLib.Alvo.Ai/Instructions/schema-assistant.md` (the version line; §4 *Reading a refusal*, last bullet;
  §6's stop rule); `src/MMLib.Alvo.Ai/Internal/AssistantInstructions.cs` (`VersionLine`)
- Modify: `eval/MMLib.Alvo.Ai.Eval/TurnRecord.cs` (`RefusedAttempts` skips an `unchecked` answer)
- Test: `test/MMLib.Alvo.Ai.Tests/ManagementToolsTests.cs` (lines 414–459: the two budget facts rewritten, three new
  facts), `test/MMLib.Alvo.Ai.Tests/AssistantInstructionsTests.cs` (`_toolFacts`),
  `test/MMLib.Alvo.Ai.Eval.Tests/EvalCasesTests.cs` (one fact)

**Interfaces:**
```csharp
// ManagementTools
internal const int MaximumStalledRefusals = 3;   // replaces MaximumRefusedAttempts; a refusal with no progress spends one
internal const int MaximumRefusals = 6;          // every refusal counts; the hard ceiling per turn
// ChangeOutcome — a trailing optional member; null is omitted by ToolJson (WhenWritingNull)
internal sealed record ChangeOutcome(bool Valid, int Revision, ManagementPlanSummary? Plan, IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<ToolViolation> Violations, int AttemptsLeft, bool? Unchecked = null);
internal static ChangeOutcome BudgetSpent(int revision, IReadOnlyList<string> lastRefusals);
// ToolViolation
internal string Key { get; }   // Source, Pointer, Code, Message joined by U+001F: what "the same violation" means
// ViolationMapping
internal const string UncheckedLead = "This attempt was not checked: the turn's refusal budget is spent.";
internal static ToolViolation BudgetSpent(IReadOnlyList<string> lastRefusals);
```

- [ ] **Step 1: The failing facts.** In `ManagementToolsTests`, replace
  `The_fourth_attempt_after_three_refusals_gets_only_the_budget_violation` (lines 413–442) and add the three new facts
  below it. `A_spent_budget_reports_the_revision…` (444–459) keeps its body, with `MaximumRefusedAttempts` renamed to
  `MaximumStalledRefusals`. Three stale refusals have one identical violation, so they stall, and the fourth, identical
  call is still answered unchecked with revision 7.

```csharp
    /// <summary>
    /// Three identical refusals spend the budget; the same patch a fourth time is not dry-run, and says so.
    /// </summary>
    /// <remarks>
    /// The RCA's turn (spec §8.1 step 4): the fourth call was never checked, and the old message invited the model to
    /// tell the operator it was refused. The answer now says it was not checked and carries the last checked refusal.
    /// </remarks>
    [Fact]
    public async Task The_same_patch_after_three_stalled_refusals_is_answered_unchecked_with_the_last_refusal()
    {
        var management = Serving(Descriptor);
        Refusing(management, Refusal("gross"));
        var tools = ManagementTools.For(management, Project);

        var left = new List<int>();
        for (var attempt = 0; attempt < ManagementTools.MaximumStalledRefusals; attempt++)
        {
            left.Add(JsonNode.Parse(await InvokeAsync(tools, "check_change", Change("check_change", AddNotes)))!["attemptsLeft"]!.GetValue<int>());
        }

        var fourth = JsonNode.Parse(await InvokeAsync(tools, "propose_change", Change("propose_change", AddNotes)))!;

        left.ShouldBe([2, 1, 0]);
        fourth["unchecked"]!.GetValue<bool>().ShouldBeTrue();
        var budget = fourth["violations"]!.AsArray().Single()!;
        budget["source"]!.GetValue<string>().ShouldBe("budget");
        budget["message"]!.GetValue<string>().ShouldStartWith(ViolationMapping.UncheckedLead);
        budget["message"]!.GetValue<string>().ShouldContain("/entities/bikes/fields/gross: No.");
        await management.ReceivedWithAnyArgs(3).ApplyDescriptorAsync(default!, default!, Ct);
    }

    /// <summary>A refusal that fixed something spends no attempt, so a model converging on a fix reaches it.</summary>
    [Fact]
    public async Task Refusals_that_make_progress_spend_no_attempt_and_a_valid_fourth_is_filed()
    {
        var management = Serving(Descriptor);
        var answers = new Queue<string[]>(_progressingRefusals);
        management.ApplyDescriptorAsync(Project, Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Answer(answers.Dequeue()));
        var tools = ManagementTools.For(management, Project);

        var left = new List<int>();
        foreach (var field in _fourFields.Take(3))
        {
            left.Add(JsonNode.Parse(await InvokeAsync(tools, "propose_change", Change("propose_change", Adding(field))))!["attemptsLeft"]!.GetValue<int>());
        }

        var fourth = JsonNode.Parse(await InvokeAsync(tools, "propose_change", Change("propose_change", Adding(_fourFields[3]))))!;

        left.ShouldBe([2, 2, 2]);
        fourth["valid"]!.GetValue<bool>().ShouldBeTrue();
        tools.Proposal!.Refusals.ShouldBeEmpty();
    }

    /// <summary>With the three attempts spent, a patch not yet checked is still dry-run, and a valid one is filed.</summary>
    [Fact]
    public async Task A_new_patch_after_the_attempts_are_spent_is_still_dry_run()
    {
        var management = Serving(Descriptor);
        Refusing(management, Refusal("gross"));
        management.ApplyDescriptorAsync(
                Project, Arg.Is<ManagementApplyRequest>(request => request.DescriptorJson.Contains("\"colour\"", StringComparison.Ordinal)), Arg.Any<CancellationToken>())
            .Returns(new ManagementApplyResult(Applied: false, Revision, EmptyPlan));
        var tools = ManagementTools.For(management, Project);
        for (var attempt = 0; attempt < ManagementTools.MaximumStalledRefusals; attempt++)
        {
            await InvokeAsync(tools, "propose_change", Change("propose_change", AddNotes));
        }

        var fresh = JsonNode.Parse(await InvokeAsync(tools, "propose_change", Change("propose_change", Adding("colour"))))!;

        fresh["valid"]!.GetValue<bool>().ShouldBeTrue();
        fresh["unchecked"].ShouldBeNull();
        tools.Proposal!.Refusals.ShouldBeEmpty();
    }

    /// <summary>Six refusals end the turn's dry runs even when every one of them changed something (D41's ceiling).</summary>
    [Fact]
    public async Task Six_refusals_are_the_most_a_turn_dry_runs_even_when_each_made_progress()
    {
        var management = Serving(Descriptor);
        var calls = 0;
        management.ApplyDescriptorAsync(Project, Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Throws(_ => Refusal(calls++ % 2 == 0 ? "a" : "b"));
        var tools = ManagementTools.For(management, Project);
        for (var attempt = 0; attempt < ManagementTools.MaximumRefusals; attempt++)
        {
            await InvokeAsync(tools, "check_change", Change("check_change", Adding(string.Create(CultureInfo.InvariantCulture, $"f{attempt}"))));
        }

        var seventh = JsonNode.Parse(await InvokeAsync(tools, "check_change", Change("check_change", Adding("f6"))))!;

        seventh["unchecked"]!.GetValue<bool>().ShouldBeTrue();
        await management.ReceivedWithAnyArgs(ManagementTools.MaximumRefusals).ApplyDescriptorAsync(default!, default!, Ct);
    }
```

  Add these helpers beside `Change`. The static fields avoid CA1861.

```csharp
    private static readonly string[][] _progressingRefusals = [["a", "b"], ["a"], ["c"], []];
    private static readonly string[] _fourFields = ["one", "two", "three", "four"];

    /// <summary>A refusal at one field of <c>bikes</c> per name — the blocking set a test controls.</summary>
    private static DescriptorValidationException Refusal(params string[] fields) =>
        new(new DescriptorValidationResult(
            [.. fields.Select(field => new DescriptorValidationError("/entities/bikes/fields/" + field, "No.", null, DescriptorValidationSeverity.Error))]));

    /// <summary>The dry run's answer: valid when nothing is refused, else the refusal.</summary>
    private static ManagementApplyResult Answer(string[] refused) =>
        refused.Length == 0 ? new ManagementApplyResult(Applied: false, Revision, EmptyPlan) : throw Refusal(refused);

    /// <summary>A one-operation patch adding a text field — built, not written as a raw string, so no brace run closes it.</summary>
    private static string Adding(string field) =>
        new JsonArray(new JsonObject
        {
            ["op"] = "add",
            ["path"] = "/entities/bikes/fields/" + field,
            ["value"] = new JsonObject { ["type"] = "text" },
        }).ToJsonString();
```

  - `using System.Globalization;` goes into the file's `System.*` block.
  - `.Throws(Func<CallInfo, Exception>)` is `NSubstitute.ExceptionExtensions`, which the file already imports.
  - In `Refusals_that_make_progress…`, the first refusal is stalled against the empty set, so 2. `{a}` is not a
    superset of `{a, b}`, so 2. `{c}` is not a superset of `{a}`, so 2.

  In `AssistantInstructionsTests._toolFacts`, replace `"spends one of the same three attempts"` with
  `"only when it makes no progress"`, and add `"`\"unchecked\": true`"`.

  In `EvalCasesTests`, add this fact. It is built from `Turns.Turn`, `Turns.Propose` and `Turns.Refused`, which
  exist:

```csharp
    [Fact]
    public void An_unchecked_budget_answer_is_no_refused_attempt()
    {
        var budget = Turns.Refused("budget", "This attempt was not checked: the turn's refusal budget is spent.");
        budget["unchecked"] = true;

        Turns.Turn(answer: "ok", calls: [Turns.Propose(Turns.Refused("validation", "No."), round: 1), Turns.Propose(budget, round: 2)])
            .RefusedAttempts.ShouldBe(1);
    }
```

- [ ] **Step 2: Run** `dotnet test --project test/MMLib.Alvo.Ai.Tests` and
  `dotnet test --project test/MMLib.Alvo.Ai.Eval.Tests`. They fail to compile (`MaximumStalledRefusals`,
  `UncheckedLead`), and that is the red state.

- [ ] **Step 3: Implement.**
  1. In `ChangeOutcome.cs`:
     - add `bool? Unchecked = null` as the record's last positional member, with a `<param>`: *"`true` only on the
       budget answer: this attempt was not dry-run, so nothing about it is known."*;
     - change `BudgetSpent(int revision, IReadOnlyList<string> lastRefusals)` to
       `new(Valid: false, revision, Plan: null, [], [ViolationMapping.BudgetSpent(lastRefusals)], AttemptsLeft: 0, Unchecked: true)`;
     - add to `ToolViolation`:
       `internal string Key => string.Join('\u001f', Source, Pointer, Code ?? string.Empty, Message);`
  2. In `ViolationMapping.cs`, replace `BudgetSpent()`:

```csharp
    internal const string UncheckedLead = "This attempt was not checked: the turn's refusal budget is spent.";

    internal static ToolViolation BudgetSpent(IReadOnlyList<string> lastRefusals) => new(
        ToolViolation.Budget, string.Empty,
        $"{UncheckedLead} The last checked attempt was refused with: {string.Join(" | ", lastRefusals)}. "
        + "Quote that refusal to the operator; never call this attempt refused.",
        Fix: null, Code: AttemptsExhaustedCode);
```

  3. In `ManagementTools.cs`:
     - rename the constant;
     - add `MaximumRefusals`;
     - replace `_refusedAttempts` with the following, and change `AttemptsLeft` and the attempt path:

```csharp
    private readonly List<(int BaseRevision, JsonElement Operations)> _checked = [];
    private HashSet<string> _lastBlocking = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _lastRefusals = [];
    private int _stalledRefusals;
    private int _refusals;

    private int AttemptsLeft =>
        Math.Max(0, Math.Min(MaximumStalledRefusals - _stalledRefusals, MaximumRefusals - _refusals));

    private async Task<(DraftAttempt? Attempt, ChangeOutcome Outcome)> AttemptAsync(
        int baseRevision, JsonElement operations, CancellationToken ct)
    {
        if (Unchecked(baseRevision, operations))
        {
            return (null, ChangeOutcome.BudgetSpent(_currentRevision, _lastRefusals));
        }

        var attempt = await DescriptorDraft.BuildAsync(_management, _project, baseRevision, operations, ct).ConfigureAwait(false);
        Record(attempt, baseRevision, operations);

        return (attempt, ChangeOutcome.From(attempt, AttemptsLeft));
    }

    /// <summary>Whether this attempt is answered without a dry run: the ceiling is spent, or the budget is and it was already checked.</summary>
    private bool Unchecked(int baseRevision, JsonElement operations) =>
        _refusals >= MaximumRefusals
        || (_stalledRefusals >= MaximumStalledRefusals
            && _checked.Exists(done => done.BaseRevision == baseRevision && JsonElement.DeepEquals(done.Operations, operations)));

    private void Record(DraftAttempt attempt, int baseRevision, JsonElement operations)
    {
        _currentRevision = attempt.CurrentRevision;
        _checked.Add((baseRevision, operations.Clone()));
        if (!attempt.Valid)
        {
            Refused(attempt);
        }
    }

    /// <summary>Counts a refusal, and spends an attempt only when it made no progress (D41).</summary>
    private void Refused(DraftAttempt attempt)
    {
        var blocking = attempt.Violations.Where(violation => violation.Blocks).Select(violation => violation.Key).ToHashSet(StringComparer.Ordinal);
        _refusals++;
        _stalledRefusals += blocking.IsSupersetOf(_lastBlocking) ? 1 : 0;
        (_lastBlocking, _lastRefusals) = (blocking, attempt.Refusals);
    }
```

     - rewrite the class remarks' budget paragraphs to D41:
       - *"A refusal spends one of three attempts only when it made no progress: the same blocking violations as the
         refusal before it, or more."*
       - *"Six refusals are the ceiling, progress or not."*
       - *"A patch not yet checked is always dry-run before a budget answer, and that answer says it was not checked
         (`unchecked: true`)."*
     - the "sequential invocation" paragraph stays true; say that the new fields are plain state too.
  4. In `AlvoAssistant.cs`, the remark becomes *"The tools stop dry-running after three refusals that made no
     progress, or six in all (D41)…"*.
  5. In `schema-assistant.md`:
     - version line `v5`;
     - replace the last bullet of *Reading a refusal* with:

```markdown
- A refusal spends one of three attempts only when it makes no progress — the same blocking violations as the
  refusal before it, or more; `attemptsLeft` says how many remain, and a turn ends after six refusals in all. An
  answer with `"unchecked": true` was **not** dry-run: never call that attempt refused — quote the last checked
  refusal its message carries, and stop.
```

     - in §6, the refusal bullet's *"After three refused attempts — or at once, …"* becomes
       *"When `attemptsLeft` is 0 or a violation's `source` is `budget` — or at once, …"*.
  6. In `AssistantInstructions.cs`: `VersionLine = "<!-- alvo-schema-assistant v5 -->"`.
  7. In `eval/MMLib.Alvo.Ai.Eval/TurnRecord.cs`, change `RefusedAttempts` to
     `Outcomes.Where(outcome => outcome["unchecked"] is null).Count(outcome => !(…valid…))`, keeping the existing
     predicate. Remark: *"An `unchecked` budget answer was no dry run (D41)."*

- [ ] **Step 4: Run** Ai.Tests and Eval.Tests, which should be green. Then run Host.Tests: the worked examples' claimed
  `"attemptsLeft": 2` (examples (e) and (f)) still hold, because a first refusal is stalled against the empty set.
  `git grep -n "MaximumRefusedAttempts\|Stop proposing"` should find nothing live outside `docs/`.
- [ ] **Step 5: Measure the always-in-context budget.** `AlvoAssistantTests.The_always_in_context_instructions…`
  must stay green, because §4 grew by about 150 bytes. Record the measured size in the commit body (see T13 step 0
  for how to read it).
- [ ] **Step 6: Gates and commit.**
  - Run `scripts/test-ring1`.
  - Run `dotnet build MMLib.Alvo.slnx -c Release -warnaserror`.
  - Commit `fix(ai): the refusal budget measures progress, and an unchecked attempt is never called refused`.

---
### Task 12: A rule wrapped whole in quotes gets its own refusal, and every result-type refusal echoes its source (S3, D42)

**Security core** (CEL compile): apply the `alvo-security-core-review` checklist, and mark the PR report
*needs-deep-review*. The accepted set must not move. The change only ever rewrites the text of a refusal the
compiler already returns.

**Files:**
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelCompiler.cs` (`Compile`, `CheckAndAssemble`,
  `AppendResultTypeError`, `ValidateResultType`, plus a private `Authored` record struct and `Echo`)
- Modify: `docs/architecture/cel.md` (one sentence in the result-type section)
- Test: `test/MMLib.Alvo.Tests/Expressions/CelCompilerResultTypeTests.cs`

**Nothing quotes the changed text today.** `git grep -n "must evaluate to a boolean\|Add a comparison"` finds it
only in `CelCompiler.cs` and a historical plan. `alvo-descriptor-computed-and-rollups` quotes *"A computed-field
expression must evaluate to a non-boolean scalar"*, and `CelProfileTests.cs:95` asserts `"non-boolean scalar"`. Both
are kept, because every message keeps its first sentence as its prefix. T13 adds the one new quote, the fix's
*"Remove the outer quotes"*, and a fact that holds it to the dry run.

**Interfaces:**
```csharp
namespace MMLib.Alvo.Expressions.Internal;
internal sealed class CelCompiler : ICelCompiler
{
    internal const int EchoLength = 120;                                   // an echoed source is cut here, plus "…"
    internal const string QuotedFixLead = "Remove the outer quotes; the value is the expression itself: ";
    public CelCompilationResult Compile(string source, CelProfile profile, EntitySchema entity);   // unchanged contract
}
```

- [ ] **Step 1: The failing facts**, added to `CelCompilerResultTypeTests`. `CelFixtures.Orders` has `owner_id`
  (a nullable uuid) and `title`. `owner_id == @user.id` compiles under `Rule` (`CelCompilerTests.cs:16`).

```csharp
    /// <summary>
    /// A rule wrapped whole in quotes is a string, and the refusal says to remove the quotes — not to add a comparison it
    /// already has (spec §8.1 step 3; the three shapes the RCA's model sent).
    /// </summary>
    [Theory]
    [InlineData("'owner_id == @user.id'", "owner_id == @user.id")]
    [InlineData("\"owner_id == @user.id\"", "owner_id == @user.id")]
    [InlineData("'true'", "true")]
    public void A_predicate_wrapped_whole_in_quotes_is_refused_with_the_quotes_named(string source, string content)
    {
        var result = _compiler.Compile(source, CelProfile.Rule, CelFixtures.Orders);

        result.IsSuccess.ShouldBeFalse();
        var error = result.Errors.ShouldHaveSingleItem();
        error.Message.ShouldStartWith("A Rule expression must evaluate to a boolean");
        error.Message.ShouldContain(source);
        error.FixSuggestion.ShouldBe(CelCompiler.QuotedFixLead + content);
    }

    /// <summary>A quoted string whose content is no predicate keeps the generic fix: there is nothing to unwrap to.</summary>
    [Theory]
    [InlineData("'admin'")]
    [InlineData("\"'true'\"")]
    public void A_quoted_string_that_is_no_quoted_predicate_keeps_the_generic_fix(string source) =>
        _compiler.Compile(source, CelProfile.Rule, CelFixtures.Orders).Errors.ShouldHaveSingleItem()
            .FixSuggestion.ShouldNotStartWith("Remove the outer quotes");

    /// <summary>Access and Condition are predicates too, and get the same refusal.</summary>
    [Theory]
    [InlineData(CelProfile.Access)]
    [InlineData(CelProfile.Condition)]
    public void Every_predicate_profile_names_a_quoted_predicate(CelProfile profile) =>
        _compiler.Compile("'true'", profile, CelFixtures.Orders).Errors.ShouldHaveSingleItem()
            .FixSuggestion.ShouldBe(CelCompiler.QuotedFixLead + "true");

    /// <summary>Every result-type refusal quotes the source it refused, cut at a bound so a long one cannot flood the answer.</summary>
    [Fact]
    public void A_result_type_refusal_echoes_its_source_and_cuts_a_long_one()
    {
        var longSource = "'" + new string('x', 300) + "'";

        _compiler.Compile("total", CelProfile.Rule, CelFixtures.Orders).Errors.Single().Message.ShouldContain("total");
        var echoed = _compiler.Compile(longSource, CelProfile.Condition, CelFixtures.Orders).Errors.Single().Message;
        echoed.ShouldContain(longSource[..CelCompiler.EchoLength] + "…");
        echoed.ShouldNotContain(longSource);
    }

    /// <summary>A quoted string stays a legitimate Computed or Mutate value: no unwrap is offered there.</summary>
    [Fact]
    public void A_quoted_string_is_never_refused_under_mutate()
    {
        var result = _compiler.Compile("'owner_id == @user.id'", CelProfile.Mutate, CelFixtures.Orders);

        result.IsSuccess.ShouldBeTrue();
    }
```

  - The 302-character literal is well under `CelParser.MaxSourceLength` (2000).
  - Its `Condition` result is `String`, so it is refused.
  - Its content (`xxx…`) is no field, so it takes the generic fix, and the echo is what is asserted.
  - The existing `A_result_type_rejection_names_the_type…` and `…always_carries_a_fix_suggestion` theories must stay
    green unchanged.

- [ ] **Step 2: Run** `dotnet test --project test/MMLib.Alvo.Tests --filter-class "*CelCompilerResultTypeTests"`. It
  does not compile, because `QuotedFixLead` and `EchoLength` do not exist yet.

- [ ] **Step 3: Implement.** Keep every method ≤ ~25 lines.
  1. Add `private readonly record struct Authored(string Source, CelProfile Profile, EntitySchema Entity, CelNode Parsed, bool DetectQuoted);`.
  2. `Compile` delegates to `private static CelCompilationResult Compile(string source, CelProfile profile, EntitySchema entity, bool detectQuoted)`.
     The public overload passes `true`, and the argument checks stay in the public one.
  3. `CheckAndAssemble(Authored authored)` passes `authored` and the checker's `resultType`/`position` to
     `AppendResultTypeError`.
  4. `ValidateResultType(Authored authored, CelValueType resultType, int position)`:
     - keep each existing branch's first sentence byte for byte;
     - append `" The expression: " + Echo(authored.Source) + "."` to each message;
     - in the predicate branch, choose the fix:

```csharp
        if (IsPredicateProfile(authored.Profile) && resultType != CelValueType.Bool)
        {
            var quoted = QuotedPredicate(authored);
            return new CelCompilationError(
                $"A {authored.Profile} expression must evaluate to a boolean; this expression evaluates to {resultType}."
                + (quoted is null ? " The expression: " : " The whole expression is one quoted string: ") + Echo(authored.Source) + ".",
                quoted is null ? "Add a comparison, e.g. field == value, so the expression yields true/false." : QuotedFixLead + quoted,
                position);
        }
```

  5. Add the two helpers:

```csharp
    /// <summary>
    /// The content of a string literal that is itself a predicate, or <see langword="null"/>. Compiled once, with this check
    /// off, so nesting cannot recurse: at most two compilations per source, and only for a source already refused.
    /// </summary>
    private static string? QuotedPredicate(Authored authored) =>
        authored.DetectQuoted
        && authored.Parsed is CelLiteral { Type: CelValueType.String, Value: string content }
        && Compile(content, authored.Profile, authored.Entity, detectQuoted: false).IsSuccess
            ? content
            : null;

    private static string Echo(string source) =>
        source.Length <= EchoLength ? source : string.Concat(source.AsSpan(0, EchoLength), "…");
```

     `IsSuccess` under a predicate profile already implies a `Bool` result, because the branch above refuses anything
     else.
  6. Add a class-remark paragraph: *"A quoted predicate (D42): the refusal names the quotes. The accepted set is
     untouched — the check runs only on a source already refused, and the inner compile cannot recurse."*
  7. In `docs/architecture/cel.md`, the result-type section gets one sentence: *"A predicate wrapped whole in a string
     literal is refused with a fix that names the outer quotes, and every result-type refusal echoes its source (at
     most 120 characters)."*

- [ ] **Step 4: Run** the whole `MMLib.Alvo.Tests` project, then Host.Tests. The skills' Computed quotes
  (`InstructionExampleOutcomeTests`) and `SkillCoreClaimsTests` are fragment matches, so they must stay green. A red
  one means a first sentence moved: restore it, and never loosen the test.
- [ ] **Step 5: Security-core pass.**
  - Walk the `alvo-security-core-review` checklist against the diff, and record in the commit body, one line each:
    - no source's `IsSuccess` changed (Step 1's negative facts, and the unchanged existing suites);
    - the inner compile is bounded (`detectQuoted: false`);
    - the echo is capped;
    - no exception can escape `Compile`: the inner call is the same no-throw path.
  - Before the PR, mutation runs on `main` post-merge, so run it on demand via `workflow_dispatch` if the controller
    judges the merge risky (CLAUDE.md, Hard rules).
- [ ] **Step 6: Gates and commit.**
  - Run `scripts/test-ring1`.
  - Run `dotnet build MMLib.Alvo.slnx -c Release -warnaserror`.
  - Commit `fix(cel): a predicate wrapped whole in quotes is refused with the quotes named, and the source echoed`.

---

### Task 13: Teach a rule's JSON shape, a whole new entity, and `<x>_id` ref naming (S2, D43)

**Files:**
- Modify: `src/MMLib.Alvo.Ai/Instructions/schema-assistant.md`:
  - §2 `### Skills`: a new-entity load rule, as its own paragraph;
  - §3: ref naming;
  - §4: line 82, the rule shape;
  - §5: example (i), `new-entity-bike-notes`.
- Modify: `.claude/skills/alvo-descriptor-rules-and-cel/SKILL.md`:
  - the rule-shape paragraph replaces lines 17–18's last sentence;
  - `gen:cel-rule` gains two refused entries;
  - the worked example `new-entity-with-rules`.
- Modify: `.claude/skills/alvo-descriptor-field-types-and-formats/SKILL.md` (lines 59–70: `checked_in_by` becomes
  `check_in_technician_id`)
- Test:
  - `test/MMLib.Alvo.Host.Tests/SkillClaimTests.cs` (two probes and one fact);
  - `test/MMLib.Alvo.Ai.Tests/AssistantInstructionsTests.cs` (the ref-naming fact; `_toolFacts`);
  - `test/MMLib.Alvo.Ai.Eval.Tests/InstructionRepliesTests.cs` (`WorkedReplies` 9 → 10);
  - `test/MMLib.Alvo.Ai.Eval.Tests/NewEntityLoadRuleTests.cs` (new);
  - `test/MMLib.Alvo.Ai.Eval.Tests/MMLib.Alvo.Ai.Eval.Tests.csproj` (links `_shared/ai/InstructionExamples.cs`).

**The two caps, measured first.** The always-in-context text (base prompt plus skill list) is ≤ **22,758** bytes
(`AlvoAssistantTests.AlwaysInContextBudget`). D39 measured it at about 21.4 KB, which leaves about 1.35 KB. This task
adds about 1.3 KB there. `rules-and-cel/SKILL.md` is 2,577 of **6,144** bytes (`SkillConformanceTests.MaximumSkillBytes`)
and gains about 1.9 KB.

- [ ] **Step 0: Measure the headroom.**
  1. Temporarily set `AlwaysInContextBudget = 0` in `AlvoAssistantTests`.
  2. Run `dotnet test --project test/MMLib.Alvo.Ai.Tests --filter-method "*The_always_in_context_instructions*"`.
     Shouldly's message states the actual size (*"should be less than or equal to 0 but was N"*).
  3. Restore the constant, and write `N` (after T11) in the commit body.
  4. Draft the base-prompt text below. If `N` plus the draft exceeds 22,758, trim prose, in this order:
     - the example (h) sentence *"The field name stays English snake_case; only the prose follows the operator."*
       (the name rule in §3 already says it);
     - §4's *"`move` puts the member last in its object; that order has no meaning, so do not move it back."*, shortened
       to *"`move` puts the member last; leave it there."*. Keep the `_toolFacts` phrase *"`move` puts the member last"*.
  5. **Never raise the budget, and never trim a drift-tested fact or a `_toolFacts` phrase.**

- [ ] **Step 1: The failing facts.**

  (a) `SkillClaimTests._probes` gains two rows, the RCA's B1 and B4 on this branch's `technicians`:

```csharp
        ("/entities/technicians/rules/update", "\"'user_id == @user.id'\"", false),
        ("/entities/technicians/rules/update", "\"user_id == @user.id\"", true),
```

  In RFC 6902, `add` onto an existing member replaces it, so the probe's `add` op needs no change. Then add the fact
  that holds the skill's quote to the dry run:

```csharp
    /// <summary>A rule wrapped whole in quotes draws the fix the rules skill quotes, naming the bare rule (D42, D43).</summary>
    [Fact]
    public async Task A_rule_wrapped_whole_in_quotes_is_refused_with_the_fix_the_rules_skill_quotes()
    {
        await using var world = await AlvoHostWorld.StartAsync(InstructionExampleOutcomeTests.BikeWorkshop);
        var management = world.Services.GetRequiredService<IAlvoManagement>();
        world.Services.GetRequiredService<IAlvoContextAccessor>().Principal = InstructionExampleOutcomeTests.Administrator();

        var attempt = await InstructionExampleOutcomeTests.AttemptAsync(management, "/entities/technicians/rules/update", "\"'user_id == @user.id'\"");

        attempt.Valid.ShouldBeFalse();
        attempt.Violations.ShouldContain(violation => violation.Fix == "Remove the outer quotes; the value is the expression itself: user_id == @user.id");
        SkillCatalogue.Named("rules-and-cel").Body.ShouldContain("*\"Remove the outer quotes\"*");
    }
```

  (b) `AssistantInstructionsTests` gains the ref-naming fact:

```csharp
    /// <summary>
    /// Every <c>ref</c> the project declares, and every one a worked example adds, is named <c>&lt;x&gt;_id</c> — the
    /// convention §3 states (D43). Seven of seven in <c>bike-workshop</c> when this was written.
    /// </summary>
    [Fact]
    public void Every_ref_in_the_project_and_in_every_worked_example_ends_in_id()
    {
        var project = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "examples", "bike-workshop", "bike-workshop.alvo.json")))!;
        var examples = InstructionExamples.Parse(_text).Concat(SkillCatalogue.All.SelectMany(skill => InstructionExamples.Parse(skill.Body)));

        RefNames(project, key: null).Concat(examples.SelectMany(ExampleRefNames)).ShouldAllBe(name => name.EndsWith("_id", StringComparison.Ordinal));
    }

    private static IEnumerable<string> ExampleRefNames(InstructionExample example) =>
        example.Operations.EnumerateArray()
            .Where(operation => operation.TryGetProperty("value", out _))
            .SelectMany(operation => RefNames(JsonNode.Parse(operation.GetProperty("value").GetRawText()), operation.GetProperty("path").GetString()!.Split('/')[^1]));

    /// <summary>The key of every object whose <c>type</c> is <c>ref</c>, walked from <paramref name="node"/> under <paramref name="key"/>.</summary>
    private static IEnumerable<string> RefNames(JsonNode? node, string? key) => node switch
    {
        JsonObject field when field["type"] is JsonValue type && type.GetValue<string>() == "ref" && key is not null => [key],
        JsonObject members => members.SelectMany(member => RefNames(member.Value, member.Key)),
        JsonArray items => items.SelectMany(item => RefNames(item, key)),
        _ => [],
    };
```

  `SkillCatalogue.All` and `SkillOnDisk.Body` are the real members, and `SkillCatalogue.cs` and `InstructionExamples.cs`
  are already linked into Ai.Tests. Add to `_toolFacts`: `"Never wrap the whole expression in quotes"`.

  (c) `Eval.Tests/NewEntityLoadRuleTests.cs`, new. In the csproj, link `../_shared/ai/InstructionExamples.cs` beside
  `SkillCaseAnswers.cs`:

```csharp
using MMLib.Alvo.Ai.Tests;

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>
/// The base prompt's load rule for a new entity names exactly the skills the eval's grader needs for the prompt's own
/// whole-entity example (D43) — so the instruction and the grade cannot drift apart.
/// </summary>
public sealed partial class NewEntityLoadRuleTests
{
    private const string Lead = "A new entity is one area per thing it declares";

    private static readonly string _instructions = File.ReadAllText(
        Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo.Ai", "Instructions", "schema-assistant.md")).ReplaceLineEndings("\n");

    [Fact]
    public void The_new_entity_load_rule_names_what_the_grader_needs_for_the_whole_entity_example()
    {
        var rule = _instructions.Split("\n\n").Single(paragraph => paragraph.StartsWith(Lead, StringComparison.Ordinal));
        var example = InstructionExamples.Parse(_instructions).Single(candidate => candidate.Name == "new-entity-bike-notes");
        var operation = example.Operations.EnumerateArray().Single();

        var needed = SkillsRead.AreasOf(operation.GetProperty("path").GetString()!, JsonNode.Parse(operation.GetProperty("value").GetRawText()))
            .Select(area => "alvo-descriptor-" + area);

        SkillName().Matches(rule).Select(match => match.Value).Distinct().Order(StringComparer.Ordinal)
            .ShouldBe(needed.Order(StringComparer.Ordinal));
    }

    [GeneratedRegex("alvo-descriptor-[a-z-]*[a-z]", RegexOptions.CultureInvariant)]
    private static partial Regex SkillName();
}
```

  (d) `InstructionRepliesTests.WorkedReplies = 10`.

- [ ] **Step 2: Run** Ai.Tests, Eval.Tests and Host.Tests. Expect these reds:
  - the two probes (the quoted-rule message exists after T12, but the skill does not quote it yet);
  - `NewEntityLoadRuleTests`, because neither the paragraph nor the example exists;
  - `WorkedReplies`;
  - `_toolFacts`.

  The ref-naming fact is red **only** for `checked_in_by`.

- [ ] **Step 3: The base prompt.** These are the exact texts; keep the Markdown line width of the file.
  1. The `### Skills` section gets a second paragraph, after the first:

```markdown
A new entity is one area per thing it declares: load `alvo-descriptor-entities-and-fields`, and also
`alvo-descriptor-rules-and-cel` when it has `rules` and `alvo-descriptor-traits-and-tenancy` when it sets `audit` or
`tenancy` — every one before its first `check_change` or `propose_change`.
```

     The pin reads exactly the three `alvo-descriptor-*` names in this paragraph. Hooks, indexes and computed fields
     are covered by the per-area sentence above it. If a later example declares one of those, extend both the
     paragraph and the example.
  2. In §3, after the facets bullet:

```markdown
- Name a `ref` field for what it points at, ending in `_id` (`customer_id`, `fleet_bike_id`); when the project already
  names its refs another way, follow the project.
```

  3. In §4, replace line 82 (*"Write CEL string literals in **single quotes**…"*):

```markdown
- A rule's value is the bare CEL expression as one JSON string: `"user_id == @user.id"`. Never wrap the whole
  expression in quotes; only a text value inside it, such as a role name, goes in single quotes:
  `"'admin' in @user.roles"`. The same holds for a hook's `condition` and a `$cel` value.
```

  4. In §5, the new example goes last, after (h):

````markdown
<!-- example: new-entity-bike-notes -->
**(i) A new entity, whole: notes on a bike, each written and edited only by its author.**

```json
{"tool": "propose_change", "baseRevision": 1, "summary": "Adds bike notes, each editable only by its author.",
 "operations": [{"op": "add", "path": "/entities/bike_notes",
                 "value": {"audit": true,
                           "fields": {"bike_id": {"type": "ref", "entity": "bikes", "onDelete": "cascade", "required": true},
                                      "author_id": {"type": "ref", "entity": "users", "required": true},
                                      "body": {"type": "text", "required": true}},
                           "rules": {"list": "'authenticated' in @user.roles", "get": "'authenticated' in @user.roles",
                                     "create": "author_id == @user.id", "update": "author_id == @user.id"}}}]}
```

```json
{"valid": true, "changedPaths": ["/entities/bike_notes"]}
```

One `add` carries the whole entity: its fields, its trait and its `rules`, each rule a bare expression. Reply: *I
proposed a bike notes entity. Once you apply it from Preview, any signed-in caller can read notes, a caller can write
a note only as its author and edit only their own, deleting a bike deletes its notes, and nobody can delete a note,
since no delete rule is given.*
````

     The reply puts no new snake_case name in backticks: `Every_snake_case_name_in_code…` knows `bike_notes` only
     from the pointer, and `author_id` from nowhere. It says *proposed*, and it is English.
- [ ] **Step 4: The rules skill** (`.claude/skills/alvo-descriptor-rules-and-cel/SKILL.md`). The file is UTF-8
  without BOM, LF, and ≤ 6,144 bytes.
  1. Lines 17–18's *"Write CEL string literals in single quotes."* becomes:

```markdown
A rule's value is the bare CEL expression as one JSON string: `"user_id == @user.id"`. Never wrap the whole expression
in quotes; only a text value inside it, such as a role name, goes in single quotes. A rule wrapped whole in quotes is
a string, not a condition, and is refused with *"Remove the outer quotes"*.
```

  2. `gen:cel-rule`'s refused line gains, at its end, `` `'user_id == @user.id'` `'true'` ``. `SkillCoreClaimsTests`
     then proves three things:
     - both are refused under `Rule`, and after T12 they draw the quoted-predicate refusal;
     - both compile under another profile (`Computed` and `Mutate` accept a string);
     - the allowed `user_id == @user.id` is already on the allowed line.
  3. Before the tool-neutral closing lines, add a paragraph and the example:

````markdown
A new entity carries its whole `rules` object in the `add` that creates it. Say every operation the request allows;
a missing one is denied. An owner clause compares a `ref` to `users` with `@user.id`.

<!-- example: new-entity-with-rules -->
**Comments on a service order: everyone signed in reads them, each author edits and deletes their own, admins delete any.**

```json
{"tool": "propose_change", "baseRevision": 1, "summary": "Adds comments on service orders, each editable by its author.",
 "operations": [{"op": "add", "path": "/entities/order_comments",
                 "value": {"audit": true,
                           "fields": {"order_id": {"type": "ref", "entity": "service_orders", "onDelete": "cascade", "required": true},
                                      "author_id": {"type": "ref", "entity": "users", "required": true},
                                      "body": {"type": "text", "required": true}},
                           "rules": {"list": "'authenticated' in @user.roles", "get": "'authenticated' in @user.roles",
                                     "create": "author_id == @user.id", "update": "author_id == @user.id",
                                     "delete": "'admin' in @user.roles || author_id == @user.id"}}}]}
```

```json
{"valid": true, "changedPaths": ["/entities/order_comments"]}
```
````

     A `ref` to `users` takes no `onDelete` (D38); a `ref` to `service_orders` does.
  4. The field-types skill: rename the example `add-rental-checked-in-by` to `add-rental-check-in-technician`.
     - Its heading stays.
     - Its path and `changedPaths` become `/entities/rentals/fields/check_in_technician_id`.
     - Its summary becomes *"Adds check_in_technician_id, the technician who checked the rental in."*
     - `git grep -n checked_in_by` must then find nothing.
- [ ] **Step 5: Run** Ai.Tests, Eval.Tests and Host.Tests. Everything should be green, and these facts are the proof:
  - `SkillExampleOutcomeTests` runs `new-entity-with-rules` and `add-rental-check-in-technician` through the real tool
    on `bike-workshop`;
  - `InstructionExampleOutcomeTests` runs `new-entity-bike-notes`;
  - `SkillConformanceTests` holds the 6,144-byte and 200-line caps and the patch claims;
  - `AlvoAssistantTests` holds the 22,758-byte budget;
  - `EmbeddedSkillsTests` proves the embedded catalogue equals the files after LF normalisation.

  The eval's `SkillsRead` pin passes with `[alvo-descriptor-entities-and-fields, alvo-descriptor-rules-and-cel,
  alvo-descriptor-traits-and-tenancy]`.

  If an outcome test refuses an example, read the refusal and fix the **example**, never the claim, then re-run.
  Likely causes:
  - a `ref` to `users` with `required` refused;
  - an `onDelete` on `users`.
- [ ] **Step 6: Gates and commit.**
  - Run `scripts/test-ring1`.
  - Run `dotnet build MMLib.Alvo.slnx -c Release -warnaserror`.
  - Commit `feat(ai): the assistant is taught a rule's JSON shape, a whole new entity, and ref naming`. The body
    records the measured always-in-context size, before and after.

---
### Task 14: Every turn leaves a trace — in the log, and in the drawer for the operator who asked (S7, D44–D46)

**Files:**
- Create in `src/MMLib.Alvo.Ai/Internal/`:
  - `TurnRecorder.cs` (a `DelegatingChatClient`, plus `TracedCall`);
  - `TurnTrace.cs` (the builder, the cap, `TurnHeader`, `TurnEnd`);
  - `SecretScrub.cs`.
- Modify: `src/MMLib.Alvo.Ai/AlvoAssistant.cs` (wraps the client, emits the trace, events 6202/6203, remarks)
- Modify in Abstractions:
  - `src/MMLib.Alvo.Abstractions/Ai/AssistantModels.cs` (`AssistantRequest.IncludeTrace` and
    `AssistantUpdate.TurnTraced`, both `internal`);
  - `src/MMLib.Alvo.Abstractions/Properties/AssemblyInfo.cs` (four grants, D45).
- Modify in Admin:
  - `src/MMLib.Alvo.Admin/Components/Assistant/AssistantDrawer.razor` (asks for the trace, and *Turn details* /
    *Copy JSON*);
  - `src/MMLib.Alvo.Admin/wwwroot/alvo.css` (`.a-assistant__trace`).
- Test:
  - `test/MMLib.Alvo.Ai.Tests/AlvoAssistantTests.cs` (the trace facts; `RunAsync` gains `trace` and `history`;
    `CapturingLogger` keeps event ids);
  - `test/MMLib.Alvo.Ai.Tests/TurnTraceTests.cs` (new: the cap and the scrub);
  - `test/MMLib.Alvo.Admin.Tests.EndToEnd/ScriptedAssistant.cs` (emits a trace when asked);
  - `test/MMLib.Alvo.Admin.Tests.EndToEnd/AssistantScenarios.cs` (one scenario).
- Baseline: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt` gains exactly four
  `InternalsVisibleTo` lines, and nothing else.

**Interfaces:**
```csharp
// MMLib.Alvo.Abstractions — no public symbol (D45)
public sealed record AssistantRequest(string Project, string Message, IReadOnlyList<AssistantTurn> History)
{
    /// <summary>Whether the caller wants the turn's trace as its last update. The dashboard only (D45).</summary>
    internal bool IncludeTrace { get; init; }
}
public abstract record AssistantUpdate
{
    /// <summary>The turn's trace, as JSON (<c>alvo.assistant.turn/1</c>): emitted last, and only when the request asked.</summary>
    internal sealed record TurnTraced(string Json) : AssistantUpdate;
}

// MMLib.Alvo.Ai.Internal
internal sealed class TurnRecorder(IChatClient inner) : DelegatingChatClient(inner)
{
    internal int ToolRounds { get; }
    internal IReadOnlyList<TracedCall> Calls { get; }   // RecordedCall's members, so a trace line and an eval line read alike
}
internal sealed record TracedCall(int Round, string CallId, string Tool, IDictionary<string, object?>? Arguments, string? Result);
internal sealed record TurnHeader(string Instructions, string Provider, string Model, string Project);
internal static class TurnEnd { internal const string Answered = "answered", IterationCap = "iteration-cap", EndpointFailed = "endpoint-failed"; }
internal static class TurnTrace
{
    internal const string Format = "alvo.assistant.turn/1";
    internal const int MaximumBytes = 65_536;
    internal static JsonObject Of(TurnHeader header, IReadOnlyList<TracedCall> calls, string end);
    internal static JsonObject LogLine(JsonObject entry);   // the entry with each operation reduced to its op and path
}
internal static partial class SecretScrub
{
    internal const string Redacted = "[redacted]";
    internal static string Scrub(string text);
    internal static JsonNode? Scrub(JsonNode? node);   // every string value, in place of a copy
}
```

**What the trace holds (D44).** It is built at the turn's end from `TurnRecorder.Calls`:
- **Header:**
  - `format`;
  - `instructions`, which is `AssistantInstructions.VersionLine` without `<!-- ` and ` -->`;
  - `provider` (`connection.Kind`), `model` and `project`;
  - `baseRevision`, the `revision` of the first `get_descriptor` answer, or null;
  - `end`, `rounds` and `calls`;
  - `truncated: true` whenever the cap cost anything, and `droppedCalls` when whole entries were dropped.
- **One entry per call**, `{round, tool, arguments, result}`:
  - `arguments` keeps `baseRevision`, `operations` (the patch, parsed if it arrived as a string), `skillName` and
    `resourceName`. `summary` becomes `summaryChars`, and any other key becomes its name only.
  - `result` for `check_change` and `propose_change`: `valid`, `revision`, `changedPaths`, `attemptsLeft`,
    `unchecked`, and `violations` (every member), or `{error, message}`.
  - `result` for `get_descriptor`: `{revision}`.
  - `result` for `load_skill` and `read_skill_resource`: `{chars, found}`, where `found` is false when the answer
    starts `Error:`.
  - `result` for any other read: `{chars}`.
- **Never:** the request's `Message`, its `History`, the model's text, or the `summary` argument's text.
- **Scrubbed:** every string, after the trace is built.
- **Capped at 65,536 bytes of UTF-8:** an entry that would cross the cap first loses its `operations`
  (`{"omittedBytes": n}`), and the header says `truncated`. If it still crosses, recording stops, and `droppedCalls`
  says how many calls were left out.

- [ ] **Step 1: The Abstractions seam first, so the facts compile.**
  - Add the two internal members above, with their `<summary>`s.
  - In `AssemblyInfo.cs`, add a comment block in the file's style, then the grants:
    - `MMLib.Alvo.Ai`, the producer;
    - `MMLib.Alvo.Admin`, the one consumer;
    - `MMLib.Alvo.Ai.Tests`;
    - `MMLib.Alvo.Admin.Tests.EndToEnd`, whose scripted assistant emits it.

    Say why internal (D45, and this file's own `AlvoFrameworkTables` precedent: un-publishing is breaking, publishing
    is one word away), why opt-in (a third-party consumer never meets a case it cannot name), and the forgeability
    caveat.
  - Run `dotnet test --project test/MMLib.Alvo.Abstractions.Tests`. The public-API approval fails with exactly four
    added `InternalsVisibleTo` lines: accept that `.received.txt` as the new `.verified.txt`, and check that the diff
    is those four lines.
  - The turn-review gate will send you to the snapshot judge and to `alvo-architecture-rules` for a grown
    `PublicApi` baseline. The justification is D45: grants only, no symbol.

- [ ] **Step 2: The failing facts.** `TurnTraceTests.cs`, new:

```csharp
using MMLib.Alvo.Ai.Internal;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>What a trace may hold and how big it may get (D44): no secret, no more than its cap, always its end.</summary>
public sealed class TurnTraceTests
{
    private static readonly TurnHeader _header = new("alvo-schema-assistant v5", "OpenAiCompatible", "m", "p");

    [Theory]
    [InlineData("key sk-live-0123456789abcdefghij", "sk-live-0123456789abcdefghij")]
    [InlineData("token ghp_0123456789abcdefghijABCDEFGHIJ", "ghp_0123456789abcdefghijABCDEFGHIJ")]
    [InlineData("hook xoxb-1234567890-abcdefghij", "xoxb-1234567890-abcdefghij")]
    [InlineData("aws AKIAIOSFODNN7EXAMPLE", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl")]
    [InlineData("Server=db;User Id=alvo;Password=hunter2;", "hunter2")]
    public void A_secret_like_value_is_redacted(string text, string secret)
    {
        var scrubbed = SecretScrub.Scrub(text);

        scrubbed.ShouldNotContain(secret);
        scrubbed.ShouldContain(SecretScrub.Redacted);
    }

    /// <summary>What a trace is for survives the scrub: pointers, CEL, and the framework's own refusals.</summary>
    [Theory]
    [InlineData("/entities/service_orders/fields/problem_description")]
    [InlineData("'admin' in @user.roles || assigned_user_id == @user.id")]
    [InlineData("A Rule expression must evaluate to a boolean; this expression evaluates to String.")]
    public void A_descriptor_text_is_left_alone(string text) => SecretScrub.Scrub(text).ShouldBe(text);

    /// <summary>A turn of forty 10 KB patches stays under the cap, and still says how it ended.</summary>
    [Fact]
    public void A_trace_never_exceeds_its_cap_and_keeps_its_header()
    {
        var note = string.Concat(Enumerable.Repeat("a long note ", 850));
        var calls = Enumerable.Range(1, 40).Select(round => new TracedCall(
            round, "c" + round.ToString(System.Globalization.CultureInfo.InvariantCulture), "propose_change",
            new Dictionary<string, object?> { ["baseRevision"] = 1, ["operations"] = Patch(note), ["summary"] = "s" },
            """{"valid":false,"violations":[],"attemptsLeft":2}""")).ToList();

        var trace = TurnTrace.Of(_header, calls, TurnEnd.IterationCap);

        Encoding.UTF8.GetByteCount(trace.ToJsonString()).ShouldBeLessThanOrEqualTo(TurnTrace.MaximumBytes);
        trace["end"]!.GetValue<string>().ShouldBe(TurnEnd.IterationCap);
        trace["format"]!.GetValue<string>().ShouldBe(TurnTrace.Format);
        trace["truncated"]!.GetValue<bool>().ShouldBeTrue();
    }

    /// <summary>The log line names where a patch wrote, never what it wrote (D44).</summary>
    [Fact]
    public void A_log_line_keeps_each_operations_path_and_drops_its_value()
    {
        var trace = TurnTrace.Of(_header, [new TracedCall(1, "c1", "propose_change",
            new Dictionary<string, object?> { ["baseRevision"] = 1, ["operations"] = Patch("private words") }, """{"valid":true}""")], TurnEnd.Answered);

        var line = TurnTrace.LogLine(trace["calls"]![0]!.AsObject()).ToJsonString();

        line.ShouldContain("/entities/bikes/fields/notes");
        line.ShouldNotContain("private words");
    }

    private static JsonElement Patch(string description) => JsonSerializer.SerializeToElement(new JsonArray(new JsonObject
    {
        ["op"] = "add",
        ["path"] = "/entities/bikes/fields/notes",
        ["value"] = new JsonObject { ["type"] = "text", ["description"] = description },
    }));
}
```

  The note repeats words with spaces, so the scrub's long-token rule does not shrink it and the cap is what is
  measured.

  `AlvoAssistantTests`:
  - `RunAsync` gains `bool trace = false, IReadOnlyList<AssistantTurn>? history = null` and builds
    `new AssistantRequest("p", message, history ?? []) { IncludeTrace = trace }`.
  - `CapturingLogger` gains `internal List<int> EventIds { get; } = [];`, filled in `Log`.

```csharp
    /// <summary>A traced turn ends with its trace: one entry per call, in order, with what each asked and was told.</summary>
    [Fact]
    public async Task A_traced_turn_ends_with_one_entry_per_call_and_how_it_ended()
    {
        var management = Describing(revision: 4);
        management.ApplyDescriptorAsync("p", Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ManagementApplyResult(Applied: false, Revision: 4, EmptyPlan));

        var updates = await RunAsync(management, Configured(), new ScriptedChatClient(
            Scripted.Calls("get_descriptor", []),
            Scripted.Calls("load_skill", new Dictionary<string, object?> { ["skillName"] = "alvo-descriptor-entities-and-fields" }),
            Scripted.Calls("propose_change", Proposing(revision: 4)),
            Scripted.Says("I proposed notes.")), trace: true);

        var trace = JsonNode.Parse(updates[^1].ShouldBeOfType<AssistantUpdate.TurnTraced>().Json)!;
        trace["instructions"]!.GetValue<string>().ShouldBe("alvo-schema-assistant v5");
        trace["model"]!.GetValue<string>().ShouldBe("qwen3:8b");
        trace["baseRevision"]!.GetValue<int>().ShouldBe(4);
        trace["end"]!.GetValue<string>().ShouldBe("answered");
        var calls = trace["calls"]!.AsArray();
        calls.Select(call => call!["tool"]!.GetValue<string>()).ShouldBe(["get_descriptor", "load_skill", "propose_change"]);
        calls[1]!["arguments"]!["skillName"]!.GetValue<string>().ShouldBe("alvo-descriptor-entities-and-fields");
        calls[2]!["arguments"]!["operations"]![0]!["path"]!.GetValue<string>().ShouldBe("/entities/bikes/fields/notes");
        calls[2]!["result"]!["valid"]!.GetValue<bool>().ShouldBeTrue();
        calls[2]!["result"]!["attemptsLeft"]!.GetValue<int>().ShouldBe(Internal.ManagementTools.MaximumStalledRefusals);
    }

    /// <summary>A refused dry run's trace entry carries its violations and what the budget has left.</summary>
    [Fact]
    public async Task A_refused_attempts_entry_carries_its_violations_and_attempts_left()
    {
        var management = Describing(revision: 4);
        management.ApplyDescriptorAsync("p", Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Throws(new DescriptorValidationException(new DescriptorValidationResult(
                [new DescriptorValidationError("/entities/bikes/fields/notes", "No.", "Fix it.", DescriptorValidationSeverity.Error)])));

        var updates = await RunAsync(management, Configured(), new ScriptedChatClient(
            Scripted.Calls("propose_change", Proposing(revision: 4)), Scripted.Says("It was refused.")), trace: true);

        var result = JsonNode.Parse(updates.OfType<AssistantUpdate.TurnTraced>().Single().Json)!["calls"]![0]!["result"]!;
        result["attemptsLeft"]!.GetValue<int>().ShouldBe(2);
        result["violations"]![0]!["pointer"]!.GetValue<string>().ShouldBe("/entities/bikes/fields/notes");
        result["violations"]![0]!["fix"]!.GetValue<string>().ShouldBe("Fix it.");
    }

    /// <summary>Neither the trace nor the log carries a word the operator or the model wrote (D44).</summary>
    [Fact]
    public async Task Neither_the_trace_nor_the_log_carries_what_the_operator_or_the_model_wrote()
    {
        var logger = new CapturingLogger();
        var management = Describing(revision: 4);
        management.ApplyDescriptorAsync("p", Arg.Any<ManagementApplyRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ManagementApplyResult(Applied: false, Revision: 4, EmptyPlan));
        var proposing = Proposing(revision: 4);
        proposing["summary"] = "summary-words-marker";

        var updates = await RunAsync(management, Configured(), new ScriptedChatClient(
                Scripted.Calls("propose_change", proposing), Scripted.Says("reply-words-marker")),
            logger, message: "typed-words-marker", trace: true,
            history: [new AssistantTurn(AssistantRole.Operator, "history-words-marker")]);

        var written = updates.OfType<AssistantUpdate.TurnTraced>().Single().Json + string.Join('\n', logger.Lines);
        _operatorAndModelText.ShouldAllBe(marker => !written.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>One structured event per call and one for the turn's end.</summary>
    [Fact]
    public async Task Every_call_is_logged_as_6202_and_the_turns_end_as_6203()
    {
        var logger = new CapturingLogger();

        await RunAsync(Describing(revision: 1), Configured(), new ScriptedChatClient(
            Scripted.Calls("get_descriptor", []), Scripted.Calls("get_schema", []), Scripted.Says("ok")), logger);

        logger.EventIds.Count(id => id == 6202).ShouldBe(2);
        logger.EventIds.Count(id => id == 6203).ShouldBe(1);
    }

    /// <summary>A caller that did not ask gets no trace: a third-party consumer never meets a case it cannot name (D45).</summary>
    [Fact]
    public async Task An_untraced_request_gets_no_trace() =>
        (await RunAsync(Describing(revision: 1), Configured(), new ScriptedChatClient(Scripted.Says("ok"))))
            .OfType<AssistantUpdate.TurnTraced>().ShouldBeEmpty();

    /// <summary>A turn whose endpoint failed still ends with its trace, saying so.</summary>
    [Fact]
    public async Task A_failed_turns_trace_says_the_endpoint_failed()
    {
        var updates = await RunAsync(Substitute.For<IAlvoManagement>(), Configured(), new ThrowingChatClient("boom"), trace: true);

        JsonNode.Parse(updates.OfType<AssistantUpdate.TurnTraced>().Single().Json)!["end"]!.GetValue<string>().ShouldBe("endpoint-failed");
    }
```

  Add the static `_operatorAndModelText` array (CA1861):
  `["typed-words-marker", "history-words-marker", "reply-words-marker", "summary-words-marker"]`.
  - `using NSubstitute.ExceptionExtensions;` goes into the file's `NSubstitute` block.
  - `get_schema` on a bare substitute answers `null`, which the tool serialises as `null`, so the call is still a call.
  - `A_failed_turns_trace…` also holds `With_no_connection…`'s rule the other way: the no-connection answer stays a
    single `Failed`, with no trace, because nothing ran.

- [ ] **Step 3: Run** `dotnet test --project test/MMLib.Alvo.Ai.Tests`. It fails to compile (`TurnTrace`,
  `SecretScrub`, `TracedCall`), which is the red state.

- [ ] **Step 4: Implement `MMLib.Alvo.Ai`.**
  1. `TurnRecorder`:
     - the eval's `RecordingChatClient` algorithm, re-stated (D44: the shape is reused, not the class): a call is keyed
       by round and call id, and a result is matched to the latest unanswered call with its id;
     - `Tokens` and `Requests` are left out;
     - the remark cites `eval/MMLib.Alvo.Ai.Eval/RecordingChatClient.cs` as the rule's other home.
  2. `SecretScrub`, with three `[GeneratedRegex]`s, `RegexOptions.CultureInvariant`:
     - Tokens, replaced whole:
       `sk-(?:ant-|proj-|live-)?[A-Za-z0-9_\-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|xox[abprs]-[A-Za-z0-9\-]{10,}|AKIA[0-9A-Z]{16}|eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+`;
     - an unbroken alphanumeric run: `\b[A-Za-z0-9]{32,}\b`. A snake_case name always has `_`, so it cannot match,
       and a pointer has `/`;
     - a key–value pair, whose value is replaced:
       `(?<name>\b(?:password|pwd|secret|api[_-]?key|access[_-]?token)\b)(?<separator>\s*[=:]\s*)[^;\s"',]+`,
       with `RegexOptions.IgnoreCase`, replaced with `${name}${separator}[redacted]`.

     `Scrub(JsonNode?)` walks `JsonObject`/`JsonArray` and replaces each `JsonValue` string in place.
  3. `TurnTrace.Of`:
     - build the header, then add the entries one by one;
     - after each, measure with `Encoding.UTF8.GetByteCount(trace.ToJsonString())`;
     - over the cap, the entry's `arguments.operations` becomes `{"omittedBytes": n}`, and `truncated` is set. Still
       over, remove the entry, set `droppedCalls`, and stop;
     - scrub once at the end.

     The quadratic re-measure is bounded by 12 rounds of calls and by the cap. Keep each method ≤ ~25 lines: extract
     `Header`, `Entry`, `Arguments`, `Result`, `DryRunResult` and `TryAdd`.
  4. `TurnTrace.LogLine`: a deep clone of the entry, whose `arguments.operations` becomes
     `[{op, path, from?}]` only.
  5. `AlvoAssistant.AskAsync`:
     - wrap `_clients(connection)` in `new TurnRecorder(client)` and hand the recorder to `RunAsync`/`AgentFor`, so
       it sits **under** the function-invoking loop, as the eval's does;
     - on each exit path, after the proposal or after the `Failed`, call `Finish(recorder, request, connection, end)`:
       - it logs one `CallTraced` (6202) per entry, with `TurnTrace.LogLine`, and one `TurnEnded` (6203);
       - when `request.IncludeTrace`, it yields `new AssistantUpdate.TurnTraced(trace.ToJsonString())`.
     - `end` is `TurnEnd.EndpointFailed` on the failure path, `IterationCap` when
       `recorder.ToolRounds >= MaximumIterations`, and `Answered` otherwise;
     - extract helpers so that `AskAsync` stays readable, because it is already past 25 lines.

```csharp
    [LoggerMessage(EventId = 6202, Level = LogLevel.Information, Message = "Assistant call {Round} {Tool}: {Call}")]
    private static partial void CallTraced(ILogger logger, int round, string tool, string call);

    [LoggerMessage(EventId = 6203, Level = LogLevel.Information, Message = "Assistant turn ended ({End}) after {Calls} calls in {Rounds} tool rounds.")]
    private static partial void TurnEnded(ILogger logger, string end, int calls, int rounds);
```

     The remark *"Nothing the operator typed is logged"* gains: *"…and neither is what the model wrote. The trace
     (D44) records calls, not prose, and the log records where a patch wrote, never what."*

- [ ] **Step 5: Run** Ai.Tests. Everything should be green, including the untouched
  `The_operators_message_is_never_logged` and the iteration-cap fact.

- [ ] **Step 6: The drawer.** Every addition to `AssistantDrawer.razor` is `private`, so the Admin baseline does
  not move.
  1. `AskAsync` sends `new AssistantRequest(await ProjectAsync(), asked, [.. _turns]) { IncludeTrace = true }`.
  2. There is a new field, `private string? _trace;`. `Begin` clears it. `Apply` gains the case
     `case AssistantUpdate.TurnTraced traced: _trace = Indented(traced.Json); break;`, where `Indented` is
     `JsonNode.Parse(json)!.ToJsonString(_indented)` over a `static readonly JsonSerializerOptions { WriteIndented = true }`.
  3. Under the proposal block, and above the confirm, add the following. The `a-disclosure` pattern is the one
     `RecordForm.razor:87` and `FieldEditor.razor:322` use.

```razor
    @if (_trace is { } trace && !_busy)
    {
        <details class="a-disclosure" data-testid="assistant-turn-details">
            <summary>Turn details</summary>
            <pre class="a-mono a-assistant__trace" data-testid="assistant-trace">@trace</pre>
            <AlvoButton Small="true" data-testid="assistant-trace-copy" OnClick="_ => CopyTraceAsync(trace)">Copy JSON</AlvoButton>
        </details>
    }
```

  4. `CopyTraceAsync` follows `Access.razor:526-545`: `Interop.CopyAsync`. On a `JSException`, it shows the
     refusal panel (`_failure.Show`, titled *"The turn details could not be copied"*) saying to select the text above
     by hand. It never shows a snackbar alone.
  5. In `alvo.css`, beside `.a-assistant__tools`:
     `.a-assistant__trace { max-height: 16rem; overflow: auto; white-space: pre-wrap; font-size: var(--a-font-size-sm, .8125rem); }`.
     Reuse the file's existing size token; read the neighbouring rules first.
  6. Run `dotnet test --project test/MMLib.Alvo.Admin.Tests`. The pattern-language, public-API and gallery facts
     should be green, and the Admin baseline unchanged.

- [ ] **Step 7: The e2e scenario.** In `ScriptedAssistant.AskAsync`, add
  `if (request.IncludeTrace) { yield return new AssistantUpdate.TurnTraced(TraceJson); }` as the last update of both
  proposal branches. `TraceJson` is an `internal const` holding a two-call trace, and it names no operator text:
  `{"format":"alvo.assistant.turn/1","end":"answered","calls":[{"round":1,"tool":"get_descriptor"},{"round":2,"tool":"propose_change"}]}`.
  In `AssistantScenarios`:

```csharp
    /// <summary>
    /// A turn's details show the calls it made, and copy as the JSON a maintainer can paste into an issue (spec §8, D46).
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_turns_details_show_its_calls_and_copy_as_json()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.Page.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        await session.GoAsync("/schema");

        await session.Page.ClickAsync("[data-testid='assistant-launch']");
        await session.Page.FillAsync("#assistant-message", "add an invoices entity");
        await session.Page.ClickAsync("[data-testid='assistant-send']");
        await session.Page.Locator("[data-testid='assistant-proposal']").WaitForAsync();

        await session.Page.ClickAsync("[data-testid='assistant-turn-details'] > summary");
        (await session.Page.Locator("[data-testid='assistant-trace']").InnerTextAsync()).ShouldContain("\"tool\": \"propose_change\"");
        await session.Page.ClickAsync("[data-testid='assistant-trace-copy']");

        var copied = await session.Page.EvaluateAsync<string>("() => navigator.clipboard.readText()");
        System.Text.Json.Nodes.JsonNode.Parse(copied)!["calls"]!.AsArray().Count.ShouldBe(2);
        copied.ShouldNotContain("add an invoices entity");
        session.AssertConsoleClean();
    }
```

  `GrantPermissionsAsync` and `EvaluateAsync<string>` are the pattern `PersonEditorScenarios.cs:89-99` already uses.

- [ ] **Step 8: Gates.** These are the last task's gates, all green:
  - `scripts/test-ring1`.
  - `dotnet build MMLib.Alvo.slnx -c Release -warnaserror`, with 0 warnings and 0 errors. Watch for:
    - CA1861 on the new arrays;
    - CA1859 on private helpers returning `IEnumerable`;
    - IDE0065 in the new files.
  - `scripts/test-ring2`.
  - `scripts/test-admin-e2e`, **whole** and not filtered, because Admin moved (D46). Every assistant scenario must
    still pass unchanged: the added `<details>` is closed by default, and `LibraryProbe` measures only the launcher
    and the question box.
  - `docker build -f src/MMLib.Alvo.Host/Dockerfile .`.
  - `git diff --stat main -- '*.verified.*'`: exactly one file,
    `PublicApi.MMLib.Alvo.Abstractions.verified.txt`, **+4 lines**, all of them `InternalsVisibleTo`.

- [ ] **Step 9: Commit** `feat(ai): every assistant turn leaves a trace, logged per call and shown in the drawer on request`.

The controller then:
- runs `alvo-plan-guard`;
- runs the reviewer subagents substituting for `/code-review` and `/security-review` (T12 is security core);
- builds the PR report (`alvo-pr-report`), which marks T12 *needs-deep-review* and records D46's open question for
  the maintainer.

None of these is a task step.

---

## Self-review — RCA extension (Tasks 11–14)

- **Spec coverage (§8):**
  - D41 → T11;
  - D42 → T12;
  - D43 → T13;
  - D44, D45 and D46 → T14.
- **AC map (§8.4):**
  - AC1 → T11 steps 1 and 4;
  - AC2 → T12 step 1;
  - AC3 → T13 steps 0, 1 and 5;
  - AC4 → T14 steps 2, 5 and 7;
  - AC5 → T14 step 8.
- **Order:**
  - T12 lands before T13, so the skill's quote *"Remove the outer quotes"* is held to a refusal that exists.
  - T11 moves the instructions to v5 before T13 adds to them.
  - T14 asserts `"alvo-schema-assistant v5"`.
- **Checked against the code at `c440388`:**
  - `ManagementTools.cs:48` (the constant), `:183-205` (the attempt path), `ViolationMapping.cs:45-48`;
  - `ChangeOutcome`'s `WhenWritingNull` serialisation (`ToolJson.Options`), so `unchecked` is absent unless true;
  - `CelCompiler.cs:81-113`, and the lexer's unescaped `StringLiteral` text (`CelLexer.cs:148-166`);
  - `schema-assistant.md:28-32`, `:82`, `:99-100`, `:317-319`; `rules-and-cel/SKILL.md` at 2,577 bytes;
  - `SkillsRead.AreasOf` (eval);
  - `AssistantUpdate`'s private constructor, which a nested record may call;
  - `AdminInterop.CopyAsync`; the `a-disclosure` pattern.
- **Traps this branch hit, restated per task:**
  - `$$` brace runs → `Adding()` and `Patch()` are built from `JsonArray`;
  - CA1861 → the static test arrays;
  - CRLF → `ReplaceLineEndings("\n")` in `NewEntityLoadRuleTests`;
  - culture → `string.Create`/`ToString(CultureInfo.InvariantCulture)` in the test loops.
- **Open for the maintainer:** D46. The dashboard shows the trace to the operator who asked, not only to the
  `admin` level. A level gate needs a caller-level query on `IAlvoManagement`, which is public API.

---

## RCA 2 extension — Tasks 15–17 (spec §9, D47–D53)

**Goal:** fix what the second task-management turn (spec §9.1) showed: a model that stopped with two attempts left, on a
refusal it could have fixed in one edit, over a draft whose owner rule could never be true.
- **A + B + C** (T15): one automatic follow-up per turn; the stop clause narrowed; the managed-column fix reads as one
  action.
- **D + E** (T16): a refusal names the skill it needs; comparing `@user.id` with a ref to another entity is a warning,
  and warnings reach the model on a valid dry run.
- **F** (T17): the eval case `task_management_workers`.

**Architecture:**
- T15 adds an internal `FollowUp` to `MMLib.Alvo.Ai` and a per-turn `AgentSession` in `AlvoAssistant`; it changes
  `ManagementTools` state, one table in the core's `ManagedColumnNames`, and the base prompt.
- T16 adds `SkillAreas` (Ai), `OwnerComparisonCheck` (core, **security core**), and one internal member on
  Abstractions' `ManagementApplyResult` that the core already has a grant to fill and the Ai one to read (D52).
- T17 is eval code and its ring0 suite.

**Evidence base:** `…/scratchpad/rca2/analysis.md` (the drawer's trace). Every line number below was re-read on this
branch at `fc4a4a7`. The always-in-context size was **measured** there at **22,693** bytes of 22,758 (the T13 step 0
procedure), which leaves **65** bytes.

### Global Constraints, added for Tasks 15–17

Everything above still holds, with these amendments:
- **Instructions v6.** T15 moves the resource's first line and `AssistantInstructions.VersionLine` to
  `<!-- alvo-schema-assistant v6 -->`, and the two tests that pin `v5` (`AlvoAssistantTests.cs:251`,
  `TurnTraceTests.cs:15`) and the `TurnHeader` doc example (`TurnTrace.cs:9`). T16 edits stay under v6.
- **Security literals unchanged.** The follow-up (D47) adds a model round, never a tool: every dry run is still
  `DryRun: true, AllowDestructive: false`, and the follow-up message carries only validator pointers.
- **The always-in-context budget is not raised** (22,758 B). Each task that edits the base prompt measures first
  (T13 step 0) and records before/after in its commit body. Trim prose only, never a drift-tested fact or a
  `_toolFacts` phrase.
- **Public API:** no `PublicApi.*.verified.txt` moves in any of the three tasks, and no `InternalsVisibleTo` line is
  added. T16's `ManagementApplyResult.Warnings` is `internal` (D52): the grants to `MMLib.Alvo` and `MMLib.Alvo.Ai`
  exist (`src/MMLib.Alvo.Abstractions/Properties/AssemblyInfo.cs:3`, `:56`). If the turn-review gate stops on a grown
  baseline, the change is wrong. `MMLib.Alvo.Admin` is not touched, so `scripts/test-admin-e2e` is not a gate. If an
  implementer finds a reason to touch Admin, stop and ask; touching it makes that script, run whole, a gate.
- **Security core (T16):** `OwnerComparisonCheck` sits on the rule-compile boundary.
  - Apply the `alvo-security-core-review` checklist.
  - The PR report marks T16 **needs-deep-review**.
  - `/security-review` is user-only here: the controller dispatches a reviewer subagent labelled a substitute.
- **The lessons of this branch, restated for every step:**
  - **Facts first, run red, then implement.**
  - Release `-warnaserror` catches what Debug rings do not:
    - CA1861: a constant array argument becomes a `static readonly` field;
    - CA1875: `Regex.Count`, never `.Matches(...).Count`;
    - CA1859: a private helper returns its concrete type;
    - CA1870: `SearchValues` or the `string` overload;
    - IDE0065: `using`s above `namespace`.
  - **CA1873 caught T14 only in `docker build`.** A log call whose arguments cost anything (a `string.Join`, a
    `Select`, a serialisation) is guarded with `if (_logger.IsEnabled(LogLevel.…))`, as `AlvoAssistant.LogTurn` does.
    A local Release build did not reproduce it; the Dockerfile's build did.
  - Windows CI checks out CRLF: a regex over Markdown ends a line with `\r?$` or captures `[^\r\n]`, and text is
    `.ReplaceLineEndings("\n")` before it is parsed or measured.
  - A `$$"""…"""` raw string must not contain `}}` or `{{`: a JSON object nested to its end is built with
    `JsonObject`/`JsonArray`, or concatenated.
  - Culture-sensitive interpolation goes through `string.Create(CultureInfo.InvariantCulture, …)`.
  - `.cs` files are UTF-8 with BOM and CRLF, and a file written by a shell tool is normalised before commit. Skill and
    prompt files are UTF-8 without BOM, LF.
- **Every task ends with**, all green:
  - `scripts/test-ring1`;
  - `dotnet build MMLib.Alvo.slnx -c Release -warnaserror`, with 0 warnings and 0 errors.
- **T17, the last task, also runs:**
  - `scripts/test-ring2` (the two known `PagingPerformanceTests` Npgsql *connect* timeouts are infrastructure: read the
    trace first);
  - `docker build -f src/MMLib.Alvo.Host/Dockerfile .`, the only build that reproduces CI's analyzers (CA1873);
  - `git diff --stat fc4a4a7 -- '*.verified.*'`, which must be **empty**.
- **Commits:** one per task, Conventional Commits, ending with
  `Claude-Session: https://claude.ai/code/session_01TrcGmun8WAuVxN5FYAtu6H`. Never push. Never switch branch. One writer
  in the worktree.

---

### Task 15: One follow-up when a turn stops with attempts left; the stop clause narrowed; the managed-column fix as one action (A, B, C; D47–D49)

**Files:**
- Create: `src/MMLib.Alvo.Ai/Internal/FollowUp.cs`
- Modify: `src/MMLib.Alvo.Ai/AlvoAssistant.cs`:
  - `AnswerAsync` (`:122-160`): a per-turn session, the held answer, and the follow-up run;
  - `Translate` (`:328-345`): releases held text on a call;
  - `TurnState`: `FollowUpAfterRound`;
  - `LogTurn` and the 6203 template: `FollowedUp`;
  - the class remarks' *"A turn is bounded twice"* paragraph.
- Modify: `src/MMLib.Alvo.Ai/Internal/ManagementTools.cs` (the last dry run, `FollowUpMayBeDue`,
  `FollowUpPointersAsync`)
- Modify: `src/MMLib.Alvo.Ai/Internal/TurnTrace.cs` (`Of` gains `int? followUpAfterRound = null`; the header member)
- Modify: `src/MMLib.Alvo/Descriptor/Internal/ManagedColumnNames.cs` (`:132-183`, the seven fixes)
- Modify: `src/MMLib.Alvo.Ai/Instructions/schema-assistant.md`:
  - line 1 → v6;
  - `:88`, the `move` sentence;
  - `:248`, example (f)'s stop line;
  - `:328-332`, the stop clause.
- Modify: `src/MMLib.Alvo.Ai/Internal/AssistantInstructions.cs` (`VersionLine`)
- Test:
  - `test/MMLib.Alvo.Ai.Tests/AlvoAssistantFollowUpTests.cs` (new);
  - `test/MMLib.Alvo.Ai.Tests/ScriptedChatClient.cs` (`Requests`);
  - `test/MMLib.Alvo.Ai.Tests/AssistantInstructionsTests.cs` (`_toolFacts`);
  - `test/MMLib.Alvo.Ai.Tests/AlvoAssistantTests.cs` and `TurnTraceTests.cs` (`v6`);
  - `test/MMLib.Alvo.Tests/Descriptor/DescriptorValidatorTests.cs` (one theory).

**Interfaces:**
```csharp
// FollowUp (internal static, MMLib.Alvo.Ai.Internal)
internal const string Lead = "Alvo (not the operator): ";
internal static string Message(IReadOnlyList<string> pointers);
// ManagementTools
internal bool FollowUpMayBeDue { get; }                                   // (a)–(d) of D47; synchronous, no I/O
internal Task<IReadOnlyList<string>?> FollowUpPointersAsync(CancellationToken ct);  // null unless (a)–(e) hold
// TurnTrace
internal static JsonObject Of(TurnHeader header, IReadOnlyList<TracedCall> calls, string end, int? followUpAfterRound = null);
```

- [ ] **Step 0: Measure the headroom** (T13 step 0): set `AlwaysInContextBudget = 0`, run
  `dotnet test --project test/MMLib.Alvo.Ai.Tests --filter-method "*The_always_in_context_instructions*"`, read *"but
  was N"*, restore the constant. Expected **22,693**. The step 5 texts net **+41** bytes (measured with a UTF-8 byte
  count against `fc4a4a7`: stop clause +64, example (f) +26, `move` −49), so about **22,734** afterwards.

- [ ] **Step 1: The failing facts.**

  (a) `ScriptedChatClient` records each request whole, so a test can tell one follow-up from a follow-up re-sent in
  every later request of the session:

```csharp
    /// <summary>Each request's messages, one list per request, in order.</summary>
    internal List<List<ChatMessage>> Requests { get; } = [];
```

  In both `GetResponseAsync` and `GetStreamingResponseAsync`, add `Requests.Add([.. messages]);` beside
  `Sent.AddRange(messages);`.

  (b) `AlvoAssistantFollowUpTests` (new). It reuses the arrangement idioms of `AlvoAssistantTests`: copy `RunAsync`,
  `Describing`, `Proposing` and `Configured` as private helpers here, or make them `internal static` there and call
  them. Do not duplicate `CapturingLogger`. Every management substitute arranges `GetCapabilitiesAsync` explicitly,
  because NSubstitute answers a sealed record with `null`.

```csharp
public sealed class AlvoAssistantFollowUpTests
{
    private static readonly ManagementPlanSummary EmptyPlan = new(IsEmpty: false, HasDestructiveChanges: false, []);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The RCA 2 turn (spec §9.1): refused with attempts left, the model answers; the harness follows up once, the
    /// retry is valid and filed, and the operator reads only the answer that came after it (D47).
    /// </summary>
    [Fact]
    public async Task A_turn_that_stops_on_a_refusal_with_attempts_left_is_followed_up_once_and_its_retry_is_filed()
    {
        var management = Refusing(then: Valid());
        var model = new ScriptedChatClient(
            Scripted.Calls("propose_change", Proposing(revision: 4)),
            Scripted.Says("It was refused."),
            Scripted.Calls("propose_change", Proposing(revision: 4)),
            Scripted.Says("I proposed a notes field."));

        var updates = await RunAsync(management, model);

        updates.OfType<AssistantUpdate.Proposal>().ShouldHaveSingleItem().Refusals.ShouldBeEmpty();
        string.Concat(updates.OfType<AssistantUpdate.Text>().Select(text => text.Delta)).ShouldBe("I proposed a notes field.");
        FollowUps(model).ShouldBe(1);
    }

    /// <summary>The follow-up continues the same conversation: it is sent after the refused call's own result.</summary>
    [Fact]
    public async Task The_follow_up_is_sent_after_the_refused_calls_result_in_the_same_session()
    {
        var model = new ScriptedChatClient(
            Scripted.Calls("propose_change", Proposing(revision: 4)), Scripted.Says("It was refused."), Scripted.Says("Still refused."));

        await RunAsync(Refusing(then: null), model);

        var followUp = model.Requests.Single(request => IsFollowUp(request[^1]));
        followUp.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ShouldHaveSingleItem();
        followUp[^1].Text.ShouldContain("/entities/bikes/fields/notes");
    }

    /// <summary>A follow-up run that ends refused again is not followed up: one per turn, the most (D47).</summary>
    [Fact]
    public async Task A_turn_is_followed_up_at_most_once()
    {
        var model = new ScriptedChatClient(
            Scripted.Calls("propose_change", Proposing(revision: 4)), Scripted.Says("Refused."),
            Scripted.Calls("propose_change", Proposing(revision: 4)), Scripted.Says("Refused again."));

        var updates = await RunAsync(Refusing(then: null), model);

        FollowUps(model).ShouldBe(1);
        string.Concat(updates.OfType<AssistantUpdate.Text>().Select(text => text.Delta)).ShouldBe("Refused again.");
    }

    /// <summary>What the budget, the operator or the build must decide is never followed up (D47 (c)–(e)).</summary>
    [Theory]
    [InlineData("attempts-spent")]
    [InlineData("destructive")]
    [InlineData("access")]
    [InlineData("unsupported")]
    public async Task A_refusal_the_model_cannot_fix_is_not_followed_up(string why)
    {
        var model = new ScriptedChatClient(Stopping(why));

        var updates = await RunAsync(Stopped(why), model);

        FollowUps(model).ShouldBe(0);
        updates.OfType<AssistantUpdate.Text>().ShouldNotBeEmpty();
    }

    /// <summary>A valid proposal, or a refused <c>check_change</c> (an answer to "would this work?"), is never followed up.</summary>
    [Theory]
    [InlineData("check_change")]
    [InlineData("valid")]
    public async Task A_turn_that_proposed_validly_or_only_checked_is_not_followed_up(string shape)
    {
        var model = new ScriptedChatClient(
            Scripted.Calls(shape == "valid" ? "propose_change" : "check_change", shape == "valid" ? Proposing(4) : Checking(4)),
            Scripted.Says("Answered."));

        await RunAsync(shape == "valid" ? Answering(Valid()) : Refusing(then: null), model);

        FollowUps(model).ShouldBe(0);
    }

    /// <summary>A followed-up turn still makes at most <see cref="AlvoAssistant.MaximumIterations"/> tool rounds.</summary>
    /// <remarks>
    /// Counted as invocations of a tool nothing else calls (<c>get_revisions</c>), not as <c>ToolInvoked</c> updates: the
    /// invoker's last response at the cap may name a call it never runs, and the draft pipeline reads the descriptor
    /// itself, so neither of those counts is one call per round.
    /// </remarks>
    [Fact]
    public async Task A_followed_up_turn_never_passes_the_iteration_cap()
    {
        var management = Refusing(then: null);
        var first = Enumerable.Range(0, AlvoAssistant.MaximumIterations - 2).Select(_ => Scripted.Calls("get_revisions", []));
        var looping = Enumerable.Range(0, 30).Select(_ => Scripted.Calls("get_revisions", []));
        var model = new ScriptedChatClient(
            [.. first, Scripted.Calls("propose_change", Proposing(revision: 4)), Scripted.Says("Refused."), .. looping]);

        await RunAsync(management, model);

        FollowUps(model).ShouldBe(1);
        await management.Received(AlvoAssistant.MaximumIterations - 1).ListRevisionsAsync("p", Arg.Any<CancellationToken>());
        await management.ReceivedWithAnyArgs(1).ApplyDescriptorAsync(default!, default!, Ct);
    }

    /// <summary>The trace marks the follow-up by round; its words reach no update, no trace byte and no log line.</summary>
    [Fact]
    public async Task The_follow_up_is_marked_in_the_trace_and_its_words_reach_nothing_the_operator_sees()
    {
        var logger = new CapturingLogger();
        var model = new ScriptedChatClient(
            Scripted.Calls("propose_change", Proposing(revision: 4)), Scripted.Says("Refused."), Scripted.Says("Stopped."));

        var updates = await RunAsync(Refusing(then: null), model, logger, trace: true);

        var trace = updates.OfType<AssistantUpdate.TurnTraced>().Single().Json;
        JsonNode.Parse(trace)!["followUpAfterRound"]!.GetValue<int>().ShouldBe(2);
        trace.ShouldNotContain(FollowUp.Lead.Trim());
        logger.Lines.ShouldAllBe(line => !line.Contains(FollowUp.Lead.Trim(), StringComparison.Ordinal));
        updates.OfType<AssistantUpdate.Text>().ShouldAllBe(text => !text.Delta.Contains(FollowUp.Lead.Trim(), StringComparison.Ordinal));
    }

    /// <summary>The endpoint failing during the follow-up shows the held answer first, then the failure.</summary>
    [Fact]
    public async Task An_endpoint_failure_during_the_follow_up_shows_the_held_answer_then_the_failure()
    {
        var model = new FailingAfter(2, new ScriptedChatClient(Scripted.Calls("propose_change", Proposing(revision: 4)), Scripted.Says("Refused.")));

        var updates = await RunAsync(Refusing(then: null), model);

        updates.OfType<AssistantUpdate.Text>().ShouldHaveSingleItem().Delta.ShouldBe("Refused.");
        updates[^1].ShouldBeOfType<AssistantUpdate.Failed>();
    }

    private static int FollowUps(ScriptedChatClient model) => model.Requests.Count(request => IsFollowUp(request[^1]));

    private static bool IsFollowUp(ChatMessage message) =>
        message.Role == ChatRole.User && message.Text.StartsWith(FollowUp.Lead, StringComparison.Ordinal);
}
```

  The helpers these facts name, below them in the same class:
  - `Refusing(then)`: `Describing(4)`; `GetCapabilitiesAsync` → `new ManagementCapabilities([], [], [])`; the first
    `ApplyDescriptorAsync` throws a validation refusal at `/entities/bikes/fields/notes` (`"No."`, fix `"Fix it."`); the
    next answers `then` (a valid `ManagementApplyResult`), or throws the same refusal again when `then` is null.
  - `Answering(result)` and `Valid()`: the same, answering `result` every time.
  - `Stopping(why)` / `Stopped(why)`: the script and the management for each theory row:
    - `attempts-spent`: three identical `propose_change` calls, then an answer (the third answers `attemptsLeft` 0);
    - `destructive`: one call refused with `DestructiveChangeNotAllowedException` (source `plan`);
    - `access`: one call refused with `ManagementEscalationException`;
    - `unsupported`: one call refused with the message `"Not in this build."`, and `GetCapabilitiesAsync` returns one
      `ManagementRefusedFeature("field.validation", "Not in this build.", "Remove it.")`.
  - `FailingAfter(n, inner)`: a `DelegatingChatClient` that throws `HttpRequestException` on its `n+1`-th request. It
    sits under the recorder exactly as `ThrowingChatClient` does in `AlvoAssistantTests`; reuse that type if it can
    take a count.
  - `CapturingLogger.Lines`: use whatever member `AlvoAssistantTests` already reads the log lines through; add none.

  `ThrowingChatClient`, `CapturingLogger`, `Checking` and `Describing` exist in or beside `AlvoAssistantTests`
  (`:400-470`); read them before writing the helpers.

  (c) `AssistantInstructionsTests._toolFacts` gains `"every fix adds something the operator did not ask for"` and
  `"is an ordinary fix"`. `"When a refusal carries `attemptsLeft` 0"` stays, unchanged.

  (d) `DescriptorValidatorTests` gains, beside
  `A_declared_framework_managed_column_is_reported_with_its_path_and_its_own_reason` (`:695-720`), a theory over the
  same seven rows:

```csharp
    /// <summary>
    /// A managed column's fix leads with the one edit that fixes it; the alternative, dropping the trait, is a later
    /// sentence, so a model cannot read the pair as "the fix changes what was asked" (spec §9, D49).
    /// </summary>
    [Theory]
    [InlineData(@"""audit"": true", "created_at")]
    [InlineData(@"""audit"": true", "created_by")]
    [InlineData(@"""audit"": true", "updated_at")]
    [InlineData(@"""audit"": true", "updated_by")]
    [InlineData(@"""tenancy"": ""scoped""", "tenant_id")]
    [InlineData(@"""softDelete"": true", "deleted_at")]
    [InlineData(@"""audit"": true", "id")]
    public void A_managed_columns_fix_leads_with_the_single_edit(string traits, string column)
    {
        var json = $$"""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "orders": { {{traits}}, "fields": {
            "title": { "type": "string" },
            "{{column}}": { "type": "datetime" } } } } }
        """;

        var fix = Validate(json).Errors.Single(error => error.Path == $"/entities/orders/fields/{column}").FixSuggestion.ShouldNotBeNull();
        var first = fix[..(fix.IndexOf(". ", StringComparison.Ordinal) + 1)];

        fix.ShouldStartWith($"Remove '{column}' from the fields: ");
        first.ShouldNotContain("drop", Case.Sensitive);
        first.ShouldNotContain(" or ", Case.Sensitive);
    }
```

  The `$$` string ends in `} } } } }` with spaces: no brace run, as the neighbouring theory already proves.

- [ ] **Step 2: Run** Ai.Tests and `dotnet test --project test/MMLib.Alvo.Tests --filter-class "*DescriptorValidatorTests"`.
  Expected reds: `FollowUp` does not exist (the new suite does not compile), `_toolFacts`, and the fix theory (7 rows).

- [ ] **Step 3: `FollowUp` and the tool state.**
  1. `FollowUp.cs`:

```csharp
namespace MMLib.Alvo.Ai.Internal;

/// <summary>The one message the harness sends a model that stopped on a refusal it could still fix (D47).</summary>
/// <remarks>
/// Framework text, never the operator's: it reaches the model only, as a user-role turn (a system turn after the first
/// is refused by several OpenAI-compatible servers), led by <see cref="Lead"/> so the model does not quote it as the
/// operator's words. It carries validator pointers and nothing else.
/// </remarks>
internal static class FollowUp
{
    internal const string Lead = "Alvo (not the operator): ";

    internal static string Message(IReadOnlyList<string> pointers) =>
        $"{Lead}your last propose_change was refused at {string.Join(", ", pointers)}, and attempts are left. "
        + "Apply each violation's fix at its pointer and call propose_change again. Answer the operator only after a "
        + "valid proposal, or when the refusal says the construct is unsupported or every fix adds something the "
        + "operator did not ask for.";
}
```

  2. `ManagementTools` remembers the last dry run's answer and whether it was a proposal. Set it in `CheckChangeAsync`
     and `ProposeAsync` from the outcome they return, including the budget answer:

```csharp
    private (bool Proposed, ChangeOutcome Outcome)? _lastDryRun;

    /// <summary>
    /// Whether the turn's last dry run leaves the model something to fix (D47 (a)–(d)): a refused
    /// <c>propose_change</c>, no valid proposal filed, attempts left, checked, and nothing only the budget, the
    /// operator or an administrator can decide. Synchronous and free, so the stream can ask it per text chunk.
    /// </summary>
    internal bool FollowUpMayBeDue =>
        _lastValid is null
        && _lastDryRun is { Proposed: true, Outcome: { Valid: false, Unchecked: not true, AttemptsLeft: > 0 } last }
        && !last.Violations.Any(violation => violation.Blocks && _notTheModels.Contains(violation.Source));

    /// <summary>The refused pointers to follow up on, or <see langword="null"/> when no follow-up is due (D47 (a)–(e)).</summary>
    /// <remarks>
    /// "Unsupported" is the framework's own list: a blocking violation whose message carries a consequence
    /// <c>get_capabilities</c> refuses. Read once, only here; a read that throws, or answers nothing, means no follow-up.
    /// </remarks>
    internal async Task<IReadOnlyList<string>?> FollowUpPointersAsync(CancellationToken ct)
    {
        if (!FollowUpMayBeDue)
        {
            return null;
        }

        var blocking = _lastDryRun!.Value.Outcome.Violations.Where(violation => violation.Blocks).ToList();
        var refused = await RefusedConsequencesAsync(ct).ConfigureAwait(false);
        return refused is null || blocking.Exists(violation => refused.Exists(consequence => violation.Message.Contains(consequence, StringComparison.Ordinal)))
            ? null
            : [.. blocking.Select(violation => violation.Pointer.Length == 0 ? "(the whole change)" : violation.Pointer).Distinct(StringComparer.Ordinal)];
    }
```

     - `_notTheModels` is `static readonly HashSet<string>` of `ToolViolation.Budget`, `ToolViolation.Plan` and
       `ToolViolation.Access` (CA1861 forbids an inline array).
     - `RefusedConsequencesAsync` calls `_management.GetCapabilitiesAsync(_project, ct)` and returns
       `[.. capabilities.Refused.Select(feature => feature.Consequence)]`. It returns `null` when the answer is `null`,
       and `null` on `ManagementForbiddenException` or `ManagementProjectNotFoundException`, the two it documents.
       Anything else is a bug and propagates.
     - Keep each member ≤ ~25 lines; `FollowUpPointersAsync` may need its `Pointers(blocking)` helper extracted.
     - The class remarks gain one paragraph: *"The last dry run is state too (D47): the assistant asks it whether a
       turn that stopped may be followed up."*

- [ ] **Step 4: The assistant's follow-up** (`AlvoAssistant.cs`).
  1. `AnswerAsync` builds the agent once, opens a session with `await agent.CreateSessionAsync(ct)`, and streams the
     first run with `agent.RunStreamingAsync(Conversation(request), session, options: null, ct)`. Both are
     `Microsoft.Agents.AI` 1.22.0 public API: `AIAgent.CreateSessionAsync(CancellationToken)` and
     `RunStreamingAsync(IEnumerable<ChatMessage>, AgentSession, AgentRunOptions, CancellationToken)`.
  2. When the first run ended answered (not `EndpointFailed`), `recorder.ToolRounds < MaximumIterations` and
     `await tools.FollowUpPointersAsync(ct)` answers pointers, then:
     - set `turn.FollowUpAfterRound = recorder.Requests`. Expose `Requests` on `TurnRecorder` as a read-only count of
       `_requests`;
     - discard the held text;
     - set the agent's invoker cap to the remainder:
       `agent.ChatClient.GetService<FunctionInvokingChatClient>()!.MaximumIterationsPerRequest = MaximumIterations - recorder.ToolRounds;`
     - stream `agent.RunStreamingAsync(new ChatMessage(ChatRole.User, FollowUp.Message(pointers)), session, options: null, ct)`.
       It is the same translation loop, with holding **off**, because a second follow-up is never due.
  3. **The held answer.** Replace the `StringBuilder answer` with a private nested `TurnAnswer`. Its operations are:
     - `Add(text, hold)`: appends to the held buffer when `hold`, else to the shown text, and yields a `Text` update;
     - `Release()`: moves the held text to the shown text and yields one `Text` update, when there is any;
     - `Discard()`;
     - `Shown`, which `SummaryOf` reads.

     `Translate` calls `Release()` before it yields a `ToolInvoked`, since text before a call was a preface. It calls
     `Add(text, hold: holding && tools.FollowUpMayBeDue)` for text. `holding` is true only in the first run.
  4. At the end of the first run, when no follow-up is due, `Release()` shows the held text. On an endpoint failure in
     either run, `Release()` goes first and the `Failed` update after it. The proposal comes last, as today
     (`A_valid_proposal_becomes_the_turns_proposal_after_the_dry_run` pins `IndexOf(proposal) == Count - 1`).
  5. `turn.End` is computed after the last run, as today (`:155`).
  6. **Method size.** `AnswerAsync` is ~35 lines today, and this adds a second run. Extract `StreamAsync(run, model,
     answer, turn, holding)`, the existing `NextAsync` loop plus `Translate`, and a `FollowUpAsync` that returns the
     follow-up's stream or an empty one. Each stays ≤ ~25 lines. `yield return` cannot sit in a `try` with a `catch`,
     which is why `NextAsync` exists; keep it.
  7. The trace and the log:
     - `TurnTrace.Of(HeaderOf(...), recorder.Calls, turn.End, turn.FollowUpAfterRound)`. `Header` writes
       `["followUpAfterRound"] = followUpAfterRound` only when it is not null. That member is written before any entry
       is measured, so `Reserve` still covers only `droppedCalls`.
     - The 6203 template becomes
       `"Assistant turn ended ({End}) after {Calls} calls in {Rounds} tool rounds; followed up: {FollowedUp}."`, with a
       `bool followedUp` parameter. Its arguments are fields, so no `IsEnabled` guard is needed. A computed argument
       would need one (CA1873).
  8. The class remarks: *"A turn is bounded twice"* gains *"— and followed up at most once, inside the same cap
     (D47): a turn that stopped on a refusal it could still fix gets one more run, and the operator reads only the
     answer that came after it."*

- [ ] **Step 5: The instructions** (`schema-assistant.md`), exact texts. Keep the file's line width.
  1. Line 1 → `<!-- alvo-schema-assistant v6 -->`; `AssistantInstructions.VersionLine` the same.
  2. `:88`, the second line of the rename bullet, becomes `` `move` puts the member last; leave it there. `` The
     bullet's first line (`:87`) does not change.
  3. `:248`: `Stop here: the only fix adds a field the operator did not ask for; that is theirs to choose. Reply: quote the message and the fix in a quote`
     (re-wrap the paragraph).
  4. `:330-332`, the end of the refusal bullet:

```markdown
  again. When a refusal carries `attemptsLeft` 0 or a violation's `source` is `budget` — or at once, when the
  refusal says the construct is unsupported or every fix adds something the operator did not ask for — stop and
  explain. Removing or renaming what you added yourself is an ordinary fix.
```

- [ ] **Step 6: The managed-column fixes** (`ManagedColumnNames.cs:132-183`). The consequences (the first tuple
  member) do not move. Each fix:
  - `id`: `$"Remove 'id' from the fields: the store assigns it. {DeclareYourOwn}"`;
  - `tenant_id`: `$"Remove 'tenant_id' from the fields: 'tenancy' already adds it. {TenantNarrowing} " + DeclareYourOwn`;
  - `created_at`, `created_by` and `updated_by`:
    `$"Remove '<col>' from the fields: 'audit: true' already adds it. {AuditAlternative} {DeclareYourOwn}"`;
  - `updated_at`: `"Remove 'updated_at' from the fields: 'audit: true' already adds it. Only if the entity should keep
    no audit trail at all, drop 'audit' instead; that removes all four audit columns and the row versioning that
    'ETag' and 'If-Match' need. " + DeclareYourOwn`;
  - `deleted_at`: `$"Remove 'deleted_at' from the fields: 'softDelete' already adds it. {DeclareYourOwn}"`.

  `AuditAlternative` is a new `private const string`: *"Only if the entity should keep no audit trail at all, drop
  'audit' instead; that removes all four audit columns."* Give it a `<summary>` in the style of `DeclareYourOwn`'s.
  Then `git grep -n "puts it there"` must find nothing under `src/`, `test/`, `.claude/` or `eval/`.

- [ ] **Step 7: Run** Ai.Tests, `MMLib.Alvo.Tests` and Host.Tests.
  - Every new fact should be green.
  - Existing facts whose script ends on a refused `propose_change` now get one more request. Read each red one:
    - `A_refused_attempts_entry_carries_its_violations_and_attempts_left` asserts only `calls[0]`, so it stays
      green;
    - a fact that counts `Sent`, `Options` or requests must count the follow-up, or arrange
      `GetCapabilitiesAsync` to refuse it.
  - Never loosen an assertion about operator or model text reaching a log or the trace.
  - Host.Tests' `InstructionExampleOutcomeTests` still runs example (f) and its claimed `"attemptsLeft": 2`.
  - Then measure again (step 0) and write both numbers in the commit body.

- [ ] **Step 8: Gates and commit.**
  - Run `scripts/test-ring1`.
  - Run `dotnet build MMLib.Alvo.slnx -c Release -warnaserror`.
  - Commit `feat(ai): a turn that stops on a refusal it can fix is followed up once, and the stop rule no longer reads a fix as a choice`.
    Its body holds the measured sizes.

---

### Task 16: A refusal names its skill; comparing `@user.id` with another entity's ref is a warning, and warnings reach a valid dry run (D, E; D50–D52)

**Security core** (the rule-compile boundary): apply the `alvo-security-core-review` checklist, and mark the PR
report *needs-deep-review*. **The accepted set does not move.** `OwnerComparisonCheck` emits only
`DescriptorValidationSeverity.Warning`, runs outside `CelCompiler`, and reads compiled trees it never changes.

**Files:**
- Create: `src/MMLib.Alvo.Ai/Internal/SkillAreas.cs`
- Create: `src/MMLib.Alvo/Rules/Internal/OwnerComparisonCheck.cs`
- Modify: `src/MMLib.Alvo/Descriptor/Internal/DescriptorValidator.cs` (`RuleErrors`, `:124-145`)
- Modify: `src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs` (`ManagementApplyResult`, `:264-265`)
- Modify: `src/MMLib.Alvo/Migrations/DescriptorApplyPreview.cs`; `src/MMLib.Alvo/Migrations/RuntimeSchemaService.cs`
  (`PreviewAsync` `:170-185`, `Validate` `:251-257`); `src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs`
  (`AppliedAsync` `:238-240`, `AppendAsync` `:498`)
- Modify: `src/MMLib.Alvo.Ai/Internal/DescriptorDraft.cs` (`DryRunAsync`, `Draft.Accepted`),
  `ViolationMapping.cs` (`FromWarnings`, `SkillHint`), `ChangeOutcome.cs` (`ToolViolation.Skill`, `ChangeOutcome.Hint`),
  `ManagementTools.cs` (the ledger, `WithSkillHints`), `TurnRecorder.cs` (`LoadedSkills`), `TurnTrace.cs` (the hint,
  and `skill` on a log line), `FollowUp.cs` (the skill sentence), `AlvoAssistant.cs` (passes the ledger)
- Modify: `eval/MMLib.Alvo.Ai.Eval/SkillsRead.cs` (`AreaOf` delegates to `SkillAreas.AreaOf`)
- Modify: `src/MMLib.Alvo.Ai/Instructions/schema-assistant.md` (`:108`, the warning bullet; `:309`, example (h)'s first
  sentence); `.claude/skills/alvo-descriptor-rules-and-cel/SKILL.md` (`:51`)
- Modify: `test/MMLib.Alvo.Ai.Tests/InternalGrantArchitectureTests.cs` (the second granted use);
  `src/MMLib.Alvo.Abstractions/Properties/AssemblyInfo.cs` (the comment over the `MMLib.Alvo.Ai` grant names the new use)
- Test:
  - `test/MMLib.Alvo.Tests/Rules/OwnerComparisonCheckTests.cs` (new);
  - `test/MMLib.Alvo.Api.Tests/Management/ManagementApplyTests.cs` (two facts);
  - `test/MMLib.Alvo.Ai.Tests/ManagementToolsTests.cs` (four facts) and `SkillAreasTests.cs` (new);
  - `test/MMLib.Alvo.Ai.Tests/AlvoAssistantFollowUpTests.cs` (the message fact);
  - `test/MMLib.Alvo.Host.Tests/SkillClaimTests.cs` (the real dry run);
  - `test/MMLib.Alvo.Ai.Tests/AssistantInstructionsTests.cs` (`_toolFacts`).

**Interfaces:**
```csharp
// Abstractions — internal: no PublicApi line, no wire member (D52)
public sealed record ManagementApplyResult(bool Applied, int Revision, ManagementPlanSummary Plan, bool Replayed = false)
{
    internal IReadOnlyList<DescriptorValidationError> Warnings { get; init; } = [];
}
// core
internal sealed record DescriptorApplyPreview(MigrationPlan Plan, int CurrentRevision, bool AllowedByGuardrail)
{
    internal IReadOnlyList<DescriptorValidationError> Warnings { get; init; } = [];
}
internal static class OwnerComparisonCheck
{
    internal static IReadOnlyList<DescriptorValidationError> Warnings(AlvoDescriptor descriptor, SchemaModel schema, ICelCompiler compiler);
}
// Ai
internal static class SkillAreas
{
    internal static string? AreaOf(string path, JsonNode? proposed);   // moved from SkillsRead, unchanged
    internal static string? ForViolation(string pointer);               // AreaOf(pointer, null), plus managed columns → traits
}
internal sealed record ToolViolation(..., string Severity = ErrorSeverity, string? Skill = null);
internal sealed record ChangeOutcome(..., bool? Unchecked = null, string? Hint = null);
internal static ManagementTools For(IAlvoManagement management, string project, Func<IReadOnlySet<string>>? loadedSkills = null);
internal IReadOnlySet<string> LoadedSkills { get; }                     // TurnRecorder
```

- [ ] **Step 1: The failing facts — the warning (core).** `OwnerComparisonCheckTests` goes through the real
  `DescriptorValidator`, built as `DescriptorValidatorTests` builds it (`:888-890`). The descriptor has:
  - `technicians` (with `user_id` `uuid`);
  - `tasks`, with `technician_id` → `technicians`, `assigned_user_id` → `users`, `owner_uuid` (`uuid`) and `title`
    (`string`).

  Every row is a fact: the outcome is `IsValid` **and** the warning count at that pointer.

| Where | Source | Warnings |
|---|---|---|
| `tasks.rules.update` | `technician_id == @user.id` | 1 |
| `tasks.rules.update` | `technician_id != @user.id` | 1 |
| `tasks.rules.update` | `@user.id == technician_id` | 1 |
| `tasks.rules.update` | `'admin' in @user.roles \|\| technician_id == @user.id` | 1 |
| `tasks.rules.update` | `assigned_user_id == @user.id` (ref → `users`) | 0 |
| `tasks.rules.update` | `owner_uuid == @user.id` (`uuid`) | 0 |
| `tasks.rules.update` | `title == @user.id` (plain `string`) | 0 |
| `tasks.hooks.beforeUpdate[0].condition` | `new.technician_id != @user.id` (with a `reject`) | 1 |
| `tasks.hooks.beforeUpdate[0].condition` | `old.technician_id == @user.id` | 1 |
| `tasks.rules.update` | `technician_id == @user.id &&` (does not compile) | 0, and the compile error stays |

  Plus these facts:
  - the warning's `Path` is the rule's or the condition's pointer (`/entities/tasks/rules/update`,
    `/entities/tasks/hooks/beforeUpdate/0/condition`);
  - its message starts *"'technician_id' holds a technicians id, and @user.id is a user id"*;
  - its fix contains *"refs 'users'"*;
  - its severity is `Warning`;
  - **every** `examples/*/*.alvo.json` validates with **0** warnings, found with
    `Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "examples"), "*.alvo.json", SearchOption.AllDirectories)`.
    Use this suite's own repository-root helper; `ShippedSources.cs` is the place to look.

  Build each descriptor with `JsonObject`, or with a `$$` raw string that ends in spaced braces, as
  `DescriptorWithRule` (`:905`) does. `CelAcceptanceCorpusTests` is **not** edited. It must stay green with its
  baseline untouched, and step 6 proves it.

- [ ] **Step 2: The failing facts — the warning reaches a valid dry run (D52).**
  1. `ManagementApplyTests` gains `A_dry_run_carries_the_validators_warnings_in_process`. It works in-process
     through `world.Services.GetRequiredService<IAlvoManagement>()`. `ManagementApplyWorld`'s managed-fleet
     descriptor gains an entity with a ref to another entity and a rule comparing it with `@user.id`, built with
     `DescriptorEdits`' idiom. The dry run's `result.Warnings` has one warning at that rule's pointer, and
     `result.Applied` is false.
  2. `The_apply_response_carries_no_warnings_member`: the same descriptor through `ApplyAsync(…, query: "?dryRun=true")`.
     The response JSON has no `warnings` property at any depth. **The wire is unchanged.**

  Api.Tests has the Abstractions grant (`AssemblyInfo.cs:44`).

- [ ] **Step 3: The failing facts — the Ai side.**
  1. `ManagementToolsTests`:
     - `A_valid_dry_run_carries_its_warnings`: `ApplyDescriptorAsync` returns
       `new ManagementApplyResult(false, Revision, EmptyPlan) { Warnings = [new("/entities/bikes/rules/update", "Never true.", "Ref users.", DescriptorValidationSeverity.Warning)] }`.
       The answer is `valid: true`, with one violation of `severity: "warning"` at that pointer. The filed proposal
       has no refusals, and `attemptsLeft` stays 3.
     - `A_refusal_in_an_unloaded_skills_area_names_the_skill`: a ledger of `{"alvo-descriptor-entities-and-fields"}`,
       and a refusal at `/entities/tasks/fields/created_at`. The violation carries
       `"skill": "alvo-descriptor-traits-and-tenancy"`, and the outcome's `hint` equals `ViolationMapping.SkillHint`.
     - `A_refusal_whose_skill_is_loaded_names_none`: the same refusal, with that skill in the ledger. There is no
       `skill` and no `hint`.
     - `A_warning_never_names_a_skill`: the valid answer above, with an empty ledger. There is no `skill` and no
       `hint`.
     - `Without_a_ledger_no_refusal_names_a_skill`: `ManagementTools.For(management, Project)`, refused. There is no
       `skill`.
  2. `SkillAreasTests`: `ForViolation` maps these pointers:
     - `/entities/x/rules/update` → `rules-and-cel`;
     - `/entities/x/hooks/beforeUpdate/0/condition` → `hooks`;
     - `/entities/x/indexes/0` → `indexes`;
     - `/entities/x/audit` → `traits-and-tenancy`;
     - `/entities/x/fields/created_at` → `traits-and-tenancy`, and the same for every name in
       `AlvoManagedColumns.For(TenancyMode.Scoped, audit: true, softDelete: true)`, enumerated rather than typed;
     - `/entities/x/fields/total/computed` → `computed-and-rollups`;
     - `/entities/x/fields/notes` → `entities-and-fields`;
     - `/access` → `project-access`;
     - `#/entities/x` → `null`;
     - `""` → `null`.
  3. `AlvoAssistantFollowUpTests` gains `The_follow_up_says_to_load_a_named_skill`: the follow-up message contains
     *"Load any skill a violation names"*.
  4. `AlvoAssistantTests` gains
     `A_turns_loaded_skills_are_what_it_loaded_before_the_dry_run`. The script is `load_skill`
     (`alvo-descriptor-entities-and-fields`), then a refused `propose_change` at `/entities/bikes/fields/created_at`.
     In the trace, `calls[1].result.violations[0].skill` is `alvo-descriptor-traits-and-tenancy`, and the 6202 line
     of that call carries `skill` too.
  5. Host.Tests `SkillClaimTests` gains
     `Comparing_the_caller_with_a_technician_ref_is_a_warning_on_a_valid_dry_run`. On `bike-workshop` through
     `InstructionExampleOutcomeTests.AttemptAsync`:
     - `/entities/service_orders/rules/update` set to
       `"'admin' in @user.roles || technician_id == @user.id"` is valid, with exactly one warning violation at that
       pointer;
     - the same with `assigned_user_id` is valid with none;
     - the rules skill's body contains *"compare it only with a field that refs `users`"*.
  6. `_toolFacts` gains `"even on a valid answer"`.

- [ ] **Step 4: Run** `MMLib.Alvo.Tests`, Api.Tests, Ai.Tests, Eval.Tests and Host.Tests. The expected reds are every
  fact above; the rest must be green. `Warnings`, `SkillAreas`, `Skill` and `Hint` do not compile yet.

- [ ] **Step 5: The core.**
  1. `OwnerComparisonCheck`:
     - For each `schema.Entities` entity, take `descriptor.Entities.GetValueOrDefault(entity.Name)`.
     - Compile each non-null `Rules.List/Get/Create/Update/Delete` with `CelProfile.Rule`, at
       `/entities/{name}/rules/{op}` (lower-case op). Compile each
       `Hooks.BeforeCreate/BeforeUpdate/BeforeDelete[i].Condition` with `CelProfile.Condition`, at
       `/entities/{name}/hooks/{point}/{i}/condition`.
     - A failed compile yields nothing.
     - Walk the root with `CelTree.Children`, and for each
       `CelBinary { Operator: CelBinaryOperator.Equal or CelBinaryOperator.NotEqual }` take the pair of operands in
       either order: a `CelContextRef { Value: CelContextValue.UserId }` and a `CelFieldRef`.
     - The field is `entity.Fields.FirstOrDefault(field => field.Name == fieldRef.FieldName)`. Warn when its
       `Reference is { TargetEntity: var target }` and `target != "users"` (ordinal).
     - One warning per comparison. Its message and fix are D51's, with `'{field}'` and `{target}` interpolated
       ordinally; the strings hold no culture-sensitive value.
     - The class remarks carry:
       - why it is a separate pass: `PolicyCatalogBuilder` refuses on any entry (`:49-54`);
       - why after-hooks are skipped: they cannot read `@user`;
       - why `uuid`/`string` are not warned (`assigned_user_id`, `assigned_to` are correct uses);
       - that the row's own `id` is out of scope;
       - that each source is compiled a second time, per apply, never per request.
  2. `DescriptorValidator.RuleErrors`: after `errors.AddRange(ComputedFieldCheck.Errors(schema, _compiler));`, add
     `errors.AddRange(OwnerComparisonCheck.Warnings(descriptor, schema, _compiler));`. It runs whether or not
     `TryBuild` succeeded, so a refused first attempt already carries the warning beside its errors.
  3. `DescriptorApplyPreview` gains the `Warnings` init property. `RuntimeSchemaService.Validate` returns the
     `DescriptorValidationResult` it checked (the throw is unchanged). `PreviewAsync` sets
     `Warnings = [.. result.Errors.Where(error => error.Severity == DescriptorValidationSeverity.Warning)]`.
     `ApplyAsync`'s own `Validate` call ignores the return value.
  4. `ManagementApplyResult` gains the internal `Warnings` property with a `<summary>`:
     - *"The validator's warnings on this descriptor: advisory, never blocking. Internal (D52): the assistant reads
       them; the wire and the public contract do not carry them until a client earns it."*

     `AlvoManagementService`:
     - `AppliedAsync`'s dry-run branch gets `with { Warnings = preview.Warnings }`, and so does `AppendAsync`'s
       result (`:498`);
     - `RollbackAsync` and the replay (`:604`) carry none;
     - one remark says why.
  5. `AssemblyInfo.cs`: the comment above the `MMLib.Alvo.Ai` grant (`:56`) names the second use, the apply result's
     warnings (D52).

- [ ] **Step 6: The Ai side.**
  1. `ViolationMapping.FromWarnings(IReadOnlyList<DescriptorValidationError> warnings, IReadOnlyList<string?> targets)`
     maps as `FromValidation` does, with `Severity: ToolViolation.WarningSeverity`.
     `DescriptorDraft.DryRunAsync` passes `ViolationMapping.FromWarnings(result.Warnings, draft.Targets)` to
     `draft.Accepted(result.Plan, warnings)`.
  2. `SkillAreas`: move `SkillsRead.AreaOf`, `EntityFacetArea`, `IsDerived`, the area constants and `_traits`
     verbatim into `MMLib.Alvo.Ai.Internal.SkillAreas`. Add `ForViolation(pointer)`: when the pointer is
     `/entities/{e}/fields/{f}` and `f` is managed on some entity, answer `traits-and-tenancy`; else
     `AreaOf(pointer, null)`. "Managed on some entity" is the public
     `AlvoManagedColumns.For(TenancyMode.Scoped, audit: true, softDelete: true)`, the union spec §3 already pins. The
     internal `AlvoManagedColumns.All` would be a third use of the Abstractions grant, and nothing earns it. `SkillsRead.AreaOf` becomes `=> SkillAreas.AreaOf(path, proposed)`, and its `Areas` list reads
     `SkillAreas`' constants. `SkillsReadTests` must pass unchanged.
  3. `TurnRecorder.LoadedSkills`: the `skillName` of every `load_skill` call whose `Result` is null (answered later in
     the same round, which the sequential invoker runs first) or does not start with `"Error:"`. It is read as
     `SkillsRead.Skill` reads it: `JsonElement` string, or `ToString()`.
  4. `ManagementTools.For(…, Func<IReadOnlySet<string>>? loadedSkills = null)`. In `AttemptAsync`, a refused outcome
     is passed through `WithSkillHints`:
     - for each **blocking** violation whose `SkillAreas.ForViolation(pointer)` names an area whose
       `"alvo-descriptor-" + area` is not in the ledger, add `Skill` to that violation;
     - when any got one, set `Hint = ViolationMapping.SkillHint`, which is D50's sentence;
     - no ledger means no hint;
     - `ToolViolation.Key` does not include `Skill`, so the budget (D41) is unchanged.
  5. `AlvoAssistant.AnswerAsync` passes `() => recorder.LoadedSkills`.
  6. `TurnTrace`:
     - `_dryRunMembers` gains `"hint"`;
     - `_loggedViolationMembers` gains `"skill"` (a catalogue name, never author text);
     - the `LogLine` remarks say so.
  7. `FollowUp.Message`'s second sentence becomes *"Load any skill a violation names, apply each violation's fix at
     its pointer, and call propose_change again."*
  8. `InternalGrantArchitectureTests`:
     - the summary says the grant is used for the trace seam **and** the apply result's warnings;
     - a second control fact, `_referenced.ShouldContain("MMLib.Alvo.Management.ManagementApplyResult::get_Warnings")`,
       shows the scan sees the new use;
     - the security-core deny list is unchanged.

- [ ] **Step 7: The texts.** Measure first (T15 step 0). Expected: T15's end size.
  1. `schema-assistant.md:108` becomes:

```markdown
- A violation whose `severity` is `warning` does not block, even on a valid answer: fix the `error` ones, and a
  warning that says the change cannot do what the operator asked.
```

  2. `:309`: drop *"The field name stays English snake_case; only the prose follows the operator. "*. The §3 name rule
     already says it, and T13 step 0 named this sentence the first to trim. Re-wrap the paragraph.

     Measured with a UTF-8 byte count against `fc4a4a7`, the two edits net **+13** bytes, so about **22,747** after
     T15's +41. If the measurement says otherwise and crosses 22,758, shorten the bullet's second clause. Never trim
     the `_toolFacts` phrase.
  3. The rules skill, `:51`, after *"An owner clause compares a `ref` to `users` with `@user.id`."*:

```markdown
`@user.id` is a `users` id: compare it only with a field that refs `users`, or with a `uuid` that holds a user id. A
ref to any other entity holds that entity's id and never equals it, and the validator warns.
```

     The skill stays ≤ 6,144 bytes (4,256 now) and ≤ 200 lines, with UTF-8 without BOM and LF. `EmbeddedSkillsTests`
     proves the embed equals the file.

- [ ] **Step 8: Run** every suite of step 4, and they should be green.
  - `CelAcceptanceCorpusTests` is green, and `git diff fc4a4a7 -- src/MMLib.Alvo/Expressions test/MMLib.Alvo.Tests/Expressions`
    is **empty**. The compiler and its corpus did not move.
  - `git diff --stat fc4a4a7 -- '*.verified.*'` is **empty**. In particular
    `PublicApi.MMLib.Alvo.Abstractions.verified.txt` does not move, because the new member is internal.
  - Security-core checklist (`alvo-security-core-review`), answered in the commit body:
    - the check can only add a `Warning`, never an `Error`, and never removes one;
    - it reads compiled trees and changes none;
    - it runs on the apply path only;
    - a descriptor that was valid is still valid, and one that was refused is refused with the same errors.

- [ ] **Step 9: Gates and commit.**
  - Run `scripts/test-ring1`.
  - Run `dotnet build MMLib.Alvo.slnx -c Release -warnaserror`. Watch for:
    - CA1861 on the pointer tables in `SkillAreasTests`;
    - CA1859 on the walker's return type;
    - CA1873 on any new log argument.
  - Commit `feat(ai): a refusal names the skill it needs, and a rule comparing the caller with another entity's ref is a warning the model sees`.
    The body holds the measured sizes and the checklist answers.

---

### Task 17: The eval case `task_management_workers`, and the counts that name the suite (F, D53)

**Files:**
- Create: `eval/MMLib.Alvo.Ai.Eval/EvalCases.TaskManagement.cs`
- Modify: `eval/MMLib.Alvo.Ai.Eval/EvalCases.cs` (`All`, and the class summary's case count at `:34-36`)
- Modify: `eval/MMLib.Alvo.Ai.Eval/RecordingChatClient.cs` (`FollowUps`); `TurnRecord.cs` (`FollowUps`);
  `EvalRunner.cs` (the verdict line)
- Modify: `test/_shared/ai/SkillCaseAnswers.cs` (`TaskManagement`, **not** in `RightAnswers`: `SkillCasesTests`'
  theories over `RightAnswers` assume one skill per case)
- Modify: `CLAUDE.md` (the assistant-eval paragraph); `scripts/eval-assistant` (`:6`, `:152`)
- Test:
  - `test/MMLib.Alvo.Ai.Eval.Tests/TaskManagementCaseTests.cs` (new);
  - `test/MMLib.Alvo.Ai.Eval.Tests/EvalCasesTests.cs` (`:17`, 16 → 17);
  - `test/MMLib.Alvo.Host.Tests/SkillCaseAnswerTests.cs` (one fact).

- [ ] **Step 1: The right answer**, in `SkillCaseAnswers`. It is `bike-workshop`'s shape of the RCA turn, with its two
  defects fixed:
  - no `created_at`/`updated_at`;
  - the assignee's owner clause goes through a ref to `users`.

```csharp
    /// <summary>
    /// <c>task_management_workers</c>' right answer (spec §9, D53): tasks linked to a technician, a customer and a part,
    /// and a discussion on each — audited, no managed column declared, and every owner clause on a ref to users.
    /// </summary>
    internal const string TaskManagement = """
        [{"op": "add", "path": "/entities/tasks",
          "value": {"audit": true,
                    "fields": {"title": {"type": "string", "required": true, "maxLength": 200},
                               "status": {"type": "enum", "required": true, "values": ["open", "done"], "default": "open"},
                               "technician_id": {"type": "ref", "entity": "technicians", "onDelete": "setNull"},
                               "assigned_user_id": {"type": "ref", "entity": "users"},
                               "customer_id": {"type": "ref", "entity": "customers", "onDelete": "setNull"},
                               "part_id": {"type": "ref", "entity": "parts", "onDelete": "setNull"}},
                    "rules": {"list": "'authenticated' in @user.roles", "get": "'authenticated' in @user.roles",
                              "create": "'admin' in @user.roles || 'manager' in @user.roles",
                              "update": "'admin' in @user.roles || 'manager' in @user.roles || assigned_user_id == @user.id",
                              "delete": "'admin' in @user.roles"}}},
         {"op": "add", "path": "/entities/task_comments",
          "value": {"audit": true,
                    "fields": {"task_id": {"type": "ref", "entity": "tasks", "onDelete": "cascade", "required": true},
                               "author_id": {"type": "ref", "entity": "users", "required": true},
                               "body": {"type": "text", "required": true}},
                    "rules": {"list": "'authenticated' in @user.roles", "get": "'authenticated' in @user.roles",
                              "create": "author_id == @user.id", "update": "author_id == @user.id",
                              "delete": "'admin' in @user.roles || author_id == @user.id"}}}]
        """;
```

  `SkillCaseAnswerTests` gains `The_task_management_right_answer_passes_the_real_dry_run_with_no_warning`, through
  its own `AttemptAsync`. The attempt is valid, and `attempt.Violations` holds **0** entries of severity
  `"warning"` (D52 puts them there).
  - If the dry run refuses the patch, fix the **answer**, never the grader, and re-run. The likely causes are an
    `onDelete` on a ref to `users` (D38) or a `setNull` on a `required` ref.

- [ ] **Step 2: The failing facts** (`TaskManagementCaseTests`). They follow `SkillCasesTests`: the right turn is
  `SkillCaseAnswers.TaskManagement` applied to `Turns.Original` through `DescriptorDiff.Patched`. It loads
  `entities-and-fields`, `rules-and-cel` and `traits-and-tenancy` in round 1, reads `get_descriptor` in round 1,
  proposes valid in round 2, and answers *"I proposed tasks and task comments. Nothing changes until you apply it
  from Preview."*.
  - `The_right_answer_passes_its_case` and `The_right_answer_passes_the_whole_grading` (`EvalRunner.Graded`, English).
  - One fact per clause, each the right turn with **exactly one** thing wrong, and each must fail its case:
    1. *not valid*: the same proposal, with `refusals: ["refused"]` and a refused `Propose`;
    2. *fewer than two new entities*: the `task_comments` operation removed;
    3. *a changed path that is not a new entity*: `bikes.notes` added beside;
    4. *a missing link*: `part_id` removed, so `parts` is never referenced;
    5. *a ref to an undeclared entity*: `part_id` → `products` (the RCA 1 defect; graded, though validity implies
       it, D16's reasoning);
    6. *a managed column declared*: `created_at` added to `tasks` (audited);
    7. *the caller compared with another entity's ref*: `update` ends `|| technician_id == @user.id` — and, as a second
       row, a `beforeUpdate` condition `new.technician_id != @user.id` with a `reject`;
    8. *two refusals*: two refused `Propose` calls before the valid one.
  - Whole-grading facts that are not case clauses:
    - the right turn without the `traits-and-tenancy` load fails `EvalRunner.Graded` (`SkillsRead`);
    - with the answer *"I created the tables."* it fails too (`ProposalWording`).
  - `EvalCasesTests.The_suite_has_the_reliability_first_try_and_skill_cases` → `ShouldBe(17)`.

- [ ] **Step 3: Run** `dotnet test --project test/MMLib.Alvo.Ai.Eval.Tests` and Host.Tests' `SkillCaseAnswerTests`.
  Expect red: the case is missing and the count is 16.

- [ ] **Step 4: The case** (`EvalCases.TaskManagement.cs`, `internal static partial class EvalCases`):
  1. `All` gains, after `can_alvo_call_http`:

```csharp
        new("task_management_workers",
            "Create tables for task management for workers: a task links to the employee, the customer they may serve and the goods, and each task has a discussion.",
            "Vytvor tabuľky na správu úloh pre pracovníkov: úloha je priradená zamestnancovi, zákazníkovi, ktorého môže obslúžiť, a tovaru, a ku každej úlohe sa dá viesť diskusia.",
            TaskManagementWorkers),
```

  2. The grader, one clause per fact, with a `Why` naming each clause's reading, as `AuditWhy` does:
     - `newEntities`: every `turn.ChangedPaths` matches `EntityPointer()`, and `DescriptorDiff.At(original, path)` is
       null. There must be at least 2.
     - `links`: the union of the new entities' ref targets (`field.Value?["entity"]`) ⊇ `{technicians, customers, parts}`.
     - `declared`: every ref target is an original entity, a new entity, or `users`.
     - `managed`: `DeclaredManagedColumns(entity)` is empty for every new entity.
     - `callerVsRef`: over every new entity's rule strings and before-hook `condition`s, normalised with `Normal`, the
       regex `(?:new\.|old\.)?([a-z][a-z0-9_]*)(?:==|!=)@user\.id|@user\.id(?:==|!=)(?:new\.|old\.)?([a-z][a-z0-9_]*)`
       captures field names. A captured field whose declared `type` is `ref` and whose `entity` is not `users` fails
       the turn.
       - The regex is a `[GeneratedRegex]` with `CultureInvariant`.
       - It runs over `Normal`'s output, which drops whitespace and reads `"` as `'`.
     - `turn.RefusedAttempts <= 1`, and `turn.HasValidProposal`.
  3. The skills and *"proposed"* are **not** repeated here. `SkillsRead` and `ProposalWording` grade every turn (D53,
     spec §9.5).

- [ ] **Step 5: The follow-up is visible in the eval** (D47, D53).
  - `RecordingChatClient.FollowUps` counts sent requests whose last message is user-role and starts with
    `FollowUp.Lead`. The eval has the Ai grant (`src/MMLib.Alvo.Ai/Properties/AssemblyInfo.cs`).
  - Count each follow-up once, where it is first sent: a request whose last message is the follow-up is exactly that
    request.
  - `TurnRecord` gains a trailing `int FollowUps = 0`.
  - `EvalRunner`'s verdict line prints `followUps=N` for every turn, so a real run shows how often A fired. Its
    format lives in `CaseRun.Line`, which uses `string.Create(CultureInfo.InvariantCulture, …)`.
  - A fact in `RecordingChatClientTests` counts one follow-up over a two-request session.

- [ ] **Step 6: The counts.**
  - `CLAUDE.md`, in the *assistant eval* paragraph: *"asks a real model the seven cases of
    `docs/superpowers/specs/2026-09-28-f5-assistant-reliability-design.md` §4.2"* becomes *"asks a real model the
    seventeen cases: the seven of `docs/superpowers/specs/2026-09-28-f5-assistant-reliability-design.md` §4.2 and the
    ten of `docs/superpowers/specs/2026-09-29-f5-assistant-first-try-design.md` (§5, §7.5, §9)"*. Nothing else in
    `CLAUDE.md` moves.
  - `scripts/eval-assistant:6` and `:152`: *"sixteen"* → *"seventeen"*. Add an example `--case task_management_workers`
    line beside `:13`.
  - `EvalCases.cs:34-36`, the class summary, adds *"and the RCA 2 case (§9, D53)"*.
  - `git grep -n "sixteen\|16 cases"` over `CLAUDE.md scripts eval test/MMLib.Alvo.Ai.Eval.Tests` then finds
    nothing. Sweep for the claim, not the file.
  - `bash -n scripts/eval-assistant`. The script is **not run**: it needs a real model (D8).

- [ ] **Step 7: The last task's gates**, all green:
  - `scripts/test-ring1`.
  - `dotnet build MMLib.Alvo.slnx -c Release -warnaserror`, with 0 warnings and 0 errors.
  - `scripts/test-ring2`. Read a `PagingPerformanceTests` connect timeout's trace before calling it a regression.
  - `docker build -f src/MMLib.Alvo.Host/Dockerfile .`. This is the build that caught CA1873 in T14; a local
    Release build is not a substitute.
  - `git diff --stat fc4a4a7 -- '*.verified.*'` is **empty**. `git diff --stat fc4a4a7 -- src/MMLib.Alvo.Admin` is
    **empty**, so `scripts/test-admin-e2e` is not a gate. If either is non-empty, stop: the first means the change is
    wrong, the second makes the e2e script, run whole, a gate.

- [ ] **Step 8: Commit** `test(eval): the task-management turn is a graded case, and the eval shows each follow-up`.

The controller then runs, none of which is a task step:
- `alvo-plan-guard`;
- the reviewer subagents substituting for `/code-review` and `/security-review` (T16 is security core);
- the PR report (`alvo-pr-report`), which marks T16 *needs-deep-review* and records D52's internal member.

---

## Self-review — RCA 2 extension (Tasks 15–17)

- **Spec coverage (§9):**
  - D47, D48 and D49 → T15;
  - D50, D51 and D52 → T16;
  - D53 → T17.
- **AC map (§9.4):**
  - AC1 → T15 step 1 (b);
  - AC2 → T15 steps 0, 1 (c)–(d) and 7;
  - AC3 → T16 step 3;
  - AC4 → T16 steps 1–3 and 8;
  - AC5 → T17 steps 1–2;
  - AC6 → the gates of each task, and T17 step 7.
- **Order:**
  - T15 introduces `FollowUp` before T16 amends its message.
  - T16's `FromWarnings` lands before T17's right answer asserts zero warnings on a valid attempt.
  - T15 moves the instructions to v6 before T16 edits them.
- **Checked against the code at `fc4a4a7`:**
  - `AlvoAssistant.cs:122-160`, `:328-345`, and `LogTurn`; `TurnRecorder` (`ToolRounds`, `_requests`);
  - `ManagementTools.cs:160-256` (`CheckChangeAsync` to `Refused`); `ChangeOutcome`'s positional shape;
    `ToolViolation.Key`;
  - `ViolationMapping.FromValidation` and `SeverityOf`, which already map a `Warning`; `DescriptorDraft.cs:69-94`,
    `:128-129`;
  - `ManagedColumnNames.cs:106-183`; `DescriptorValidatorTests.cs:695-720`, `:888-905`;
  - `DescriptorValidator.cs:124-145`; `PolicyCatalogBuilder.cs:49-54`, `:117-136`; `BeforeHookCompiler.cs:98-160`;
    `AfterHookCompiler.HonoursTheEnvelope`;
  - `CelTree.Children`, and the public node records `CelBinary`, `CelFieldRef` and `CelContextRef`;
    `RefSchema.TargetEntity`;
  - `RuntimeSchemaService.cs:170-185`, `:251-257`; `AlvoManagementService.cs:230-240`, `:498`, `:604`;
    `ManagementModels.cs:264-265`;
  - Abstractions' grants (`AssemblyInfo.cs:3`, `:44`, `:56-58`); Ai's grants (Ai.Tests, Host.Tests, Ai.Eval);
    `InternalGrantArchitectureTests`;
  - `SkillsRead.AreaOf` (eval); `SkillCasesTests`' `RightAnswers` theories, the reason `TaskManagement` stays out;
  - `CapabilityReport.Project()`, whose `Refused` consequences are `UnhonouredFeatures.EveryRefusal`'s, the same
    text the validator reports (`DescriptorValidator.cs:555`);
  - `Microsoft.Agents.AI` 1.22.0: `AIAgent.CreateSessionAsync(CancellationToken)` and
    `RunStreamingAsync(ChatMessage, AgentSession, …)`, read from the package's XML docs;
  - the size **22,693 B**, measured.
- **Traps restated per task:**
  - CA1873 → the 6203 arguments are fields, and any computed one is guarded (T15 step 4.7, T16 step 9);
  - `$$` → spaced closing braces or `JsonObject` (T15 step 1 (d), T16 step 1);
  - CRLF → `ReplaceLineEndings("\n")` wherever Markdown is read or measured;
  - CA1861 → the static theory and pointer tables;
  - culture → `string.Create` in the verdict line (T17 step 5).
- **Open for the maintainer:**
  - D52 keeps the warnings internal, so Preview does not draw them and HTTP clients do not see them. Publishing them
    is one public member and a baseline line, whenever the dashboard earns it.
  - D47's held answer delays a refused turn's text until the turn ends; the maintainer judges that in the drawer.
