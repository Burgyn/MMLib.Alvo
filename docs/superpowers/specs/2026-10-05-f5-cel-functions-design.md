# F5 — CEL functions: a minimal catalog, host functions and five built-ins (slices C1 + C2)

Status: design, 2026-10-05. Written autonomously from the controller's brief and rulings R1–R8; the brainstorming
approvals are **delegated, not given** — the gate that remains is the PR. Builds on
[expression check](2026-10-01-f5-expression-check-design.md) (slice A, PR #298), which is the decomposition this
continues (its §2 names this work C + D; this file renames them **C1** and **C2**).

Inputs, read in this order: the [automation analysis](2026-10-01-f5-automation-analysis.md) §2–3, §7 and its
[review](2026-10-01-f5-automation-analysis-review.md) §1–2, §7 (gaps G1–G12), issues #272 (the request), #244 (lexer
hang), #85 (close `CelNode`), `docs/architecture/cel.md`, `baas-analyza.md` §2.1/§2.7/§3, `alvo-specifikacia.md` §0
(principles 3, 6) and the code on `feat/cel-functions` @ `0527bb9`. Every load-bearing claim below cites the file it
was verified in; the one claim not verified by running code is marked *(unverified)*.

## 1. Intent and assumptions

Asked (#272): a host developer registers a function **with a name, typed parameters and a typed result** at startup
and calls it from the descriptor's CEL; the registry is readable through the Management API so the dashboard and
agents can offer it; an unknown function, a wrong arity or type is refused **at apply** with a structured error.

| Who | Wants | Gets in C1 | Gets in C2 |
|---|---|---|---|
| Host developer (persona 1) | `AddCelFunction("normalizePhone", (string s) => …)` | that, in hook conditions and before-hook `mutate` values | nothing new (host functions stay in-process) |
| Operator (persona 2) | normalise / round a value without code | `replace trim size abs round` in conditions and `mutate` | the same five in rules and computed fields |
| Agent (persona 3) | discover before apply; a refusal that names the fix | `cel/functions`, `get_cel_functions`, "did you mean" + the known list | — |

Assumed: **embedded hosts** are where host functions live; the standalone image and the CLI have no host code and
therefore know the built-ins only. Operators and agents use built-ins far more than hosts register functions (the
review's §3 argument), so the built-ins are not an afterthought.

## 2. Decomposition

| Slice | Scope | Ships |
|---|---|---|
| **C1** (planned: `docs/superpowers/plans/2026-10-05-f5-cel-functions-c1.md`) | catalog, generic N-ary call path, `AddCelFunction` (Condition + Mutate), the five built-ins **interpreter-side** (Condition + Mutate), fail-closed function errors, `cel/functions`, skills/docs/assistant | this PR |
| **C2** (specified in §9, planned later) | SQL translation of the five built-ins for Rule + Computed through one `IFieldSqlRenderer` default member, per engine, with real-engine differential tests | a later PR |

C1 is useful on its own: every built-in already works where `mutate` and hook conditions run, and C2 only *widens*
the profile set (deny-by-default: nothing C1 refuses becomes silently legal by accident).

## 3. Prior art and what Alvo adopts

| Source | What it says | Adopted |
|---|---|---|
| CEL language definition, "Functions" / overloads ([langdef](https://github.com/google/cel-spec/blob/master/doc/langdef.md)) | a function is a name with one or more typed overloads; the checker resolves the overload from argument types; no implicit conversions; `size(string) -> int` counts code points | name + typed overloads, checker-side resolution, `size` exactly |
| cel-go `cel.Function(name, cel.Overload(id, argTypes, resultType, binding))` ([cel-go](https://github.com/google/cel-go)) | a host declares a function by name, typed signature and a Go binding; errors are values | the declaration shape (name, typed params, result, binding); **not** error values (§5.6) |
| cel-go `ext.Strings` (`s.trim()`, `s.replace(a, b)`) and `ext.Math` (`math.abs`, `math.round`, ties away from zero) | receiver/namespaced spellings | names and semantics; call shape deviates (deviation F1) |
| spec §0 principle 6 | CEL for conditions, JSONata for transforms; CEL is safe-by-construction and runs in-transaction | functions are scalar, synchronous, side-effect-free by contract |
| spec §2.4 / `alvo-specifikacia.md` §0 l.135 | authorization goes into SQL `WHERE`, never a post-filter; fail-fast at save | host functions refused in Rule/Computed; every refusal at apply |

## 4. Rulings recorded as decisions (R1–R8)

| # | Decision (controller ruling) | Cost if wrong | Status here |
|---|---|---|---|
| R1 | Host name pattern `^[a-z][a-zA-Z0-9_]*$`; may not collide with `has changed now in true false null` or any built-in | a host wanting `NormalizePhone` must rename; loosening later is additive | kept; **extended** (X3) and anchored with `\z` (a `$` admits a trailing newline in .NET); **length capped at 64** (a longer name cannot be called inside the 2,000-character expression cap; the schema's `$defs/identifier` allows 63, one fewer, deliberately not mirrored) |
| R2 | Host functions in-process only: Condition + Mutate; refused in Rule/Computed/Access with why and where. `lowerAscii`/`now()` keep their grammar and Mutate-only profile, catalogued for discovery | persona 1 wants rules/computed first (review §3); the recipe "store with `mutate`, filter by the field" is the answer, stated in the refusal's fix | kept; cost X10 recorded |
| R3 | `Delegate`, ≤ 4 params of `string long int decimal bool DateTimeOffset Guid` (+ nullable forms), same result set; singleton closure; non-nullable param + null → null without invoking; `T?` receives null; result nullability from the return type | a scoped service cannot be used (G3); DateOnly not accepted (#272's own example uses a date) | kept; DateOnly is open question Q5 |
| R4 | A throwing host function → `CelFunctionException` (internal), rethrown past the interpreter's catch-alls, surfaced as a rolled-back write with RFC 7807 (distinct type, names the function, no exception text); fail closed; purity by contract | the hook network ban and time bound stop being structural (X7) | kept; X7 needs maintainer sign-off |
| R5 | Public surface minimal and additive: `ICelFunctionCatalog` + `CelFunctionInfo` in Abstractions; `AddCelFunction` in core; `IAlvoManagement.GetCelFunctionsAsync` (Viewer); no Purity/Cost/Examples/Evaluation | each `IAlvoManagement` member breaks external implementers | kept except X1 (catalog interface stays internal) and X2 (nullability shown) |
| R6 | C1 built-ins `replace(s,a,b)`, `trim(s)` (ASCII ws), `size(s)` (code points, string only), `abs(x)`, `round(x)` (one arg, half away from zero, same numeric type), interpreter only; C2 adds SQL via one `RenderFunction` default member | a semantic chosen now must be what every engine can produce later — §6 pins each against SQL | kept |
| R7 | #244 guard first; #85 optional | none | guard is Task 0; #85 skipped (would narrow the baseline but adds nothing C1 needs) |
| R8 | `cel/check` answers unknown/profile refusals once the catalog feeds the compiler; endpoint and dashboard need no change for correctness | none — verified: `CheckExpressionAsync` runs the DI `IDescriptorValidator` (`AlvoManagementService.cs:216`), whose DI constructor takes the DI `ICelCompiler` (`DescriptorValidator.cs:89`) | kept; Task 10 proves parity |

**Questioned rulings / deviations from the brief** (each also in §11):

* **X1 — `ICelFunctionCatalog` stays internal (R5 said public).** No caller outside the core needs it: the dashboard
  holds no reference to `MMLib.Alvo` (architecture test) and reaches functions through `IAlvoManagement`; the
  assistant does the same (`ManagementTools.cs`). `alvo-architecture-rules` ("`public` is the contract — widen only
  when a real external caller needs it") and the Stop hook's "baseline grew" check would have to justify a symbol
  with no caller. Smallest change: the catalog is an internal class (`CelFunctionCatalog`); only the DTOs the
  Management contract returns are public. Making it public later is additive; un-publishing is breaking.
* **X2 — nullability is shown.** R5's list has no nullability, but R3 makes it semantic (null-propagation vs
  receives-null) and G7 asks for it in the signature. Two booleans: `CelFunctionParameter.AcceptsNull`,
  `CelFunctionInfo.ResultMayBeNull`.
* **X3 — R1's reserved set extended** with `old`, `new` (row-image prefixes), CEL's comprehension macros
  (`all exists exists_one map filter`) and the words the CEL spec reserves (`as break const continue else for
  function if import let loop package namespace return var void while`) and the standard CEL type and function
  names (`int uint double bool string bytes list map timestamp duration dyn type contains startsWith endsWith
  matches`). An agent trained on CEL reads these as syntax; refusing them at registration costs nothing. Case
  variants of built-ins stay allowed.
* **X4 — an overload is one entry.** `abs` and `round` are Int→Int and Decimal→Decimal (R6 "same numeric type").
  Rather than a second public "signature" type, discovery lists one `CelFunctionInfo` **per overload** (the name
  repeats), which is CEL's own model flattened. Host functions have exactly one entry (no host overloads).
* **X5 — `IsNeverNull`, `ValidateSqlOperandShape`, `SqlPredicateRenderer` and `ComputedValueShape` arms for calls move
  to C2.** In C1 a call is refused in both SQL-rendered profiles before any of them is consulted
  (`CelTypeChecker.CheckComparison` skips operand checks when an operand already failed, `:591`), so each arm would be
  unreachable code no test can hold — the shape `CelTypeChecker`'s own remarks reject.
* **X6 — surfacing (RFC 7807) is Task 8, after registration (Task 7),** not Task 5: its end-to-end test needs
  `AddCelFunction` to put a throwing function into a real host.

## 5. Architecture (C1)

### 5.1 Data flow

```
AddCelFunction(name, Delegate, summary?)  ── eager validation ──►  CelFunctionRegistration (DI singleton instance)
                                                                          │ IEnumerable
CelFunctionCatalog (singleton) = built-ins ∪ host ◄───────────────────────┘
        │                         │                          │
  CelParser.Parse(src, catalog)   CelTypeChecker(…, catalog)  AlvoManagementService.GetCelFunctionsAsync
  name known? → generic call      resolve overload, profile,  → IReadOnlyList<CelFunctionInfo>
  else "not a recognized          bind CelFunction into node
  function" + did-you-mean              │
                                  CompiledExpression (in PolicyCatalog, primed at apply)
                                        │
                     CelInterpreter.Evaluate → CelCall.Function.Invoke(args) → marshal → delegate
                                        │ throws
                     CelFunctionException ──► (not caught by the interpreter) ──► write rolled back
                                        ──► AlvoExceptionHandler → 500 application/problem+json, type function-failed
```

### 5.2 Components

| Component | Kind | Where | Responsibility |
|---|---|---|---|
| `CelFunctionArgument`, `CelFunction` | internal records | `Expressions/Internal/CelFunction.cs` | one overload: name, typed params (CEL type, nullability, CLR type), result, summary, host/built-in, profiles, body; `Invoke` marshals, null-propagates, wraps failures |
| `CelBuiltInFunctions` | internal static | `Expressions/Internal/CelBuiltInFunctions.cs` | the built-in entries (legacy `lowerAscii`/`now` with no body + the five) and their bodies |
| `CelFunctionCatalog` | internal sealed class | `Expressions/Internal/CelFunctionCatalog.cs` | name → overloads; `BuiltIns`, `With(host)`, `Names`, `Contains`, `Overloads`, `Describe()` |
| `CelArgumentMarshaller` | internal static | `Expressions/Internal/CelArgumentMarshaller.cs` | loose runtime values → the CLR type a body takes; unconvertible → null |
| `CelFunctionException` | internal exception | `Expressions/Internal/CelFunctionException.cs` | function name, host flag, optional built-in reason; inner = the host's exception |
| `HostCelFunction` | internal static | `Expressions/Internal/HostCelFunction.cs` | `Delegate` → `CelFunction`, refusing at registration what the catalog cannot honour |
| `CelFunctionRegistration` | internal record | `Expressions/Internal/CelFunctionRegistration.cs` | the DI carrier of one host function |
| `AddCelFunction` | **public** extension | core `AlvoBuilderExtensions.cs` | the registration API |
| `CelFunctionInfo`, `CelFunctionParameter`, `CelFunctionProvenance` | **public** | Abstractions `Expressions/CelFunctionInfo.cs` | what discovery returns |
| `GetCelFunctionsAsync` | **public** interface member | `IAlvoManagement` | the read |
| `AlvoProblemTypes.FunctionFailed` | **public** const | core `Api/AlvoProblemTypes.cs` | the distinct problem `type` |

### 5.3 Parser (option B: the parser takes the catalog)

`CelParser.Parse(string source, CelFunctionCatalog catalog)`; `Parse(string)` stays (50 call sites, `grep -c`) and
means "built-ins only". The positive switch in `ParseCall` (`CelParser.cs:419-425`) keeps its three named arms first —
`changed`, `lowerAscii`, `now` keep their productions byte for byte (R2) — and gains one arm: *a name the catalog
contains* parses generically. Anything else stays the same syntax-time refusal, so the five `unknown_macro(a)` corpus
rows (`CelAcceptanceBaseline.jsonl`) do not move. Exact grammar change:

```
Primary      := … | Identifier CallTail | Identifier [ '.' Identifier ]      (unchanged)
CallTail     := '(' ')' | '(' Argument { ',' Argument } ')'                 (new, for catalogued names)
Argument     := Conditional                                                 (one nested level each)
```

Each argument is parsed through `ParseNestedGroup` (`:303`), so **every call level costs one unit of `MaxDepth` (32)**;
siblings do not accumulate. `f(f(f(…)))` in 2,000 characters is refused at 33 levels by the parser, before
`MaxTreeDepth` (128) would see ~400 levels. Arity is *not* the parser's business (one error per problem is the
checker's contract).

The unknown-function refusal keeps its message `'X' is not a recognized function.` and gets a new **fix**: `Did you
mean 'Y'? ` (via `NameSuggestion.Closest`, 2 edits) + `Known functions: a, b, ….` + a sentence that a host-registered
function exists only in that host (G5). `lower` keeps the `lowerAscii` fix; the five macro names keep the
"comprehension macro … hooks.beforeUpdate" fix (`CelProfileTests.cs:75`, `CelParserTests.cs:94` pin it). A receiver
or namespace spelling of a catalogued function (`name.trim()`, `math.abs(x)`) keeps the "no nested field access"
message and gets a fix naming `trim(x)` / `abs(x)` (deviation F1). The corpus compares message prefixes, not fixes.

### 5.4 The `CelCall` node

`CelCall(string Name, IReadOnlyList<CelNode> Arguments)` + `ResultType` (init, set by the checker) + `Function`
(init, the bound overload; `null` for `lowerAscii`/`now`, which are evaluated by name). Internal (`CelTree.cs:63`),
so no public API moves. `CelTree.Children` returns every argument — which is what makes the depth cap, the role-literal
walk, `OwnerComparisonCheck` and the hook-phase check (`BeforeHookCompiler.Reads`, `:218`, so `trim(old.x)` in a
`beforeCreate` is refused) see inside a call.

### 5.5 Type checker

* `CelTypeChecker.Check(…, CelFunctionCatalog catalog)`; the 4-argument overload stays (built-ins).
* **Two gates, both deny-by-default.** A new construct row `FunctionCall` in `_allowedProfiles` = `{Condition,
  Mutate}` is the *ceiling* (C2 widens it); each function's own `Profiles` must also contain the profile. The legacy
  row `Call` (`:148`) stays Mutate-only for `lowerAscii`/`now`.
* **Overload resolution:** arity match; then an exact type match; then a match allowing **Int → Decimal** widening
  (deviation F7); a null literal fits only a nullable parameter. No match → one error naming every overload's
  signature (`replace(text: String, search: String, replacement: String) -> String`).
* Arguments are checked first; an argument error stops the call from adding a second, cascading error.

### 5.6 Interpreter, marshalling, failures

* `EvaluateCall` invokes `call.Function` when bound; arguments are evaluated eagerly, left to right (CEL functions
  are strict).
* **Marshaller** (values arrive loosely typed — `int/long/decimal/double`, `DateTimeOffset/DateTime/DateOnly/string`,
  `Guid/string`): reuses `CelInterpreter.TryToDecimal`/`TryToDateTimeOffset` (`:556`, `:533`, made `internal`); a
  `date` column's `DateOnly` (`FilterValueReader.cs:16` documents that date columns hold one) becomes midnight UTC;
  anything that does not convert reads as **null**, the interpreter's existing rule for a value of an unexpected CLR
  type (`CelInterpreter.cs:72-77`). An Int outside `int`'s range reads as null for an `int` parameter.
* **Null policy (R3):** a null argument for a non-nullable parameter → the call is null and the body is **not**
  invoked; a nullable parameter receives null. Built-ins are all non-nullable → null in, null out (= SQL).
* **Failure policy (R4), fail closed:** any exception from a body becomes `CelFunctionException` (built-ins throw it
  directly with a reason). `EvaluatePredicate` and `EvaluateMutation` catch `Exception … when (… is not
  CelFunctionException)`, so it escapes. Where it lands:

| Caller | Outcome |
|---|---|
| before-hook condition or `mutate` (`BeforeHookRunner` → EF write, in the transaction) | propagates out of `IBeforeHookRunner.Run`, the transaction is disposed un-committed, `AlvoExceptionHandler` answers **500**, `type …/errors/function-failed`, detail names the function, no exception text; logged at Error **with** the exception |
| batch write | the whole batch rolls back (the batch already catches only `AlvoAuthorizationException` around hooks, `EfAlvoData.cs:2626`) |
| after-hook condition (`EventSubscriptions.cs:136-141`, post-commit) | already caught there and logged `ConditionRefusedTheHook`: the hook does not fire. Nothing to roll back — fail closed means "no side effect" |
| Rule / mask / computed entry points | unreachable in C1 (calls refused there); their catch-alls stay untouched until C2 |

`BeforeHookRunner.Fires`' remark (deviation 84: the open direction is safe only because nothing can throw) is
rewritten: a function failure is no longer collapsed into "did not fire" — it refuses the write.

**Status 500, not 422,** because the failing party is server-side code the caller cannot fix by changing the request
(open question Q2). **Naming the function** follows R4 and the precedent of `BeforeHookRunner.EnsureNotRejected`,
which already discloses the descriptor-authored hook pointer; the review's persona-4 worry (do not leak names) is
answered by the name being descriptor-authored, not data.

### 5.7 Registration

`public static IAlvoBuilder AddCelFunction(this IAlvoBuilder builder, string name, Delegate function, string? summary = null)`
in core (no new package, no new `IAlvoBuilder` member). Validated **eagerly**, as an `ArgumentException` at the call:
name (R1 + X3), multicast delegate, open generic, > 4 parameters, `ref`/`out`, unsupported parameter or result type,
`void`, `Task`/`ValueTask` ("asynchronous; a CEL function runs inside the write's transaction — do I/O in an
after-hook", G1), duplicate name (scans `builder.Services`, skipping keyed descriptors — reading
`ImplementationInstance` on one throws). The signature is read from the delegate type's `Invoke`; parameter names and
`string?` annotations from `Delegate.Method` when its arity matches (a lambda), via `NullabilityInfoContext`; an
oblivious context reads as a non-nullable *parameter* (the safe default: null-propagation). Nullability is read only from
`Method`'s own `ParameterInfo` and only when its parameters mirror the delegate type's; a closed extension method, an
open-instance delegate, a compiled expression or a dynamic method reads as non-nullable parameters. A *result* of unknown
nullability (oblivious, or no readable return parameter) is **may-be-null**; only an annotated non-null reference or a
non-nullable value type is never-null. Invocation is
`Invoke.Invoke(delegate, BindingFlags.DoNotWrapExceptions, …)`.

DI: `CelFunctionCatalog` singleton = `BuiltIns.With(GetServices<CelFunctionRegistration>())`; `ICelCompiler` becomes a
factory registration `new CelCompiler(catalog)` (the parameterless constructor stays: 12 test files and the CLI's
`new DescriptorValidator()` use it, and mean built-ins only).

**Limits, stated honestly.**

| Limit | Consequence |
|---|---|
| singleton closure, no DI scope | a function cannot use a scoped `DbContext`/service (G3); capture singletons only |
| embedded only | the standalone image and `alvo validate` refuse a host-function descriptor as *unknown function* — descriptor portability is a property of the host, said in the fix (G5) |
| synchronous, no `CancellationToken`, no timeout | a slow function holds the write's transaction for as long as it runs; it is invoked exactly once per evaluation |
| thread-safety | the delegate is called concurrently from every request; it must be thread-safe |
| purity by contract | Alvo cannot see what a delegate does (X7) |
| registration after build | `IServiceCollection` is read-only once a host is built; `Add` throws `InvalidOperationException` (what `WebApplication`/`HostApplicationBuilder` do; a bare `ServiceCollection` is not made read-only by anyone and a late registration is silently absent) |

### 5.8 Security core: what R2 + R4 change (X7)

`alvo-security-core-review` requires a before-hook network call to be **inexpressible**, "direct or indirect via
injected services", and a time budget; `BeforeHookIsolationArchitectureTests` measures the runner's dependencies and
`IBeforeHookRunner`'s remarks argue the time bound is structural ("no user-defined function"). A host delegate is
arbitrary code: it can hold an `HttpClient` in its closure and can block. With R2 + R4 the property becomes:

* **still inexpressible for the descriptor author** (operator, agent): they can call only what the host exposed;
* **expressible for the host developer**, who already owns the process (they can replace `IBeforeHookRunner` itself).

The arch test stays green and gains a fact that the runner's constructor still takes only the policy catalog (the
delegate travels in the compiled tree, not as a dependency). The checklist item, the port's remarks and the arch
test's remarks are amended to say exactly the sentence above. **This is a trust-boundary decision the maintainer must
accept in the PR** (Q1); the change is labelled `needs-deep-review`.

### 5.9 `cel/functions`

Decision: **a new `IAlvoManagement.GetCelFunctionsAsync(string project, CancellationToken)` → `IReadOnlyList<CelFunctionInfo>`,
level Viewer, `GET {m}/projects/{project}/cel/functions`** (R5), over extending `ManagementCapabilities`.

| Option | For | Against |
|---|---|---|
| new member (chosen) | one question, one route, one tool; `ManagementContractTests` pins it; capabilities stays "what this build honours" | breaks external `IAlvoManagement` implementers (here: 3 test doubles, `ManagementAccessTests.cs:278`, `ManagementDecorator.cs:12`, `KeptFollowScenarios.cs:71`); baseline +1 member |
| field on `ManagementCapabilities` | no new member | a positional record: adding a parameter is source-breaking for constructors anyway; mixes "what is refused" with "what you may call" |

Project-scoped although the catalog is per instance (X13): symmetric with `cel/check`, and it keeps per-project
function visibility possible without a route change. Viewer: it discloses names and summaries the host chose, like
capabilities. The dashboard does **not** consume it in C1 (R8; offering functions belongs to slice B's guided form);
the assistant gets a `get_cel_functions` tool and its instructions say to call it before writing a call (G12).

### 5.10 Public surface (justified per `alvo-architecture-rules`)

| Symbol | Package | Why public |
|---|---|---|
| `CelFunctionInfo` (Name, Parameters, Result, ResultMayBeNull, Summary, Provenance, Profiles) | Abstractions | returned by the public `IAlvoManagement`; Admin and Ai reach it only there |
| `CelFunctionParameter` (Name, Type, AcceptsNull) | Abstractions | element of the above |
| `CelFunctionProvenance { BuiltIn, Host }` | Abstractions | tells an operator which calls run host code (X7) |
| `[JsonConverter(JsonStringEnumConverter<…>)]` on `CelValueType`, `CelProfile` | Abstractions | the HTTP and tool JSON show `"String"`, not `3`; no Management model carries either enum today (verified: outside `Expressions/` only `Rules/PolicyDecision.cs` reaches them, through `CompiledExpression`; that it is never serialized is *(unverified)* — Task 7 checks) |
| `IAlvoManagement.GetCelFunctionsAsync` | Abstractions | R5 |
| `AlvoBuilderExtensions.AddCelFunction` | core | the registration API |
| `AlvoProblemTypes.FunctionFailed` | core | the distinct problem `type` (R4) an agent branches on |

`CelFunctionInfo`/`CelFunctionParameter` are **init-only records with `required` members**, not positional (house
style in `ManagementModels.cs`) — a deliberate deviation (X9): this DTO is the one most likely to grow (examples G6,
`since` G4, a summary key G10), and an added optional init member is additive where an added positional parameter is
not.

## 6. Built-in semantics (interpreter now; C2 must produce the same in SQL)

All five: Condition + Mutate in C1; arguments non-nullable, so **any null argument → null** (SQL's `NULL` in → `NULL`
out); never culture-sensitive.

| Function | Signature(s) | Semantics | Edge cases pinned by tests |
|---|---|---|---|
| `replace` | `(text: String, search: String, replacement: String) -> String` | every non-overlapping occurrence, left to right, ordinal | `search == ''` → `text` unchanged (SQL; deviation F6); `replace('aaa','aa','b')` = `'ba'`; a replacement that would **grow** the text past **1,048,576** characters fails closed (X12); a text already over the cap that does not grow (equal-length or shrinking replace) is returned |
| `trim` | `(text: String) -> String` | removes U+0020, U+0009, U+000A, U+000D from both ends, nothing else | NBSP (U+00A0) and U+2003 stay (deviation F5); `trim('')` = `''` |
| `size` | `(text: String) -> Int` | Unicode code points (`EnumerateRunes`, as `ComputedValueShape.Longest` already counts) | `'😀'` = 1; `'é'` = 2; a lone surrogate counts 1 (U+FFFD) |
| `abs` | `(x: Int) -> Int`, `(x: Decimal) -> Decimal` | magnitude, same type | `abs(-9223372036854775808)` fails closed (CEL: overflow is an error; engines raise) |
| `round` | `(x: Int) -> Int`, `(x: Decimal) -> Decimal` | nearest whole number, **halves away from zero** (`MidpointRounding.AwayFromZero`; .NET's default is banker's) | 2.5→3, −2.5→−3, 0.5→1, −0.5→−1, 1.4999→1; Int is the identity |

The cap bounds growth, not size: the input already got in (it passed the request-body limit or an earlier write), so
refusing a replace that does not make it larger would only block shrinking it.

**Notes for C2** (what SQL cannot or must be checked to reproduce):

- `replace` is UTF-16 ordinal in-process; a lone-surrogate search can split a surrogate pair, which SQL cannot
  reproduce. This affects only lone-surrogate data.
- `round` returns scale 0 in-process, whereas SQL `round(numeric)` keeps the column's scale.
- C2 must confirm each driver encodes lone surrogates as U+FFFD, so that `length`/`char_length` = 1 matches `SizeOf`.
- There is no result cap in SQL (see the §9 divergence for texts over 1 MiB).

Built-in failures (overflow, the replace cap) are `CelFunctionException` with a reason, so the problem detail can say
*why* ("its result would be N characters…") — text Alvo wrote, never the host's.

## 7. Per-profile legality

| Function | Rule | Computed | Condition | Mutate | Access |
|---|---|---|---|---|---|
| `lowerAscii(field)`, `now()` (legacy grammar, unchanged) | ✗ | ✗ | ✗ | ✓ | ✗ |
| `replace trim size abs round` — C1 | ✗ | ✗ | ✓ | ✓ | ✗ |
| same — after C2 | ✓ | ✓ | ✓ | ✓ | ✗ |
| host function (any C) | ✗ | ✗ | ✓ | ✓ | ✗ |

X10, recorded cost of R2: in C1 `trim(email) == 'x'` compiles in a hook condition while `lowerAscii(email) == 'x'` does
not. Widening `lowerAscii` to Condition is additive and left for a follow-up (it needs no new semantics).

## 8. Errors (exact first sentences; the corpus pins prefixes)

| Case | Message | Fix |
|---|---|---|
| unknown name | `'normalisePhone' is not a recognized function.` (unchanged) | `Did you mean 'normalizePhone'? Known functions: abs, lowerAscii, normalizePhone, now, replace, round, size, trim. A function a host registers with AddCelFunction exists only in that host; the standalone image and the CLI know the built-in ones only.` |
| arity | `'trim' takes 1 argument; this call passes 2.` | `Call it as trim(text: String) -> String.` |
| argument type | `'trim(...)' accepts no (Decimal); it accepts trim(text: String) -> String.` | types of fields, and Int-for-Decimal |
| profile (Rule) | `'trim(...)' is not available in the Rule profile; it is available in Condition and Mutate. A rule becomes a SQL filter, and this function runs only in-process; authorization is never a filter applied after the query.` | `Store the value in a field with a before-hook mutate (hooks.beforeCreate / beforeUpdate), then compare that field here.` |
| profile (Computed / Access) | same lead, profile-specific reason | `mutate` into a regular field / test the caller |
| call too deep | `CEL expression nests 33 levels deep, exceeding the maximum of 32.` (unchanged) | now also names "nested function calls" |
| registration | `ArgumentException`: `The CEL function 'x' takes 5 parameters; a CEL function takes at most 4.` (one sentence per §5.7 case) | — |
| runtime | 500, `type https://alvo.dev/errors/function-failed`, `The CEL function 'normalizePhone' failed while this write was evaluated, so nothing was written. Its own error is in the server log.` | — |

The Rule reason says "SQL filter", not "SQL WHERE clause": `SqlTextConfinedToRendererArchitectureTests` bans the text `WHERE ` in any source file outside the renderer, and a refusal message is source. The profile-specific reason is chosen per refused profile (Rule, Computed, Access); any other refusal, such as a Mutate-only function in Condition, says plainly that the function is not enabled for that profile. The "available in" list names only the profiles inside the checker's ceiling (Condition and Mutate in C1) that the function also lists, never the profile being refused.

## 9. Slice C2 — SQL translation (specified, not planned)

Goal: the five built-ins in **Rule** and **Computed** on SQLite and PostgreSQL (and the T-SQL fake), equal to §6.

| Work item | Where (verified) | Note |
|---|---|---|
| one default member `string? RenderFunction(string name, IReadOnlyList<string> renderedArguments)` returning `null` = "no translation on this engine" (deny-by-default, the `RenderStringLiteral` precedent) | `IFieldSqlRenderer` (Abstractions) | implementers: `SqliteFieldSqlRenderer`, `PostgreSqlFieldSqlRenderer`, test `TSqlFieldSqlRenderer`, `TestFieldSqlRenderer`, `ComputedFieldCheck.NeutralFields` (else every computed field using a built-in is "unrenderable"), plus test-only `TypeMarkingFieldSqlRenderer`, `ReadStatementComposerTests`, `DialectContractTests` *(list from the explorer pass; re-grep)* |
| `CelCall` arms in `RenderOperand` (`:372`), `RenderScalarOperand` (`:525`) and `ValueTypeOf` (`:300`, from `CelCall.ResultType`, or `trim(a) + trim(b)` renders as numeric `+`) | `SqlPredicateRenderer` | an unrendered function is a `NotSupportedException` caught as a refusal at apply, never at read |
| `IsSqlOperand` (`:694`) admits a call whose function renders; `IsNeverNull` (`:570`) — a call is never null when every argument is | `CelTypeChecker` | moved here from C1 (X5) |
| `ReferencesContextValue`/`ReferencesRowField` (`PolicyCatalogBuilder.cs:190`, `:457`) recurse into arguments | rules + masks | today `_ => true`; correct (conservative) but would deny anonymous callers for every rule using a function |
| `ComputedValueShape.Longest` (`:142-149`) computes a call's width (`size` → integer; `replace` → unbounded) | computed DDL | |
| widen `FunctionCall` row and the five built-ins' `Profiles` to Rule + Computed; host functions unchanged | catalog | |

Per-engine spelling (engine knowledge **to be measured**, not trusted):

| Built-in | SQLite | PostgreSQL | T-SQL | Risk to measure |
|---|---|---|---|---|
| `replace` | `replace(t,s,r)` | `replace(t,s,r)` | `REPLACE` + binary collation | empty search; T-SQL collation; no result cap in SQL (X12 divergence for > 1 MiB) |
| `trim` | `trim(t, ' '‖char(9)‖char(10)‖char(13))` | `btrim(t, ' '‖chr(9)‖…)` | `TRIM(… FROM t)` (2017+) | default `trim` strips spaces only on every engine; `RequireDdlText` (`CelTypeChecker.cs:233`) refuses control characters in an inline literal, so the set is spelled with `char()`/`chr()` |
| `size` | `length(t)` | `char_length(t)` | `LEN` ignores trailing spaces, counts UTF-16 units → `DATALENGTH`/`_SC` workaround | code points everywhere |
| `abs` | `abs` | `abs` | `ABS` | Int min overflow raises; SQLite decimals are TEXT-backed and coerce to REAL |
| `round` | `round(x)` (REAL) | `round(x::numeric)` (half away from zero on `numeric`, half even on `double precision`) | `ROUND(x, 0)` | tie rows 2.5, −2.5, 0.5 on real engines |

One-argument `round` only: a computed column binds no parameters and numeric literals are not inlined
(`computed refuses any expression that binds a parameter`), so `round(x, 2)` would be refused anyway. All five are
deterministic/immutable, as generated columns require (PG `IMMUTABLE`, SQLite deterministic, T-SQL `PERSISTED`).
C2 acceptance: `AlvoDataDifferentialTests` + `AlvoDataComputed*` on SQLite and PostgreSQL (Testcontainers) with
null, tie, code-point and empty-search rows; `CompiledRendersPropertyTests` gains call nodes and a string leaf set;
`NoInterpolationPropertyTests` covers function arguments; renderer snapshots per engine move and are judged;
`PublicApi.MMLib.Alvo.Abstractions` grows by one interface member.

## 10. Testing strategy (C1)

| Layer | What | Where |
|---|---|---|
| corpus | `CelAcceptanceBaseline.jsonl` (1,235 rows) **does not move**: no row names a new built-in (verified by grep), unknown-name messages and the depth message keep their text; only fixes change, which the corpus does not compare. Green-unmodified is the behaviour-neutrality proof for Tasks 0–7 | `CelAcceptanceCorpusTests` |
| unit | lexer progress; `Children`; catalog; parser; checker (profiles, arity, types, widening, null literal, cascades); marshaller; null policy; failure propagation; each built-in's table in §6 | `test/MMLib.Alvo.Tests/Expressions/*` |
| property | CsCheck: `trim`/`size`/`replace` never throw for any string (lone surrogates included) and equal a reference definition | `CelBuiltInPropertyTests` |
| registration | name/type/arity/async/duplicate/read-only refusals; nullability and names read from the lambda | `CelFunctionRegistrationTests` |
| HTTP | a host function normalises a written row; a throwing one → 500 `function-failed`, no row, no exception text; null-returning stores null | `CelFunctionWriteTests` (Api) |
| problem catalogue | `function-failed` emitted by the factory and reachable over HTTP | `ProblemDetailsTests` |
| management | route, level, contract, three doubles; built-ins over HTTP; host functions in-process | `ManagementCelFunctionsTests` |
| parity with apply | `cel/check` and dry-run apply agree on host and built-in calls in every slot kind | `ExpressionCheckAgreementTests` (corpus extended) |
| architecture | runner's constructor unchanged (the delegate travels in the tree) | `BeforeHookIsolationArchitectureTests` |
| skills drift | `mutate-functions` region equals the built-ins the catalog admits in Mutate; allowed/refused examples compile where stated | `SkillCoreClaimsTests` |
| assistant | tool set (now seven), instructions list it | `ManagementToolsTests`, `AssistantInstructionsTests` |
| public API | Abstractions + core baselines grow by exactly §5.10 | approval tests |

## 11. Deviations (one place, so a reader tells a decision from an oversight)

From CEL / cel-go: **F1** global call shape for `trim`, `replace` (cel-go receiver style) and `abs`, `round` (cel-go
`math.` namespace) — the `lowerAscii` precedent (cel.md deviation 15): name and semantics adopted, shape not; the
receiver/namespace spellings get a fix. **F2** `size` is conformant (global form). **F3** null → null (R3) where CEL
has no matching overload — SQL parity and the `lowerAscii` precedent. **F4** a failure aborts evaluation; CEL's error
values and commutative `&&`/`||` absorption are not modelled (`f(x) && false` fails closed rather than answering
`false`). **F5** `trim`'s set is four ASCII characters, cel-go's is Unicode whitespace — SQL parity and lexer
escapability (`\n \t \r` are the escapes `CelLexer.ReadEscape` has). **F6** `replace` with an empty search returns the
text (SQL) where cel-go inserts between runes. **F7** Int may bind a Decimal parameter; CEL has no implicit conversion
— Alvo comparisons already widen numerics (`CelInterpreter` remarks). **F8** `round` ties away from zero = cel-go
`math.round`; Int overload is the identity; Decimal, not double.

From the brief / house rules: X1–X6 (§4), **X7** security core (§5.8), **X8** JSON string enums (§5.10), **X9**
init-only DTOs (§5.10), **X10** `lowerAscii` narrower than `trim` (§7), **X11** status 500 (§5.6), **X12** the replace
cap = 1,048,576 characters, **derived** from `AlvoApiOptions.MaxRequestBodyBytes`' default (1 MiB, the largest value a
client can send in one default request) — no source gives a number, **X13** project-scoped route (§5.9).

## 12. Forecloses / keeps open

| Later | How it lands without breaking |
|---|---|
| C2 SQL translation | `RenderFunction` default member; widen two profile sets; no C1 public symbol changes |
| DI-resolved host function (G3) | `AddCelFunction(string, Func<IServiceProvider, Delegate>)` resolving **singletons** at catalog build — additive. A *scoped* function needs a service scope inside `IBeforeHookRunner.Run`, which the isolation test forbids today: foreclosed without a port change, stated |
| `[CelFunction("name")]` + `AddCelFunctionsFrom(Type)` (G8) | `MethodInfo.CreateDelegate` → the same `HostCelFunction.Create`; additive |
| macro functions (review §3: a host function defined as a CEL body) | a third `CelFunction` shape (body = compiled template) the checker inlines; renders wherever its body renders; discovery unchanged (Provenance Host) |
| `Date` type (D7) | a new `CelValueType` member + DateOnly marshalling; string-serialized, so additive on the wire |
| versioning (G4) | convention, documented in cel.md and the skill: **new semantics = new name** (`vat_rate_v2`); the descriptor binds a name, the catalog has no versions |
| examples / i18n summary key (G6, G10) | optional init members on `CelFunctionInfo` |
| `AlvoTest.Cel(…)` helper for host developers (G6) | in `MMLib.Alvo.Testing`, follow-up |
| async / I/O (G1) | never a CEL function: an after-hook action, a separate design |

Foreclosed deliberately: host overloads (one signature per name); host functions in Rule/Computed until SQL templates
or macros exist; a function reading ambient `@user`/`@tenant` (pass them as arguments, G2 — they are legal in
Condition).

## 13. Risks

* **Trust boundary (X7)** — mitigated by documentation, `Provenance` in discovery, `needs-deep-review`; not enforced.
* **A slow host function** holds row locks; no timeout. Mitigation: documented; invoked once per evaluation (pinned).
* **Silent nulls** from unconvertible arguments (e.g. Int out of `int` range) — documented; register `long`.
* **`NullabilityInfoContext` on lambdas** relies on the compiler's nullable metadata *(unverified for every compiler
  configuration; pinned for the repo's own build by a test)*; oblivious → non-nullable is the safe side.
* **Admin's `CelNames.Rename`** treats `name(` as a call; a call written with a space (`size (x)`, legal CEL) may be
  read as a field reference by the dashboard's rename *(unverified)* — follow-up.

## 14. Open questions for the maintainer

1. **Q1** Accept the trust shift in §5.8 (host code may do I/O inside a before-hook)?
2. **Q2** `function-failed` as 500 (chosen) or 422?
3. **Q3** Should `trim` also strip U+000B/U+000C (C's `isspace`)? Chosen: no — not escapable in a CEL literal.
4. **Q4** Keep the replace cap at 1,048,576, or make it follow `AlvoApiOptions.MaxRequestBodyBytes` at runtime (needs
   options in the static interpreter)?
5. **Q5** Admit `DateOnly` parameters (#272's `vat_rate(…, date)`) now, mapped to Timestamp?
6. **Q6** Is an optional `summary` right, or should discovery require one?
7. **Q7** Confirm X1 (internal catalog) over R5's public `ICelFunctionCatalog`.

## 15. Acceptance criteria (numbers only where a source or a ruling gives one)

1. A registered host function compiles in Condition and Mutate and is refused in Rule, Computed and Access with the
   §8 messages; `cel/check` and dry-run apply agree on it in every slot kind.
2. ≤ 4 parameters (R3); call nesting counts against `MaxDepth` = 32; the 2,000-character limit and `MaxTreeDepth` =
   128 are unchanged; `CelAcceptanceBaseline.jsonl` is unmodified and green.
3. The five built-ins behave exactly as §6, including every listed edge case.
4. A throwing host function leaves no row and answers 500 `function-failed` naming the function, with no exception
   text in the body; the exception is logged.
5. `GET {m}/projects/{p}/cel/functions` is Viewer, one route per member, absent from OpenAPI, and lists built-ins and
   host functions with provenance, profiles and nullability.
6. Public baselines grow by exactly §5.10; each symbol justified in the PR.
7. The hooks skill's `mutate-functions` region equals the catalog; the assistant has `get_cel_functions`.

## 16. Follow-ups

`lowerAscii` in Condition (X10); DI singleton-factory registration; `[CelFunction]` attribute; `AlvoTest.Cel`; macro
functions; `DateOnly`; reword `UnhonouredSubsystems`' "functions" warning so it is not read as host CEL functions;
`mutate` of a possibly-null function result into a `required` field refused at apply; the dashboard offering
functions (slice B); #85.

## 17. As built

Slice C1 as it landed on `feat/cel-functions` (`git log --oneline 0527bb9..`, oldest first; the docs/skills/assistant
commit that writes this section follows the last one listed):

- `3c0b88b` docs(f5): the CEL functions design and its C1 plan
- `1adcb3c` fix(cel): refuse a lexer step that consumes nothing instead of looping
- `97710b9` refactor(cel): a call node carries every argument and its checked result type
- `80b718d` feat(cel): an internal function catalog that lists the two built-in calls
- `39548a1` feat(cel): the parser reads a catalogued function as an N-ary call and names the known ones when it cannot
- `f36dcef` feat(cel): resolve a function call against its signatures and gate it by its profiles
- `73d98e8` fix(cel): pin both function-profile gates and say the right thing when one refuses
- `3cd2ef9` feat(cel): invoke a bound function through a marshaller and let its failure escape, fail closed
- `ff91c48` feat(cel): replace, trim, size, abs and round in hook conditions and mutate values
- `ee3cfab` test(cel): pin ordinal replace, the cap boundary and a real trim invariant; spec the cap and C2 notes
- `98317d5` feat(cel): AddCelFunction registers a host function, validated at the call, discoverable as CelFunctionInfo
- `166d2c1` fix(cel): host function nullability falls to the safe side for odd delegate shapes; reserve standard CEL names; cap name length
- `cec38ed` feat(api): a failing CEL function rolls the write back and answers function-failed
- `d38845b` fix(api): warn when a CEL function drops an after-hook, find wrapped failures, pin update/delete/batch/outbox fail-closed
- `015675e` test(api): make the refused-write outbox assertion real with an after-create webhook
- `97d52bc` feat(management): cel/functions lists every function a descriptor may call, for viewers
- `37b2d7b` fix(management): return cel/functions as an object envelope, pin its wire format and authorization
- `9a12232` test(management): cel/check and apply agree on host and built-in function calls in every slot
- `8283ff3` fix(rules): a call reads exactly what its arguments read in the context and row-field walkers
- `9842cf7` test(rules): pin the context an after-hook call reads, through the gate and the check

**Rulings on the open questions (§14), taken autonomously while the maintainer was away; each is the maintainer's to
overturn in the PR.**

| Ruling | Decision | Cost if wrong |
|---|---|---|
| K | accept X7: a host function is host code, the trust shift is inherent in "define functions from code"; the descriptor author still cannot reach the network; the checklist and the remarks are amended; the PR carries `needs-deep-review` | a stricter reading of "no network in a before-hook" would forbid the feature |
| L | `function-failed` answers HTTP 500, no exception text | clients treat it as a server error |
| M | `trim` strips the four ASCII characters only (not form feed or vertical tab) | a form feed survives `trim` |
| N | the `replace` growth cap stays the constant 1,048,576 characters | a host with a larger body limit cannot replace beyond it |
| O | no `DateOnly` parameter in C1 (a timestamp is `DateTimeOffset` only) | a `vat_rate(…, date)` function needs a Timestamp until a Date type exists |
| P | `summary` stays optional | a function can be registered undocumented |
| Q | `ICelFunctionCatalog` stays internal (`CelFunctionCatalog`); only `CelFunctionInfo`, `CelFunctionParameter` and `CelFunctionProvenance` are public | making it public later is additive |
| S | `cel/functions` answers an object envelope `ManagementCelFunctions { functions: [...] }`, not the bare list §5.9 first wrote | the one extra public record; a bare array could never grow |

**Deviations from the plan, each ruled.**

* **The envelope (Ruling S)** changed Task 9's contract after review: `IAlvoManagement.GetCelFunctionsAsync` returns
  `ManagementCelFunctions`, and the wire shape is `{ "functions": [...] }`. §5.9's "`IReadOnlyList<CelFunctionInfo>`" is
  superseded by this line. Nothing consumed the route yet.
* **A walker fix** (found while extending the agreement evidence in Task 10): `ReferencesContextValue` and
  `ReferencesRowField` swallowed a `CelCall` in their `_ => true` wildcard, so a host-function call in an *after-hook*
  condition was refused with a misleading `@tenant.id`/`@user.roles` reason. An explicit arm now recurses into the
  call's arguments, so a call reads exactly what its arguments read. A walker audit found no other walker with the
  gap.
* **The after-hook failure is logged at Warning, not Debug** (§5.6 said "already caught and logged"): a function that
  throws in an after-hook condition silently drops, for example, an audit webhook, and its before-hook twin logs at
  Error. The log carries the function's name and no record data.
* **§8's Rule refusal says "SQL filter", not "SQL `WHERE` clause"** — `SqlTextConfinedToRendererArchitectureTests` bans
  the latter text in any source file outside the renderer, and a refusal message is source. The "available in" list
  names only the profiles inside the checker's ceiling that the function also lists.
* **`NullabilityInfoContext` on a lambda held** for parameters: names and `string?` annotations are read from
  `Delegate.Method` when its parameters mirror the delegate type's. A closed extension-method delegate, an
  open-instance delegate, a compiled expression and a dynamic method read as non-nullable parameters (the safe
  default), and a lambda's *result* reads as may-be-null because the compiler emits no non-null return annotation
  for it — conservative, not pinned by a test.
* **Test and text adapted in this task:** `SkillCoreClaimsTests` derives the Mutate-functions list from the catalog
  instead of a constant; `ManagementToolsTests` and `EmbeddedSkillsTests` count seven management tools; and the
  assistant instructions' always-in-context budget (22,758 bytes) held only after trimming three sentences that
  repeated what the Skills paragraph and the skills themselves already say (the budget was not touched).
  `docs/architecture/cel.md` now carries deviations 17–24 (the §11 F-series, with the `Int`→`Decimal` widening as 22 and
  the reserved-names narrowing X3 as 24).

**Deferred (the final fix wave, or later).**

* Defence in depth in `CelFunction.Run`: a runtime arity guard (fewer arguments than parameters is an
  `IndexOutOfRangeException` today, which `EvaluatePredicate` collapses to `false`) and a check of the result against
  the declared type (`Normalize` maps only `int` to `long`). Both are unreachable through the type checker.
* `EvaluateMask` and `EvaluateScalar` still swallow `CelFunctionException`: unreachable while a call is refused in
  Rule and Computed, so a **C2 checklist item** — the catch-alls must let it through before a call is admitted there.
* `lowerAscii` in a Condition (X10) and the other §16 follow-ups.
* A timestamp text without an offset parses in the machine's zone (pre-existing; relevant when `Date` arrives).
* `CelProfile.Mutate`'s XML remarks still say the allow-list is "exactly two entries"; a comment in the public
  Abstractions assembly, left for the final wave.

