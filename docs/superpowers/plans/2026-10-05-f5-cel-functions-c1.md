# F5 CEL functions, slice C1 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A minimal CEL function catalog with a generic N-ary call path, host-registered functions (`AddCelFunction`) usable in hook conditions and before-hook `mutate` values, five built-ins (`replace trim size abs round`) evaluated in-process, fail-closed function errors surfaced as RFC 7807, and `cel/functions` discovery for operators and agents.

**Architecture:** One internal `CelFunctionCatalog` (built-ins ∪ host registrations) feeds the parser (a catalogued name parses generically; anything else stays a syntax-time refusal), the type checker (overload resolution, two deny-by-default profile gates, the overload bound into the `CelCall` node) and the Management read. The interpreter invokes the bound overload through a marshaller, so `BeforeHookRunner` gains no dependency. A failing function throws an internal `CelFunctionException` that escapes the interpreter's catch-alls, rolls the write back and is answered `500 …/errors/function-failed`.

**Tech Stack:** .NET 10, C# (nullable on), Microsoft.Extensions.DependencyInjection, ASP.NET Core minimal APIs, Microsoft.Testing.Platform + xUnit v3 + Shouldly + CsCheck + NSubstitute, Verify (public-API and OpenAPI snapshots).

**Spec:** `docs/superpowers/specs/2026-10-05-f5-cel-functions-design.md` — read it first; every task argues from it. Background: `docs/superpowers/specs/2026-10-01-f5-expression-check-design.md`, `docs/architecture/cel.md`.

## Global Constraints

