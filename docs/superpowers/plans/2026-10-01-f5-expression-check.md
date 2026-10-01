# F5 expression check (`cel/check`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One read-only Management operation, `CheckExpressionAsync`, that tells an operator (or agent) what apply would say about a single expression slot, plus the dashboard wiring that shows it under the expression inputs.

**Architecture:** The service splices the candidate expression into the supplied working-copy descriptor and runs the **same** `IDescriptorValidator` apply runs, keeping the findings at or under the slot's pointer. No second code path, no store, no revision. The dashboard keeps a small focus-free `ExpressionCheck` state per input, debounced, latest-wins, and never blocks editing if the check fails.

**Tech Stack:** .NET 10, `System.Text.Json.Nodes`, Blazor Server + MudBlazor (dashboard), Microsoft.Testing.Platform + xUnit + Shouldly, Microsoft.Playwright (admin e2e).

**Spec:** `docs/superpowers/specs/2026-10-01-f5-expression-check-design.md` (read it first; this plan argues from it). Background: the analysis and its review in the same directory.

## Global Constraints

- Rings are `scripts/test-ring0` after every step, `scripts/test-ring1` after a slice, `scripts/test-ring2` before the PR. CI builds **Release**; a CA analyzer error passes Debug rings. Build `-c Release` once before the PR.
- New/edited `.cs` files are **UTF-8 with BOM and CRLF** (pre-commit `dotnet format` rejects otherwise). Create files with the `Write` tool, then normalise: `python3 -c "p='<file>';b=open(p,'rb').read().replace(b'\r\n',b'\n').replace(b'\n',b'\r\n');b=b if b.startswith(b'\xef\xbb\xbf') else b'\xef\xbb\xbf'+b;open(p,'wb').write(b)"`. Edits to existing files must keep their line endings.
- Methods ≤ ~25 lines, one purpose each; every internal member has XML docs (repo convention, `alvo-dotnet-conventions`).
- Admin holds **no reference to `MMLib.Alvo`** (architecture test); it talks to `IAlvoManagement` in Abstractions only.
- `PublicApi.MMLib.Alvo.Abstractions.verified.txt` may grow by **exactly** one interface member and two records (spec §5.6). The Stop hook will demand a justification per added symbol.
- Management routes are excluded from OpenAPI (`ManagementContractTests.No_management_route_reaches_the_published_openapi_document`), so no OpenAPI snapshot, Vacuum or TeaPie change.
- Never merge or push to `main`. Branch `feat/expression-check`, PR, human merges.
- Commit messages: Conventional Commits, ending with `Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`.
- One writer per worktree. Work in `/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-wt-expr`.

## Review Focus

Failure modes the spec implies but no obvious test would catch; each is pinned in the task named in brackets.

