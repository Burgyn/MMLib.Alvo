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
cap (12), instructions and eval are built and unchanged; deviations D15–D21 are the spec's.

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

## Self-review

- **Spec coverage:** scope 1 → Task 1 (pattern, reserved, managed columns, probes, schema description); scope 2 →
  Task 2; scope 3 → Task 3; scope 4 → Task 4; scope 5 (drawer) → no task, D18, checked by Task 4 step 10's diff.
- **AC map:** AC1–2 Task 1; AC3 Task 2 step 5; AC4 Tasks 2–3 fixtures (*je vytvorená*, *is created*, *Pojďme*,
  English-to-Slovak, quote-then-Slovak); AC5 Task 4; AC6 unchanged `EvalReport.SuitePasses`; AC7 Task 4 step 10.
- **Windows/Release traps:** every new Markdown regex tolerates `\r`; no `.Matches(…).Count`; the one culture-sensitive
  interpolation goes through `string.Create(CultureInfo.InvariantCulture, …)`.