- Rings: `scripts/test-ring0` after every step that compiles, `scripts/test-ring1` after each task, `scripts/test-ring2` before the PR. **Rings are Debug, CI is Release**: a CA analyzer error passes every ring; run `dotnet build -c Release` before the PR (and `docker build` if you touched anything the standalone image compiles).
- New and edited `.cs` files are **UTF-8 with BOM and CRLF** (pre-commit `dotnet format` refuses otherwise). Create files with the `Write` tool, then normalise each one: `python3 -c "p='<file>';b=open(p,'rb').read().replace(b'\r\n',b'\n').replace(b'\n',b'\r\n');b=b if b.startswith(b'\xef\xbb\xbf') else b'\xef\xbb\xbf'+b;open(p,'wb').write(b)"`. Edits to existing files keep their line endings (the `Edit` tool does).
- Methods ≤ ~25 lines, one purpose each; every type and member — internal ones too — has XML docs (`alvo-dotnet-conventions`).
- `PublicApi.*.verified.txt` growth is allowed **only** for the symbols in spec §5.10; the Stop hook asks for a justification per added symbol (`alvo-architecture-rules`, "public is the contract"). A moved `*.verified.*` snapshot is judged by `alvo-snapshot-judge`.
- Never merge, never push to `main`. Branch `feat/cel-functions` (stacked on `feat/expression-check`, PR #298).
- Commits: Conventional Commits, each message ending with the trailer line `Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`. Never write a closing keyword (`Closes #…`/`Fixes #…`) in a commit body: it fires from prose; write `Refs #244`.
- One writer per worktree: `/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-wt-fn`. Never `git add -A` while another agent writes there; add the files the task names.
- `CelAcceptanceBaseline.jsonl` must stay **unmodified and green** after every task (spec §10): it is the behaviour-neutrality proof. If `CelAcceptanceCorpusTests` fails, the change is wrong — never regenerate the baseline in this slice.
- Test commands: `dotnet test --project <test project> --filter-class '*<ClassName>'` (MTP; the `test` section of `global.json` selects it).

## Review Focus

Hostile or odd inputs a person will hit; each line names the task whose tests pin it.

1. `f(` / `f(a` / `f(a,` / `f(,)` / `f(a,)` — a syntax error at a position, never a crash or a hang. [Task 3]
2. `f(f(f(…)))` nested 33 deep, and a 2,001-character source made of calls — refused by `MaxDepth`/length before any walker recurses. [Task 3]
3. A field named like a function (`round`, `size`) and a function named like a field (`title(title)`) — the `(` decides; both work. [Tasks 3, 4, 6]
4. A Unicode or otherwise invalid name (`normalizéPhone`, `Normalize`, `abc\n`) — refused at registration; in source it is "not a recognized function". [Tasks 3, 7]
5. A null argument, an empty string, a value of an unexpected CLR type — null in, null out; the body is not invoked for a non-nullable parameter. [Tasks 5, 6]
6. Huge numbers: `abs` of the smallest Int; a `replace` whose result would exceed 1,048,576 characters — fail closed with a reason, never an overflow or an OOM. [Task 6]
7. A host delegate that throws (in a `mutate` and in a condition), returns null, or is slow — throws → 500 `function-failed`, nothing stored, no exception text in the body; null → stored null; invoked exactly once per evaluation. [Tasks 5, 8]
8. Duplicate registration, a registration after the host is built, an async/void/object/`ref`/5-parameter/multicast delegate — `ArgumentException` (or the read-only collection's `InvalidOperationException`) at the call. [Task 7]
9. A host-function descriptor in a host that did not register it (standalone, CLI) — refused as unknown, with "did you mean" and the known list. [Tasks 3, 7]
10. A host function in a rule or a computed field — exactly one refusal saying why and where it works, never a second misleading operand-shape error. [Tasks 4, 10]

---

## File structure

| File | Responsibility | Task |
|---|---|---|
| `src/MMLib.Alvo/Expressions/Internal/CelLexer.cs` | progress invariant | 0 |
| `src/MMLib.Alvo/Expressions/Internal/CelTree.cs` | `CelCall(Name, Arguments)` + `ResultType` + `Function`; `Children` | 1, 4 |
| `src/MMLib.Alvo/Expressions/Internal/CelFunction.cs` | `CelFunctionArgument`, `CelFunction` (+ `Invoke`, `Describe`) | 2, 5, 7 |
| `src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.cs` | built-in entries and bodies | 2, 6 |
| `src/MMLib.Alvo/Expressions/Internal/CelFunctionCatalog.cs` | name → overloads | 2, 7 |
| `src/MMLib.Alvo/Expressions/Internal/CelParser.cs` | generic call production, fixes | 3 |
| `src/MMLib.Alvo/Expressions/Internal/CelCompiler.cs` | holds the catalog | 3, 4 |
| `src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs` | overloads, profiles, binding | 1, 4 |
| `src/MMLib.Alvo/Expressions/Internal/CelArgumentMarshaller.cs` | runtime value → body CLR type | 5 |
| `src/MMLib.Alvo/Expressions/Internal/CelFunctionException.cs` | fail-closed failure | 5 |
| `src/MMLib.Alvo/Expressions/Internal/CelInterpreter.cs` | invoke, rethrow | 1, 5 |
| `src/MMLib.Alvo/Expressions/Internal/HostCelFunction.cs`, `CelFunctionRegistration.cs` | `Delegate` → `CelFunction` | 7 |
| `src/MMLib.Alvo/AlvoBuilderExtensions.cs`, `src/MMLib.Alvo/Expressions/Setup.cs` | `AddCelFunction`, DI | 7 |
| `src/MMLib.Alvo.Abstractions/Expressions/CelFunctionInfo.cs` (+ attributes on `CelValueType.cs`, `CelProfile.cs`) | public discovery DTOs | 7 |
| `src/MMLib.Alvo/Api/AlvoProblemTypes.cs`, `Api/Internal/ProblemResultFactory.cs`, `Api/Internal/AlvoExceptionHandler.cs` | `function-failed` | 8 |
| Management (`IAlvoManagement`, `ManagementOperation(s)`, service, endpoints, doubles) | `cel/functions` | 9 |
| `.claude/skills/…`, `docs/architecture/*.md`, `src/MMLib.Alvo.Ai/…` | docs, skills, assistant tool | 11 |

---

### Task 0: the lexer's progress invariant (#244 insurance)

**Files:**
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelLexer.cs:17-28` (`Tokenize`)
- Test: `test/MMLib.Alvo.Tests/Expressions/CelLexerProgressTests.cs` (create)

**Interfaces:**
- Consumes: `CelLexer.Tokenize(string)`, `CelToken(Kind, Text, Position)`, `CelSyntaxException(string message, int position, string? fixSuggestion)`.
- Produces: `internal static void CelLexer.EnsureAdvanced(int start, int end)` — throws `CelSyntaxException` at `start` when `end <= start`.

- [ ] **Step 1: Write the failing test**

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The lexer's progress invariant (#244): a step that consumed no character is refused with a diagnostic, instead of
/// re-reading the same character forever and growing the token list without bound.
/// </summary>
public sealed class CelLexerProgressTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(7, 7)]
    [InlineData(7, 6)]
    public void A_step_that_consumed_nothing_is_refused_at_its_position(int start, int end) =>
        Should.Throw<CelSyntaxException>(() => CelLexer.EnsureAdvanced(start, end)).Position.ShouldBe(start);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(7, 9)]
    public void A_step_that_consumed_a_character_passes(int start, int end) =>
        Should.NotThrow(() => CelLexer.EnsureAdvanced(start, end));

    [Theory]
    [InlineData("title")]
    [InlineData("'x'")]
    [InlineData("12")]
    [InlineData("1.5")]
    [InlineData("@user")]
    [InlineData("true")]
    [InlineData("(")]
    [InlineData(",")]
    [InlineData("==")]
    [InlineData("<=")]
    [InlineData("!")]
    [InlineData("&&")]
    public void Every_token_family_advances_to_the_end_of_input(string source)
    {
        var tokens = CelLexer.Tokenize(source);

        tokens.Count.ShouldBe(2);
        tokens[^1].Kind.ShouldBe(CelTokenKind.EndOfInput);
        tokens[^1].Position.ShouldBe(source.Length);
    }
}
```

- [ ] **Step 2: Run it — expect a build failure** (`EnsureAdvanced` does not exist)

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelLexerProgressTests'`
Expected: build error CS0117 `'CelLexer' does not contain a definition for 'EnsureAdvanced'`.

- [ ] **Step 3: Implement** — replace `Tokenize` and add the guard right after it:

```csharp
    public static IReadOnlyList<CelToken> Tokenize(string source)
    {
        var tokens = new List<CelToken>();
        var position = 0;

        while (SkipWhitespace(source, ref position))
        {
            var start = position;
            tokens.Add(ReadToken(source, ref position));
            EnsureAdvanced(start, position);
        }

        tokens.Add(new CelToken(CelTokenKind.EndOfInput, string.Empty, position));
        return tokens;
    }

    /// <summary>
    /// The lexer's one progress invariant (#244): every token consumes at least one character. Without it a reader
    /// that returned a token without moving would make <see cref="Tokenize"/> re-read the same character forever,
    /// appending a token each pass — a hang that exhausts memory before any wall clock intervenes, now reachable over
    /// HTTP through <c>cel/check</c>. No source string reaches the throw today; it is the fail-loud backstop.
    /// </summary>
    /// <param name="start">The position the step started at.</param>
    /// <param name="end">The position the step left the reader at.</param>
    /// <exception cref="CelSyntaxException"><paramref name="end"/> is not past <paramref name="start"/>.</exception>
    internal static void EnsureAdvanced(int start, int end)
    {
        if (end <= start)
        {
            throw new CelSyntaxException(
                $"The expression could not be read past position {start}: the reader made no progress there.",
                start,
                "This is a defect in Alvo's CEL reader, not in the expression; report it with the expression attached.");
        }
    }
```

- [ ] **Step 4: Run the tests — expect PASS**, then the corpus

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelLexerProgressTests' --filter-class '*CelLexerTests' --filter-class '*CelAcceptanceCorpusTests'`
Expected: all pass.

- [ ] **Step 5: Normalise, ring0, commit**

```bash
python3 -c "p='test/MMLib.Alvo.Tests/Expressions/CelLexerProgressTests.cs';b=open(p,'rb').read().replace(b'\r\n',b'\n').replace(b'\n',b'\r\n');b=b if b.startswith(b'\xef\xbb\xbf') else b'\xef\xbb\xbf'+b;open(p,'wb').write(b)"
scripts/test-ring0
git add src/MMLib.Alvo/Expressions/Internal/CelLexer.cs test/MMLib.Alvo.Tests/Expressions/CelLexerProgressTests.cs
git commit -m "fix(cel): refuse a lexer step that consumes nothing instead of looping" -m "Refs #244" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 1: `CelCall` carries every argument and its result type (behaviour-neutral)

**Files:**
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelTree.cs:29` (`Children` arm) and `:37-73` (the record)
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelParser.cs:455` and `:476` (two constructions)
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs:907-923` (`CheckCall`, `CheckLowerAsciiCall`)
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelInterpreter.cs:254-259` (`EvaluateCall`)
- Modify: `test/MMLib.Alvo.Tests/Expressions/CelTreeChildrenTests.cs:49,78`, `test/MMLib.Alvo.Tests/Expressions/SqlPredicateRendererTests.cs:472-473`
- Test: `test/MMLib.Alvo.Tests/Expressions/CelCallShapeTests.cs` (create)

**Interfaces:**
- Produces: `internal sealed record CelCall(string Name, IReadOnlyList<CelNode> Arguments) : CelNode` with `public CelValueType ResultType { get; init; } = CelValueType.Null;` and the constants `LowerAscii`, `Now`. The old `Argument` property is **gone** (use list patterns `{ Arguments: [var argument] }`).

- [ ] **Step 1: Write the failing test**

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>The call node carries every argument and, once checked, its result type.</summary>
public sealed class CelCallShapeTests
{
    private static readonly CelFieldRef _title = new("title", CelValueType.String, CelRecordState.Current);

    [Fact]
    public void A_call_reports_every_argument_in_order()
    {
        var search = new CelLiteral(CelValueType.String, "a");
        var replacement = new CelLiteral(CelValueType.String, "b");

        CelTree.Children(new CelCall("replace", [_title, search, replacement])).ShouldBe([_title, search, replacement]);
    }

    [Fact]
    public void A_checked_call_carries_its_result_type()
    {
        ((CelCall)CelFixtures.CompileMutate("lowerAscii(title)").Root).ResultType.ShouldBe(CelValueType.String);
        ((CelCall)CelFixtures.CompileMutate("now()").Root).ResultType.ShouldBe(CelValueType.Timestamp);
    }

    [Fact]
    public void A_parsed_call_is_unchecked_until_the_type_checker_sees_it() =>
        ((CelCall)CelParser.Parse("now()")).ResultType.ShouldBe(CelValueType.Null);
}
```

- [ ] **Step 2: Run — expect build failure** (`CelCall` has no list constructor / `ResultType`)

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelCallShapeTests'`
Expected: CS1503/CS0117 compile errors.

- [ ] **Step 3: Implement**

In `CelTree.cs`, the `Children` arm becomes `CelCall call => call.Arguments,` and the record becomes (keep the existing `<remarks>` paragraphs about why the node is internal and about the `lowerAscii` call shape; replace the summary and the `<param>`s):

```csharp
/// <summary>
/// A function call: one of the two legacy calls with their own grammar (<c>lowerAscii(field)</c>, <c>now()</c>) or a
/// function from the <c>CelFunctionCatalog</c>. Legal only where the type checker's profile gates allow it, and never
/// rendered to SQL in this slice.
/// </summary>
/// <param name="Name">The function's CEL spelling.</param>
/// <param name="Arguments">Every argument, in source order; empty for a nullary call such as <see cref="Now"/>.</param>
internal sealed record CelCall(string Name, IReadOnlyList<CelNode> Arguments) : CelNode
{
    /// <summary>The ASCII-only lower-case fold, <c>lowerAscii(field)</c>: folds <c>A</c>–<c>Z</c> and nothing else.</summary>
    public const string LowerAscii = "lowerAscii";

    /// <summary>
    /// The write's own instant, <c>now()</c> — not a clock read. It resolves to the <see cref="DateTimeOffset"/> the
    /// caller bound for the whole write (the same one the audit stamp uses), so two evaluations inside one write can
    /// never disagree.
    /// </summary>
    public const string Now = "now";

    /// <summary>
    /// The call's result type as the type checker resolved it; <see cref="CelValueType.Null"/> on a tree straight
    /// from the parser, which knows names but not types.
    /// </summary>
    public CelValueType ResultType { get; init; } = CelValueType.Null;
}
```

In `CelParser.cs`: `return new CelCall(CelCall.LowerAscii, [field]);` and `return new CelCall(CelCall.Now, []);`.

In `CelTypeChecker.cs`, inside `CheckCall` replace the `return call switch { … }` with:

```csharp
            return call switch
            {
                { Name: CelCall.LowerAscii, Arguments: [var argument] } => CheckLowerAsciiCall(call, argument, profileBad, position),
                { Name: CelCall.Now, Arguments: [] } =>
                    (call with { ResultType = CelValueType.Timestamp }, CelValueType.Timestamp, profileBad, position),
                _ => UnrecognizedNode(call),
            };
```

and the last line of `CheckLowerAsciiCall` with:

```csharp
            return (call with { Arguments = [checkedArgument], ResultType = CelValueType.String }, CelValueType.String, profileBad || argumentBad, position);
```

In `CelInterpreter.cs`:

```csharp
    private static object? EvaluateCall(CelCall call, in EvalState state) => call switch
    {
        { Name: CelCall.LowerAscii, Arguments: [var argument] } => LowerAscii(Evaluate(argument, state)),
        { Name: CelCall.Now, Arguments: [] } => state.Now,
        _ => null,
    };
```

Tests: `CelTreeChildrenTests.cs:49` → `new CelCall(CelCall.LowerAscii, [_title])`; `:78` → `new CelCall(CelCall.Now, [])`; `SqlPredicateRendererTests.cs:472-473` → `new(CelCall.LowerAscii, [new CelFieldRef("title", CelValueType.String, CelRecordState.Current)]);`.

- [ ] **Step 4: Run — expect PASS, corpus unchanged**

Run: `dotnet build` (0 warnings, 0 errors), then `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelCallShapeTests' --filter-class '*CelTreeChildrenTests' --filter-class '*CelMutateFunctionTests' --filter-class '*SqlPredicateRendererTests' --filter-class '*CelAcceptanceCorpusTests'`
Expected: all pass.

- [ ] **Step 5: Normalise the new file, ring0, commit**

```bash
python3 -c "p='test/MMLib.Alvo.Tests/Expressions/CelCallShapeTests.cs';b=open(p,'rb').read().replace(b'\r\n',b'\n').replace(b'\n',b'\r\n');b=b if b.startswith(b'\xef\xbb\xbf') else b'\xef\xbb\xbf'+b;open(p,'wb').write(b)"
scripts/test-ring0
git add src/MMLib.Alvo/Expressions/Internal/CelTree.cs src/MMLib.Alvo/Expressions/Internal/CelParser.cs src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs src/MMLib.Alvo/Expressions/Internal/CelInterpreter.cs test/MMLib.Alvo.Tests/Expressions/CelCallShapeTests.cs test/MMLib.Alvo.Tests/Expressions/CelTreeChildrenTests.cs test/MMLib.Alvo.Tests/Expressions/SqlPredicateRendererTests.cs
git commit -m "refactor(cel): a call node carries every argument and its checked result type" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 2: the function catalog (built-ins listed, legacy path untouched)

**Files:**
- Create: `src/MMLib.Alvo/Expressions/Internal/CelFunction.cs`
- Create: `src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.cs`
- Create: `src/MMLib.Alvo/Expressions/Internal/CelFunctionCatalog.cs`
- Test: `test/MMLib.Alvo.Tests/Expressions/CelFunctionCatalogTests.cs` (create)

**Interfaces:**
- Produces (all `internal`, namespace `MMLib.Alvo.Expressions.Internal`):
  - `record CelFunctionArgument(string Name, CelValueType Type, bool Nullable, System.Type ClrType)`
  - `record CelFunction(string Name, IReadOnlyList<CelFunctionArgument> Parameters, CelValueType ResultType, bool ResultNullable, string Summary, bool IsHost, IReadOnlySet<CelProfile> Profiles, Func<object?[], object?>? Body)` with `bool IsLegacy` and `string Signature()`
  - `static class CelBuiltInFunctions { IReadOnlySet<CelProfile> MutateOnly; IReadOnlySet<CelProfile> ConditionAndMutate; IReadOnlyList<CelFunction> All; CelFunctionArgument Parameter(string, CelValueType); System.Type ClrTypeOf(CelValueType); }`
  - `sealed class CelFunctionCatalog { CelFunctionCatalog(IEnumerable<CelFunction>); static CelFunctionCatalog BuiltIns; IReadOnlyList<CelFunction> Functions; IReadOnlyList<string> Names; bool Contains(string); IReadOnlyList<CelFunction> Overloads(string); CelFunctionCatalog With(IEnumerable<CelFunction>); }`

- [ ] **Step 1: Write the failing test**

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>The catalog: name to overloads, built-ins first-class, host functions added once.</summary>
public sealed class CelFunctionCatalogTests
{
    private static CelFunction Host(string name) => new(
        name, [CelBuiltInFunctions.Parameter("s", CelValueType.String)], CelValueType.String, ResultNullable: true,
        "A host function.", IsHost: true, CelBuiltInFunctions.ConditionAndMutate, arguments => arguments[0]);

    [Fact]
    public void The_built_ins_are_the_two_legacy_calls_with_their_own_grammar()
    {
        var catalog = CelFunctionCatalog.BuiltIns;

        catalog.Names.ShouldBe(["lowerAscii", "now"]);
        catalog.Functions.ShouldAllBe(function => function.IsLegacy && !function.IsHost);
        catalog.Functions.ShouldAllBe(function => function.Profiles.SetEquals(new[] { CelProfile.Mutate }));
    }

    [Fact]
    public void An_unknown_name_has_no_overloads()
    {
        CelFunctionCatalog.BuiltIns.Contains("normalizePhone").ShouldBeFalse();
        CelFunctionCatalog.BuiltIns.Overloads("normalizePhone").ShouldBeEmpty();
    }

    [Fact]
    public void A_host_function_joins_the_built_ins_in_name_order()
    {
        var catalog = CelFunctionCatalog.BuiltIns.With([Host("normalizePhone")]);

        catalog.Names.ShouldBe(["lowerAscii", "normalizePhone", "now"]);
        catalog.Overloads("normalizePhone").ShouldHaveSingleItem().IsHost.ShouldBeTrue();
    }

    [Fact]
    public void A_host_function_may_not_reuse_a_catalogued_name() =>
        Should.Throw<InvalidOperationException>(() => CelFunctionCatalog.BuiltIns.With([Host("now")]))
            .Message.ShouldContain("'now'");

    [Fact]
    public void Two_host_functions_may_not_share_a_name() =>
        Should.Throw<InvalidOperationException>(() => CelFunctionCatalog.BuiltIns.With([Host("vat"), Host("vat")]))
            .Message.ShouldContain("'vat'");

    [Fact]
    public void A_signature_names_every_parameter_its_type_and_nullability()
    {
        CelFunctionCatalog.BuiltIns.Overloads("lowerAscii").Single().Signature().ShouldBe("lowerAscii(value: String) -> String?");
        CelFunctionCatalog.BuiltIns.Overloads("now").Single().Signature().ShouldBe("now() -> Timestamp");
    }
}
```

- [ ] **Step 2: Run — expect build failure** (types do not exist)

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelFunctionCatalogTests'`
Expected: CS0246 `CelFunctionCatalog` not found.

- [ ] **Step 3: Implement**

`src/MMLib.Alvo/Expressions/Internal/CelFunction.cs`:

```csharp
namespace MMLib.Alvo.Expressions.Internal;

/// <summary>One parameter of a catalogued function overload: what the type checker matches and the marshaller converts to.</summary>
/// <param name="Name">The parameter's name, as discovery shows it.</param>
/// <param name="Type">The CEL type an argument must have; an Int argument also binds a Decimal parameter.</param>
/// <param name="Nullable">
/// Whether the parameter receives a null argument. When it does not, a null argument makes the whole call null and the
/// body is never invoked — CEL's strictness, SQL's <c>NULL</c> in, <c>NULL</c> out.
/// </param>
/// <param name="ClrType">The CLR type the body receives, never a <see cref="Nullable{T}"/>; nullability is <paramref name="Nullable"/>.</param>
internal sealed record CelFunctionArgument(string Name, CelValueType Type, bool Nullable, System.Type ClrType);

/// <summary>One overload of a function the CEL compiler knows — a built-in or a host registration.</summary>
/// <remarks>
/// Overloads of one name share <see cref="Profiles"/> and provenance (<see cref="CelFunctionCatalog"/> enforces it); a
/// host function has exactly one overload. The <see cref="Body"/> receives arguments already converted to each
/// parameter's <see cref="CelFunctionArgument.ClrType"/>.
/// </remarks>
/// <param name="Name">The function's CEL spelling.</param>
/// <param name="Parameters">The typed parameters, in call order.</param>
/// <param name="ResultType">The CEL type of the value the call yields.</param>
/// <param name="ResultNullable">Whether the call may yield null even when every argument is present.</param>
/// <param name="Summary">One sentence for discovery; empty when the host gave none.</param>
/// <param name="IsHost">Whether the embedding host registered it (as opposed to a built-in).</param>
/// <param name="Profiles">The profiles the function may appear in, inside the type checker's ceiling.</param>
/// <param name="Body">
/// The implementation, or <see langword="null"/> for <c>lowerAscii</c> and <c>now</c>, which keep their own grammar and
/// are evaluated by name.
/// </param>
internal sealed record CelFunction(
    string Name,
    IReadOnlyList<CelFunctionArgument> Parameters,
    CelValueType ResultType,
    bool ResultNullable,
    string Summary,
    bool IsHost,
    IReadOnlySet<CelProfile> Profiles,
    Func<object?[], object?>? Body)
{
    /// <summary>Gets a value indicating whether this is one of the two calls with their own grammar, evaluated by name.</summary>
    public bool IsLegacy => Body is null;

    /// <summary>The overload as one line, e.g. <c>replace(text: String, search: String, replacement: String) -> String</c>.</summary>
    /// <returns>The signature text refusals and fixes quote.</returns>
    public string Signature() =>
        $"{Name}({string.Join(", ", Parameters.Select(DescribeParameter))}) -> {ResultType}{(ResultNullable ? "?" : string.Empty)}";

    private static string DescribeParameter(CelFunctionArgument parameter) =>
        $"{parameter.Name}: {parameter.Type}{(parameter.Nullable ? "?" : string.Empty)}";
}
```

`src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.cs`:

```csharp
namespace MMLib.Alvo.Expressions.Internal;

/// <summary>The functions every Alvo build knows, whatever the host registers.</summary>
/// <remarks>
/// Every member is an expression-bodied getter rather than a <c>static readonly</c> field: a static initializer runs
/// once per test-host process, so a mutant inside one is invisible to every test (the #244 comment), and these sets
/// are profile gates in the security core.
/// </remarks>
internal static class CelBuiltInFunctions
{
    /// <summary>Gets the profile set of the two legacy calls.</summary>
    internal static IReadOnlySet<CelProfile> MutateOnly => new HashSet<CelProfile> { CelProfile.Mutate };

    /// <summary>Gets the profile set of every in-process function in this slice — built-ins and host functions alike.</summary>
    internal static IReadOnlySet<CelProfile> ConditionAndMutate =>
        new HashSet<CelProfile> { CelProfile.Condition, CelProfile.Mutate };

    /// <summary>Gets every built-in overload.</summary>
    internal static IReadOnlyList<CelFunction> All => [LowerAscii, Now];

    private static CelFunction LowerAscii => new(
        CelCall.LowerAscii, [Parameter("value", CelValueType.String)], CelValueType.String, ResultNullable: true,
        "Folds A-Z to a-z in a field's text and changes nothing else; takes a field, never an expression.",
        IsHost: false, MutateOnly, Body: null);

    private static CelFunction Now => new(
        CelCall.Now, [], CelValueType.Timestamp, ResultNullable: false,
        "The instant this write is stamped with — the same one its audit columns get, never a clock read.",
        IsHost: false, MutateOnly, Body: null);

    /// <summary>A non-nullable parameter of a CEL type, with the CLR type a body receives for it.</summary>
    /// <param name="name">The parameter's name.</param>
    /// <param name="type">Its CEL type.</param>
    /// <returns>The parameter.</returns>
    internal static CelFunctionArgument Parameter(string name, CelValueType type) => new(name, type, Nullable: false, ClrTypeOf(type));

    /// <summary>The CLR type a function body receives for a CEL type.</summary>
    /// <param name="type">A scalar CEL type.</param>
    /// <returns>The CLR type.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> is not a type a function parameter can have.</exception>
    internal static System.Type ClrTypeOf(CelValueType type) => type switch
    {
        CelValueType.String => typeof(string),
        CelValueType.Int => typeof(long),
        CelValueType.Decimal => typeof(decimal),
        CelValueType.Bool => typeof(bool),
        CelValueType.Timestamp => typeof(DateTimeOffset),
        CelValueType.Uuid => typeof(Guid),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No CEL function parameter has this type."),
    };
}
```

`src/MMLib.Alvo/Expressions/Internal/CelFunctionCatalog.cs`:

```csharp
namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// Every function the compiler knows, by name: the built-ins, plus whatever the embedding host registered. The parser
/// asks it whether a name is a function, the type checker which overload a call binds, and the Management read what
/// to list.
/// </summary>
/// <remarks>
/// <b>Internal on purpose</b> (spec X1): nothing outside the core reads it — the dashboard and the assistant reach the
/// list through <c>IAlvoManagement</c>. Publishing it is additive later; un-publishing would not be.
/// </remarks>
internal sealed class CelFunctionCatalog
{
    private readonly Dictionary<string, IReadOnlyList<CelFunction>> _byName;

    /// <summary>Initializes a new instance of the <see cref="CelFunctionCatalog"/> class.</summary>
    /// <param name="functions">Every overload; overloads of one name keep the order given.</param>
    /// <exception cref="InvalidOperationException">One name mixes provenance or profiles, or a host name repeats.</exception>
    internal CelFunctionCatalog(IEnumerable<CelFunction> functions)
    {
        ArgumentNullException.ThrowIfNull(functions);
        Functions = [.. functions.OrderBy(function => function.Name, StringComparer.Ordinal)];
        _byName = Functions
            .GroupBy(function => function.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<CelFunction>)[.. group], StringComparer.Ordinal);
        EnsureOneFunctionPerName();
    }

    /// <summary>Gets a catalog of the built-ins only — what a host with no registrations, the standalone image and the CLI know.</summary>
    internal static CelFunctionCatalog BuiltIns => new(CelBuiltInFunctions.All);

    /// <summary>Gets every overload, ordered by name (ordinal), overloads of one name in declaration order.</summary>
    internal IReadOnlyList<CelFunction> Functions { get; }

    /// <summary>Gets every distinct name, ordinal order.</summary>
    internal IReadOnlyList<string> Names => [.. _byName.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Whether <paramref name="name"/> is a function this catalog knows.</summary>
    /// <param name="name">A name as written in source.</param>
    /// <returns><see langword="true"/> when it has at least one overload.</returns>
    internal bool Contains(string name) => _byName.ContainsKey(name);

    /// <summary>The overloads of <paramref name="name"/>, or none.</summary>
    /// <param name="name">A name as written in source.</param>
    /// <returns>The overloads, in declaration order.</returns>
    internal IReadOnlyList<CelFunction> Overloads(string name) => _byName.TryGetValue(name, out var overloads) ? overloads : [];

    /// <summary>This catalog plus <paramref name="functions"/>, none of which may reuse a name already here.</summary>
    /// <param name="functions">The functions to add.</param>
    /// <returns>A new catalog.</returns>
    /// <exception cref="InvalidOperationException">A name is already in the catalog.</exception>
    internal CelFunctionCatalog With(IEnumerable<CelFunction> functions)
    {
        ArgumentNullException.ThrowIfNull(functions);
        var added = functions.ToList();
        if (added.FirstOrDefault(function => Contains(function.Name)) is { } clash)
        {
            throw new InvalidOperationException(
                $"A CEL function named '{clash.Name}' is already in the catalog; every function has its own name.");
        }

        return new CelFunctionCatalog([.. Functions, .. added]);
    }

    private void EnsureOneFunctionPerName()
    {
        foreach (var (name, overloads) in _byName)
        {
            var first = overloads[0];
            var mixed = overloads.Any(o => o.IsHost != first.IsHost || !o.Profiles.SetEquals(first.Profiles));
            if (mixed || (first.IsHost && overloads.Count > 1))
            {
                throw new InvalidOperationException(
                    $"The CEL function '{name}' is declared more than once with different provenance or profiles, or "
                    + "a host function is declared twice; a name is one function.");
            }
        }
    }
}
```

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelFunctionCatalogTests' --filter-class '*CelAcceptanceCorpusTests'`
Expected: all pass.

- [ ] **Step 5: Normalise the four new files, ring0, commit**

```bash
for p in src/MMLib.Alvo/Expressions/Internal/CelFunction.cs src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.cs src/MMLib.Alvo/Expressions/Internal/CelFunctionCatalog.cs test/MMLib.Alvo.Tests/Expressions/CelFunctionCatalogTests.cs; do python3 -c "p='$p';b=open(p,'rb').read().replace(b'\r\n',b'\n').replace(b'\n',b'\r\n');b=b if b.startswith(b'\xef\xbb\xbf') else b'\xef\xbb\xbf'+b;open(p,'wb').write(b)"; done
scripts/test-ring0
git add src/MMLib.Alvo/Expressions/Internal/CelFunction.cs src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.cs src/MMLib.Alvo/Expressions/Internal/CelFunctionCatalog.cs test/MMLib.Alvo.Tests/Expressions/CelFunctionCatalogTests.cs
git commit -m "feat(cel): an internal function catalog that lists the two built-in calls" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 3: the parser takes the catalog (generic N-ary calls)

**Files:**
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelParser.cs` (whole file — `Parse`, the nested parser's constructor, `EnterNestedProduction`'s fix, `ParseCall`, `UnrecognizedFunction`, `ParseFieldPath`; add `ParseCatalogCall`, `ParseArguments`, fix helpers)
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelCompiler.cs` (constructors; make the compile chain instance)
- Modify: `test/MMLib.Alvo.Tests/Expressions/CelMutateFunctionTests.cs:148` (fix text for `upper(…)`)
- Create: `test/MMLib.Alvo.Tests/Expressions/TestCelFunctions.cs`
- Test: `test/MMLib.Alvo.Tests/Expressions/CelCatalogCallParsingTests.cs` (create)

**Interfaces:**
- Consumes: `CelFunctionCatalog` (Task 2), `NameSuggestion.Closest(string, IEnumerable<string>)` (`src/MMLib.Alvo/Internal/NameSuggestion.cs:25`).
- Produces:
  - `CelParser.Parse(string source, CelFunctionCatalog catalog)`; `CelParser.Parse(string source)` = built-ins only.
  - `CelCompiler()` (built-ins) and `CelCompiler(CelFunctionCatalog catalog)`.
  - Test helper `TestCelFunctions` (`Items`, `Host`, `Parameter`, `Echo`, `Pair`, `Half`, `IsBlank`, `With`, `Compiler`, `Compile`) — reused by Tasks 4–6.

- [ ] **Step 1: Create the test helper**

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>Host-like functions and an entity for the function tests, built without the registration API.</summary>
internal static class TestCelFunctions
{
    /// <summary>Gets an entity with a field of every type a function test needs; two are named like built-ins.</summary>
    internal static EntitySchema Items { get; } = new()
    {
        Name = "items",
        Fields =
        [
            new FieldSchema { Name = "name", Type = FieldType.String, MaxLength = 200, Nullable = true },
            new FieldSchema { Name = "qty", Type = FieldType.Integer, Nullable = true },
            new FieldSchema { Name = "price", Type = FieldType.Decimal, Precision = 18, Scale = 4, Nullable = true },
            new FieldSchema { Name = "due", Type = FieldType.Date, Nullable = true },
            new FieldSchema { Name = "ref_id", Type = FieldType.Uuid, Nullable = true },
            new FieldSchema { Name = "round", Type = FieldType.String, MaxLength = 20, Nullable = true },
            new FieldSchema { Name = "size", Type = FieldType.String, MaxLength = 20, Nullable = true },
        ],
    };

    /// <summary>Gets <c>echo(s: String) -> String</c>, which answers its argument.</summary>
    internal static CelFunction Echo => Host("echo", CelValueType.String, arguments => arguments[0], Parameter("s", CelValueType.String));

    /// <summary>Gets <c>pair(a: String, b: String) -> String</c>.</summary>
    internal static CelFunction Pair => Host(
        "pair", CelValueType.String, arguments => $"{arguments[0]}|{arguments[1]}",
        Parameter("a", CelValueType.String), Parameter("b", CelValueType.String));

    /// <summary>Gets <c>half(x: Decimal) -> Decimal</c>.</summary>
    internal static CelFunction Half => Host("half", CelValueType.Decimal, arguments => (decimal)arguments[0]! / 2, Parameter("x", CelValueType.Decimal));

    /// <summary>Gets <c>isBlank(s: String?) -> Bool</c>, the one parameter here that receives null.</summary>
    internal static CelFunction IsBlank => Host(
        "isBlank", CelValueType.Bool, arguments => string.IsNullOrWhiteSpace((string?)arguments[0]),
        Parameter("s", CelValueType.String, nullable: true));

    /// <summary>A host function with the Condition + Mutate profiles.</summary>
    internal static CelFunction Host(string name, CelValueType result, Func<object?[], object?> body, params CelFunctionArgument[] parameters) =>
        new(name, parameters, result, ResultNullable: true, $"Test function {name}.", IsHost: true, CelBuiltInFunctions.ConditionAndMutate, body);

    /// <summary>A parameter of a CEL type with the CLR type a body receives.</summary>
    internal static CelFunctionArgument Parameter(string name, CelValueType type, bool nullable = false) =>
        new(name, type, nullable, CelBuiltInFunctions.ClrTypeOf(type));

    /// <summary>The built-ins plus <paramref name="functions"/>.</summary>
    internal static CelFunctionCatalog With(params CelFunction[] functions) => CelFunctionCatalog.BuiltIns.With(functions);

    /// <summary>A compiler over the built-ins plus <paramref name="functions"/>.</summary>
    internal static CelCompiler Compiler(params CelFunction[] functions) => new(With(functions));

    /// <summary>Compiles against <see cref="Items"/>, or throws with every refusal.</summary>
    internal static CompiledExpression Compile(string source, CelProfile profile, params CelFunction[] functions)
    {
        var result = Compiler(functions).Compile(source, profile, Items);
        return result.IsSuccess
            ? result.Expression!
            : throw new InvalidOperationException(
                $"'{source}' did not compile as {profile}: {string.Join("; ", result.Errors.Select(error => error.Message))}");
    }
}
```

- [ ] **Step 2: Write the failing parser tests**

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>A catalogued name parses as an N-ary call; anything else is the same syntax-time refusal as before.</summary>
public sealed class CelCatalogCallParsingTests
{
    private static readonly CelFunctionCatalog _catalog = TestCelFunctions.With(
        TestCelFunctions.Echo,
        TestCelFunctions.Pair,
        TestCelFunctions.Host("normalizePhone", CelValueType.String, arguments => arguments[0], TestCelFunctions.Parameter("phone", CelValueType.String)));

    [Fact]
    public void A_catalogued_function_parses_with_every_argument_in_order()
    {
        var call = CelParser.Parse("pair(title, 'x')", _catalog).ShouldBeOfType<CelCall>();

        call.Name.ShouldBe("pair");
        call.Arguments.Count.ShouldBe(2);
        call.Arguments[0].ShouldBeOfType<CelFieldRef>().FieldName.ShouldBe("title");
        call.Arguments[1].ShouldBeOfType<CelLiteral>().Value.ShouldBe("x");
    }

    [Theory]
    [InlineData("echo()", 0)]
    [InlineData("echo (title)", 1)]
    [InlineData("echo(new.title)", 1)]
    [InlineData("pair(echo(title), old.title)", 2)]
    [InlineData("echo(title == 'x')", 1)]
    public void An_argument_is_any_expression_and_arity_is_left_to_the_checker(string source, int arguments) =>
        CelParser.Parse(source, _catalog).ShouldBeOfType<CelCall>().Arguments.Count.ShouldBe(arguments);

    [Theory]
    [InlineData("echo(")]
    [InlineData("echo(title")]
    [InlineData("echo(title,")]
    [InlineData("echo(,)")]
    [InlineData("echo(title,)")]
    [InlineData("echo(title))")]
    public void An_unbalanced_or_empty_argument_list_is_a_syntax_error(string source) =>
        Should.Throw<CelSyntaxException>(() => CelParser.Parse(source, _catalog));

    [Fact]
    public void Each_call_level_counts_against_the_nesting_cap()
    {
        string Nested(int depth) => string.Concat(Enumerable.Repeat("echo(", depth)) + "title" + new string(')', depth);

        Should.NotThrow(() => CelParser.Parse(Nested(CelParser.MaxDepth), _catalog));
        Should.Throw<CelSyntaxException>(() => CelParser.Parse(Nested(CelParser.MaxDepth + 1), _catalog))
            .Message.ShouldStartWith($"CEL expression nests {CelParser.MaxDepth + 1} levels deep");
    }

    [Fact]
    public void A_source_of_calls_over_the_length_limit_is_refused_before_it_is_read()
    {
        var source = "pair(" + string.Join(", ", Enumerable.Repeat("title", 400)) + ")";

        source.Length.ShouldBeGreaterThan(CelParser.MaxSourceLength);
        Should.Throw<CelSyntaxException>(() => CelParser.Parse(source, _catalog)).Message.ShouldContain("exceeding the maximum of 2000");
    }

    [Fact]
    public void An_unknown_function_keeps_its_message_and_names_the_closest_and_every_known_function()
    {
        var refused = Should.Throw<CelSyntaxException>(() => CelParser.Parse("normalisePhone(title)", _catalog));

        refused.Message.ShouldBe("'normalisePhone' is not a recognized function.");
        refused.FixSuggestion.ShouldNotBeNull().ShouldContain("Did you mean 'normalizePhone'?");
        refused.FixSuggestion.ShouldContain("Known functions: echo, lowerAscii, normalizePhone, now, pair.");
        refused.FixSuggestion.ShouldContain("AddCelFunction");
    }

    [Fact]
    public void The_catalog_free_overload_knows_the_built_ins_only() =>
        Should.Throw<CelSyntaxException>(() => CelParser.Parse("echo(title)")).Message.ShouldBe("'echo' is not a recognized function.");

    [Theory]
    [InlineData("all(f, f > 0)")]
    [InlineData("exists(f)")]
    [InlineData("exists_one(f)")]
    [InlineData("map(f)")]
    [InlineData("filter(f)")]
    public void A_comprehension_macro_keeps_the_hook_fix(string source) =>
        Should.Throw<CelSyntaxException>(() => CelParser.Parse(source, _catalog)).FixSuggestion.ShouldNotBeNull().ShouldContain("hooks.beforeUpdate");

    [Fact]
    public void Lower_keeps_the_lower_ascii_fix() =>
        Should.Throw<CelSyntaxException>(() => CelParser.Parse("lower(title)", _catalog)).FixSuggestion.ShouldNotBeNull().ShouldContain("lowerAscii(field)");

    [Theory]
    [InlineData("title.echo()")]
    [InlineData("math.echo(title)")]
    public void A_receiver_or_namespaced_spelling_of_a_function_is_told_the_call_shape(string source)
    {
        var refused = Should.Throw<CelSyntaxException>(() => CelParser.Parse(source, _catalog));

        refused.Message.ShouldStartWith("Alvo has no nested field access");
        refused.FixSuggestion.ShouldNotBeNull().ShouldContain("echo(x)");
    }

    [Fact]
    public void A_name_without_parentheses_is_a_field_even_when_a_function_has_that_name() =>
        CelParser.Parse("echo", _catalog).ShouldBeOfType<CelFieldRef>().FieldName.ShouldBe("echo");

    [Theory]
    [InlineData("lowerAscii('ABC')")]
    [InlineData("lowerAscii(title, title)")]
    [InlineData("now(title)")]
    public void The_legacy_calls_keep_their_narrow_grammar(string source) =>
        Should.Throw<CelSyntaxException>(() => CelParser.Parse(source, _catalog));

    [Fact]
    public void A_unicode_name_is_simply_not_a_known_function() =>
        Should.Throw<CelSyntaxException>(() => CelParser.Parse("normalizéPhone(title)", _catalog))
            .Message.ShouldBe("'normalizéPhone' is not a recognized function.");
}
```

- [ ] **Step 3: Run — expect build failure** (no `Parse(string, CelFunctionCatalog)`, no `CelCompiler(CelFunctionCatalog)`)

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelCatalogCallParsingTests'`
Expected: CS1501 / CS1729.

- [ ] **Step 4: Implement the parser**

Add `using MMLib.Alvo.Internal;`. Replace `Parse` with:

```csharp
    /// <summary>Parses CEL source into an untyped AST, knowing the built-in functions only.</summary>
    /// <param name="source">The CEL expression source.</param>
    /// <exception cref="CelSyntaxException">The source is too long, nests too deeply, or violates the grammar.</exception>
    public static CelNode Parse(string source) => Parse(source, CelFunctionCatalog.BuiltIns);

    /// <summary>Parses CEL source into an untyped AST; a name <paramref name="catalog"/> knows parses as a call.</summary>
    /// <param name="source">The CEL expression source.</param>
    /// <param name="catalog">The functions this compilation knows.</param>
    /// <exception cref="CelSyntaxException">The source is too long, nests too deeply, or violates the grammar.</exception>
    public static CelNode Parse(string source, CelFunctionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(catalog);

        if (source.Length > MaxSourceLength)
        {
            throw new CelSyntaxException(
                $"CEL expression is {source.Length} characters long, exceeding the maximum of {MaxSourceLength}.",
                MaxSourceLength,
                $"Split the condition across multiple rules/hooks, or shorten it below {MaxSourceLength} characters.");
        }

        var tokens = CelLexer.Tokenize(source);
        return new RecursiveDescentParser(tokens, catalog).ParseProgram();
    }
```

`private sealed class RecursiveDescentParser(IReadOnlyList<CelToken> tokens, CelFunctionCatalog catalog)`. In `MaxDepth`'s summary add "each level of a function call's argument list" to the counted productions, and in `EnterNestedProduction` change only the fix to `"Simplify the expression — reduce parenthesised grouping, nested function calls, ternary chaining, or repeated negation, or split the condition across multiple rules/hooks."` (the message stays byte for byte).

Replace `ParseCall` and `UnrecognizedFunction` with:

```csharp
        /// <summary>
        /// The closed set of identifiers that may be followed by <c>(</c>: the three calls with their own grammar, then
        /// whatever the catalog knows. A <b>positive</b> list on purpose — a name missing from it is refused, so a
        /// function is unavailable until somebody catalogues it. Profiles are not decided here: the parser is
        /// profile-blind and the type checker gates every call.
        /// </summary>
        private CelNode ParseCall(CelToken identifierToken) => identifierToken.Text switch
        {
            "changed" => ParseChangedCall(),
            CelCall.LowerAscii => ParseLowerAsciiCall(),
            CelCall.Now => ParseNowCall(),
            var name when catalog.Contains(name) => ParseCatalogCall(identifierToken),
            _ => throw UnrecognizedFunction(identifierToken),
        };

        /// <summary>
        /// Parses <c>name(argument, …)</c> for a catalogued function. Each argument is a whole expression parsed as one
        /// nested level, so call nesting counts against <see cref="MaxDepth"/>; arity is the type checker's question.
        /// </summary>
        private CelCall ParseCatalogCall(CelToken nameToken)
        {
            Expect(CelTokenKind.LeftParen);
            IReadOnlyList<CelNode> arguments = Current.Kind == CelTokenKind.RightParen ? [] : ParseArguments();
            Expect(CelTokenKind.RightParen);
            return new CelCall(nameToken.Text, arguments);
        }

        private List<CelNode> ParseArguments()
        {
            var arguments = new List<CelNode> { ParseNestedGroup() };
            while (Match(CelTokenKind.Comma))
            {
                arguments.Add(ParseNestedGroup());
            }

            return arguments;
        }

        private CelSyntaxException UnrecognizedFunction(CelToken identifierToken) =>
            new(
                $"'{identifierToken.Text}' is not a recognized function.",
                identifierToken.Position,
                UnrecognizedFunctionFix(identifierToken.Text));

        private string UnrecognizedFunctionFix(string name) => name switch
        {
            "lower" => LowerAsciiSuggestion,
            "all" or "exists" or "exists_one" or "map" or "filter" => MacroNotSupportedSuggestion,
            _ => KnownFunctionsSuggestion(name),
        };

        /// <summary>The closest catalogued name, when one is within two edits, and every known name (review G5).</summary>
        private string KnownFunctionsSuggestion(string name)
        {
            var closest = NameSuggestion.Closest(name, catalog.Names);
            var lead = closest is null ? string.Empty : $"Did you mean '{closest}'? ";
            return lead + $"Known functions: {string.Join(", ", catalog.Names)}. A function a host registers with "
                + "AddCelFunction exists only in that host; the standalone image and the CLI know the built-in ones only.";
        }
```

In `ParseFieldPath`, replace `MacroNotSupportedSuggestion` in the first `throw` with `NestedAccessFix()` and add:

```csharp
        /// <summary>
        /// The fix for <c>x.trim()</c> or <c>math.abs(x)</c> — CEL's receiver and namespaced spellings (deviation F1):
        /// when the member after the dot is a catalogued function followed by <c>(</c>, say how Alvo spells the call.
        /// </summary>
        private string NestedAccessFix() =>
            ReceiverCallName() is { } function && catalog.Contains(function)
                ? $"Write {function}(...) with the value as an argument: Alvo calls a function as {function}(x), never "
                    + $"as x.{function}() or with a namespace such as math.{function}(x)."
                : MacroNotSupportedSuggestion;

        private string? ReceiverCallName() =>
            _index + 2 < tokens.Count
            && tokens[_index + 1].Kind == CelTokenKind.Identifier
            && tokens[_index + 2].Kind == CelTokenKind.LeftParen
                ? tokens[_index + 1].Text
                : null;
```

(`_index` points at the `.` when `ParseFieldPath` runs: `ResolveFieldReference` calls it only when `Current.Kind == CelTokenKind.Dot`.)

- [ ] **Step 5: Implement the compiler's catalog**

In `CelCompiler.cs` add a field and two constructors at the top of the class:

```csharp
    private readonly CelFunctionCatalog _catalog;

    /// <summary>Initializes a new instance of the <see cref="CelCompiler"/> class that knows the built-in functions only.</summary>
    public CelCompiler()
        : this(CelFunctionCatalog.BuiltIns)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CelCompiler"/> class.</summary>
    /// <param name="catalog">The functions every compilation knows: the built-ins plus the host's registrations.</param>
    public CelCompiler(CelFunctionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }
```

Remove `static` from `Compile(string, CelProfile, EntitySchema, bool)`, `TryParse`, `CheckAndAssemble`, `AppendResultTypeError`, `ValidateResultType`, `ValidatePredicateResult` and `QuotedPredicate` (they form the chain `Compile → … → QuotedPredicate → Compile`), and in `TryParse` call `CelParser.Parse(source, _catalog)`. Both constructors are `public` on an `internal` class; DI is wired explicitly in Task 7, so the container never has to choose between them.

- [ ] **Step 6: Update the one pinned fix** — `CelMutateFunctionTests.An_unlisted_function_is_still_refused_inside_the_mutate_profile`: the assertion becomes `refused.Errors[0].FixSuggestion.ShouldNotBeNull().ShouldContain("Known functions:");` and its summary says the unknown name now gets the known list (review G5) rather than the macro advice, which stays for the five macro names.

- [ ] **Step 7: Run — expect PASS, corpus unchanged**

Run: `dotnet build`, then `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelCatalogCallParsingTests' --filter-class '*CelParser*' --filter-class '*CelProfileTests' --filter-class '*CelMutateFunctionTests' --filter-class '*CelAcceptanceCorpusTests'`
Expected: all pass. If a `CelParserTests` fact pins the old depth fix text, update it to the new text in the same commit and say so in the message.

- [ ] **Step 8: Normalise, ring0, commit**

```bash
for p in test/MMLib.Alvo.Tests/Expressions/TestCelFunctions.cs test/MMLib.Alvo.Tests/Expressions/CelCatalogCallParsingTests.cs; do python3 -c "p='$p';b=open(p,'rb').read().replace(b'\r\n',b'\n').replace(b'\n',b'\r\n');b=b if b.startswith(b'\xef\xbb\xbf') else b'\xef\xbb\xbf'+b;open(p,'wb').write(b)"; done
scripts/test-ring0
git add src/MMLib.Alvo/Expressions/Internal/CelParser.cs src/MMLib.Alvo/Expressions/Internal/CelCompiler.cs test/MMLib.Alvo.Tests/Expressions/TestCelFunctions.cs test/MMLib.Alvo.Tests/Expressions/CelCatalogCallParsingTests.cs test/MMLib.Alvo.Tests/Expressions/CelMutateFunctionTests.cs
git commit -m "feat(cel): the parser reads a catalogued function as an N-ary call and names the known ones when it cannot" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 4: the signature-driven type checker

**Files:**
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelTree.cs` (add `Function` to `CelCall`)
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs` (`Check` overloads, `CelConstructKind.FunctionCall`, the table, `Visitor` constructor, `CheckCall` split, new private methods)
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelCompiler.cs` (`CheckAndAssemble` passes `_catalog`)
- Test: `test/MMLib.Alvo.Tests/Expressions/CelFunctionTypeCheckTests.cs` (create)

**Interfaces:**
- Consumes: `CelFunctionCatalog.Contains/Overloads`, `CelFunction.Signature()`, `CelFunction.Profiles`, `TestCelFunctions` (Task 3).
- Produces: `CelCall.Function` (`public CelFunction? Function { get; init; }`, the bound overload; `null` for `lowerAscii`/`now`); `CelTypeChecker.Check(CelNode, string, EntitySchema, CelProfile, CelFunctionCatalog)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>A catalogued call is resolved against its signatures and gated by its profiles, one error per problem.</summary>
public sealed class CelFunctionTypeCheckTests
{
    private static CelCompilationResult Compile(string source, CelProfile profile, params CelFunction[] functions) =>
        TestCelFunctions.Compiler(functions).Compile(source, profile, TestCelFunctions.Items);

    private static CelFunction Magnitude(CelValueType type) => new(
        "magnitude", [TestCelFunctions.Parameter("x", type)], type, ResultNullable: false, "A test overload.",
        IsHost: false, CelBuiltInFunctions.ConditionAndMutate, arguments => arguments[0]);

    [Fact]
    public void A_call_in_a_hook_condition_binds_its_overload_and_result_type()
    {
        var compiled = Compile("echo(name) == 'x'", CelProfile.Condition, TestCelFunctions.Echo);

        compiled.IsSuccess.ShouldBeTrue();
        var call = ((CelBinary)compiled.Expression!.Root).Left.ShouldBeOfType<CelCall>();
        call.Function.ShouldNotBeNull().Name.ShouldBe("echo");
        call.ResultType.ShouldBe(CelValueType.String);
    }

    [Fact]
    public void A_call_is_a_mutate_value_of_its_result_type()
    {
        var compiled = Compile("echo(new.name)", CelProfile.Mutate, TestCelFunctions.Echo);

        compiled.IsSuccess.ShouldBeTrue();
        compiled.Expression!.ResultType.ShouldBe(CelValueType.String);
    }

    [Theory]
    [InlineData(CelProfile.Rule, "echo(name) == 'x'", "Rule", "before-hook mutate")]
    [InlineData(CelProfile.Computed, "echo(name)", "Computed", "regular field")]
    [InlineData(CelProfile.Access, "echo('a') == 'a'", "Access", "@user.roles")]
    public void A_function_outside_its_profiles_is_refused_once_with_why_and_where(CelProfile profile, string source, string named, string fix)
    {
        var refused = Compile(source, profile, TestCelFunctions.Echo);

        var error = refused.Errors.ShouldHaveSingleItem();
        error.Message.ShouldStartWith($"'echo(...)' is not available in the {named} profile; it is available in Condition and Mutate.");
        error.FixSuggestion.ShouldNotBeNull().ShouldContain(fix);
    }

    [Fact]
    public void A_wrong_arity_names_the_signature()
    {
        var error = Compile("echo(name, name)", CelProfile.Mutate, TestCelFunctions.Echo).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe("'echo' takes 1 argument; this call passes 2.");
        error.FixSuggestion.ShouldBe("Call it as echo(s: String) -> String?.");
    }

    [Fact]
    public void A_wrong_argument_type_names_what_was_passed_and_what_is_accepted() =>
        Compile("echo(price)", CelProfile.Mutate, TestCelFunctions.Echo).Errors.ShouldHaveSingleItem()
            .Message.ShouldBe("'echo(...)' accepts no (Decimal); it accepts echo(s: String) -> String?.");

    [Fact]
    public void An_int_binds_a_decimal_parameter()
    {
        var compiled = Compile("half(qty)", CelProfile.Mutate, TestCelFunctions.Half);

        compiled.IsSuccess.ShouldBeTrue();
        compiled.Expression!.ResultType.ShouldBe(CelValueType.Decimal);
    }

    [Fact]
    public void An_exact_overload_wins_over_a_widened_one()
    {
        var overloads = new[] { Magnitude(CelValueType.Int), Magnitude(CelValueType.Decimal) };

        Compile("magnitude(qty)", CelProfile.Mutate, overloads).Expression!.ResultType.ShouldBe(CelValueType.Int);
        Compile("magnitude(price)", CelProfile.Mutate, overloads).Expression!.ResultType.ShouldBe(CelValueType.Decimal);
    }

    [Fact]
    public void A_null_literal_fits_only_a_parameter_that_receives_null()
    {
        Compile("echo(null)", CelProfile.Mutate, TestCelFunctions.Echo).IsSuccess.ShouldBeFalse();
        Compile("isBlank(null)", CelProfile.Condition, TestCelFunctions.IsBlank).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_bad_argument_is_reported_once_and_does_not_cascade() =>
        Compile("echo(nope)", CelProfile.Mutate, TestCelFunctions.Echo).Errors.ShouldHaveSingleItem()
            .Message.ShouldBe("'nope' is not a field of entity 'items'.");

    [Fact]
    public void A_wrong_profile_and_a_wrong_arity_are_two_independent_errors() =>
        Compile("echo(name, name) == 'x'", CelProfile.Rule, TestCelFunctions.Echo).Errors.Count.ShouldBe(2);

    [Fact]
    public void A_field_named_like_a_built_in_is_a_field_argument() =>
        Compile("echo(size) == round", CelProfile.Condition, TestCelFunctions.Echo).IsSuccess.ShouldBeTrue();

    [Fact]
    public void A_function_named_like_a_field_reads_the_field_as_its_argument()
    {
        var title = TestCelFunctions.Host("name", CelValueType.String, arguments => arguments[0], TestCelFunctions.Parameter("s", CelValueType.String));

        Compile("name(name) == name", CelProfile.Condition, title).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void The_legacy_calls_keep_their_mutate_only_refusal_text() =>
        Compile("now() == now()", CelProfile.Condition).Errors[0].Message
            .ShouldBe("'now(...)' is legal only in the Mutate profile (a before-hook mutate value).");
}
```

- [ ] **Step 2: Run — expect build failure** (`CelCall.Function` missing)

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelFunctionTypeCheckTests'`
Expected: CS1061.

- [ ] **Step 3: Implement**

In `CelTree.cs` add to `CelCall`:

```csharp
    /// <summary>
    /// The overload the type checker bound, which the interpreter invokes; <see langword="null"/> before checking and
    /// for <see cref="LowerAscii"/>/<see cref="Now"/>, which are evaluated by name. Binding it into the tree is what
    /// keeps the interpreter and <c>BeforeHookRunner</c> free of any catalog or container dependency.
    /// </summary>
    public CelFunction? Function { get; init; }
```

In `CelTypeChecker.cs`:

1. `Check` — keep the 4-argument signature delegating to a new 5-argument one:

```csharp
    public static (CelNode Root, CelValueType ResultType, int Position, IReadOnlyList<CelCompilationError> Errors) Check(
        CelNode root, string source, EntitySchema entity, CelProfile profile) =>
        Check(root, source, entity, profile, CelFunctionCatalog.BuiltIns);

    /// <summary>Checks a parsed tree against an entity's schema, a profile and the functions this compilation knows.</summary>
    /// <param name="root">The parsed, untyped tree.</param>
    /// <param name="source">The original CEL source, used only to locate positions.</param>
    /// <param name="entity">The entity to resolve row fields against.</param>
    /// <param name="profile">Which constructs are legal.</param>
    /// <param name="catalog">The functions a call may bind.</param>
    /// <returns>The rewritten tree, its result type, the result-type anchor position and every error.</returns>
    public static (CelNode Root, CelValueType ResultType, int Position, IReadOnlyList<CelCompilationError> Errors) Check(
        CelNode root, string source, EntitySchema entity, CelProfile profile, CelFunctionCatalog catalog)
    {
        var visitor = new Visitor(source, entity, profile, catalog);
        var (node, type, _, position) = visitor.CheckNode(root);
        return (node, type, position, visitor.Errors);
    }
```

(move the existing XML docs to the 4-argument one as `<inheritdoc cref="…"/>` or keep them; both overloads need docs).

2. Add `FunctionCall,` after `Call,` in `CelConstructKind` with a doc comment ("A call to a catalogued function; the row is the ceiling, each function's own profiles narrow it"), and `[CelConstructKind.FunctionCall] = _conditionAndMutate,` to `_allowedProfiles` after the `Call` row. Update the table's summary: Mutate now also holds `FunctionCall`, Condition holds `FunctionCall`.

3. `private sealed class Visitor(string source, EntitySchema entity, CelProfile profile, CelFunctionCatalog catalog)`.

4. Replace `CheckCall` with a dispatcher and rename the old body (keep its XML doc on the legacy one):

```csharp
        private (CelNode, CelValueType, bool, int) CheckCall(CelCall call) =>
            call.Name is CelCall.LowerAscii or CelCall.Now ? CheckLegacyCall(call) : CheckCatalogCall(call);
```

`CheckLegacyCall` is the Task 1 `CheckCall` body unchanged.

5. Add:

```csharp
        /// <summary>
        /// Checks a call to a catalogued function: the profile gate first (as the legacy calls do), then every argument,
        /// then overload resolution. A bad argument stops the call from adding a second, cascading error.
        /// </summary>
        private (CelNode, CelValueType, bool, int) CheckCatalogCall(CelCall call)
        {
            if (!catalog.Contains(call.Name))
            {
                return UnrecognizedNode(call);
            }

            var position = FindPosition(call.Name);
            var profileBad = CheckFunctionProfile(call.Name, position);
            var arguments = call.Arguments.Select(CheckNode).ToList();
            var rewritten = call with { Arguments = [.. arguments.Select(argument => argument.Node)] };

            return arguments.Any(argument => argument.HasError)
                ? (rewritten, CelValueType.Null, true, position)
                : Bind(rewritten, ResolveOverload(call.Name, arguments, position), profileBad, position);
        }

        private static (CelNode, CelValueType, bool, int) Bind(CelCall call, CelFunction? overload, bool profileBad, int position) =>
            overload is null
                ? (call, CelValueType.Null, true, position)
                : (call with { ResultType = overload.ResultType, Function = overload }, overload.ResultType, profileBad, position);

        /// <summary>
        /// The two deny-by-default gates: the <see cref="CelConstructKind.FunctionCall"/> row is the ceiling, and the
        /// function's own profiles narrow it. Overloads of one name share profiles, so the first one answers.
        /// </summary>
        private bool CheckFunctionProfile(string name, int position)
        {
            var function = catalog.Overloads(name)[0];
            if (IsAllowed(profile, CelConstructKind.FunctionCall) && function.Profiles.Contains(profile))
            {
                return false;
            }

            Errors.Add(new CelCompilationError(
                $"'{name}(...)' is not available in the {profile} profile; it is available in "
                + $"{string.Join(" and ", function.Profiles.Order())}. {FunctionProfileReason()}",
                FunctionProfileFix(),
                position));
            return true;
        }

        private string FunctionProfileReason() => profile switch
        {
            CelProfile.Rule => "A rule becomes a SQL WHERE clause, and this function runs only in-process; "
                + "authorization is never a filter applied after the query.",
            CelProfile.Computed => "A computed field is a column the database computes, and this function runs only in-process.",
            _ => "An access level is a predicate over the caller alone and calls no function.",
        };

        private string FunctionProfileFix() => profile switch
        {
            CelProfile.Rule => "Store the value in a field with a before-hook mutate (hooks.beforeCreate / beforeUpdate), "
                + "then compare that field here.",
            CelProfile.Computed => "Write the value with a before-hook mutate into a regular field instead of computing it.",
            _ => "Test the caller instead, e.g. 'admin' in @user.roles.",
        };

        /// <summary>
        /// Arity, then an exact type match, then a match that lets an Int stand for a Decimal (deviation F7); a null
        /// literal fits only a parameter that receives null. No match is one error naming every signature.
        /// </summary>
        private CelFunction? ResolveOverload(
            string name, List<(CelNode Node, CelValueType Type, bool HasError, int Position)> arguments, int position)
        {
            var overloads = catalog.Overloads(name);
            var types = arguments.Select(argument => argument.Type).ToList();
            var chosen = overloads.FirstOrDefault(o => Accepts(o, types, widen: false))
                ?? overloads.FirstOrDefault(o => Accepts(o, types, widen: true));
            if (chosen is null)
            {
                Errors.Add(SignatureMismatch(name, overloads, types, position));
            }

            return chosen;
        }

        private static bool Accepts(CelFunction overload, List<CelValueType> types, bool widen) =>
            overload.Parameters.Count == types.Count
            && overload.Parameters.Zip(types).All(pair => Fits(pair.First, pair.Second, widen));

        private static bool Fits(CelFunctionArgument parameter, CelValueType argument, bool widen) =>
            argument == parameter.Type
            || (argument == CelValueType.Null && parameter.Nullable)
            || (widen && argument == CelValueType.Int && parameter.Type == CelValueType.Decimal);

        private static CelCompilationError SignatureMismatch(
            string name, IReadOnlyList<CelFunction> overloads, List<CelValueType> types, int position)
        {
            var signatures = string.Join(" or ", overloads.Select(overload => overload.Signature()));
            if (overloads.All(overload => overload.Parameters.Count != types.Count))
            {
                return new CelCompilationError(
                    $"'{name}' takes {Arity(overloads)}; this call passes {types.Count}.", $"Call it as {signatures}.", position);
            }

            return new CelCompilationError(
                $"'{name}(...)' accepts no ({string.Join(", ", types)}); it accepts {signatures}.",
                "Pass values of those types: string, text and enum fields are String, date and datetime fields are "
                + "Timestamp, and an Int may stand where a Decimal is expected.",
                position);
        }

        private static string Arity(IReadOnlyList<CelFunction> overloads)
        {
            var counts = overloads.Select(overload => overload.Parameters.Count).Distinct().Order().ToList();
            var noun = counts is [1] ? "argument" : "arguments";
            return $"{string.Join(" or ", counts)} {noun}";
        }
```

Note `IsNeverNull`, `ValidateSqlOperandShape` and `DescribeOperand` get **no** call arm in C1 (spec X5): a call in Rule/Computed already failed its profile gate, so `CheckComparison` (`:591`) never reaches them.

6. In `CelCompiler.CheckAndAssemble` (instance since Task 3) call `CelTypeChecker.Check(authored.Parsed, authored.Source, authored.Entity, authored.Profile, _catalog)`.

- [ ] **Step 4: Run — expect PASS, corpus unchanged**

Run: `dotnet build`, then `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelFunctionTypeCheckTests' --filter-class '*CelTypeChecker*' --filter-class '*AccessProfileTests' --filter-class '*CelMutateFunctionTests' --filter-class '*CelAcceptanceCorpusTests'`
Expected: all pass.

- [ ] **Step 5: Normalise, ring0, commit**

```bash
python3 -c "p='test/MMLib.Alvo.Tests/Expressions/CelFunctionTypeCheckTests.cs';b=open(p,'rb').read().replace(b'\r\n',b'\n').replace(b'\n',b'\r\n');b=b if b.startswith(b'\xef\xbb\xbf') else b'\xef\xbb\xbf'+b;open(p,'wb').write(b)"
scripts/test-ring0
git add src/MMLib.Alvo/Expressions/Internal/CelTree.cs src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs src/MMLib.Alvo/Expressions/Internal/CelCompiler.cs test/MMLib.Alvo.Tests/Expressions/CelFunctionTypeCheckTests.cs
git commit -m "feat(cel): resolve a function call against its signatures and gate it by its profiles" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 5: invocation — marshaller, null policy, fail-closed failures

**Files:**
- Create: `src/MMLib.Alvo/Expressions/Internal/CelFunctionException.cs`
- Create: `src/MMLib.Alvo/Expressions/Internal/CelArgumentMarshaller.cs`
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelFunction.cs` (add `Invoke`)
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelInterpreter.cs` (`EvaluateCall`; `TryToDecimal`/`TryToDateTimeOffset` → `internal`; catch filters at `:112` and `:228`; remarks)
- Test: `test/MMLib.Alvo.Tests/Expressions/CelFunctionInvocationTests.cs` (create)

**Interfaces:**
- Consumes: `CelCall.Function` (Task 4), `TestCelFunctions` (Task 3).
- Produces:
  - `internal sealed class CelFunctionException : Exception` — `CelFunctionException(string functionName, bool isHost, Exception failure)`, `CelFunctionException(string functionName, string reason)`; properties `FunctionName`, `IsHost`, `Reason` (`string?`).
  - `internal static class CelArgumentMarshaller { object? Convert(object? value, System.Type target); object? Normalize(object? result); }`
  - `CelFunction.Invoke(IReadOnlyList<object?> arguments) -> object?`
  - `CelInterpreter.TryToDecimal(object, out decimal)` and `TryToDateTimeOffset(object, out DateTimeOffset)` now `internal static`.

- [ ] **Step 1: Write the failing tests**

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>A bound call: values marshalled, nulls propagated, failures never collapsed into a value.</summary>
public sealed class CelFunctionInvocationTests
{
    private static readonly DateTimeOffset _now = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static object? Mutate(string source, AlvoRecord row, params CelFunction[] functions) =>
        CelInterpreter.EvaluateMutation(TestCelFunctions.Compile(source, CelProfile.Mutate, functions), row, previous: null, _now);

    private static bool Condition(string source, AlvoRecord row, params CelFunction[] functions) =>
        CelInterpreter.EvaluatePredicate(TestCelFunctions.Compile(source, CelProfile.Condition, functions), row, previous: null, CelFixtures.Alice);

    private static CelFunction Counting(Action counted) => TestCelFunctions.Host(
        "count", CelValueType.String, arguments => { counted(); return arguments[0]; }, TestCelFunctions.Parameter("s", CelValueType.String));

    private static CelFunction Echoing(string name, CelValueType type) =>
        TestCelFunctions.Host(name, type, arguments => arguments[0], TestCelFunctions.Parameter("v", type));

    private static CelFunction Boom => TestCelFunctions.Host(
        "boom", CelValueType.String, _ => throw new FormatException("host secret"), TestCelFunctions.Parameter("s", CelValueType.String));

    [Fact]
    public void A_bound_call_invokes_the_function_with_the_argument_value() =>
        Mutate("echo(name)", CelFixtures.Row(("name", "Ada")), TestCelFunctions.Echo).ShouldBe("Ada");

    [Fact]
    public void A_null_argument_for_a_parameter_that_takes_none_is_null_without_invoking()
    {
        var calls = 0;

        Mutate("count(name)", CelFixtures.Row(("name", null)), Counting(() => calls++)).ShouldBeNull();
        calls.ShouldBe(0);
    }

    [Fact]
    public void A_nullable_parameter_receives_null() =>
        Condition("isBlank(name)", CelFixtures.Row(("name", null)), TestCelFunctions.IsBlank).ShouldBeTrue();

    [Fact]
    public void A_value_of_an_unexpected_clr_type_reads_as_null()
    {
        var calls = 0;

        Mutate("count(name)", CelFixtures.Row(("name", 5)), Counting(() => calls++)).ShouldBeNull();
        calls.ShouldBe(0);
    }

    [Fact]
    public void An_int_runtime_value_binds_a_decimal_parameter() =>
        Mutate("half(qty)", CelFixtures.Row(("qty", 3)), TestCelFunctions.Half).ShouldBe(1.5m);

    [Fact]
    public void A_date_column_value_binds_a_timestamp_parameter_as_midnight_utc() =>
        Mutate("stamp(due)", CelFixtures.Row(("due", new DateOnly(2031, 3, 4))), Echoing("stamp", CelValueType.Timestamp))
            .ShouldBe(new DateTimeOffset(2031, 3, 4, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_guid_text_binds_a_uuid_parameter() =>
        Mutate("id(ref_id)", CelFixtures.Row(("ref_id", "6f9619ff-8b86-d011-b42d-00c04fc964ff")), Echoing("id", CelValueType.Uuid))
            .ShouldBe(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"));

    [Fact]
    public void An_int_result_is_returned_as_a_cel_int()
    {
        var width = TestCelFunctions.Host("width", CelValueType.Int, _ => 5, TestCelFunctions.Parameter("s", CelValueType.String));

        Mutate("width(name)", CelFixtures.Row(("name", "x")), width).ShouldBeOfType<long>().ShouldBe(5L);
    }

    [Fact]
    public void A_throwing_function_in_a_mutate_escapes_as_a_function_failure()
    {
        var failure = Should.Throw<CelFunctionException>(() => Mutate("boom(name)", CelFixtures.Row(("name", "x")), Boom));

        failure.FunctionName.ShouldBe("boom");
        failure.IsHost.ShouldBeTrue();
        failure.InnerException.ShouldBeOfType<FormatException>();
        failure.Message.ShouldNotContain("host secret");
    }

    [Fact]
    public void A_throwing_function_in_a_condition_escapes_instead_of_reading_as_false() =>
        Should.Throw<CelFunctionException>(() => Condition("boom(name) == 'x'", CelFixtures.Row(("name", "x")), Boom));

    [Fact]
    public void A_failure_that_carries_a_reason_passes_through_unchanged()
    {
        var capped = TestCelFunctions.Host(
            "capped", CelValueType.String, _ => throw new CelFunctionException("capped", "too long"), TestCelFunctions.Parameter("s", CelValueType.String));

        Should.Throw<CelFunctionException>(() => Mutate("capped(name)", CelFixtures.Row(("name", "x")), capped)).Reason.ShouldBe("too long");
    }

    [Fact]
    public void A_function_is_invoked_exactly_once_per_evaluation()
    {
        var calls = 0;

        Condition("count(name) == 'x'", CelFixtures.Row(("name", "x")), Counting(() => calls++)).ShouldBeTrue();
        calls.ShouldBe(1);
    }

    [Fact]
    public void A_function_that_answers_null_makes_a_comparison_false()
    {
        var nothing = TestCelFunctions.Host("nothing", CelValueType.String, _ => null, TestCelFunctions.Parameter("s", CelValueType.String));

        Condition("nothing(name) == 'x'", CelFixtures.Row(("name", "x")), nothing).ShouldBeFalse();
        Condition("!(nothing(name) == 'x')", CelFixtures.Row(("name", "x")), nothing).ShouldBeTrue();
    }

    [Fact]
    public void The_two_legacy_calls_still_evaluate_by_name()
    {
        Mutate("lowerAscii(name)", CelFixtures.Row(("name", "ABC"))).ShouldBe("abc");
        Mutate("now()", CelFixtures.Row()).ShouldBe(_now);
    }
}
```

- [ ] **Step 2: Run — expect build failure** (`CelFunctionException` missing)

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelFunctionInvocationTests'`
Expected: CS0246.

- [ ] **Step 3: Implement**

`CelFunctionException.cs`:

```csharp
namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// A catalogued CEL function failed while an expression was evaluated. Deliberately <b>not</b> collapsed into a value
/// the way every other evaluation surprise is (false, null, masked): a function is the one construct whose failure is
/// reachable, so it fails closed — the interpreter lets it escape, the write rolls back, and the caller receives
/// <c>…/errors/function-failed</c> (spec §5.6). Internal: only the core throws it and only the core catches it.
/// </summary>
internal sealed class CelFunctionException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="CelFunctionException"/> class for a body that threw.</summary>
    /// <param name="functionName">The function's name, which the problem document may show (descriptor-authored).</param>
    /// <param name="isHost">Whether the host registered it.</param>
    /// <param name="failure">What the body threw; kept for the log, never shown to the caller.</param>
    internal CelFunctionException(string functionName, bool isHost, Exception failure)
        : base($"The CEL function '{functionName}' failed.", failure)
    {
        FunctionName = functionName;
        IsHost = isHost;
    }

    /// <summary>Initializes a new instance of the <see cref="CelFunctionException"/> class for a built-in that refused.</summary>
    /// <param name="functionName">The built-in's name.</param>
    /// <param name="reason">Why, in Alvo's own words — safe to show the caller.</param>
    internal CelFunctionException(string functionName, string reason)
        : base($"The CEL function '{functionName}' failed: {reason}.")
    {
        FunctionName = functionName;
        Reason = reason;
    }

    /// <summary>Gets the failed function's name.</summary>
    public string FunctionName { get; }

    /// <summary>Gets a value indicating whether the host registered the function.</summary>
    public bool IsHost { get; }

    /// <summary>Gets Alvo's own reason for a built-in's refusal, or <see langword="null"/> when a body threw.</summary>
    public string? Reason { get; }
}
```

`CelArgumentMarshaller.cs`:

```csharp
namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// Turns the loosely typed values a record carries (<c>int</c>/<c>long</c>/<c>decimal</c>/<c>double</c>,
/// <c>DateTimeOffset</c>/<c>DateTime</c>/<c>DateOnly</c>/text, <c>Guid</c>/text) into the CLR type a function body
/// takes. A value that does not convert reads as <see langword="null"/> — the interpreter's existing rule for a value
/// of an unexpected CLR type — and never throws.
/// </summary>
internal static class CelArgumentMarshaller
{
    /// <summary>The value as <paramref name="target"/>, or <see langword="null"/> when it is absent or does not convert.</summary>
    /// <param name="value">The evaluated argument.</param>
    /// <param name="target">The parameter's CLR type (never a <see cref="Nullable{T}"/>).</param>
    /// <returns>The converted value, boxed, or <see langword="null"/>.</returns>
    internal static object? Convert(object? value, System.Type target) => value is null ? null : target switch
    {
        _ when target == typeof(string) => value as string,
        _ when target == typeof(bool) => ToBool(value),
        _ when target == typeof(decimal) => ToDecimal(value),
        _ when target == typeof(long) => ToInteger(value),
        _ when target == typeof(int) => ToInt32(value),
        _ when target == typeof(DateTimeOffset) => ToInstant(value),
        _ when target == typeof(Guid) => ToGuid(value),
        _ => null,
    };

    /// <summary>A body's result in the representation the rest of the interpreter uses: an <c>int</c> becomes a <c>long</c>.</summary>
    /// <param name="result">What the body returned.</param>
    /// <returns>The normalised result.</returns>
    internal static object? Normalize(object? result) => result is int whole ? (long)whole : result;

    private static object? ToBool(object value) => value is bool flag ? flag : null;

    private static object? ToDecimal(object value) => CelInterpreter.TryToDecimal(value, out var number) ? number : null;

    private static object? ToInteger(object value) =>
        CelInterpreter.TryToDecimal(value, out var number) && decimal.Truncate(number) == number
        && number is >= long.MinValue and <= long.MaxValue
            ? (long)number
            : null;

    private static object? ToInt32(object value) =>
        ToInteger(value) is long whole && whole is >= int.MinValue and <= int.MaxValue ? (int)whole : null;

    private static object? ToInstant(object value) => value switch
    {
        DateOnly date => (object)new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        _ => CelInterpreter.TryToDateTimeOffset(value, out var instant) ? instant : null,
    };

    private static object? ToGuid(object value) => value switch
    {
        Guid id => (object)id,
        string text when Guid.TryParse(text, out var parsed) => parsed,
        _ => null,
    };
}
```

(Every helper returns `object?` and the `(object)` casts in the two inner switches are load-bearing: without them a switch's natural type is `DateTimeOffset`/`Guid` and its `null` arm does not compile.)

In `CelFunction.cs` add to the record:

```csharp
    /// <summary>
    /// Calls the body with <paramref name="arguments"/> converted to each parameter's CLR type. A null (or
    /// unconvertible) argument for a parameter that takes none makes the call null without invoking the body (spec R3).
    /// </summary>
    /// <param name="arguments">The evaluated arguments, one per parameter.</param>
    /// <returns>The normalised result, or <see langword="null"/>.</returns>
    /// <exception cref="CelFunctionException">The body failed; whatever it threw is the inner exception.</exception>
    public object? Invoke(IReadOnlyList<object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var values = new object?[Parameters.Count];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = CelArgumentMarshaller.Convert(arguments[index], Parameters[index].ClrType);
            if (values[index] is null && !Parameters[index].Nullable)
            {
                return null;
            }
        }

        return Run(values);
    }

    private object? Run(object?[] values)
    {
        var body = Body ?? throw new InvalidOperationException($"'{Name}' has its own grammar and is evaluated by name, never invoked.");
        try
        {
            return CelArgumentMarshaller.Normalize(body(values));
        }
        catch (CelFunctionException)
        {
            throw;
        }
#pragma warning disable CA1031 // A body may throw anything; every failure becomes the one fail-closed type.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            throw new CelFunctionException(Name, IsHost, failure);
        }
    }
```

(If the build reports IDE0079 "unnecessary suppression", delete the two pragma lines; if it reports CA1031, keep them.)

In `CelInterpreter.cs`:

```csharp
    private static object? EvaluateCall(CelCall call, in EvalState state) => call switch
    {
        { Function: { } function } => function.Invoke(EvaluateArguments(call.Arguments, state)),
        { Name: CelCall.LowerAscii, Arguments: [var argument] } => LowerAscii(Evaluate(argument, state)),
        { Name: CelCall.Now, Arguments: [] } => state.Now,
        _ => null,
    };

    /// <summary>Evaluates every argument eagerly, left to right — CEL functions are strict.</summary>
    private static object?[] EvaluateArguments(IReadOnlyList<CelNode> arguments, in EvalState state)
    {
        var values = new object?[arguments.Count];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = Evaluate(arguments[index], state);
        }

        return values;
    }
```

Change `private static bool TryToDecimal` and `private static bool TryToDateTimeOffset` to `internal static`. In `EvaluatePredicate` and `EvaluateMutation` (only these two — Rule/mask/computed cannot hold a call in C1) change `catch (Exception)` to `catch (Exception failure) when (failure is not CelFunctionException)` (keep the CA1031 pragmas). Rewrite the paragraphs that say nothing in a Condition/Mutate tree can throw: `EvaluateMutation`'s `<remarks>` and the class remark "No exception ever escapes …" now say: *a `CelFunctionException` escapes on purpose — a function failure fails closed; every other surprise still collapses as before.*

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet build`, then `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelFunctionInvocationTests' --filter-class '*CelInterpreter*' --filter-class '*DifferentialBackendTests' --filter-class '*CelAcceptanceCorpusTests'`
Expected: all pass.

- [ ] **Step 5: Normalise, ring0, commit**

```bash
for p in src/MMLib.Alvo/Expressions/Internal/CelFunctionException.cs src/MMLib.Alvo/Expressions/Internal/CelArgumentMarshaller.cs test/MMLib.Alvo.Tests/Expressions/CelFunctionInvocationTests.cs; do python3 -c "p='$p';b=open(p,'rb').read().replace(b'\r\n',b'\n').replace(b'\n',b'\r\n');b=b if b.startswith(b'\xef\xbb\xbf') else b'\xef\xbb\xbf'+b;open(p,'wb').write(b)"; done
scripts/test-ring0
git add src/MMLib.Alvo/Expressions/Internal/CelFunctionException.cs src/MMLib.Alvo/Expressions/Internal/CelArgumentMarshaller.cs src/MMLib.Alvo/Expressions/Internal/CelFunction.cs src/MMLib.Alvo/Expressions/Internal/CelInterpreter.cs test/MMLib.Alvo.Tests/Expressions/CelFunctionInvocationTests.cs
git commit -m "feat(cel): invoke a bound function through a marshaller and let its failure escape, fail closed" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 6: the five built-ins

**Files:**
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.cs`
- Modify: `test/MMLib.Alvo.Tests/Expressions/CelFunctionCatalogTests.cs` (`The_built_ins_are_…` fact)
- Test: `test/MMLib.Alvo.Tests/Expressions/CelBuiltInFunctionTests.cs`, `test/MMLib.Alvo.Tests/Expressions/CelBuiltInPropertyTests.cs` (create)

**Interfaces:**
- Consumes: `CelFunctionException(string, string)` (Task 5), `TestCelFunctions.Items/Compile` (Task 3).
- Produces: built-in overloads `replace(text, search, replacement)`, `trim(text)`, `size(text) -> Int`, `abs(x: Int|Decimal)`, `round(x: Int|Decimal)`, all `ConditionAndMutate`, `ResultNullable: false`; `internal const int CelBuiltInFunctions.MaxTextLength = 1_048_576`; `internal static string ReplaceText(string, string, string)`, `internal static string TrimText(string)`, `internal static long SizeOf(string)`.

- [ ] **Step 1: Write the failing tests**

`CelBuiltInFunctionTests.cs`:

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using System.Globalization;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>The five built-ins, each against the edge cases spec §6 pins (and C2's SQL must reproduce).</summary>
public sealed class CelBuiltInFunctionTests
{
    private static readonly DateTimeOffset _now = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static object? Evaluate(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluateMutation(TestCelFunctions.Compile(source, CelProfile.Mutate), CelFixtures.Row(row), previous: null, _now);

    [Theory]
    [InlineData("a-b-c", "-", "+", "a+b+c")]
    [InlineData("aaa", "aa", "b", "ba")]
    [InlineData("abc", "", "x", "abc")]
    [InlineData("abc", "z", "x", "abc")]
    [InlineData("ABC", "b", "x", "ABC")]
    [InlineData("", "a", "b", "")]
    public void Replace_is_ordinal_left_to_right_and_an_empty_search_changes_nothing(string text, string search, string replacement, string expected) =>
        Evaluate($"replace(name, '{search}', '{replacement}')", ("name", text)).ShouldBe(expected);

    [Theory]
    [InlineData("  a b  ", "a b")]
    [InlineData("\t\n\r a \r\n\t", "a")]
    [InlineData(" a ", " a ")]
    [InlineData(" a", " a")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Trim_removes_the_four_ascii_whitespace_characters_and_nothing_else(string text, string expected) =>
        Evaluate("trim(name)", ("name", text)).ShouldBe(expected);

    [Theory]
    [InlineData("", 0L)]
    [InlineData("abc", 3L)]
    [InlineData("😀", 1L)]
    [InlineData("é", 2L)]
    public void Size_counts_unicode_code_points(string text, long expected) =>
        Evaluate("size(name)", ("name", text)).ShouldBe(expected);

    [Fact]
    public void Size_counts_a_lone_surrogate_as_one() =>
        Evaluate("size(name)", ("name", "a\ud800")).ShouldBe(2L);

    [Theory]
    [InlineData(-5L, 5L)]
    [InlineData(5L, 5L)]
    [InlineData(0L, 0L)]
    public void Abs_of_an_int_is_an_int(long value, long expected) =>
        Evaluate("abs(qty)", ("qty", value)).ShouldBe(expected);

    [Fact]
    public void Abs_of_a_decimal_is_a_decimal() =>
        Evaluate("abs(price)", ("price", -2.50m)).ShouldBe(2.50m);

    [Fact]
    public void Abs_of_the_smallest_int_fails_closed_with_a_reason()
    {
        var failure = Should.Throw<CelFunctionException>(() => Evaluate("abs(qty)", ("qty", long.MinValue)));

        failure.FunctionName.ShouldBe("abs");
        failure.Reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("2.5", "3")]
    [InlineData("-2.5", "-3")]
    [InlineData("0.5", "1")]
    [InlineData("-0.5", "-1")]
    [InlineData("1.4999", "1")]
    [InlineData("2.4", "2")]
    public void Round_takes_halves_away_from_zero(string value, string expected) =>
        Evaluate("round(price)", ("price", decimal.Parse(value, CultureInfo.InvariantCulture)))
            .ShouldBe(decimal.Parse(expected, CultureInfo.InvariantCulture));

    [Fact]
    public void Round_of_an_int_is_the_int() => Evaluate("round(qty)", ("qty", 7L)).ShouldBe(7L);

    [Theory]
    [InlineData("trim(name)")]
    [InlineData("size(name)")]
    [InlineData("replace(name, 'a', 'b')")]
    [InlineData("abs(qty)")]
    [InlineData("round(price)")]
    public void A_null_argument_makes_every_built_in_null(string source) => Evaluate(source).ShouldBeNull();

    [Fact]
    public void A_replace_that_would_grow_past_the_cap_fails_closed()
    {
        var source = $"replace(name, 'a', '{new string('b', 1100)}')";

        Should.Throw<CelFunctionException>(() => Evaluate(source, ("name", new string('a', 1000)))).FunctionName.ShouldBe("replace");
    }

    [Fact]
    public void A_replace_that_shrinks_a_text_longer_than_the_cap_is_not_capped() =>
        Evaluate("replace(name, 'a', '')", ("name", new string('a', CelBuiltInFunctions.MaxTextLength + 1))).ShouldBe(string.Empty);

    [Fact]
    public void Built_ins_compose_in_a_mutate() =>
        Evaluate("trim(replace(name, '-', ' '))", ("name", "-a-b-")).ShouldBe("a b");

    [Fact]
    public void A_built_in_works_in_a_hook_condition() =>
        CelInterpreter.EvaluatePredicate(
            TestCelFunctions.Compile("size(name) > 3", CelProfile.Condition), CelFixtures.Row(("name", "abcd")), null, CelFixtures.Alice)
            .ShouldBeTrue();

    [Fact]
    public void A_built_in_is_refused_in_a_rule_in_this_slice() =>
        new CelCompiler().Compile("trim(name) == 'x'", CelProfile.Rule, TestCelFunctions.Items).Errors.ShouldHaveSingleItem()
            .Message.ShouldStartWith("'trim(...)' is not available in the Rule profile");

    [Fact]
    public void A_field_named_like_a_built_in_is_still_a_field()
    {
        Evaluate("trim(round)", ("round", " x ")).ShouldBe("x");
        Evaluate("size(size)", ("size", "abc")).ShouldBe(3L);
    }
}
```

`CelBuiltInPropertyTests.cs`:

```csharp
using CsCheck;
using MMLib.Alvo.Expressions.Internal;
using System.Text;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>The text built-ins never throw for any string — lone surrogates included — and agree with a reference definition.</summary>
public sealed class CelBuiltInPropertyTests
{
    private const int Iterations = 5_000;
    private const string AsciiWhitespace = " \t\n\r";

    [Fact]
    public void Trim_leaves_no_ascii_whitespace_at_either_end_and_only_removes_from_the_ends() =>
        Gen.String.Sample(text =>
        {
            var trimmed = CelBuiltInFunctions.TrimText(text);

            (trimmed.Length == 0 || (!AsciiWhitespace.Contains(trimmed[0]) && !AsciiWhitespace.Contains(trimmed[^1]))).ShouldBeTrue();
            text.Contains(trimmed, StringComparison.Ordinal).ShouldBeTrue();
        },
        iter: Iterations);

    [Fact]
    public void Size_equals_a_code_point_scan() =>
        Gen.String.Sample(text => CelBuiltInFunctions.SizeOf(text).ShouldBe(CodePoints(text)), iter: Iterations);

    [Fact]
    public void Replace_agrees_with_a_left_to_right_scan() =>
        Gen.Select(Text(0, 40), Text(1, 3), Text(0, 3)).Sample(
            sample => CelBuiltInFunctions.ReplaceText(sample.Item1, sample.Item2, sample.Item3)
                .ShouldBe(Scan(sample.Item1, sample.Item2, sample.Item3)),
            iter: Iterations);

    /// <summary>Text over a three-letter alphabet, so searches actually match.</summary>
    private static Gen<string> Text(int shortest, int longest) =>
        Gen.Char["abc"].Array[shortest, longest].Select(characters => new string(characters));

    private static long CodePoints(string text)
    {
        long count = 0;
        for (var index = 0; index < text.Length; index++, count++)
        {
            if (char.IsSurrogatePair(text, index))
            {
                index++;
            }
        }

        return count;
    }

    private static string Scan(string text, string search, string replacement)
    {
        var built = new StringBuilder();
        for (var at = 0; at < text.Length;)
        {
            var matches = at + search.Length <= text.Length && string.CompareOrdinal(text, at, search, 0, search.Length) == 0;
            built.Append(matches ? replacement : text[at].ToString());
            at += matches ? search.Length : 1;
        }

        return built.ToString();
    }
}
```

(If the CsCheck version in `Directory.Packages.props` spells the generators differently, keep the property and adapt the generator calls; `CompiledRendersPropertyTests` shows the API this repo uses.)

In `CelFunctionCatalogTests` rename `The_built_ins_are_the_two_legacy_calls_with_their_own_grammar` to `The_built_ins_are_the_legacy_calls_and_the_five_functions` and make it:

```csharp
    [Fact]
    public void The_built_ins_are_the_legacy_calls_and_the_five_functions()
    {
        var catalog = CelFunctionCatalog.BuiltIns;

        catalog.Names.ShouldBe(["abs", "lowerAscii", "now", "replace", "round", "size", "trim"]);
        catalog.Overloads("abs").Select(o => o.ResultType).ShouldBe([CelValueType.Int, CelValueType.Decimal]);
        catalog.Overloads("round").Select(o => o.ResultType).ShouldBe([CelValueType.Int, CelValueType.Decimal]);
        catalog.Functions.Where(f => f.IsLegacy).Select(f => f.Name).ShouldBe(["lowerAscii", "now"]);
        catalog.Functions.Where(f => !f.IsLegacy).ShouldAllBe(f => f.Profiles.SetEquals(new[] { CelProfile.Condition, CelProfile.Mutate }));
    }
```

and `A_host_function_joins_the_built_ins_in_name_order` expects `["abs", "lowerAscii", "normalizePhone", "now", "replace", "round", "size", "trim"]`.

- [ ] **Step 2: Run — expect FAIL** (`'trim' is not a recognized function`, `TrimText` missing)

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelBuiltIn*' --filter-class '*CelFunctionCatalogTests'`
Expected: build error CS0117 for `TrimText`/`MaxTextLength`.

- [ ] **Step 3: Implement** — in `CelBuiltInFunctions.cs` add `using System.Globalization;`, change `All`, and add the entries and bodies:

```csharp
    /// <summary>
    /// The most characters a <c>replace</c> may grow a text to: the Data API's default request body limit
    /// (<c>AlvoApiOptions.MaxRequestBodyBytes</c>, 1 MiB), the largest value a client could send in one default request.
    /// Derived, not sourced (spec X12, Q4); without it one 2,000-character replacement over a large text is an
    /// out-of-memory inside a write transaction.
    /// </summary>
    internal const int MaxTextLength = 1_048_576;

    /// <summary>The characters <c>trim</c> removes: the four the CEL lexer can escape or type (spec deviation F5).</summary>
    private const string AsciiWhitespace = " \t\n\r";

    /// <summary>Gets every built-in overload.</summary>
    internal static IReadOnlyList<CelFunction> All =>
        [LowerAscii, Now, Replace, Trim, Size, Abs(CelValueType.Int), Abs(CelValueType.Decimal), Round(CelValueType.Int), Round(CelValueType.Decimal)];

    private static CelFunction Replace => InProcess(
        "replace", CelValueType.String,
        "Replaces every occurrence of search in text with replacement, left to right, comparing characters exactly; an empty search changes nothing.",
        arguments => ReplaceText((string)arguments[0]!, (string)arguments[1]!, (string)arguments[2]!),
        Parameter("text", CelValueType.String), Parameter("search", CelValueType.String), Parameter("replacement", CelValueType.String));

    private static CelFunction Trim => InProcess(
        "trim", CelValueType.String,
        "Removes spaces, tabs, line feeds and carriage returns from both ends of text; nothing else counts as whitespace.",
        arguments => TrimText((string)arguments[0]!), Parameter("text", CelValueType.String));

    private static CelFunction Size => InProcess(
        "size", CelValueType.Int, "The number of Unicode code points in text.",
        arguments => SizeOf((string)arguments[0]!), Parameter("text", CelValueType.String));

    private static CelFunction Abs(CelValueType type) => InProcess(
        "abs", type, "The absolute value of x, of the same numeric type.",
        arguments => type == CelValueType.Int ? (object)AbsInt((long)arguments[0]!) : Math.Abs((decimal)arguments[0]!),
        Parameter("x", type));

    private static CelFunction Round(CelValueType type) => InProcess(
        "round", type, "x rounded to a whole number, halves away from zero (2.5 is 3, -2.5 is -3), of the same numeric type.",
        arguments => type == CelValueType.Int ? arguments[0] : Math.Round((decimal)arguments[0]!, MidpointRounding.AwayFromZero),
        Parameter("x", type));

    private static CelFunction InProcess(
        string name, CelValueType result, string summary, Func<object?[], object?> body, params CelFunctionArgument[] parameters) =>
        new(name, parameters, result, ResultNullable: false, summary, IsHost: false, ConditionAndMutate, body);

    /// <summary><c>replace</c>: ordinal, left to right, non-overlapping; an empty search returns the text (SQL's answer).</summary>
    /// <exception cref="CelFunctionException">The result would grow past <see cref="MaxTextLength"/>.</exception>
    internal static string ReplaceText(string text, string search, string replacement)
    {
        if (search.Length == 0)
        {
            return text;
        }

        var grown = (long)text.Length + ((long)Occurrences(text, search) * (replacement.Length - search.Length));
        if (replacement.Length > search.Length && grown > MaxTextLength)
        {
            throw new CelFunctionException("replace", string.Create(
                CultureInfo.InvariantCulture,
                $"its result would be {grown:N0} characters, over the {MaxTextLength:N0} a text may grow to here"));
        }

        return text.Replace(search, replacement, StringComparison.Ordinal);
    }

    /// <summary><c>trim</c>: the four ASCII whitespace characters from both ends, nothing else.</summary>
    internal static string TrimText(string text) => text.AsSpan().Trim(AsciiWhitespace).ToString();

    /// <summary><c>size</c>: Unicode code points; a lone surrogate counts as one (it enumerates as U+FFFD).</summary>
    internal static long SizeOf(string text) => text.EnumerateRunes().Count();

    private static long AbsInt(long value) => value == long.MinValue
        ? throw new CelFunctionException("abs", "the absolute value of the smallest Int is not an Int")
        : Math.Abs(value);

    private static int Occurrences(string text, string search)
    {
        var count = 0;
        for (var at = text.IndexOf(search, StringComparison.Ordinal); at >= 0; at = text.IndexOf(search, at + search.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
```

(The `(object)` cast in `Abs` is load-bearing: without it the conditional's common type is `decimal` and `abs` of an Int would return a Decimal. `Round`'s Int branch is already `object?`.)

- [ ] **Step 4: Run — expect PASS, corpus unchanged**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelBuiltIn*' --filter-class '*CelFunctionCatalogTests' --filter-class '*CelCatalogCallParsingTests' --filter-class '*CelAcceptanceCorpusTests'`
Expected: all pass. (`CelCatalogCallParsingTests.An_unknown_function_keeps_its_message…` expects the test catalog's names, which now include the five built-ins: update its expected list to `"Known functions: abs, echo, lowerAscii, normalizePhone, now, pair, replace, round, size, trim."` in this step.)

- [ ] **Step 5: Normalise, ring0, commit**

```bash
for p in test/MMLib.Alvo.Tests/Expressions/CelBuiltInFunctionTests.cs test/MMLib.Alvo.Tests/Expressions/CelBuiltInPropertyTests.cs; do python3 -c "p='$p';b=open(p,'rb').read().replace(b'\r\n',b'\n').replace(b'\n',b'\r\n');b=b if b.startswith(b'\xef\xbb\xbf') else b'\xef\xbb\xbf'+b;open(p,'wb').write(b)"; done
scripts/test-ring0
git add src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.cs test/MMLib.Alvo.Tests/Expressions/CelBuiltInFunctionTests.cs test/MMLib.Alvo.Tests/Expressions/CelBuiltInPropertyTests.cs test/MMLib.Alvo.Tests/Expressions/CelFunctionCatalogTests.cs test/MMLib.Alvo.Tests/Expressions/CelCatalogCallParsingTests.cs
git commit -m "feat(cel): replace, trim, size, abs and round in hook conditions and mutate values" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 7: `AddCelFunction`, the public discovery types, DI

**Files:**
- Create: `src/MMLib.Alvo/Expressions/Internal/HostCelFunction.cs`, `src/MMLib.Alvo/Expressions/Internal/CelFunctionRegistration.cs`
- Create: `src/MMLib.Alvo.Abstractions/Expressions/CelFunctionInfo.cs`
- Modify: `src/MMLib.Alvo.Abstractions/Expressions/CelValueType.cs`, `CelProfile.cs` (one attribute each)
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelFunction.cs` (`Describe`), `CelFunctionCatalog.cs` (`Describe`)
- Modify: `src/MMLib.Alvo/Expressions/Setup.cs`, `src/MMLib.Alvo/AlvoBuilderExtensions.cs`
- Modify: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`, `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt` (regenerate, review)
- Test: `test/MMLib.Alvo.Tests/Expressions/CelFunctionRegistrationTests.cs` (create)

**Interfaces:**
- Produces (public, Abstractions, namespace `MMLib.Alvo.Expressions`): `enum CelFunctionProvenance { BuiltIn, Host }`; `sealed record CelFunctionParameter { required string Name; required CelValueType Type; required bool AcceptsNull; }`; `sealed record CelFunctionInfo { required string Name; required IReadOnlyList<CelFunctionParameter> Parameters; required CelValueType Result; required bool ResultMayBeNull; required string Summary; required CelFunctionProvenance Provenance; required IReadOnlyList<CelProfile> Profiles; }`.
- Produces (public, core): `AlvoBuilderExtensions.AddCelFunction(this IAlvoBuilder builder, string name, Delegate function, string? summary = null) -> IAlvoBuilder`.
- Produces (internal): `HostCelFunction.Create(string name, Delegate function, string? summary) -> CelFunction`; `HostCelFunction.MaxParameters = 4`; `record CelFunctionRegistration(CelFunction Function)`; `CelFunction.Describe() -> CelFunctionInfo`; `CelFunctionCatalog.Describe() -> IReadOnlyList<CelFunctionInfo>`; DI singletons `CelFunctionCatalog` and `ICelCompiler` (factory).

- [ ] **Step 1: Write the failing tests**

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>Registering a host function: what is accepted, what is refused at the call, and what discovery shows.</summary>
public sealed class CelFunctionRegistrationTests
{
    private delegate string ByRef(ref string value);

    private static ServiceProvider Build(Action<IAlvoBuilder> register)
    {
        var services = new ServiceCollection();
        register(services.AddAlvo());
        return services.BuildServiceProvider();
    }

    private static IAlvoBuilder Alvo() => new ServiceCollection().AddAlvo();

    [Fact]
    public void A_registered_function_compiles_and_runs_in_a_mutate()
    {
        using var provider = Build(alvo => alvo.AddCelFunction(
            "normalizePhone", (string phone) => phone.Replace(" ", string.Empty, StringComparison.Ordinal), "Strips spaces."));

        var compiled = provider.GetRequiredService<ICelCompiler>().Compile("normalizePhone(name)", CelProfile.Mutate, TestCelFunctions.Items);

        compiled.IsSuccess.ShouldBeTrue();
        CelInterpreter.EvaluateMutation(compiled.Expression!, CelFixtures.Row(("name", "+421 900")), null, DateTimeOffset.UnixEpoch)
            .ShouldBe("+421900");
    }

    [Fact]
    public void A_compiler_built_without_the_container_knows_no_host_function() =>
        new CelCompiler().Compile("normalizePhone(name)", CelProfile.Mutate, TestCelFunctions.Items).Errors.ShouldHaveSingleItem()
            .Message.ShouldBe("'normalizePhone' is not a recognized function.");

    [Theory]
    [InlineData("Normalize")]
    [InlineData("1abc")]
    [InlineData("normalize-phone")]
    [InlineData("normalizéPhone")]
    [InlineData("abc\n")]
    [InlineData("has")]
    [InlineData("changed")]
    [InlineData("now")]
    [InlineData("lowerAscii")]
    [InlineData("trim")]
    [InlineData("abs")]
    [InlineData("in")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("old")]
    [InlineData("new")]
    [InlineData("exists")]
    [InlineData("while")]
    public void A_name_the_catalog_cannot_honour_is_refused_at_registration(string name) =>
        Should.Throw<ArgumentException>(() => Alvo().AddCelFunction(name, (string s) => s)).Message.ShouldContain(name.TrimEnd('\n'));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_is_refused(string name) =>
        Should.Throw<ArgumentException>(() => Alvo().AddCelFunction(name, (string s) => s));

    [Theory]
    [InlineData("vat_rate")]
    [InlineData("normalizePhone")]
    [InlineData("a")]
    [InlineData("x9")]
    public void A_lower_camel_or_snake_name_is_accepted(string name) =>
        Should.NotThrow(() => Alvo().AddCelFunction(name, (string s) => s));

    [Fact]
    public void A_second_registration_of_a_name_is_refused()
    {
        var alvo = Alvo().AddCelFunction("vat_rate", (string country) => 0.2m);

        Should.Throw<ArgumentException>(() => alvo.AddCelFunction("vat_rate", (string country) => 0.1m)).Message.ShouldContain("already registered");
    }

    public static TheoryData<string, Delegate> Unsupported() => new()
    {
        { "a DateTime parameter", (DateTime value) => "x" },
        { "an object parameter", (object value) => "x" },
        { "five parameters", (string a, string b, string c, string d, string e) => a },
        { "no result", (Action<string>)(_ => { }) },
        { "a Task result", (string value) => Task.FromResult(value) },
        { "a ValueTask result", (string value) => ValueTask.FromResult(value) },
        { "an object result", (string value) => (object)value },
        { "a ref parameter", (ByRef)((ref string value) => value) },
        { "a multicast delegate", Multicast() },
    };

    [Theory]
    [MemberData(nameof(Unsupported))]
    public void A_delegate_the_catalog_cannot_call_is_refused_at_registration(string why, Delegate function)
    {
        _ = why;

        Should.Throw<ArgumentException>(() => Alvo().AddCelFunction("probe", function));
    }

    [Fact]
    public void An_asynchronous_function_is_told_where_io_belongs() =>
        Should.Throw<ArgumentException>(() => Alvo().AddCelFunction("probe", (string value) => Task.FromResult(value)))
            .Message.ShouldContain("after-hook");

    [Fact]
    public void Every_supported_type_is_described_with_its_cel_type_and_nullability()
    {
        HostCelFunction.Create("first", (string s, long l, int? i, decimal? d) => s, summary: null).Parameters
            .Select(p => (p.Name, p.Type, p.Nullable))
            .ShouldBe([("s", CelValueType.String, false), ("l", CelValueType.Int, false), ("i", CelValueType.Int, true), ("d", CelValueType.Decimal, true)]);
        HostCelFunction.Create("second", (bool b, DateTimeOffset t, Guid g, string? n) => n, summary: null).Parameters
            .Select(p => (p.Type, p.Nullable))
            .ShouldBe([(CelValueType.Bool, false), (CelValueType.Timestamp, false), (CelValueType.Uuid, false), (CelValueType.String, true)]);
    }

    [Fact]
    public void A_registration_after_the_host_is_built_is_refused_by_the_read_only_collection()
    {
        var services = new ServiceCollection();
        var alvo = services.AddAlvo();
        services.MakeReadOnly();

        Should.Throw<InvalidOperationException>(() => alvo.AddCelFunction("late", (string s) => s));
    }

    [Fact]
    public void Discovery_shows_a_host_function_among_the_built_ins()
    {
        using var provider = Build(alvo => alvo.AddCelFunction("normalizePhone", (string phone) => phone, "Keeps the digits."));

        var described = provider.GetRequiredService<CelFunctionCatalog>().Describe();

        var host = described.Single(f => f.Provenance == CelFunctionProvenance.Host);
        host.Name.ShouldBe("normalizePhone");
        host.Summary.ShouldBe("Keeps the digits.");
        host.Result.ShouldBe(CelValueType.String);
        host.Profiles.ShouldBe([CelProfile.Condition, CelProfile.Mutate]);
        host.Parameters.ShouldHaveSingleItem().Name.ShouldBe("phone");
        described.Select(f => f.Name).ShouldBe(described.Select(f => f.Name).Order(StringComparer.Ordinal));
        described.Where(f => f.Provenance == CelFunctionProvenance.BuiltIn).Select(f => f.Name).Distinct()
            .ShouldBe(["abs", "lowerAscii", "now", "replace", "round", "size", "trim"]);
    }

    private static Delegate Multicast()
    {
        Func<string, string> first = s => s;
        Func<string, string> second = s => s;
        return first + second;
    }
}
```

- [ ] **Step 2: Run — expect build failure** (`AddCelFunction`, `HostCelFunction`, `CelFunctionProvenance` missing)

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelFunctionRegistrationTests'`
Expected: CS1061/CS0246.

- [ ] **Step 3: Public DTOs** — `src/MMLib.Alvo.Abstractions/Expressions/CelFunctionInfo.cs`:

```csharp
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Expressions;

/// <summary>Where a CEL function comes from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CelFunctionProvenance>))]
public enum CelFunctionProvenance
{
    /// <summary>Shipped with Alvo: every host, the standalone image and the CLI know it.</summary>
    BuiltIn,

    /// <summary>Registered by the embedding host with <c>AddCelFunction</c>: only that host knows it, and it runs host code.</summary>
    Host,
}

/// <summary>One parameter of a CEL function overload, as discovery shows it.</summary>
public sealed record CelFunctionParameter
{
    /// <summary>Gets the parameter's name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the CEL type an argument must have; an Int also binds a Decimal parameter.</summary>
    public required CelValueType Type { get; init; }

    /// <summary>
    /// Gets a value indicating whether the function receives a null argument. When it does not, a null argument makes
    /// the call's value null and the function is not invoked.
    /// </summary>
    public required bool AcceptsNull { get; init; }
}

/// <summary>
/// One overload of a CEL function a descriptor may call. A name with several overloads (<c>abs</c>, <c>round</c>)
/// appears once per overload; a host function has exactly one.
/// </summary>
/// <remarks>
/// Init-only rather than positional, unlike the Management records beside it: this is the description most likely to
/// grow (examples, a "since", a summary key), and an added optional init member is additive where an added positional
/// parameter is not.
/// </remarks>
public sealed record CelFunctionInfo
{
    /// <summary>Gets the function's CEL spelling.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the parameters, in call order.</summary>
    public required IReadOnlyList<CelFunctionParameter> Parameters { get; init; }

    /// <summary>Gets the CEL type of the value the call yields.</summary>
    public required CelValueType Result { get; init; }

    /// <summary>Gets a value indicating whether the call may yield null even when every argument is present.</summary>
    public required bool ResultMayBeNull { get; init; }

    /// <summary>Gets one sentence on what the function does; empty when the host gave none.</summary>
    public required string Summary { get; init; }

    /// <summary>Gets where the function comes from.</summary>
    public required CelFunctionProvenance Provenance { get; init; }

    /// <summary>Gets the profiles a call to it compiles in.</summary>
    public required IReadOnlyList<CelProfile> Profiles { get; init; }
}
```

Add `[JsonConverter(typeof(JsonStringEnumConverter<CelValueType>))]` above `public enum CelValueType` and `[JsonConverter(typeof(JsonStringEnumConverter<CelProfile>))]` above `public enum CelProfile` (each file gains `using System.Text.Json.Serialization;`). No Management model carries either enum; the one other reach is `PolicyDecision` → `CompiledExpression`. Verify it is never serialized: `rg -n "Serialize|Results.Ok|Json\(" src | rg -i "PolicyDecision|CompiledExpression"` must print nothing; if it prints a hit, stop and report — that output's enum values would change from numbers to names.

- [ ] **Step 4: `Describe`** — in `CelFunction.cs`:

```csharp
    /// <summary>This overload as discovery shows it.</summary>
    /// <returns>The public description.</returns>
    public CelFunctionInfo Describe() => new()
    {
        Name = Name,
        Parameters = [.. Parameters.Select(p => new CelFunctionParameter { Name = p.Name, Type = p.Type, AcceptsNull = p.Nullable })],
        Result = ResultType,
        ResultMayBeNull = ResultNullable,
        Summary = Summary,
        Provenance = IsHost ? CelFunctionProvenance.Host : CelFunctionProvenance.BuiltIn,
        Profiles = [.. Profiles.Order()],
    };
```

and in `CelFunctionCatalog.cs`:

```csharp
    /// <summary>Every overload as discovery shows it, in <see cref="Functions"/> order.</summary>
    /// <returns>The public descriptions.</returns>
    internal IReadOnlyList<CelFunctionInfo> Describe() => [.. Functions.Select(function => function.Describe())];
```

- [ ] **Step 5: `HostCelFunction`** — `src/MMLib.Alvo/Expressions/Internal/HostCelFunction.cs`:

```csharp
using System.Reflection;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// Turns a host's <see cref="Delegate"/> into a catalogued <see cref="CelFunction"/>, refusing at registration — as an
/// <see cref="ArgumentException"/> at the <c>AddCelFunction</c> call — everything the catalog cannot honour (spec §5.7).
/// </summary>
/// <remarks>
/// The call signature is the delegate type's <c>Invoke</c>, which is right for every delegate shape; parameter names and
/// <c>string?</c> annotations come from <see cref="Delegate.Method"/> when its arity matches (a lambda or a method
/// group). An oblivious nullability context reads as non-nullable — the safe side: null-propagation.
/// </remarks>
internal static partial class HostCelFunction
{
    /// <summary>The most parameters a host function may take (spec R3).</summary>
    internal const int MaxParameters = 4;

    private const string SupportedTypes =
        "a CEL function's parameters and result are string, long, int, decimal, bool, DateTimeOffset or Guid, or a nullable one of those";

    /// <summary>The catalogued function for <paramref name="function"/>.</summary>
    /// <param name="name">The CEL name.</param>
    /// <param name="function">The implementation.</param>
    /// <param name="summary">One sentence for discovery, or <see langword="null"/>.</param>
    /// <returns>The function, with the Condition and Mutate profiles.</returns>
    /// <exception cref="ArgumentException">The name or the delegate cannot be catalogued.</exception>
    internal static CelFunction Create(string name, Delegate function, string? summary)
    {
        EnsureValidName(name);
        ArgumentNullException.ThrowIfNull(function);
        var invoke = InvokeMethodOf(function, name);
        var (result, resultNullable) = Result(function, invoke, name);

        return new CelFunction(
            name, Parameters(function, invoke, name), result, resultNullable, summary ?? string.Empty,
            IsHost: true, CelBuiltInFunctions.ConditionAndMutate,
            arguments => invoke.Invoke(function, BindingFlags.DoNotWrapExceptions, binder: null, arguments, culture: null));
    }

    private static void EnsureValidName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!NamePattern().IsMatch(name))
        {
            throw new ArgumentException(
                $"'{name}' is not a CEL function name: start with a lower-case ASCII letter and continue with ASCII "
                + "letters, digits or '_', for example normalizePhone or vat_rate.",
                nameof(name));
        }

        if (ReservedReason(name) is { } reason)
        {
            throw new ArgumentException($"'{name}' cannot name a host function: {reason}.", nameof(name));
        }
    }

    private static string? ReservedReason(string name) => name switch
    {
        "has" or "changed" => "it is one of Alvo's own CEL macros",
        "in" or "true" or "false" or "null" => "it is a CEL keyword",
        "old" or "new" => "it names a row image (old./new.)",
        "all" or "exists" or "exists_one" or "map" or "filter" => "it is a CEL comprehension macro, which no Alvo profile admits",
        "as" or "break" or "const" or "continue" or "else" or "for" or "function" or "if" or "import" or "let"
            or "loop" or "package" or "namespace" or "return" or "var" or "void" or "while" => "it is a word the CEL specification reserves",
        _ when CelFunctionCatalog.BuiltIns.Contains(name) => "it is a built-in function",
        _ => null,
    };

    private static MethodInfo InvokeMethodOf(Delegate function, string name)
    {
        var targets = function.GetInvocationList().Length;
        if (targets != 1)
        {
            throw Refused(name, $"was given a delegate that calls {targets} methods; a CEL function is one method");
        }

        return function.Method.ContainsGenericParameters
            ? throw Refused(name, "is an open generic method; close it over concrete types")
            : function.GetType().GetMethod("Invoke")!;
    }

    private static List<CelFunctionArgument> Parameters(Delegate function, MethodInfo invoke, string name)
    {
        var signature = invoke.GetParameters();
        if (signature.Length > MaxParameters)
        {
            throw Refused(name, $"takes {signature.Length} parameters; a CEL function takes at most {MaxParameters}");
        }

        var declared = function.Method.GetParameters();
        var annotated = declared.Length == signature.Length ? declared : signature;
        return [.. signature.Select((parameter, index) => Parameter(parameter, annotated[index], name))];
    }

    private static CelFunctionArgument Parameter(ParameterInfo parameter, ParameterInfo annotated, string name)
    {
        var clr = parameter.ParameterType;
        if (clr.IsByRef || TypeOf(clr) is not { } type)
        {
            throw Refused(name, $"has parameter '{annotated.Name}' of type {clr.Name}; {SupportedTypes}");
        }

        return new CelFunctionArgument(annotated.Name ?? $"arg{parameter.Position}", type, IsNullable(clr, annotated), Underlying(clr));
    }

    private static (CelValueType Type, bool Nullable) Result(Delegate function, MethodInfo invoke, string name)
    {
        var returned = invoke.ReturnType;
        if (returned == typeof(void))
        {
            throw Refused(name, "returns nothing; a CEL function returns a value");
        }

        if (IsAsynchronous(returned))
        {
            throw Refused(name, "is asynchronous; a CEL function runs inside the write's transaction and cannot await — do I/O in an after-hook instead");
        }

        var type = TypeOf(returned) ?? throw Refused(name, $"returns {returned.Name}; {SupportedTypes}");
        var annotated = function.Method.ReturnType == returned ? function.Method.ReturnParameter : invoke.ReturnParameter;
        return (type, IsNullable(returned, annotated));
    }

    private static bool IsAsynchronous(System.Type type) =>
        typeof(Task).IsAssignableFrom(type) || type == typeof(ValueTask)
        || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>));

    private static CelValueType? TypeOf(System.Type clr) => Underlying(clr) switch
    {
        var t when t == typeof(string) => CelValueType.String,
        var t when t == typeof(long) || t == typeof(int) => CelValueType.Int,
        var t when t == typeof(decimal) => CelValueType.Decimal,
        var t when t == typeof(bool) => CelValueType.Bool,
        var t when t == typeof(DateTimeOffset) => CelValueType.Timestamp,
        var t when t == typeof(Guid) => CelValueType.Uuid,
        _ => (CelValueType?)null,
    };

    private static System.Type Underlying(System.Type clr) => Nullable.GetUnderlyingType(clr) ?? clr;

    private static bool IsNullable(System.Type clr, ParameterInfo annotated) =>
        Nullable.GetUnderlyingType(clr) is not null
        || (clr == typeof(string) && new NullabilityInfoContext().Create(annotated).ReadState == NullabilityState.Nullable);

    private static ArgumentException Refused(string name, string reason) => new($"The CEL function '{name}' {reason}.", "function");

    /// <summary>R1's pattern, anchored with <c>\z</c>: <c>$</c> would admit a trailing newline in .NET.</summary>
    [GeneratedRegex(@"^[a-z][a-zA-Z0-9_]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
```

**Verify first** (spec §13, unverified): `NullabilityInfoContext` reads a lambda parameter's `string?`. If `Every_supported_type_is_described…` fails only on the `string? n` element, the compiler emitted no nullable metadata for the lambda: change that one registration to a static local method group (`static string? Echo(bool b, DateTimeOffset t, Guid g, string? n) => n;`), keep the lambda case as a separate fact asserting what was observed, and record it in the spec's *As built*. Do not drop the method-group assertion.

`CelFunctionRegistration.cs`:

```csharp
namespace MMLib.Alvo.Expressions.Internal;

/// <summary>One host function, as <c>AddCelFunction</c> registers it; the catalog collects every instance.</summary>
/// <param name="Function">The catalogued function, already validated.</param>
internal sealed record CelFunctionRegistration(CelFunction Function);
```

- [ ] **Step 6: Registration API and DI**

In `src/MMLib.Alvo/AlvoBuilderExtensions.cs` add `using MMLib.Alvo.Expressions.Internal;` and:

```csharp
    /// <summary>
    /// Registers a host function the descriptor's CEL may call in a hook <c>condition</c> and a before-hook
    /// <c>mutate</c> value — and nowhere else: rules and computed fields are evaluated by the database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Up to four parameters of <see cref="string"/>, <see cref="long"/>, <see cref="int"/>, <see cref="decimal"/>,
    /// <see cref="bool"/>, <see cref="DateTimeOffset"/> or <see cref="Guid"/> (or a nullable one), and a result of the
    /// same set. A null argument for a non-nullable parameter makes the call null without invoking the function.
    /// Everything is checked here, at the call, so a mistake fails startup rather than an apply.
    /// </para>
    /// <para>
    /// <b>The function is host code inside the write's transaction.</b> It must be synchronous, fast, thread-safe and
    /// free of side effects; it runs once per evaluation with no timeout. If it throws, nothing is written and the caller
    /// receives <c>…/errors/function-failed</c>. It captures what it closes over for the host's lifetime (no DI scope).
    /// A changed meaning deserves a new name, so stored descriptors keep theirs. Only this host knows the function: the
    /// standalone image and the CLI refuse a descriptor that calls it as an unknown function.
    /// </para>
    /// </remarks>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="name">The CEL name: a lower-case ASCII letter, then ASCII letters, digits or <c>_</c>.</param>
    /// <param name="function">The implementation, e.g. <c>(string phone) =&gt; …</c>.</param>
    /// <param name="summary">One sentence for discovery (<c>cel/functions</c>, the assistant).</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentException">The name is invalid, reserved or taken, or the delegate cannot be called from CEL.</exception>
    public static IAlvoBuilder AddCelFunction(this IAlvoBuilder builder, string name, Delegate function, string? summary = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var registration = new CelFunctionRegistration(HostCelFunction.Create(name, function, summary));
        EnsureNotRegistered(builder.Services, name);
        builder.Services.AddSingleton(registration);

        return builder;
    }

    /// <summary>Refuses a second registration of <paramref name="name"/>; keyed descriptors are skipped (reading their instance throws).</summary>
    private static void EnsureNotRegistered(IServiceCollection services, string name)
    {
        if (services.Any(descriptor => !descriptor.IsKeyedService
            && descriptor.ImplementationInstance is CelFunctionRegistration registered && registered.Function.Name == name))
        {
            throw new ArgumentException($"A CEL function named '{name}' is already registered; register each name once.", nameof(name));
        }
    }
```

In `src/MMLib.Alvo/Expressions/Setup.cs` replace the compiler line with:

```csharp
        services.TryAddSingleton(provider => CelFunctionCatalog.BuiltIns.With(
            provider.GetServices<CelFunctionRegistration>().Select(registration => registration.Function)));
        services.TryAddSingleton<ICelCompiler>(provider => new CelCompiler(provider.GetRequiredService<CelFunctionCatalog>()));
```

and update the summary to mention the catalog.

- [ ] **Step 7: Run — expect PASS except the public-API approvals**

Run: `dotnet build`, then `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelFunctionRegistrationTests' --filter-class '*ExpressionsSetupTests' --filter-class '*CelAcceptanceCorpusTests'`
Expected: PASS. Then `dotnet test --project test/MMLib.Alvo.Abstractions.Tests` and `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*PublicApi*'` — expected FAIL with a `.received.txt` each.

- [ ] **Step 8: Approve the baselines deliberately** — copy each `.received.txt` over its `.verified.txt` and read the diff. Abstractions must add **exactly**: `CelFunctionInfo` (7 properties), `CelFunctionParameter` (3), `CelFunctionProvenance` (2 members + its attribute), and one `[JsonConverter(...)]` line on each of `CelValueType` and `CelProfile`. Core must add **exactly** `AddCelFunction`. Anything else is a mistake to undo. Write the justification (spec §5.10 table) into the commit body; the Stop hook will ask for it.

- [ ] **Step 9: Normalise, ring0, commit**

```bash
for p in src/MMLib.Alvo/Expressions/Internal/HostCelFunction.cs src/MMLib.Alvo/Expressions/Internal/CelFunctionRegistration.cs src/MMLib.Alvo.Abstractions/Expressions/CelFunctionInfo.cs test/MMLib.Alvo.Tests/Expressions/CelFunctionRegistrationTests.cs; do python3 -c "p='$p';b=open(p,'rb').read().replace(b'\r\n',b'\n').replace(b'\n',b'\r\n');b=b if b.startswith(b'\xef\xbb\xbf') else b'\xef\xbb\xbf'+b;open(p,'wb').write(b)"; done
scripts/test-ring0
git add src/MMLib.Alvo/Expressions src/MMLib.Alvo/AlvoBuilderExtensions.cs src/MMLib.Alvo.Abstractions/Expressions test/MMLib.Alvo.Tests/Expressions/CelFunctionRegistrationTests.cs test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(cel): AddCelFunction registers a host function, validated at the call, discoverable as CelFunctionInfo" -m "Public API: CelFunctionInfo/CelFunctionParameter/CelFunctionProvenance are what IAlvoManagement will return (Admin and Ai reach functions only there); the JsonConverter attributes make CelValueType/CelProfile serialize as names; AddCelFunction is the registration API. The catalog itself stays internal (spec X1)." -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 8: a failing function refuses the write — `function-failed`

**Files:**
- Modify: `src/MMLib.Alvo/Api/AlvoProblemTypes.cs` (const + `All`)
- Modify: `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs` (`FunctionFailed`)
- Modify: `src/MMLib.Alvo/Api/Internal/AlvoExceptionHandler.cs` (`AnswerAsync` arm, `AnswerFunctionFailureAsync`, log message)
- Modify: `src/MMLib.Alvo.Abstractions/Rules/IBeforeHookRunner.cs`, `src/MMLib.Alvo.Abstractions/Expressions/IPredicateEvaluator.cs` (remarks only), `src/MMLib.Alvo/Rules/Internal/BeforeHookRunner.cs` (`Fires` remarks)
- Modify: `test/MMLib.Alvo.Api.Tests/ProblemDetailsTests.cs` (`EveryFactoryResult`, the reachability fact, a probe)
- Modify: `test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.verified.txt` (the `type` enum gains one URI — judged by `alvo-snapshot-judge`)
- Modify: `test/MMLib.Alvo.Tests/Rules/BeforeHookIsolationArchitectureTests.cs` (one fact + remarks)
- Modify: `docs/architecture/data-api.md` (problem table, near `:1001`)
- Create: `test/MMLib.Alvo.Api.Tests/descriptors/cel-functions.alvo.json`, `test/MMLib.Alvo.Api.Tests/CelFunctionsWorld.cs`
- Test: `test/MMLib.Alvo.Api.Tests/CelFunctionWriteTests.cs` (create)

**Interfaces:**
- Consumes: `AddCelFunction` (Task 7), `CelFunctionException` (Task 5), `AlvoApiWorld.FromDescriptorAsync(string, IReadOnlyList<TestApiKey>?, AlvoApiWorldSetup?)`, `AlvoApiWorldSetup(MapAlvoProblemDetails:, ConfigureServicesAfterAlvo:)` (`test/_shared/api/AlvoApiWorld.cs:756-863`).
- Produces: `public const string AlvoProblemTypes.FunctionFailed = "function-failed"`; `internal static IResult ProblemResultFactory.FunctionFailed(string detail)`; `internal static string AlvoExceptionHandler.FunctionFailedDetail(CelFunctionException)`; test helper `CelFunctionsWorld { Project, Summary, Writer, StartAsync(Func<string, string?>), Register(IServiceCollection, Func<string, string?>) }` — reused by Tasks 9 and 10.

- [ ] **Step 1: The fixture and the world**

`test/MMLib.Alvo.Api.Tests/descriptors/cel-functions.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "cel-functions",
  "description": "One entity whose before-create hook writes a host function's result: normalizePhone(new.phone) into phone_normalized. Every world over this descriptor registers normalizePhone, or the descriptor is refused as calling an unknown function — which is the embedded-only property the fixture exists to exercise.",
  "auth": {
    "providers": ["local"],
    "roles": ["writer"]
  },
  "access": {
    "viewer": "'writer' in @user.roles"
  },
  "entities": {
    "contacts": {
      "description": "A contact; its phone is normalised by the host as it is written.",
      "fields": {
        "phone": { "type": "string", "maxLength": 40 },
        "phone_normalized": { "type": "string", "maxLength": 40 }
      },
      "rules": {
        "list": "true",
        "get": "true",
        "create": "true",
        "update": "true",
        "delete": "true"
      },
      "hooks": {
        "beforeCreate": [
          {
            "action": {
              "mutate": {
                "phone_normalized": { "$cel": "normalizePhone(new.phone)" }
              }
            }
          }
        ]
      }
    }
  }
}
```

`test/MMLib.Alvo.Api.Tests/CelFunctionsWorld.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.Api.Tests;

/// <summary>A host over <c>cel-functions.alvo.json</c> with <c>normalizePhone</c> registered as the fact needs it.</summary>
internal static class CelFunctionsWorld
{
    /// <summary>The project's name, as the descriptor declares it.</summary>
    internal const string Project = "cel-functions";

    /// <summary>The summary the host registers, so discovery can be compared with it.</summary>
    internal const string Summary = "Keeps the digits and a leading plus of a phone number.";

    /// <summary>Gets a key that may read and write contacts.</summary>
    internal static TestApiKey Writer { get; } = new("contacts-writer", ["writer"], ["contacts:read", "contacts:write"]);

    /// <summary>Starts the world with Alvo's problem documents mapped.</summary>
    /// <param name="normalizePhone">What the host's <c>normalizePhone</c> does.</param>
    internal static Task<AlvoApiWorld> StartAsync(Func<string, string?> normalizePhone) =>
        AlvoApiWorld.FromDescriptorAsync(
            "cel-functions.alvo.json",
            [Writer],
            new AlvoApiWorldSetup(MapAlvoProblemDetails: true, ConfigureServicesAfterAlvo: services => Register(services, normalizePhone)));

    /// <summary>Registers <c>normalizePhone</c> into <paramref name="services"/>, as a host's <c>AddAlvo().AddCelFunction(…)</c> would.</summary>
    /// <param name="services">The host's services, after <c>AddAlvo</c>.</param>
    /// <param name="normalizePhone">The implementation.</param>
    internal static void Register(IServiceCollection services, Func<string, string?> normalizePhone) =>
        new Builder(services).AddCelFunction("normalizePhone", (string phone) => normalizePhone(phone), Summary);

    /// <summary>The builder a host holds; <c>AddCelFunction</c> reads only <see cref="Services"/>.</summary>
    private sealed class Builder(IServiceCollection services) : IAlvoBuilder
    {
        /// <inheritdoc/>
        public IServiceCollection Services { get; } = services;
    }
}
```

- [ ] **Step 2: Write the failing HTTP tests** — `test/MMLib.Alvo.Api.Tests/CelFunctionWriteTests.cs`:

```csharp
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>A host function inside a before-hook: it shapes the row, and when it fails nothing is written.</summary>
public sealed class CelFunctionWriteTests
{
    private static JsonObject Contact(string? phone) => phone is null ? new JsonObject() : new JsonObject { ["phone"] = phone };

    [Fact]
    public async Task A_host_function_in_a_before_hook_mutate_shapes_the_stored_row()
    {
        await using var world = await CelFunctionsWorld.StartAsync(phone => string.Concat(phone.Where(c => char.IsAsciiDigit(c) || c == '+')));

        using var created = await world.SendAsync(HttpMethod.Post, "/api/contacts", CelFunctionsWorld.Writer, body: Contact("+421 900 123 456"));

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await created.ReadJsonObjectAsync())["phone_normalized"]!.GetValue<string>().ShouldBe("+421900123456");
    }

    [Fact]
    public async Task A_throwing_host_function_refuses_the_write_and_stores_nothing()
    {
        await using var world = await CelFunctionsWorld.StartAsync(_ => throw new FormatException("secret-detail-from-the-host"));

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/contacts", CelFunctionsWorld.Writer, body: Contact("x"));

        refused.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var text = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.ShouldContain("https://alvo.dev/errors/function-failed");
        text.ShouldContain("normalizePhone");
        text.ShouldNotContain("secret-detail-from-the-host");
        text.ShouldNotContain(nameof(FormatException));
        using var list = await world.SendAsync(HttpMethod.Get, "/api/contacts", CelFunctionsWorld.Writer);
        (await list.ReadJsonObjectAsync())["items"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_function_that_answers_null_stores_null()
    {
        await using var world = await CelFunctionsWorld.StartAsync(_ => null);

        using var created = await world.SendAsync(HttpMethod.Post, "/api/contacts", CelFunctionsWorld.Writer, body: Contact("x"));

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await created.ReadJsonObjectAsync())["phone_normalized"].ShouldBeNull();
    }

    [Fact]
    public async Task A_missing_phone_is_never_passed_to_a_function_that_takes_no_null()
    {
        var calls = 0;
        await using var world = await CelFunctionsWorld.StartAsync(phone => { Interlocked.Increment(ref calls); return phone; });

        using var created = await world.SendAsync(HttpMethod.Post, "/api/contacts", CelFunctionsWorld.Writer, body: Contact(null));

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        calls.ShouldBe(0);
    }
}
```

- [ ] **Step 3: Run — expect FAIL** (the throwing case answers `…/errors/internal`)

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*CelFunctionWriteTests'`
Expected: `A_throwing_host_function_…` fails on `function-failed`; the other three pass.

- [ ] **Step 4: Implement**

`AlvoProblemTypes.cs` — after `Internal`:

```csharp
    /// <summary>A CEL function failed while the write was evaluated, so nothing was written (500).</summary>
    /// <remarks>
    /// Distinct from <see cref="Internal"/> because the failing party is a function — host code registered with
    /// <c>AddCelFunction</c>, or a built-in that refused (an overflow, a result too long) — not an Alvo invariant. The
    /// detail names the function (descriptor-authored) and, for a built-in, Alvo's own reason; never the host's
    /// exception text, which is the log's. Emitted only by <c>AlvoExceptionHandler</c>.
    /// </remarks>
    public const string FunctionFailed = "function-failed";
```

and add `FunctionFailed,` after `Internal,` in `All`.

`ProblemResultFactory.cs` — after `Internal()`:

```csharp
    /// <summary>The 500 for a CEL function that failed during the write; the write was rolled back.</summary>
    /// <param name="detail">Names the function, and a built-in's reason; never the host's exception text.</param>
    /// <returns>The problem result.</returns>
    internal static IResult FunctionFailed(string detail) =>
        Problem(StatusCodes.Status500InternalServerError, AlvoProblemTypes.FunctionFailed, detail);
```

`AlvoExceptionHandler.cs` — add `using MMLib.Alvo.Expressions.Internal;`; in `AnswerAsync`, after the `BadHttpRequestException` block:

```csharp
        if (exception is CelFunctionException function)
        {
            await AnswerFunctionFailureAsync(httpContext, function).ConfigureAwait(false);
            return;
        }
```

and:

```csharp
    /// <summary>
    /// Answers a CEL function's failure: logged at Error with the host's exception (its stack is the operator's), and a
    /// <c>function-failed</c> document that names the function and never the host's text.
    /// </summary>
    /// <param name="httpContext">The failed request's context.</param>
    /// <param name="failure">The function's failure.</param>
    private async Task AnswerFunctionFailureAsync(HttpContext httpContext, CelFunctionException failure)
    {
        FunctionFailed(logger, failure.InnerException ?? failure, failure.FunctionName, httpContext.Request.Method, httpContext.Request.Path.Value);
        await ProblemResultFactory.FunctionFailed(FunctionFailedDetail(failure)).ExecuteAsync(httpContext).ConfigureAwait(false);
    }

    /// <summary>The caller-facing sentence for a function failure.</summary>
    /// <param name="failure">The failure.</param>
    /// <returns>The detail text.</returns>
    internal static string FunctionFailedDetail(CelFunctionException failure) => failure.Reason is { } reason
        ? $"The CEL function '{failure.FunctionName}' failed: {reason}. Nothing was written."
        : $"The CEL function '{failure.FunctionName}' failed while this write was evaluated, so nothing was written. Its own error is in the server log.";

    /// <summary>The function-failure record, with the host's exception attached.</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "The CEL function {Function} failed during {Method} {Path}; the write was rolled back.")]
    private static partial void FunctionFailed(ILogger logger, Exception exception, string function, string method, string? path);
```

Documentation-only edits (no signature changes):
- `IBeforeHookRunner.cs`: in the time-bound paragraph replace "no user-defined function" with "no user-defined function of the descriptor's own; a host-registered function (`AddCelFunction`) is host code the host vouches for — synchronous and once per evaluation, but bounded only by the host's own contract"; add `/// <exception cref="Exception">A CEL function failed (an internal fail-closed type); the write must not proceed.</exception>` with that wording to `Run`.
- `IPredicateEvaluator.cs`: one sentence on `Evaluate` — a Condition expression that calls a failing CEL function throws (an internal type) rather than answering `false`.
- `BeforeHookRunner.cs` `Fires` remarks: replace the "unreachable half" and "obligation" paragraphs' claim that nothing can throw with: *a function failure is not collapsed: `CelInterpreter.EvaluatePredicate` lets it escape and the write is refused — fail closed; the open direction remains only for the two-valued null rule.* Keep the deviation-84 reference and say it is now discharged.

`BeforeHookIsolationArchitectureTests.cs` — add to the remarks the spec §5.8 sentence (descriptor author: inexpressible; host developer: host code), and the fact:

```csharp
    /// <summary>
    /// A host function reaches a hook through the compiled tree (the overload bound into the call node), never as a
    /// dependency of the runner — so the walk above still measures everything the runner itself can reach.
    /// </summary>
    [Fact]
    public void A_host_function_reaches_a_hook_through_the_compiled_tree_not_the_runners_constructor() =>
        typeof(BeforeHookRunner).GetConstructors().ShouldHaveSingleItem().GetParameters()
            .Select(parameter => parameter.ParameterType).ShouldBe([typeof(IPolicyCatalogProvider)]);
```

`ProblemDetailsTests.cs`: add `ProblemResultFactory.FunctionFailed("A CEL function failed."),` after `ProblemResultFactory.Internal(),` in `EveryFactoryResult`; add `reached.Add(await FunctionFailedSlugAnsweredByAThrowingHostFunctionAsync());` after the internal probe in `Only_the_slugs_awaiting_a_later_task_are_unreachable_over_http`; add:

```csharp
    /// <summary>
    /// The <c>function-failed</c> slug's probe: its own world, because only a host that registered a throwing CEL
    /// function can produce it.
    /// </summary>
    private static async Task<string> FunctionFailedSlugAnsweredByAThrowingHostFunctionAsync()
    {
        await using var world = await CelFunctionsWorld.StartAsync(_ => throw new InvalidOperationException("probe"));

        return await SlugAnsweredByAsync(
            world, new Probe(HttpMethod.Post, "/api/contacts", CelFunctionsWorld.Writer, new JsonObject { ["phone"] = "x" }));
    }
```

`docs/architecture/data-api.md`: add after the `internal` row: `| 500 | \`function-failed\` | a CEL function failed while a write was evaluated (a host function threw, or a built-in refused) — nothing was written; the detail names the function, never the host's exception text; same opt-in as \`internal\` |`, and extend the sentence above the table that lists the handler-only slugs.

- [ ] **Step 5: Run — expect PASS except the OpenAPI snapshot**

Run: `dotnet build`, then `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*CelFunctionWriteTests' --filter-class '*ProblemDetailsTests'` and `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*BeforeHookIsolationArchitectureTests'`
Expected: PASS. Then `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*OpenApiDocumentTests'` — `The_document_is_stable` FAILS with a received file whose only change is `https://alvo.dev/errors/function-failed` in the problem `type` enum. Accept it (copy received over verified); the `alvo-snapshot-judge` gate will review it against this task. Also the core public-API baseline: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*PublicApi*'` fails with exactly `FunctionFailed` added — approve it.

- [ ] **Step 6: Normalise, ring1, commit**

```bash
for p in test/MMLib.Alvo.Api.Tests/CelFunctionsWorld.cs test/MMLib.Alvo.Api.Tests/CelFunctionWriteTests.cs; do python3 -c "p='$p';b=open(p,'rb').read().replace(b'\r\n',b'\n').replace(b'\n',b'\r\n');b=b if b.startswith(b'\xef\xbb\xbf') else b'\xef\xbb\xbf'+b;open(p,'wb').write(b)"; done
scripts/test-ring1
git add src/MMLib.Alvo/Api src/MMLib.Alvo.Abstractions/Rules/IBeforeHookRunner.cs src/MMLib.Alvo.Abstractions/Expressions/IPredicateEvaluator.cs src/MMLib.Alvo/Rules/Internal/BeforeHookRunner.cs test/MMLib.Alvo.Api.Tests/descriptors/cel-functions.alvo.json test/MMLib.Alvo.Api.Tests/CelFunctionsWorld.cs test/MMLib.Alvo.Api.Tests/CelFunctionWriteTests.cs test/MMLib.Alvo.Api.Tests/ProblemDetailsTests.cs test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.verified.txt test/MMLib.Alvo.Tests/Rules/BeforeHookIsolationArchitectureTests.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt docs/architecture/data-api.md
git commit -m "feat(api): a failing CEL function rolls the write back and answers function-failed" -m "Public API: AlvoProblemTypes.FunctionFailed is the distinct problem type an agent branches on (spec R4). The OpenAPI snapshot gains the one type URI." -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 9: `cel/functions` — the Management read end to end

Template: `docs/superpowers/plans/2026-10-01-f5-expression-check.md` Task 2 (read it; this follows it for a GET).

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Management/IAlvoManagement.cs` (member after `GetCapabilitiesAsync`, `:115`)
- Modify: `src/MMLib.Alvo/Management/Access/ManagementOperation.cs` (`GetCelFunctions` after `GetCapabilities`)
- Modify: `src/MMLib.Alvo/Management/Access/ManagementOperations.cs` (`[ManagementOperation.GetCelFunctions] = ManagementLevel.Viewer,` after the `GetCapabilities` line)
- Modify: `src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs` (primary-ctor parameter `CelFunctionCatalog functions` after `IDescriptorValidator validator`, + member after `GetCapabilitiesAsync`, `:193`)
- Modify: `src/MMLib.Alvo/Management/Setup.cs:96-113` (pass `provider.GetRequiredService<Expressions.Internal.CelFunctionCatalog>()` after the validator)
- Modify: `src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs` (`MapCelFunctions(group);` after `MapCapabilities(group);` at `:59`, + method)
- Modify: `test/MMLib.Alvo.Api.Tests/Management/ManagementAccessTests.cs` (`RefusingManagement`, `:312`), `test/MMLib.Alvo.Admin.Tests.EndToEnd/ManagementDecorator.cs` (`:51`), `test/MMLib.Alvo.Admin.Tests.EndToEnd/KeptFollowScenarios.cs` (`Holding`, `:103`)
- Modify: `test/MMLib.Alvo.Tests/Management/ManagementOperationsTests.cs` (viewer list `:46-56`; count fact `:92` → sixteen)
- Modify: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`
- Modify: `docs/architecture/management-api.md` (table row after `:25`; a short section)
- Test: `test/MMLib.Alvo.Api.Tests/Management/ManagementCelFunctionsTests.cs` (create)

**Interfaces:**
- Consumes: `CelFunctionCatalog.Describe()` (Task 7), `CelFunctionsWorld` (Task 8), `ManagedFleet` (`test/MMLib.Alvo.Api.Tests/Management/ManagedFleet.cs`), `ManagementInProcessAccessTests.Publish(AlvoApiWorld, string)` (`:211`), `ReadJsonArrayAsync`.
- Produces: `Task<IReadOnlyList<CelFunctionInfo>> IAlvoManagement.GetCelFunctionsAsync(string project, CancellationToken ct = default)`; route `GET {m}/projects/{project}/cel/functions`; `ManagementOperation.GetCelFunctions` (Viewer).

- [ ] **Step 1: Write the failing tests**

```csharp
using MMLib.Alvo.Expressions;
using System.Net;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary><c>GET {m}/projects/{p}/cel/functions</c> — every function a descriptor may call here, with its signature.</summary>
public sealed class ManagementCelFunctionsTests
{
    private static readonly TestApiKey _ops = new("mgmt-ops", ["ops"], ["*:read"]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_viewer_reads_the_built_ins_with_their_signatures_profiles_and_nullability()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var functions = await (await world.SendAsync(HttpMethod.Get, $"{ManagedFleet.Routes}/cel/functions", _ops)).ReadJsonArrayAsync();

        functions.Select(f => f!["name"]!.GetValue<string>()).Distinct().ShouldBe(["abs", "lowerAscii", "now", "replace", "round", "size", "trim"]);
        functions.Count(f => f!["name"]!.GetValue<string>() == "abs").ShouldBe(2);
        var trim = functions.Single(f => f!["name"]!.GetValue<string>() == "trim")!;
        trim["result"]!.GetValue<string>().ShouldBe("String");
        trim["provenance"]!.GetValue<string>().ShouldBe("BuiltIn");
        trim["profiles"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldBe(["Condition", "Mutate"]);
        trim["parameters"]![0]!["acceptsNull"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task An_unknown_project_is_a_404()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        using var response = await world.SendAsync(HttpMethod.Get, "/management/projects/nope/cel/functions", _ops);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_host_function_is_listed_as_the_host_registered_it()
    {
        await using var world = await CelFunctionsWorld.StartAsync(phone => phone);
        var management = ManagementInProcessAccessTests.Publish(world, "writer");

        var host = (await management.GetCelFunctionsAsync(CelFunctionsWorld.Project, Ct)).Single(f => f.Provenance == CelFunctionProvenance.Host);

        host.Name.ShouldBe("normalizePhone");
        host.Summary.ShouldBe(CelFunctionsWorld.Summary);
        host.Parameters.ShouldHaveSingleItem().Name.ShouldBe("phone");
        host.Result.ShouldBe(CelValueType.String);
        host.Profiles.ShouldBe([CelProfile.Condition, CelProfile.Mutate]);
    }
}
```

(If `ManagementInProcessAccessTests.Publish` is not accessible from this namespace, it lives in the same test project; use its full name.)

- [ ] **Step 2: Run — expect build failure** (`GetCelFunctionsAsync` missing)

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*ManagementCelFunctionsTests'`

- [ ] **Step 3: Contract** — in `IAlvoManagement.cs` after `GetCapabilitiesAsync` (add `using MMLib.Alvo.Expressions;` if absent):

```csharp
    /// <summary>
    /// Every CEL function a descriptor may call on this instance — the built-ins and the host's registrations — one
    /// entry per overload, with parameters, result, nullability and the profiles each compiles in.
    /// </summary>
    /// <remarks>
    /// The list is the instance's: a host function exists only in the host that registered it, so a descriptor that
    /// calls one is refused as an unknown function by the standalone image and the CLI. Read it before writing a call.
    /// </remarks>
    /// <param name="project">The project name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The functions, ordered by name.</returns>
    /// <exception cref="ManagementForbiddenException">The caller does not reach this operation's level.</exception>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    Task<IReadOnlyList<CelFunctionInfo>> GetCelFunctionsAsync(string project, CancellationToken ct = default);
```

- [ ] **Step 4: Level and operation** — `ManagementOperation.cs` after `GetCapabilities`:

```csharp
    /// <summary>Read the CEL functions a descriptor may call: built-ins and host registrations.</summary>
    GetCelFunctions,
```

`ManagementOperations.cs`: `[ManagementOperation.GetCelFunctions] = ManagementLevel.Viewer,`. `ManagementOperationsTests`: add `ManagementOperation.GetCelFunctions,` after `GetCapabilities` in the viewer list, and change `The_management_surface_is_fifteen_operations` to `The_management_surface_is_sixteen_operations` with `ShouldBe(16)` and "sixteen"/"seventeenth" in its summary.

- [ ] **Step 5: Service** — add the primary-constructor parameter `CelFunctionCatalog functions,` after `IDescriptorValidator validator,` with `/// <param name="functions">The CEL functions this instance knows; what <see cref="GetCelFunctionsAsync"/> lists.</param>`, pass it in `Setup.cs` at the same position, and add after `GetCapabilitiesAsync`:

```csharp
    /// <inheritdoc/>
    public Task<IReadOnlyList<CelFunctionInfo>> GetCelFunctionsAsync(string project, CancellationToken ct = default)
    {
        EnsureMayPerform(ManagementOperation.GetCelFunctions);
        EnsureServed(project);

        return Task.FromResult(functions.Describe());
    }
```

- [ ] **Step 6: Route** — in `ManagementEndpoints.cs`:

```csharp
    /// <summary>
    /// <c>GET {prefix}/projects/{project}/cel/functions</c> — <see cref="IAlvoManagement.GetCelFunctionsAsync"/>.
    /// </summary>
    /// <param name="group">The group to map into.</param>
    private static void MapCelFunctions(RouteGroupBuilder group) =>
        Gate(
            group.MapGet(
                "/projects/{project}/cel/functions",
                (string project, IAlvoManagement management, CancellationToken ct) =>
                    Answer(() => management.GetCelFunctionsAsync(project, ct))),
            new ManagementRoute(nameof(IAlvoManagement.GetCelFunctionsAsync), ManagementOperation.GetCelFunctions));
```

- [ ] **Step 7: The three doubles** — copy each file's `GetCapabilitiesAsync` shape exactly:
  - `RefusingManagement`: `public Task<IReadOnlyList<CelFunctionInfo>> GetCelFunctionsAsync(string project, CancellationToken ct = default) => throw new ManagementForbiddenException();` with `/// <inheritdoc/>`.
  - `ManagementDecorator`: `public virtual Task<IReadOnlyList<CelFunctionInfo>> GetCelFunctionsAsync(string project, CancellationToken ct = default) => inner.GetCelFunctionsAsync(project, ct);`
  - `Holding`: `public Task<IReadOnlyList<CelFunctionInfo>> GetCelFunctionsAsync(string project, CancellationToken ct = default) => inner.GetCelFunctionsAsync(project, ct);`
  (each file gains `using MMLib.Alvo.Expressions;` if absent).

- [ ] **Step 8: Docs** — `management-api.md`: row `| \`GET {m}/projects/{project}/cel/functions\` | \`GetCelFunctionsAsync\` | \`viewer\` |` after the `cel/check` row, and a section *"Why `cel/functions` is Viewer and project-scoped"*: it discloses only names and summaries the host chose (like capabilities); the catalog is per instance, the route is per project for symmetry with `cel/check` and to keep per-project visibility possible without a route change; it lists one entry per overload.

- [ ] **Step 9: Run — expect PASS except the Abstractions baseline**

Run: `dotnet build`, then `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*ManagementCelFunctionsTests' --filter-class '*ManagementContractTests' --filter-class '*ManagementAccessTests'` and `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ManagementOperationsTests'`
Expected: PASS. `PublicApi.MMLib.Alvo.Abstractions` fails with exactly `GetCelFunctionsAsync` added — approve it. If a reflective test over `IAlvoManagement`'s members elsewhere (Admin, Ai) fails, follow its message: it is asking for the same member on another double.

- [ ] **Step 10: Normalise, ring1, commit**

```bash
python3 -c "p='test/MMLib.Alvo.Api.Tests/Management/ManagementCelFunctionsTests.cs';b=open(p,'rb').read().replace(b'\r\n',b'\n').replace(b'\n',b'\r\n');b=b if b.startswith(b'\xef\xbb\xbf') else b'\xef\xbb\xbf'+b;open(p,'wb').write(b)"
scripts/test-ring1
git add src/MMLib.Alvo.Abstractions/Management/IAlvoManagement.cs src/MMLib.Alvo/Management test/MMLib.Alvo.Api.Tests/Management test/MMLib.Alvo.Admin.Tests.EndToEnd/ManagementDecorator.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/KeptFollowScenarios.cs test/MMLib.Alvo.Tests/Management/ManagementOperationsTests.cs test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt docs/architecture/management-api.md
git commit -m "feat(management): cel/functions lists every function a descriptor may call, for viewers" -m "Public API: IAlvoManagement.GetCelFunctionsAsync (spec R5); returns the CelFunctionInfo records Task 7 published." -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 10: `cel/check` agrees with apply on functions

**Files:**
- Modify: `test/MMLib.Alvo.Api.Tests/Management/ExpressionCheckAgreementTests.cs` (`_cases` `:68-…`; `World.InitializeAsync` `:346-350`)

**Interfaces:**
- Consumes: `CelFunctionsWorld.Register(IServiceCollection, Func<string, string?>)` (Task 8); the file's own `Check_and_apply_refuse_the_same_expression_the_same_way` theory and `The_corpus_…` coverage fact (`:223-239`).
- Produces: nothing new — this task is evidence for spec R8 and criterion 1.

- [ ] **Step 1: Register the host function in the fixture's world**

```csharp
        public async ValueTask InitializeAsync() =>
            _world = await AlvoApiWorld.FromDescriptorAsync(
                "expression-agreement.alvo.json",
                [],
                new AlvoApiWorldSetup(
                    MapBeforePriming: true,
                    MapManagementApi: true,
                    ConfigureServicesAfterAlvo: services => CelFunctionsWorld.Register(services, phone => phone)));
```

(`CelFunctionsWorld` is in namespace `MMLib.Alvo.Api.Tests`; add the `using` if needed.)

- [ ] **Step 2: Add the cases** — append to `_cases`, keeping each kind's rows together:

```csharp
        ("rule", Orders + "/rules/list", "normalizePhone(note) == 'x'"),
        ("rule", Orders + "/rules/get", "trim(note) == 'x'"),
        ("beforeHook", BeforeCreate, "normalizePhone(new.note) == '1'"),
        ("beforeHook", BeforeCreate, "size(new.title) > 3"),
        ("beforeHook", BeforeCreate, "trim(old.note) == 'x'"),
        ("beforeHook", BeforeUpdate, "normalisePhone(new.note) == 'x'"),
        ("afterHook", AfterCreate, "normalizePhone(new.note) == 'x'"),
        ("mutate", Mutate + "note", "normalizePhone(new.note)"),
        ("mutate", Mutate + "note", "trim(replace(new.title, '-', ' '))"),
        ("mutate", Mutate + "note", "normalizePhone(new.note, new.note)"),
        ("mutate", Mutate + "quantity", "abs(new.quantity)"),
        ("mutate", Mutate + "quantity", "round(new.price)"),
        ("computed", Computed, "normalizePhone(note)"),
        ("computed", Computed, "round(price)"),
```

What each row exercises: a host and a built-in refused in a rule (one error each — Review Focus 10); host and built-in accepted in a before-hook condition; `old.` inside a call refused in `beforeCreate` (the phase walker reads call arguments through `CelTree.Children`); a typo refused with a "did you mean"; a host function in an after-hook condition; host, nested built-ins, wrong arity, Int into an integer field, Decimal into an integer field (the mutate type fit), and both refused in a computed field.

- [ ] **Step 3: Add one pin on the refusal's wording** (the theory asserts agreement, not text):

```csharp
    /// <summary>A host function in a rule is refused once, with the recipe that works — not with a misleading operand error.</summary>
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
```

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*ExpressionCheckAgreementTests' --filter-class '*ManagementExpressionCheckTests'`
Expected: PASS. A row where check and apply disagree is a defect in the core, not in the row: fix the core.

- [ ] **Step 5: ring1, commit**

```bash
scripts/test-ring1
git add test/MMLib.Alvo.Api.Tests/Management/ExpressionCheckAgreementTests.cs
git commit -m "test(management): cel/check and apply agree on host and built-in function calls in every slot" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 11: docs, skills, assistant tool, spec placeholder

**Files:**
- Modify: `docs/architecture/cel.md` (profile table `:21-36`; section `### The function allow-list has exactly two entries` `:135-168`; deviations `7` `:442-444`, the closing paragraph after `16` `:483-484`; add deviations 17–24)
- Modify: `.claude/skills/alvo-descriptor-hooks/SKILL.md` (`:23-38`), `.claude/skills/alvo-descriptor-rules-and-cel/SKILL.md` (one sentence), `.claude/skills/alvo-security-core-review/SKILL.md` (`:82-90`)
- Modify: `test/MMLib.Alvo.Host.Tests/SkillCoreClaimsTests.cs` (`:40` `_mutateFunctions`)
- Modify: `src/MMLib.Alvo.Ai/Internal/ManagementTools.cs` (tool + method), `src/MMLib.Alvo.Ai/Instructions/schema-assistant.md` (tools list `:17-24`, skills paragraph `:28-32`)
- Modify: `test/MMLib.Alvo.Ai.Tests/ManagementToolsTests.cs` (`:36-41`)
- Modify: `docs/superpowers/specs/2026-10-05-f5-cel-functions-design.md` §17 (*As built* — commits and ruled deviations)

**Interfaces:**
- Consumes: `CelFunctionCatalog.BuiltIns.Functions` (Tasks 2/6), `IAlvoManagement.GetCelFunctionsAsync` (Task 9).
- Produces: assistant tool `get_cel_functions`.

- [ ] **Step 1: Failing drift test** — in `SkillCoreClaimsTests.cs` replace `_mutateFunctions` with a property derived from the catalog:

```csharp
    /// <summary>The built-ins the catalog admits in a <c>mutate</c>, in its order — the region must list exactly these.</summary>
    private static IEnumerable<string> MutateFunctions => CelFunctionCatalog.BuiltIns.Functions
        .Where(function => function.Profiles.Contains(CelProfile.Mutate))
        .Select(function => function.Name)
        .Distinct(StringComparer.Ordinal);
```

and `The_mutate_functions_are_the_ones_the_profile_allow_lists` compares with `MutateFunctions`. In `ManagementToolsTests`, rename the fact to `The_tool_set_is_exactly_the_five_reads_and_the_two_dry_runs` and expect `["check_change", "get_capabilities", "get_cel_functions", "get_descriptor", "get_revisions", "get_schema", "propose_change"]`.

Run: `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*SkillCoreClaimsTests'` and `dotnet test --project test/MMLib.Alvo.Ai.Tests --filter-class '*ManagementToolsTests'`
Expected: FAIL (region lists two names; six tools).

- [ ] **Step 2: Skills** — in `alvo-descriptor-hooks/SKILL.md`:
  - `cel-condition` allowed line gains `` `size(new.description) > 3` ``.
  - Replace the paragraph before `<!-- gen:mutate-functions -->`, the region, and the `cel-mutate` allowed line with:

```markdown
A `mutate` value is a field, a literal, or a call to one of these built-in functions (calls may nest), and nothing
more: no `@user` or `@tenant`, no arithmetic, no joins. `lowerAscii` takes a field only; the others take any value
of the right type, and a null argument makes the value null.

<!-- gen:mutate-functions -->
`abs` `lowerAscii` `now` `replace` `round` `size` `trim`
<!-- /gen:mutate-functions -->

An embedded host may register its own functions; they work in a `condition` and a `mutate` and nowhere else. Call
`get_cel_functions` for this host's list with each function's parameters and result — never assume one exists. A
function whose meaning changes gets a new name (`vatRate` stays, `vatRate2` is new).
```

(No snake_case name may appear in a skill's code spans unless `AssistantInstructionsTests.KnownNames` knows it — `SkillConformanceTests.Every_snake_case_name_in_a_skills_code_is_a_known_name` — hence camelCase here.)

```markdown

<!-- gen:cel-mutate -->
- allowed: `now()` `lowerAscii(new.description)` `new.unit_price` `'part'` `trim(new.description)`
```

  - `alvo-descriptor-rules-and-cel/SKILL.md`, after the `cel-rule` region: "No function call works in a rule in this build — not `trim`, not a host function: a rule filters in SQL. To filter by a normalised value, write it into a field with a before-hook `mutate` and compare that field."
  - `alvo-security-core-review/SKILL.md` before-hook item: append "A host-registered CEL function (`AddCelFunction`) is host code: the descriptor author still cannot express a network call, the host developer can — verify any such function is synchronous, fast and side-effect-free, and that no new runner dependency appeared (spec 2026-10-05 §5.8)."

  Check `SkillConformanceTests` budgets (6,144 bytes, 200 lines per skill) still hold.

- [ ] **Step 3: Assistant tool** — in `ManagementTools` constructor, after `get_capabilities`:

```csharp
            AIFunctionFactory.Create(
                GetCelFunctionsAsync,
                "get_cel_functions",
                "The CEL functions this host knows — built-in and host-registered — with parameters, result and the profiles each works in."),
```

and the method after `GetCapabilitiesAsync`:

```csharp
    /// <summary>The CEL functions a descriptor may call here, so a call is written against the real list.</summary>
    private Task<string> GetCelFunctionsAsync(CancellationToken ct) =>
        AnsweredAsync(async () => Json(await _management.GetCelFunctionsAsync(_project, ct).ConfigureAwait(false)));
```

In `schema-assistant.md` add to *Your tools*, after `get_capabilities`: `` - `get_cel_functions` — the CEL functions this host knows, built-in and host-registered, with their parameters. `` and to the *Skills* paragraph: "Before you write a function call in a condition or a mutate, call `get_cel_functions`." Keep the addition to these two lines: `AlvoAssistantTests` holds an always-in-context budget (`AlwaysInContextBudget = 22_758`); if it fails, shorten your text, never the budget.

- [ ] **Step 4: `docs/architecture/cel.md`**
  - Profile table: replace the last row with two rows — `| Legacy call (`lowerAscii(field)`, `now()`) | ✗ | ✗ | ✗ | ✓ | ✗ |` and `| Catalogued function call (built-ins and host functions; each function's own profiles narrow this ceiling) | ✗ | ✗ | ✓ | ✓ | ✗ |`.
  - Replace the section *The function allow-list has exactly two entries* with *The function catalog*: the two legacy entries (keep the existing `lowerAscii`/`now()` paragraphs verbatim under a sub-heading), the five built-ins with the spec §6 table, host functions (§5.7 limits table, the trust sentence of §5.8), the two gates, overload resolution, null and failure policy, and the versioning convention (new semantics = new name). Link the spec.
  - Deviation 7: "any identifier immediately followed by `(` that is neither `has`/`changed` nor a catalogued function is refused…". The paragraph after 16 ("Neither is admitted outside `Mutate` …") now says the catalogued functions are admitted in Condition and Mutate.
  - Append deviations **17**–**24** = spec §11 F1, F3–F8 and the Int→Decimal widening, each one sentence with its reason (F2 is conformant and needs no number).

- [ ] **Step 5: Run — expect PASS**

Run: `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*SkillCoreClaimsTests' --filter-class '*SkillClaimTests' --filter-class '*InstructionExampleOutcomeTests'` and `dotnet test --project test/MMLib.Alvo.Ai.Tests`
Expected: PASS.

- [ ] **Step 6: Spec *As built*** — replace §17's placeholder with: the commit list (`git log --oneline 0527bb9..HEAD`), every deviation from this plan with its ruling (at least: whether the `NullabilityInfoContext` lambda check held, any test text you had to update), and anything deferred.

- [ ] **Step 7: ring1, commit**

```bash
scripts/test-ring1
git add docs/architecture/cel.md .claude/skills/alvo-descriptor-hooks/SKILL.md .claude/skills/alvo-descriptor-rules-and-cel/SKILL.md .claude/skills/alvo-security-core-review/SKILL.md test/MMLib.Alvo.Host.Tests/SkillCoreClaimsTests.cs src/MMLib.Alvo.Ai/Internal/ManagementTools.cs src/MMLib.Alvo.Ai/Instructions/schema-assistant.md test/MMLib.Alvo.Ai.Tests/ManagementToolsTests.cs docs/superpowers/specs/2026-10-05-f5-cel-functions-design.md
git commit -m "docs(cel): the function catalog in cel.md, the skills and the assistant's get_cel_functions" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 12: whole-slice verification

**Files:** none (fixes found here go into a commit naming what they fix).

- [ ] **Step 1:** `git diff --stat 0527bb9..HEAD -- test/MMLib.Alvo.Tests/Expressions/CelAcceptanceBaseline.jsonl` prints nothing (the corpus never moved).
- [ ] **Step 2:** `scripts/test-ring2` — all green. If two `PagingPerformanceTests` fail on an Npgsql *connect* timeout, read the trace before calling it a regression (known flake).
- [ ] **Step 3:** `dotnet build -c Release` — 0 warnings, 0 errors (CA analyzers fire only here).
- [ ] **Step 4:** Public baselines: `git diff 0527bb9..HEAD -- '*PublicApi*.verified.txt'` shows exactly spec §5.10 — Abstractions: `CelFunctionInfo`, `CelFunctionParameter`, `CelFunctionProvenance`, two `JsonConverter` attributes, `IAlvoManagement.GetCelFunctionsAsync`; core: `AddCelFunction`, `AlvoProblemTypes.FunctionFailed`. Nothing else.
- [ ] **Step 5:** Snapshots: the only moved `*.verified.*` besides the baselines is `OpenApiDocumentTests.The_document_is_stable.verified.txt` (one type URI).
- [ ] **Step 6:** Walk the spec §15 acceptance list and the Review Focus list; for each line name the test that pins it (in the PR report). Any line without a test is a missing test — add it to the owning task's test file and commit.
- [ ] **Step 7:** Grep for the old claims the slice made false, everywhere (not only the files this plan names): `rg -n "exactly two entries|allow-list has exactly|no user-defined function|Nothing in a .*(Mutate|Condition).* tree can throw|lowerAscii. .now.$" docs src .claude` — each hit is rewritten or justified.
- [ ] **Step 8:** Pre-PR gates (repo hard rules): `alvo-plan-guard` subagent; reviewer subagents as stand-ins for `/code-review medium` and `/security-review` (both are user-only commands here — label them substitutes), paired with the `alvo-security-core-review` checklist because the diff touches CEL and the hook path; then the `alvo-pr-report` skill. Label the PR `needs-deep-review`. Freeze the tree before dispatching reviewers; check the branch before pushing (a reviewer can switch it).