1. A slot the descriptor does not contain (`/entities/nope/rules/list`, or an array index past the end) must be a 422, not a silent green verdict. [Task 1]
2. A descriptor that is not JSON, or whose parent of the slot is a string, must be a 422, never a 500. [Task 1]
3. A candidate containing characters JSON must escape (`'`, `"`, `\`, newline, a lone surrogate) must reach the validator unchanged. [Task 1]
4. One bad expression elsewhere in the descriptor must not turn a good slot red, and a schema-level failure elsewhere must not hide the slot's verdict. [Task 1, Task 3]
5. `Path` containing `~0` / `~1` (an entity or field name with `/` or `~`) must address the right node. [Task 1]
6. A null body must be answered 422 **after** the 403 for an unauthorised caller (gate sweep). [Task 2]
7. A huge descriptor posted by a viewer must be refused before it is parsed. [Task 2]
8. The dashboard must never show a verdict for a source the operator has already replaced (stale response), and a failing check must not block saving. [Task 5]
9. The check must not steal focus while the operator types (`FieldRefusals` does, by design). [Task 5]

---

### Task 1: `ExpressionSlotCheck` — splice, validate, filter

**Files:**
- Create: `src/MMLib.Alvo/Management/Internal/ExpressionSlotCheck.cs`
- Create: `src/MMLib.Alvo/Management/Internal/JsonPointerPath.cs`
- Test: `test/MMLib.Alvo.Tests/Management/ExpressionSlotCheckTests.cs`
- Test: `test/MMLib.Alvo.Tests/Management/JsonPointerPathTests.cs`

**Interfaces:**
- Consumes: `IDescriptorValidator.Validate(string) -> DescriptorValidationResult` (`src/MMLib.Alvo.Abstractions/Descriptor/IDescriptorValidator.cs`), `DescriptorValidationError(Path, Message, FixSuggestion, Severity)`, `ManagementRequestException(string)`.
- Produces:
  - `internal static class JsonPointerPath { internal static IReadOnlyList<string> Segments(string pointer); internal static bool IsAtOrUnder(string path, string pointer); }`
  - `internal static class ExpressionSlotCheck { internal static IReadOnlyList<DescriptorValidationError> Check(IDescriptorValidator validator, string descriptorJson, string pointer, string source); }` — throws `ManagementRequestException` for an unanswerable request.

- [ ] **Step 1: Write the failing pointer tests**

```csharp
using MMLib.Alvo.Management.Internal;

namespace MMLib.Alvo.Tests.Management;

public sealed class JsonPointerPathTests
{
    [Fact]
    public void Segments_split_on_slash_and_unescape_tilde_one_before_tilde_zero() =>
        JsonPointerPath.Segments("/entities/a~1b/fields/c~0d/computed")
            .ShouldBe(["entities", "a/b", "fields", "c~d", "computed"]);

    [Theory]
    [InlineData("")]
    [InlineData("entities/orders")]
    [InlineData("/")]
    [InlineData("/entities//rules")]
    public void A_pointer_that_names_no_node_is_refused(string pointer) =>
        Should.Throw<MMLib.Alvo.Management.ManagementRequestException>(() => JsonPointerPath.Segments(pointer));

    [Theory]
    [InlineData("/entities/o/rules/list", "/entities/o/rules/list", true)]
    [InlineData("/entities/o/hooks/beforeCreate/0/condition", "/entities/o/hooks/beforeCreate/0", false)]
    [InlineData("/entities/o/hooks/beforeCreate/0/action/mutate/status", "/entities/o/hooks/beforeCreate/0/action", false)]
    [InlineData("/entities/o/rules/list/x", "/entities/o/rules/list", true)]
    [InlineData("/entities/o/rules/listing", "/entities/o/rules/list", false)]
    [InlineData("/entities/o/rules", "/entities/o/rules/list", false)]
    public void A_path_is_at_or_under_a_pointer_by_whole_segments(string path, string pointer, bool expected) =>
        JsonPointerPath.IsAtOrUnder(path, pointer).ShouldBe(expected);
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*JsonPointerPathTests'`
Expected: build FAIL — `JsonPointerPath` does not exist.

- [ ] **Step 3: Implement `JsonPointerPath`**

```csharp
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Management.Internal;

/// <summary>The RFC 6901 JSON pointer, as far as a descriptor slot needs it.</summary>
internal static class JsonPointerPath
{
    /// <summary>The unescaped segments of <paramref name="pointer"/>.</summary>
    /// <param name="pointer">A pointer that starts with <c>/</c> and has no empty segment.</param>
    /// <exception cref="ManagementRequestException">The pointer names no node.</exception>
    internal static IReadOnlyList<string> Segments(string pointer)
    {
        if (string.IsNullOrEmpty(pointer) || pointer[0] != '/' || pointer.Contains("//", StringComparison.Ordinal) || pointer == "/")
        {
            throw new ManagementRequestException(
                $"'{pointer}' is not a pointer to a descriptor node. Send an RFC 6901 pointer that starts with '/', "
                + "for example '/entities/orders/rules/list'.");
        }

        return [.. pointer[1..].Split('/').Select(Unescape)];
    }

    /// <summary>Whether <paramref name="path"/> is the node <paramref name="pointer"/> names or one beneath it.</summary>
    /// <param name="path">A finding's path.</param>
    /// <param name="pointer">The slot's pointer.</param>
    internal static bool IsAtOrUnder(string path, string pointer) =>
        string.Equals(path, pointer, StringComparison.Ordinal)
        || path.StartsWith(pointer + "/", StringComparison.Ordinal);

    private static string Unescape(string segment) =>
        segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
}
```

- [ ] **Step 4: Run to verify pass**, then normalise both new `.cs` files (BOM+CRLF, see Global Constraints) and re-run.

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*JsonPointerPathTests'`
Expected: PASS.

- [ ] **Step 5: Write the failing slot-check tests**

The tests use the real validator (the point is parity with apply). Find how the repo builds one: `grep -rn "IDescriptorValidator" test/MMLib.Alvo.Tests | head` and copy that fixture's construction; call it `Validator()` below. Use a minimal valid descriptor from the same tests as `Descriptor()` returning a `JsonObject` with entity `orders` (fields `status` enum, `total` decimal, `unit_price` decimal), `auth.roles` `["clerk"]`.

```csharp
using System.Text.Json.Nodes;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Management;
using MMLib.Alvo.Management.Internal;

namespace MMLib.Alvo.Tests.Management;

public sealed class ExpressionSlotCheckTests
{
    private const string ListRule = "/entities/orders/rules/list";

    [Fact]
    public void A_valid_rule_has_no_error_at_its_slot() =>
        Check(ListRule, "'clerk' in @user.roles").Where(IsError).ShouldBeEmpty();

    [Fact]
    public void A_rule_naming_an_undeclared_role_is_refused_at_its_slot_as_apply_refuses_it()
    {
        var findings = Check(ListRule, "'amdin' in @user.roles");

        findings.Where(IsError).ShouldNotBeEmpty("compiling alone would pass this; only the validator knows the role catalog");
        findings.ShouldAllBe(f => JsonPointerPath.IsAtOrUnder(f.Path, ListRule));
    }

    [Fact]
    public void A_syntax_error_is_data_not_an_exception() =>
        Check(ListRule, "status ==").Where(IsError).ShouldNotBeEmpty();

    [Fact]
    public void A_good_slot_is_green_while_another_slot_is_broken()
    {
        var descriptor = Descriptor();
        descriptor["entities"]!["orders"]!["rules"]!["get"] = "status ==";

        Check(descriptor, ListRule, "'clerk' in @user.roles").Where(IsError).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/entities/nope/rules/list")]
    [InlineData("/entities/orders/hooks/beforeCreate/7/condition")]
    [InlineData("/entities/orders/rules/list/deeper")]
    public void A_slot_the_descriptor_does_not_contain_is_refused(string pointer) =>
        Should.Throw<ManagementRequestException>(() => Check(pointer, "true"));

    [Fact]
    public void A_descriptor_that_is_not_json_is_refused() =>
        Should.Throw<ManagementRequestException>(() => ExpressionSlotCheck.Check(Validator(), "{not json", ListRule, "true"));

    [Fact]
    public void A_candidate_with_characters_json_escapes_reaches_the_validator_unchanged()
    {
        var findings = Check(ListRule, "'a\"b\\c' == 'x\ny'");

        findings.Where(IsError).ShouldNotBeEmpty();
        findings.Single(IsError).Message.ShouldNotContain("\\u", Case.Insensitive);
    }

    [Fact]
    public void A_mutate_value_is_spliced_in_the_dollar_cel_form_the_descriptor_documents()
    {
        var descriptor = DescriptorWithMutateHook();
        const string pointer = "/entities/orders/hooks/beforeCreate/0/action/mutate/status";

        ExpressionSlotCheck.Check(Validator(), descriptor.ToJsonString(), pointer, "'open'")
            .Where(IsError).ShouldBeEmpty();
        ExpressionSlotCheck.Check(Validator(), descriptor.ToJsonString(), pointer, "nope(")
            .Where(IsError).ShouldNotBeEmpty();
    }

    [Fact]
    public void A_pointer_with_escaped_segments_addresses_the_right_node()
    {
        var descriptor = Descriptor();
        descriptor["entities"]!["a/b"] = descriptor["entities"]!["orders"]!.DeepClone();

        Check(descriptor, "/entities/a~1b/rules/list", "'clerk' in @user.roles").Where(IsError).ShouldBeEmpty();
    }

    private static bool IsError(DescriptorValidationError f) => f.Severity == DescriptorValidationSeverity.Error;

    private static IReadOnlyList<DescriptorValidationError> Check(string pointer, string source) =>
        Check(Descriptor(), pointer, source);

    private static IReadOnlyList<DescriptorValidationError> Check(JsonNode descriptor, string pointer, string source) =>
        ExpressionSlotCheck.Check(Validator(), descriptor.ToJsonString(), pointer, source);

    // Descriptor(), DescriptorWithMutateHook(), Validator(): build from the same minimal valid descriptor and
    // validator construction the existing descriptor-validation tests use (grep IDescriptorValidator in
    // test/MMLib.Alvo.Tests). DescriptorWithMutateHook() adds
    // hooks.beforeCreate = [{ "action": { "mutate": { "status": { "$cel": "'open'" } } } }] to orders.
}
```

The three helper builders **must be written out in the file** (no stub): copy the construction the neighbouring descriptor tests use, so the validator under test is the production one.

- [ ] **Step 6: Run to verify failure**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ExpressionSlotCheckTests'`
Expected: build FAIL — `ExpressionSlotCheck` does not exist.

- [ ] **Step 7: Implement `ExpressionSlotCheck`**

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using MMLib.Alvo.Descriptor;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// What apply would say about <b>one</b> expression slot: the candidate is spliced into the descriptor and the
/// very validator apply runs judges the whole document; only the findings at or under the slot are kept.
/// </summary>
/// <remarks>
/// Compiling the source alone is not apply's check: every slot adds post-compile refusals (role literals, hook
/// phase and envelope, mutate type fit, computed shape and render). Reusing the validator is how this stays
/// the same code path rather than a second opinion that drifts.
/// </remarks>
internal static class ExpressionSlotCheck
{
    private const string MutateSegment = "mutate";

    /// <summary>The validator's findings at or under <paramref name="pointer"/> with <paramref name="source"/> in place.</summary>
    /// <param name="validator">The validator apply uses.</param>
    /// <param name="descriptorJson">The working-copy descriptor.</param>
    /// <param name="pointer">The slot's RFC 6901 pointer; the slot must already exist.</param>
    /// <param name="source">The candidate expression, as typed.</param>
    /// <exception cref="ManagementRequestException">The request cannot be answered as sent.</exception>
    internal static IReadOnlyList<DescriptorValidationError> Check(
        IDescriptorValidator validator, string descriptorJson, string pointer, string source)
    {
        var root = Parse(descriptorJson);
        Splice(root, JsonPointerPath.Segments(pointer), pointer, source);

        return [.. validator.Validate(root.ToJsonString()).Errors.Where(f => JsonPointerPath.IsAtOrUnder(f.Path, pointer))];
    }

    private static JsonNode Parse(string descriptorJson)
    {
        try
        {
            return JsonNode.Parse(descriptorJson) ?? throw NotADescriptor();
        }
        catch (JsonException)
        {
            throw NotADescriptor();
        }
    }

    private static void Splice(JsonNode root, IReadOnlyList<string> segments, string pointer, string source)
    {
        var parent = Walk(root, segments.Take(segments.Count - 1), pointer);
        var last = segments[^1];
        var value = IsMutateValue(segments) ? new JsonObject { ["$cel"] = source } : (JsonNode)JsonValue.Create(source)!;

        switch (parent)
        {
            case JsonObject obj when obj.ContainsKey(last):
                obj[last] = value;
                break;
            case JsonArray array when int.TryParse(last, out var index) && index >= 0 && index < array.Count:
                array[index] = value;
                break;
            default:
                throw Absent(pointer);
        }
    }

    private static JsonNode Walk(JsonNode root, IEnumerable<string> segments, string pointer)
    {
        var node = root;
        foreach (var segment in segments)
        {
            node = node switch
            {
                JsonObject obj when obj.TryGetPropertyValue(segment, out var child) && child is not null => child,
                JsonArray array when int.TryParse(segment, out var i) && i >= 0 && i < array.Count && array[i] is not null => array[i]!,
                _ => throw Absent(pointer),
            };
        }

        return node;
    }

    /// <summary>A mutate target holds <c>{"$cel": source}</c>, the one slot that is not a bare string.</summary>
    private static bool IsMutateValue(IReadOnlyList<string> segments) =>
        segments.Count >= 2 && string.Equals(segments[^2], MutateSegment, StringComparison.Ordinal);

    private static ManagementRequestException NotADescriptor() => new(
        "The 'descriptor' is not a JSON object. Send the working-copy descriptor exactly as the dashboard holds it.");

    private static ManagementRequestException Absent(string pointer) => new(
        $"'{pointer}' does not exist in the descriptor sent. The slot must already be in the descriptor — add the "
        + "rule or hook to the working copy first, then check the expression in it.");
}
```

- [ ] **Step 8: Run to verify pass**, normalise (BOM+CRLF), re-run, then ring0.

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ExpressionSlotCheckTests' --filter-class '*JsonPointerPathTests'` then `scripts/test-ring0`
Expected: PASS. If `A_candidate_with_characters_json_escapes…` fails because the validator reports a different message, assert on `IsError` only and keep the `\u` guard — the point is that the source arrived intact, which the `'a"b\\c'` literal failing to *parse* differently would reveal.

- [ ] **Step 9: Commit**

```bash
git add src/MMLib.Alvo/Management/Internal/ExpressionSlotCheck.cs src/MMLib.Alvo/Management/Internal/JsonPointerPath.cs test/MMLib.Alvo.Tests/Management/
git commit -m "feat(management): judge one expression slot with the validator apply uses"
```

---

### Task 2: the operation — contract, service, route, level, implementers

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Management/IAlvoManagement.cs` (add member after `SimulatePolicyAsync`)
- Modify: `src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs` (add two records after `ManagementPolicyVerdict`)
- Modify: `src/MMLib.Alvo/Management/Access/ManagementOperation.cs` (add `CheckExpression`)
- Modify: `src/MMLib.Alvo/Management/Access/ManagementOperations.cs` (`[ManagementOperation.CheckExpression] = ManagementLevel.Viewer`)
- Modify: `src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs` (primary-ctor parameter `IDescriptorValidator validator`, new member)
- Modify: `src/MMLib.Alvo/Management/Setup.cs:~96` (pass `provider.GetRequiredService<IDescriptorValidator>()` in the right position)
- Modify: `src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs` (`MapExpressionCheck`, called in `Map()`)
- Modify: `test/MMLib.Alvo.Api.Tests/Management/ManagementAccessTests.cs` (`RefusingManagement`, ~:278-340)
- Modify: `test/MMLib.Alvo.Admin.Tests.EndToEnd/ManagementDecorator.cs` and `KeptFollowScenarios.cs` (`Holding`, ~:103)
- Modify: `test/MMLib.Alvo.Tests/Management/ManagementOperationsTests.cs` (viewer set)
- Modify: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt` (regenerate, review the diff)
- Test: `test/MMLib.Alvo.Api.Tests/Management/ManagementExpressionCheckTests.cs`

**Interfaces:**
- Consumes: `ExpressionSlotCheck.Check` (Task 1).
- Produces (public, Abstractions):
  - `public sealed record ManagementExpressionCheck(string DescriptorJson, string Path, string Source);`
  - `public sealed record ManagementExpressionVerdict(IReadOnlyList<DescriptorValidationError> Findings) { public bool IsValid => ... }`
  - `Task<ManagementExpressionVerdict> IAlvoManagement.CheckExpressionAsync(string project, ManagementExpressionCheck request, CancellationToken ct = default);`
  - route `POST /management/projects/{project}/cel/check`.

- [ ] **Step 1: Write the failing HTTP tests** (template: `ManagementPolicySimulationTests`; `ManagedFleet`, `_ops` viewer key, `world.SendAsync(HttpMethod.Post, url, key, body:)`)

```csharp
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary><c>POST {m}/projects/{p}/cel/check</c> — what apply would say about one expression slot.</summary>
public class ManagementExpressionCheckTests
{
    private static readonly TestApiKey _ops = new("mgmt-ops", ["ops"], ["*:read"]);
    private const string ListRule = "/entities/vehicles/rules/list";

    [Fact]
    public async Task A_valid_expression_answers_no_error()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var verdict = await Check(world, ListRule, "'dispatcher' in @user.roles");

        verdict["isValid"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task An_expression_apply_would_refuse_answers_the_refusal_at_its_slot_with_a_fix()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var verdict = await Check(world, ListRule, "'amdin' in @user.roles");

        verdict["isValid"]!.GetValue<bool>().ShouldBeFalse();
        var finding = verdict["findings"]!.AsArray().Single()!;
        finding["path"]!.GetValue<string>().ShouldStartWith(ListRule);
        finding["fixSuggestion"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_slot_the_descriptor_lacks_is_a_422_with_the_pointer_in_the_detail()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        using var response = await Ask(world, "/entities/nope/rules/list", "true");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_missing_body_is_a_422_not_a_framework_400()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        using var response = await world.SendAsync(HttpMethod.Post, $"{ManagedFleet.Routes}/cel/check", _ops, body: null);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_descriptor_over_the_cap_is_refused_before_it_is_parsed()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);
        var body = Body(ListRule, "true");
        body["descriptor"] = new string(' ', 1_000_001);

        using var response = await world.SendAsync(HttpMethod.Post, $"{ManagedFleet.Routes}/cel/check", _ops, body: body);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_finding_never_carries_stored_state_the_caller_did_not_send()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);
        var sent = ReadFleetDescriptor();
        sent["auth"]!["roles"] = new JsonArray("only-this-role");

        var verdict = await world.SendAsync(HttpMethod.Post, $"{ManagedFleet.Routes}/cel/check", _ops, body: Body(sent, ListRule, "'dispatcher' in @user.roles"))
            .ContinueWith(t => t.Result.ReadJsonObjectAsync()).Unwrap();

        verdict["isValid"]!.GetValue<bool>().ShouldBeFalse("the role catalog is the one the caller sent, not the stored one");
    }

    private static Task<HttpResponseMessage> Ask(AlvoApiWorld world, string pointer, string source) =>
        world.SendAsync(HttpMethod.Post, $"{ManagedFleet.Routes}/cel/check", _ops, body: Body(pointer, source));

    private static async Task<JsonObject> Check(AlvoApiWorld world, string pointer, string source) =>
        await (await Ask(world, pointer, source)).ReadJsonObjectAsync();

    private static JsonObject Body(string pointer, string source) => Body(ReadFleetDescriptor(), pointer, source);

    private static JsonObject Body(JsonObject descriptor, string pointer, string source) => new()
    {
        ["descriptor"] = descriptor.ToJsonString(),
        ["path"] = pointer,
        ["source"] = source,
    };

    private static JsonObject ReadFleetDescriptor() =>
        JsonNode.Parse(File.ReadAllText(ManagedFleet.DescriptorPath))!.AsObject();
}
```

Note: the wire shape carries `descriptorJson` as a **JSON string** (the working copy's own serialised text), exactly `ManagementExpressionCheck.DescriptorJson`. Check how `SendAsync` treats a null body (`body: null`) in `AlvoApiWorld`; if it cannot send an empty POST, send `Content-Length: 0` through the underlying client the way `ManagementAccessTests` does for the gate sweep.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*ManagementExpressionCheckTests'`
Expected: FAIL — 404/405 (route absent) or build error.

- [ ] **Step 3: Add the public contract**

In `ManagementModels.cs` (after `ManagementPolicyVerdict`), BOM+CRLF preserved:

```csharp
/// <summary>One expression to check, in the descriptor it will live in.</summary>
/// <param name="Descriptor">
/// The descriptor JSON the editor holds — typically the unapplied working copy. It is the only input the answer
/// depends on: nothing stored is read.
/// </param>
/// <param name="Path">
/// The RFC 6901 pointer of the slot being edited, for example <c>/entities/orders/rules/list</c>. The slot must
/// already exist in <paramref name="Descriptor"/>.
/// </param>
/// <param name="Source">The candidate expression, as typed.</param>
public sealed record ManagementExpressionCheck(string DescriptorJson, string Path, string Source);

/// <summary>What applying would say about one expression.</summary>
/// <param name="Findings">
/// The validator's findings at or under the slot, errors and warnings alike, each with a path and a fix. A
/// warning never makes the expression invalid, as it never blocks an apply.
/// </param>
public sealed record ManagementExpressionVerdict(IReadOnlyList<DescriptorValidationError> Findings)
{
    /// <summary>Gets a value indicating whether the slot carries no error.</summary>
    public bool IsValid => Findings.All(f => f.Severity != DescriptorValidationSeverity.Error);
}
```

(`using MMLib.Alvo.Descriptor;` if the file lacks it.) In `IAlvoManagement.cs` after `SimulatePolicyAsync`:

```csharp
    /// <summary>
    /// Answers what applying would say about <b>one</b> expression — by running the validator apply runs on the
    /// descriptor with the candidate spliced in, never a second opinion.
    /// </summary>
    /// <remarks>
    /// It is not a dry-run apply: that is all-or-nothing, tied to a revision and plans a migration, so it cannot
    /// answer per keystroke and one bad expression elsewhere would mask this one. It reads no store, no revision
    /// and no runtime — only the descriptor the caller sends. A candidate that does not compile is an answer
    /// (a finding), not an exception.
    /// </remarks>
    /// <param name="project">The project name.</param>
    /// <param name="request">The descriptor, the slot's pointer and the candidate expression.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The findings at or under the slot.</returns>
    /// <exception cref="ManagementForbiddenException">The caller does not reach this operation's level.</exception>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementRequestException">
    /// The request cannot be answered as sent: no body, a descriptor over the size cap or not JSON, or a pointer
    /// to a slot the descriptor does not contain.
    /// </exception>
    Task<ManagementExpressionVerdict> CheckExpressionAsync(
        string project, ManagementExpressionCheck request, CancellationToken ct = default);
```

- [ ] **Step 4: Level and operation**

`ManagementOperation.cs`: add after `SimulatePolicy`:

```csharp
    /// <summary>Check one expression against the validator apply uses. Reads nothing stored.</summary>
    CheckExpression,
```
`ManagementOperations.cs`: `[ManagementOperation.CheckExpression] = ManagementLevel.Viewer,` after the `SimulatePolicy` line. `ManagementOperationsTests.cs`: add `ManagementOperation.CheckExpression` to the viewer expectation (`A_viewer_may_read_everything_including_the_simulator`).

- [ ] **Step 5: Service**

Add `IDescriptorValidator validator,` to the primary constructor (after `IPolicyEngine policies,`) with a `<param>` doc, and in `Setup.cs` pass `provider.GetRequiredService<Descriptor.IDescriptorValidator>(),` at the same position. Add the member after `SimulatePolicyAsync`, plus the guard:

```csharp
    /// <inheritdoc/>
    public Task<ManagementExpressionVerdict> CheckExpressionAsync(
        string project, ManagementExpressionCheck request, CancellationToken ct = default)
    {
        EnsureMayPerform(ManagementOperation.CheckExpression);
        EnsureServed(project);
        EnsureCheckable(request);

        return Task.FromResult(new ManagementExpressionVerdict(
            ExpressionSlotCheck.Check(validator, request.DescriptorJson, request.Path, request.Source)));
    }

    /// <summary>The most descriptor text a check will parse: a viewer can call this on every keystroke.</summary>
    private const int MaxCheckedDescriptorChars = 1_000_000;

    private static void EnsureCheckable(ManagementExpressionCheck? request)
    {
        if (request is null || request.Descriptor is null || request.Path is null || request.Source is null)
        {
            throw new ManagementRequestException(
                "A check needs a 'descriptor', a 'path' and a 'source'. Send all three: the answer is about one "
                + "expression in one descriptor, and a missing part would be answered as a pass.");
        }

        if (request.Descriptor.Length > MaxCheckedDescriptorChars)
        {
            throw new ManagementRequestException(
                $"The 'descriptor' is over {MaxCheckedDescriptorChars:N0} characters. Send the project's own descriptor; "
                + "a check is not an apply and takes no more than one.");
        }
    }
```

- [ ] **Step 6: Route**

In `ManagementEndpoints.cs` add `MapExpressionCheck(group);` in `Map()` next to `MapPolicySimulation(group);` and:

```csharp
    /// <summary>
    /// <c>POST {prefix}/projects/{project}/cel/check</c> — <see cref="IAlvoManagement.CheckExpressionAsync"/>.
    /// </summary>
    /// <remarks>
    /// The body binds as nullable for the reason <see cref="MapPolicySimulation"/> documents: a required body
    /// answers a framework 400 before the gate, and the gate must answer first.
    /// </remarks>
    /// <param name="group">The group to map into.</param>
    private static void MapExpressionCheck(RouteGroupBuilder group) =>
        Gate(
            group.MapPost(
                "/projects/{project}/cel/check",
                (string project,
                    ManagementExpressionCheck? request,
                    IAlvoManagement management,
                    CancellationToken ct) =>
                    Answer(() => management.CheckExpressionAsync(project, request!, ct))),
            new ManagementRoute(nameof(IAlvoManagement.CheckExpressionAsync), ManagementOperation.CheckExpression));
```

- [ ] **Step 7: Implementers** — add the member to `RefusingManagement` (throws `ManagementForbiddenException`, like its neighbours), `ManagementDecorator` (`public virtual Task<ManagementExpressionVerdict> CheckExpressionAsync(string project, ManagementExpressionCheck request, CancellationToken ct = default) => inner.CheckExpressionAsync(project, request, ct);`) and `Holding` in `KeptFollowScenarios.cs` (same pass-through). Copy the exact shape of each file's `SimulatePolicyAsync` line.

- [ ] **Step 8: Run to verify pass**

Run: `dotnet build` (0 warnings, 0 errors), then `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*ManagementExpressionCheckTests' --filter-class '*ManagementContractTests' --filter-class '*ManagementAccessTests'` and `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ManagementOperationsTests'`.
Expected: PASS. `PublicApi` approval test FAILS (baseline grew) — expected; next step.

- [ ] **Step 9: Approve the public-API growth deliberately**

Regenerate the baseline the way the repo does (see the test's failure output for the `.received.` file; copy it over the `.verified.` one) and read the diff: it must add **exactly** `CheckExpressionAsync` on `IAlvoManagement`, `ManagementExpressionCheck`, `ManagementExpressionVerdict` (+ its `IsValid`). Anything else is a mistake to undo. Record the justification (the interface is the real contract; the diagnostic type is the existing public `DescriptorValidationError`, no second one) for the PR body.

- [ ] **Step 10: ring0, then commit**

Run: `scripts/test-ring0`. Then:
```bash
git add -A
git commit -m "feat(management): CheckExpressionAsync — what apply would say about one expression, per keystroke"
```

---

### Task 3: parity with apply (property test)

**Files:**
- Test: `test/MMLib.Alvo.Host.Tests/ExpressionCheckAgreementTests.cs` (the home of `PolicySimulatorAgreementTests`; copy its world construction)

**Interfaces:**
- Consumes: `IAlvoManagement.CheckExpressionAsync` (Task 2), `ApplyDescriptorAsync(DryRun)` for the apply-side verdict.

- [ ] **Step 1: Write the failing agreement test.** A theory over a corpus of `(slotKind, pointer, source)` covering every slot kind in spec §4.1: rule (valid, undeclared role, syntax error, `old.` misuse), before-hook condition (valid, `old.x` in `beforeCreate`), after-hook condition (valid, `@tenant`), mutate value (valid, wrong type for the target field), computed (valid, comparison over arithmetic, a constant that would be a bind parameter). For each: splice the candidate into the descriptor by hand (the test's own `JsonNode` edit, **not** `ExpressionSlotCheck`), run `ApplyDescriptorAsync` with `DryRun = true`, catch `DescriptorValidationException`, restrict its errors to the slot, and assert the **set of (path, message)** equals the check's error set; and that a green apply has no check error.

```csharp
[Theory]
[MemberData(nameof(Corpus))]
public async Task Check_and_apply_refuse_the_same_expression_the_same_way(string pointer, string source)
{
    await using var host = await StartAsync();
    var descriptor = host.CurrentDescriptor();

    var checkErrors = (await host.Management.CheckExpressionAsync(host.Project,
            new ManagementExpressionCheck(descriptor.ToJsonString(), pointer, source)))
        .Findings.Where(IsError).Select(Key).Order().ToList();
    var applyErrors = await ApplyDryRunErrors(host, Splice(descriptor, pointer, source), pointer);

    checkErrors.ShouldBe(applyErrors, $"for {pointer} = {source}");
}
```

`Corpus` returns ≥ 20 cases (≥ 3 per slot kind, both outcomes); `Splice` is the test's own independent implementation (mutate → `{"$cel": ...}`); `ApplyDryRunErrors` returns `[]` when the dry run succeeds.

- [ ] **Step 2: Run to verify failure** — a missing helper, or a real disagreement. A real disagreement is a **finding**: stop and report it (it means the check is not the apply path for some slot) rather than weakening the assertion.

- [ ] **Step 3: Make it pass** (helpers; any disagreement fixed in `ExpressionSlotCheck`, never by filtering the test).

- [ ] **Step 4: Run** `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*ExpressionCheckAgreementTests'` — PASS; then `scripts/test-ring1`.

- [ ] **Step 5: Commit** `test(management): cel/check agrees with apply for every slot kind`.

---

### Task 4: measure the cost, set the debounce

**Files:**
- Create: `test/MMLib.Alvo.Host.Tests/ExpressionCheckCostTests.cs` (a measurement that **records**, does not gate on a guessed number)
- Modify: `docs/superpowers/specs/2026-10-01-f5-expression-check-design.md` §5.5 (record the numbers)

- [ ] **Step 1:** Write a test (skipped unless `ALVO_MEASURE=1`, like other measurements in the repo — `grep -rn ALVO_MEASURE test` for the idiom, else use `Skip.Unless`) that loads `examples/bike-workshop` descriptor, warms up 20 calls, runs 200 `CheckExpressionAsync` over the rule slot, and prints p50/p95/max in ms.
- [ ] **Step 2:** Run it: `ALVO_MEASURE=1 dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*ExpressionCheckCostTests'`. Record the numbers.
- [ ] **Step 3:** Decide: p95 ≤ 100 ms → debounce 300 ms, check-as-you-type. Otherwise → check-on-blur (the dashboard task below reads `ExpressionCheck.Debounce`; it is one constant). Write the numbers and the choice into spec §5.5 and the PR body.
- [ ] **Step 4:** Commit `test(management): measure what a check costs on the bike-workshop descriptor`.

---

### Task 5: dashboard — gateway and `ExpressionCheck` state

**Files:**
- Modify: `src/MMLib.Alvo.Admin/Internal/ManagementGateway.cs` (after `SimulateAsync`, ~:165)
- Create: `src/MMLib.Alvo.Admin/Components/DesignSystem/ExpressionCheck.cs`
- Test: `test/MMLib.Alvo.Admin.Tests/ExpressionCheckTests.cs`

**Interfaces:**
- Consumes: `IAlvoManagement.CheckExpressionAsync` (Task 2); `ProjectAsync`, `AsOperatorAsync` already in the gateway.
- Produces:
  - `ManagementGateway.CheckExpressionAsync(string descriptorJson, string path, string source, CancellationToken ct) -> Task<ManagementExpressionVerdict?>` — **null** when the check itself failed (never throws to the caller; spec §4.3).
  - `internal sealed class ExpressionCheck` with `Task SubmitAsync(string key, string source, Func<string, CancellationToken, Task<ManagementExpressionVerdict?>> check)`, `IReadOnlyList<DescriptorValidationError> Findings(string key)`, `string? DescribedBy(string key)`, `event Action? Changed`, `static TimeSpan Debounce`.

- [ ] **Step 1: Write the failing state tests** (pure, no browser): latest-wins and cancellation.

```csharp
public sealed class ExpressionCheckTests
{
    [Fact]
    public async Task The_latest_source_wins_even_when_an_older_check_answers_last()
    {
        var sut = new ExpressionCheck { DebounceOverride = TimeSpan.Zero };
        var slow = new TaskCompletionSource<ManagementExpressionVerdict?>();

        var first = sut.SubmitAsync("rule-list", "a", (_, _) => slow.Task);
        await sut.SubmitAsync("rule-list", "b", (_, _) => Task.FromResult<ManagementExpressionVerdict?>(Refusal("b is wrong")));
        slow.SetResult(new ManagementExpressionVerdict([]));
        await first;

        sut.Findings("rule-list").Single().Message.ShouldBe("b is wrong");
    }

    [Fact]
    public async Task A_check_that_failed_shows_nothing_and_throws_nothing()
    {
        var sut = new ExpressionCheck { DebounceOverride = TimeSpan.Zero };

        await sut.SubmitAsync("rule-list", "a", (_, _) => Task.FromResult<ManagementExpressionVerdict?>(null));

        sut.Findings("rule-list").ShouldBeEmpty();
    }

    [Fact]
    public async Task Findings_clear_when_the_source_becomes_empty()
    {
        var sut = new ExpressionCheck { DebounceOverride = TimeSpan.Zero };
        await sut.SubmitAsync("k", "x", (_, _) => Task.FromResult<ManagementExpressionVerdict?>(Refusal("no")));

        await sut.SubmitAsync("k", "", (_, _) => throw new InvalidOperationException("no call for an empty source"));

        sut.Findings("k").ShouldBeEmpty();
    }

    [Fact]
    public async Task Describing_an_input_points_at_its_sentence_only_while_there_is_one()
    {
        var sut = new ExpressionCheck { DebounceOverride = TimeSpan.Zero };
        sut.DescribedBy("k").ShouldBeNull();

        await sut.SubmitAsync("k", "x", (_, _) => Task.FromResult<ManagementExpressionVerdict?>(Refusal("no")));

        sut.DescribedBy("k").ShouldBe("k-check");
    }

    private static ManagementExpressionVerdict Refusal(string message) => new(
        [new DescriptorValidationError("/p", message, "fix", DescriptorValidationSeverity.Error)]);
}
```

- [ ] **Step 2: Run to verify failure** (`dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*ExpressionCheckTests'`) — build FAIL.

- [ ] **Step 3: Implement `ExpressionCheck`** — one `Dictionary<string, (int Generation, List<DescriptorValidationError> Findings)>`; `SubmitAsync` increments the key's generation, awaits the debounce (`DebounceOverride ?? Debounce`), returns if its generation is stale, calls `check`, and **only if the generation is still current** stores the errors (a warning is stored too; the component styles by severity) and raises `Changed`. `null` verdict stores nothing. An empty or whitespace `source` clears the key and raises `Changed` without calling `check`. `DescribedBy(key)` returns `$"{key}-check"` when there is at least one finding. `Debounce` default is the value Task 4 decided. It must **not** touch focus (Review Focus 9) — it has no JS interop at all, by construction.

- [ ] **Step 4: Gateway method**

```csharp
    /// <summary>
    /// Asks what apply would say about one expression, or <see langword="null"/> when the check itself could not be
    /// asked — a helper must never be the reason an operator cannot edit.
    /// </summary>
    public async Task<ManagementExpressionVerdict?> CheckExpressionAsync(
        string descriptorJson, string path, string source, CancellationToken ct)
    {
        try
        {
            var project = await ProjectAsync(ct);
            return await AsOperatorAsync(
                () => management.CheckExpressionAsync(project, new ManagementExpressionCheck(descriptorJson, path, source), ct), ct);
        }
        catch (Exception ex) when (ex is ManagementRequestException or ManagementForbiddenException or OperationCanceledException or HttpRequestException)
        {
            return null;
        }
    }
```

Copy the surrounding methods' exact `AsOperatorAsync` overload usage (it has `Task<T>` and stream overloads) and do **not** cache.

- [ ] **Step 5: Run** the state tests (PASS) and `scripts/test-ring0`. Add a gateway test next to the existing `ManagementGateway` tests asserting `null` on a refused request (NSubstitute `management` throwing `ManagementRequestException`).

- [ ] **Step 6: Commit** `feat(admin): a focus-free, latest-wins expression check state and its gateway call`.

---

### Task 6: dashboard — wire the inputs, one e2e each

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Schema/ExpressionSlots.cs` (+ test `test/MMLib.Alvo.Admin.Tests/ExpressionSlotsTests.cs`)
- Modify: `src/MMLib.Alvo.Admin/Components/Rules/RulesTab.razor` (`rule-{operation}`, ~:79)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor` (`hook-condition` ~:123, `hook-mutate-value` ~:182)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/FieldEditor.razor` (`new-field-computed` ~:246)
- Modify: the `.razor.cs`/draft classes those components already use
- Test: `test/MMLib.Alvo.Admin.Tests.EndToEnd/ExpressionCheckScenarios.cs`

**Interfaces:**
- Consumes: `ExpressionCheck` and `ManagementGateway.CheckExpressionAsync` (Task 5), `WorkingCopy.Json` (the working-copy text).
- Produces: `internal static class ExpressionSlots` with
  - `(string Json, string Path) ForRule(string workingJson, string entity, string operation, string source)` — ensures `/entities/{e}/rules` exists on a clone, returns the clone's JSON and `/entities/{e}/rules/{op}`;
  - `(string Json, string Path)? ForHookCondition(...)` / `ForMutateValue(...)` / `ForComputed(...)` — build the candidate on a **clone** with the component's own existing builder (`HookBuilder`, the field builder `FieldEditor` already uses to add a field), so what is checked is exactly what the form would add; return `null` when the draft is too incomplete to place (no target field yet), in which case nothing is shown.

- [ ] **Step 1: Failing `ExpressionSlots` tests** — for each `For…`: (a) the returned JSON parses and contains the candidate at the returned path, (b) the **original working JSON is untouched** (no mutation of the operator's real working copy — Review Focus), (c) the path equals the one `RefusalPlaces` uses for the same slot (so a finding can be placed with the existing helper), (d) an entity name with `/` is escaped as `~1`.
- [ ] **Step 2: Run to verify failure**, then implement `ExpressionSlots` (clone with `JsonNode.DeepClone`, never `WorkingCopy` itself).
- [ ] **Step 3: Wire the rule input first** (the simplest slot). In `RulesTab.razor` give each rule textarea `aria-describedby="@Combine(DescribedBy(operation, declared), Check.DescribedBy(id))"`, and render after the field, only when `Check.Findings(id)` is non-empty:

```razor
@foreach (var finding in Check.Findings(id))
{
    <p class="a-field__problem" id="@($"{id}-check")" role="status"
       data-testid="@($"check-{id}")" data-severity="@finding.Severity">
        @finding.Message @if (finding.FixSuggestion is { } fix) { <span class="a-field__fix">@fix</span> }
    </p>
}
```

  and in `ValueChanged` call `Drafts.Set(...)` then `_ = Check.SubmitAsync(id, text, (src, ct) => { var (json, path) = ExpressionSlots.ForRule(WorkingCopy.Json, entity, operation, src); return Gateway.CheckExpressionAsync(json, path, src, ct); })`. `Check.Changed` triggers `StateHasChanged` via `InvokeAsync`; unsubscribe on dispose (the repo's `ComponentLifetime` helper exists for this — use it as the neighbouring components do). Reuse `.a-field__problem`; add `.a-field__problem[data-severity="Warning"]` styling to the design-system CSS only if the existing token for warning text exists — do not invent a colour.
- [ ] **Step 4: e2e for the rule input** — in `ExpressionCheckScenarios.cs`, using the suite's existing world and sign-in helpers (copy the shape of `SchemaScenarios`): open the entity's Rules tab, type `'amdin' in @user.roles` into `rule-list`, expect `[data-testid="check-rule-list"]` to appear within 3 s with text mentioning the role; replace with `'admin' in @user.roles`, expect it to disappear; assert the input never lost focus (`document.activeElement.id == 'rule-list'` after both). Run: `scripts/test-admin-e2e --filter-class '*ExpressionCheckScenarios'` (read the script's usage first).
- [ ] **Step 5: Repeat Step 3–4** for `hook-condition`, `hook-mutate-value`, `new-field-computed`, one commit each, each with its own e2e scenario and its own `ExpressionSlots` test already green from Step 1. For `new-field-computed` use the field builder `FieldEditor` already has to materialise the draft field on the clone; the field's own validity errors elsewhere are filtered by the server (Review Focus 4).
- [ ] **Step 6: Field default** — no wiring. Add an e2e assertion that `new-field-default` shows **no** check sentence for a `{"$cel": …}` default (it shows the existing capabilities refusal, unchanged) so the exclusion in spec §4.2 is pinned rather than accidental.
- [ ] **Step 7:** `scripts/test-ring0`, then `scripts/test-admin-e2e` whole.
- [ ] **Step 8: Commit** per input: `feat(admin): show what apply would say under the <input> input`.

---

### Task 7: docs, review, PR

**Files:**
- Modify: `docs/architecture/management-api.md` (route table :15-26 gets a row; operation-count prose :50-53; a section "Why `cel/check` is not the dry run, and why it is Viewer")
- Modify: `docs/superpowers/specs/2026-10-01-f5-expression-check-design.md` (§4.3: the check uses its own focus-free `ExpressionCheck`, **not** `FieldRefusals`, because `FieldRefusals` takes focus on every refusal — recorded as a deviation; §5.5 numbers from Task 4)
- Modify: `docs/PLAN.md` only if plan-guard asks for a marker move (it will not: F5 stays current)

- [ ] **Step 1:** Update the three docs; run `scripts/check-brief-freshness` (spec/analysis untouched, so it must stay fresh).
- [ ] **Step 2:** `scripts/test-ring1`, then `scripts/test-ring2`, then `dotnet build -c Release` (0 warnings) — CI builds Release.
- [ ] **Step 3:** Freeze the tree (no edits while reviewers read). Dispatch the `alvo-plan-guard` subagent; then the review commands are user-only (memory), so dispatch `csharp-reviewer` for correctness and a security-focused reviewer over `ExpressionSlotCheck`/the endpoint (viewer-level, parses caller JSON; run the `alvo-security-core-review` checklist since this touches the validator/CEL boundary) and label them substitutes. Fix findings, re-run ring1.
- [ ] **Step 4:** Build the PR report with the `alvo-pr-report` skill; open the PR against `main` with a five-line pointer body ending with the session link. Include: the public-API justification (Task 2 Step 9), the measured latency (Task 4), the parity result (Task 3), and the spec/analysis deviations. Do **not** merge.
- [ ] **Step 5: Commit** any doc changes: `docs(management): cel/check — the route, why it is not the dry run, why it is Viewer`.

---

## Self-review

1. **Spec coverage:** §4.1 mechanism → Task 1; request/result/route/level/422 → Task 2; size cap → Task 2 Step 5; parity (§5.2) → Task 3; one-bad-elsewhere (§5.3) → Tasks 1 and 3; dashboard (§4.3, §5.4) → Tasks 5–6; cost (§5.5) → Task 4; API growth (§5.6) → Task 2 Step 9; docs (§5.7) → Task 7; field default excluded (§4.2) → Task 6 Step 6; `Position` and `cel/scope` deferred, nothing to build.
2. **Placeholders:** the three test-helper builders in Task 1 Step 5 and the e2e world helpers in Task 6 are named as "copy the neighbouring construction" with the exact source to copy — they depend on repo fixtures the plan author read only at the signature level; the executor must write them out in full. This is the one place the plan defers to the code, deliberately, so the production validator and the suite's own world are used rather than a re-invention.
3. **Type consistency:** `ManagementExpressionCheck(DescriptorJson, Path, Source)`, `ManagementExpressionVerdict(Findings)`, `CheckExpressionAsync`, `ManagementOperation.CheckExpression`, `ExpressionSlotCheck.Check`, `JsonPointerPath.Segments/IsAtOrUnder`, `ExpressionCheck.SubmitAsync/Findings/DescribedBy/Debounce/DebounceOverride`, `ManagementGateway.CheckExpressionAsync` — used identically across tasks.
4. **Review Focus:** items 1–5 → Task 1 tests; 6–7 → Task 2 tests; 8–9 → Task 5 tests and Task 6 e2e (focus assertion).
