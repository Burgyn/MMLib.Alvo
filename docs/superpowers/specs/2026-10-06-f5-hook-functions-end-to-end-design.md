# F5 — hook functions end to end: built-ins v2, the dashboard offers them, a host registers one (slice D)

Status: design, 2026-10-06. Written autonomously while the maintainer is away; brainstorming approvals are **delegated,
not given** — the gate that remains is the PR. Slice D combines the two sibling branches that came before it:
[CEL functions C1](2026-10-05-f5-cel-functions-design.md) (`feat/cel-functions`: catalog, `AddCelFunction`, five
built-ins, `cel/functions`) and [the hooks editor B](2026-10-05-f5-hooks-editor-design.md) (`feat/hooks-editor`: edit in
place, mutate rows, pickers, guided conditions). Its branch is `feat/hooks-editor` with `feat/cel-functions` merged in.
It is the "D" of the [expression check](2026-10-01-f5-expression-check-design.md) decomposition only in name; C1 §2
renamed the old C + D to C1 + C2, and C2 (SQL translation) is still a later slice.

Inputs, read in this order: the maintainer's goal (below); C1 spec §3–§17 (incl. "As built" rulings K–V); B spec §3,
§4.2–§4.6, §7, §10 (row "Functions"), §16 and its pre-flight (`.superpowers/sdd/2026-10-05-f5-hooks-editor/preflight.md`,
rulings C1–C8, W1); `docs/architecture/cel.md` (construct table, deviations 1–24); `baas-analyza.md` §2.7, §2.8, §3.3,
§10.1; `alvo-specifikacia.md` §0 (principles 3, 6), §1.2 lifecycle hooks, the hooks example at l.218; the frozen
artifacts `schema/project.schema.json` (`$defs/cel` 1–2000), `src/MMLib.Alvo.Abstractions/Expressions`
(`CelFunctionInfo`, `CelValueType`, `CelProfile`, `CelFieldType`); the code on `feat/cel-functions` @ `3a3c31f` (PR
#314) and `feat/hooks-editor` @ `ef6cbac` (B Tasks 1–4 built; later B members are cited from B's plan), and `origin/main`
@ `b25cffc`, where `lowerAscii` already ships (field-only, Mutate-only, used by `examples/complex-crm/crm.alvo.json:109`)
and `abs`/`round` do not exist; `samples/MMLib.Alvo.Samples.EmbeddedHost` and its `.Tests.Integration` project; the
validation verdict on this design (2026-10-06, §20 records the rulings on it). Prior art: the CEL language definition
([langdef](https://github.com/google/cel-spec/blob/master/doc/langdef.md), "Standard definitions"), cel-go `ext.Strings`
and `ext.Math`, RFC 3339. Every load-bearing claim about Alvo code cites the file it was read in; claims about B code
that is not written yet cite **B plan Task N** and its declared interface.

## 1. Intent and personas

The maintainer, verbatim intent: *"Deliver the ability to add custom functions to hooks, and create meaningful built-in
ones too, end to end: set them up from code, use and evaluate them in the API, and be able to edit them in the admin
portal."*

**How "edit them in the admin portal" is read (D-1, Q6 — the maintainer's to overturn first).** D builds editing of the
**hooks that call functions**: the hook editor offers every function its box admits and inserts a call. It does not
build editing of the **functions themselves**: a host function is C# compiled into the host, and a browser cannot edit
it. Functions an operator could define in the portal would be *descriptor-defined* CEL functions — a recorded follow-up
(§12), kept open by two things D does not foreclose: `CelFunctionProvenance` can gain a third member, and such functions
would live in a descriptor block of their own. That block must not be the frozen schema's `functions` block, which
already means C# script (csx) functions (`schema/project.schema.json`), so D also never calls CEL functions "functions"
in any descriptor-facing name.

| Who | Wants | Has after B + C1 | Gets in D |
|---|---|---|---|
| Host developer (persona 1) | register `normalizeFrameNumber` in C#, see it work in a hook | `AddCelFunction` (C1); no runnable proof that a registered function reaches the dashboard, and no code to copy | a real-browser proof over the shipped host: registered → listed → inserted → applied → a Data API write stores its result; the embedded sample registers one (`normalizeVin`) with a README section and an integration fact; a how-to in `extensibility.md` |
| Operator, not a CEL expert (persona 2, B's primary) | tidy a value, test a text, round a price, prefix a code, without knowing function names | a mutate expression box and a condition box that accept calls, but never say which exist (B §10 "not planned"); no operator in a mutate | the box offers the functions its slot admits — signature, summary, built-in or host — and a click inserts the call; the guided form gains "starts with / ends with / contains"; fail-closed arithmetic in a condition and a mutate, string `+` in a mutate, `math.round(x, digits)` |
| Agent / the assistant (persona 3) | the standard CEL names and operators it was trained on | `trim replace size abs round` in a global form | 19 names, the standard ones spelled the standard way where Alvo's grammar can (`math.abs`, `startsWith`, `timestamp`); CEL's arithmetic semantics (Int division truncates, overflow is an error); `get_cel_functions` already lists them (C1) |

**Assumed:** B is implemented through its Task 21 before D starts, and C1 through its pre-PR fix wave. Nothing in this
design ships before either; if either merges to `main` first, D rebases onto `main` (the merge task, plan Task 0, says
how).

## 2. What exists, and the gap

| Area | On the branches | Gap D closes |
|---|---|---|
| Built-ins (C1, `CelBuiltInFunctions.cs:35`) | `replace trim size abs round` + legacy `lowerAscii`/`now` | nothing to *test* a text in a condition (`startsWith`…), nothing to change case except a field-only `lowerAscii` in Mutate only, nothing to cut a text to fit `maxLength` (Ruling V now refuses an overlong mutate value with 403), no ceiling/floor, no conversions, no way to compare a date field with a fixed date |
| Names (C1 F1) | `abs`, `round` | cel-go names them `math.abs`, `math.round`; a bare name is a variant of a standard — renaming costs nothing while nothing that uses them has shipped, and is breaking afterwards |
| Discovery | `GET …/cel/functions`, `IAlvoManagement.GetCelFunctionsAsync` → `ManagementCelFunctions { Functions }` (C1 Ruling S) | **the dashboard does not read it** (C1 §5.9: "offering functions belongs to slice B's guided form"; B §10: "additive", planned nowhere) |
| Hook editor (B) | mutate rows with a CEL box per row (`hook-mutate-value-{i}`, B Task 9), a condition text box and guided rows (`hook-condition`, B Tasks 8, 19) | no function help in either box; the guided table has no call rows (B §7.1 "function calls (the functions slice may add them)") |
| Operators (`CelTypeChecker.cs:147-165`) | arithmetic, concatenation and ternary are Computed-only; the interpreter's arithmetic answers `null` on overflow and division by zero, and divides Ints in decimal (`7 / 2` is `3.5`) | no rounding to cents, no prefix, no total in a hook; a condition could not compare `new.qty * new.price` with a limit |
| Set up from code | `CelFunctionWriteTests` (C1) prove a host function over HTTP in an Api test host without the dashboard | no proof across host registration → dashboard → apply → Data API write; the admin e2e world (`AdminWorld`) has no seam for an `IAlvoBuilder`; the embedded sample (`SampleHost.cs:94`, `AddAlvo`) registers no function, so there is no code a host developer can copy |
| Docs | `cel.md` lists five built-ins; `AddCelFunction` appears only in `cel.md`'s limits table | no host-developer how-to; no reference for the new built-ins; `todo-admin.md` has no row for function offering |

## 3. Prior art, and what Alvo adopts

| Source | What it defines | Adopted in D |
|---|---|---|
| CEL langdef, standard definitions | `size(string)`; `string.contains(string)`, `string.startsWith(string)`, `string.endsWith(string)`, `string.matches(string)` (member form; `matches` also global); conversions `int(double)` (truncates toward zero, error out of range), `int(string)`, `string(int/double/bool/timestamp/…)`, `timestamp(string)` (RFC 3339); timestamp accessors `getFullYear`, `getMonth` (0-based) … all member form, optional time-zone argument; durations | `contains`, `startsWith`, `endsWith`, `size`, `int`, `string`, `timestamp` — names and semantics; **call shape global** (F1, extended as F11); `matches`, accessors, durations deferred (§12) |
| cel-go `ext.Strings` | member functions `lowerAscii`, `upperAscii`, `replace(a, b[, n])`, `split`, `substring(start[, end])` (code points, 0-based, end exclusive; error when negative, reversed or past the length), `trim` (Unicode whitespace), `indexOf`, `join` | `lowerAscii`, `upperAscii`, `substring` (both arities) — semantics exactly; `split`/`join` need lists (Alvo has no list literals, `CelParser.cs:319-322`); `indexOf` deferred |
| cel-go `ext.Math` | namespaced globals `math.abs`, `math.ceil`, `math.floor`, `math.round` (ties away from zero; one argument only), `math.trunc`, `math.greatest`/`math.least` (variadic or a list), `math.sign`, bit ops | `math.abs`, `math.round` (renamed from C1), `math.ceil`, `math.floor`, `math.greatest`, `math.least` (two arguments, F10); the namespaced global **is** cel-go's own call shape, so these are conformant names, not a deviation. `math.round(x, digits)` is an Alvo overload cel-go lacks (F17) |
| CEL langdef, arithmetic | `int` `+ - * / %` with overflow an **error**; `int / int` truncates toward zero; division and modulus by zero an error; no `(string, int)` `+`; operators are overloads named `_+_`, `_-_`, `_*_`, `_/_`, `-_`; `string + string` concatenates | Int and Decimal `+ - * /` and unary `-` in Condition and Mutate with exactly those failure semantics (an error = fail closed, D-7); `_+_`-style names in the failure detail; string `+` in Mutate (D-6). `%` is not in Alvo's grammar (§12); Alvo has no `double` |
| cel-go constant folding (`cel.OptimizeConstantFolding`) | evaluates constant sub-expressions at compile time | adopted for **failure detection only**: a built-in call whose every argument is a literal is evaluated at apply and refused there if it would always fail (E5) — spec §0 "fail-fast at save" |
| RFC 3339 §5.6 | `date-time = full-date "T" full-time`, offset `Z` or `±hh:mm` | `timestamp(text)`, `string(Timestamp)` |
| B spec §7 (guided condition) | one table, canonical CEL, a strict recognizer, a conformance fact over the real validator | three new Text rows emit `startsWith/endsWith/contains(new.f, 'v')` — same table, same proofs (E9) |
| `alvo-specifikacia.md` §0.6 | CEL for conditions, safe-by-construction, in-transaction | every new built-in is scalar, total over its domain or fails closed, side-effect-free, O(input) |
| `baas-analyza.md` §2.8 acceptance | 375 px without horizontal scroll; WCAG AA; key actions reachable | the function list wraps; every insert is a named button; AC 5 |

## 4. Decisions (each with what it costs if wrong)

| # | Decision | Why | Cost if wrong |
|---|---|---|---|
| E1 | **Built-ins v2 = 19 names, 32 overloads** (§5): text `lowerAscii upperAscii trim replace substring size contains startsWith endsWith`; numbers `math.abs math.round math.ceil math.floor math.greatest math.least`; conversions `string int timestamp`; time `now` (legacy, Mutate only) | each covers a hook author's need that nothing else in Alvo can express (normalise text in a mutate, test text in a condition, cut a text to its field's `maxLength`, money rounded to whole units or to cents, a fixed date in a condition, a number or id written into a text field — now joinable with `+`, D-6) and is a CEL-standard or cel-go name | a name added is a name kept (§13); every one is cheap to keep because each is a pure ~10-line body with pinned SQL notes for C2 |
| E2 | **`abs`/`round` become `math.abs`/`math.round`**; the parser learns one production, a qualified name `Identifier '.' Identifier '('` whose dotted name the catalog knows (built-ins only: a host name cannot hold a dot, `HostCelFunction` R1) | cel-go's names; nothing using the bare names has shipped: `abs`/`round` exist only on the unmerged C1 branch (`origin/main` @ `b25cffc` has neither, and there is no tag or release) | every C1 test, doc and skill line naming `abs`/`round` changes in plan Task 1 (swept for the claim, not the file); after a release this rename would be breaking, which is exactly why it happens now |
| E3 | **Receiver form stays out** (`x.trim()`, `new.email.endsWith(…)`): every function keeps the global form; the refusal of a receiver spelling (both `x.f()` and `new.x.f()`) names the global call as its fix | adding member-call syntax touches every Admin text scanner (`CelNames`, B's `ConditionText` recognizer), the corpus, and the field-path narrowing (`cel.md` deviation 8); admitting it later accepts strictly more source, so nothing is foreclosed | an agent writes `new.email.endsWith(…)` and gets one refusal with the fix; cost measured by the assistant eval when it runs |
| E4 | **`lowerAscii` becomes an ordinary catalogued built-in**: any `String` expression, Condition + Mutate, body in `CelBuiltInFunctions`; its own parse arm, legacy type-check arm and interpreter arm go. `now()` stays the one legacy call | `upperAscii` arrives as an ordinary function; a field-only `lowerAscii` beside an any-expression `upperAscii` is a variant of a variant. Closes C1 X10 (`trim(email) == 'x'` compiled in a condition, `lowerAscii(email) == 'x'` did not). **`lowerAscii` is on `main`** (field-only, Mutate-only, `examples/complex-crm/crm.alvo.json:109`): D only **widens** it — every source `main` accepts compiles and evaluates to the same value, so this is not a breaking change | the corpus moves: the 30 rows whose source names `lowerAscii` are re-judged and the moved ones regenerated (plan Task 2); a reviewer reads that diff |
| E5 | **A built-in call whose every argument is a literal is evaluated at apply**; a `CelFunctionException` becomes a compile error at the call ("always fails"). Host functions are never evaluated at apply | `timestamp('2026-13-01T00:00:00Z')` in a condition would otherwise answer 500 `function-failed` on every write; §0 "fail-fast at save" | one extra evaluation per literal-only call at apply (bounded by the 2,000-character source) |
| E6 | **Operators in hook slots, fail-closed** (D-6, D-7; §5.6): arithmetic (`+ - * /`, unary `-`, over Int and Decimal) joins Condition and Mutate; string `+` joins Mutate. In those two profiles an Int overflow, a Decimal overflow and a division by zero **throw** the internal function failure — the write rolls back with 500 `function-failed`, an after-hook condition drops its hook with a Warning, exactly as a failing function does (C1 Ruling U) — and never answer `null`. Int `/` Int truncates toward zero and stays Int (CEL langdef). Rule and Computed keep today's semantics (`EvaluateScalar` answers `null`, `CelInterpreter.cs:154-158`). Comparison and ternary stay out of Mutate; `now()` stays Mutate-only | today's arithmetic answers `null` on overflow and division by zero (`CelInterpreter.cs:607-622`) — in a before-hook condition that null reads as `false` and turns a `reject` off, the fail-open C1 §17 closed for functions. One flag from `CompiledExpression.Profile` closes it for operators, and with it "round a price to cents" (`math.round(new.price * 1.2, 2)`) and "prefix a code" (`'+421' + new.phone`) become expressible. `now()` in a condition still needs an instant threaded through `EvaluatePredicate` for after-hooks, which have no write stamp | the corpus moves: every row carrying an arithmetic or concatenation gate message (272 at C1 HEAD, by rule, listed in the commits); a hook that divides by a field that can be zero refuses that write with 500 rather than skipping; a reviewer must check the throw is gated on the profile (Review Focus) |
| E7 | **Deferred built-ins**: `matches` (RE2 vs .NET dialects disagree on `\d`, `\w`, named groups; SQLite has no regex for C2; a field's `format` pattern already validates shape), timestamp accessors (member form only in the standard, 0-based months, time zones need tzdata in the image), durations (no type), `split`/`join` (lists), `indexOf`, `math.trunc`, `math.sign` | each has a dialect, type or runtime question larger than its body | an author who needs one writes a host function (embedded) or waits |
| E8 | **The dashboard offers functions inside the hook editor**, as a disclosure under each CEL box (a mutate row in expression mode, the condition in text mode): one row per name, every overload's signature, the summary, a `built-in`/`this host` badge, and an Insert button. Filtered by the slot's profile (`Mutate` or `Condition`). Rendered by `HooksTab` fragments over an internal `FunctionOffer`, so **no new public component** | B §10 names this place; a fragment needs no `PublicApi` growth (a Razor component cannot be internal and remain a tag, B §14) | a later Rules/Computed offering (after C2) extracts the fragment into a component then, with a reason |
| E9 | **The guided table gains three Text rows** — "starts with", "ends with", "contains" — emitting `startsWith(new.f, 'v')` etc. **Generic function rows stay text-only** | the three are CEL-standard, exist in every build (so B's conformance fact can enumerate them over the built-ins-only validator), and have a fixed `(field, literal)` shape the strict recognizer reads. A host function exists per host and has any signature: the table could not prove it, the recognizer could not read it | an operator wanting `normalizePhone(new.phone) == …` switches to Text, where the list offers it |
| E10 | **The e2e world registers a host function through a test-side `IAlvoBuilder` adapter** over the shipped host's service collection; no public seam is added to `AlvoHost`. The standalone image and `scripts/demo-admin` stay built-ins only | `AddCelFunction` touches only `builder.Services` (`AlvoBuilderExtensions.cs:97-105`) and the catalog reads registrations at first resolve; C1's own Api test world already registers this way (`test/MMLib.Alvo.Api.Tests/CelFunctionsWorld.cs`, a private `Builder(IServiceCollection) : IAlvoBuilder` used after `AddAlvo`); `MMLib.Alvo.Host` is `IsPackable=false`, so a public overload would have no external caller (`alvo-architecture-rules`); C1 G5 says the standalone image knows built-ins only | the e2e registers after `AddAlvo`'s callback instead of inside it — equivalent for this extension, stated in the world's remarks. The **copyable** registration is in the embedded sample instead (E16) |
| E11 | **The e2e writes through the HTTP Data API** with a dev key the world configures (`Alvo:Auth:DevKeys:0:*`, header `X-Alvo-Api-Key: {id}.{secret}`, as `scripts/demo-admin:124-127,366-370`) | "use and evaluate them in the API" — the maintainer's words; the in-process `IAlvoData` would skip the API's own pipeline | none beyond one more configured key in a test world |
| E12 | **No new eval case; no assistant-instruction change.** The hooks skill's `mutate-functions` region and its `cel-condition`/`cel-mutate` examples follow the catalog (drift-tested); `AlwaysInContextBudget` (22,758) is untouched | the assistant already calls `get_cel_functions` before writing a call (C1); the eval's 17 cases are fixed by two specs | a model's use of the new names is unmeasured until `scripts/eval-assistant` runs (plan Task 18 runs it if a model is configured, else records "not run") |
| E13 | **The dashboard restates the core's signature text** (`name(p: T, …) -> R`, `?` for nullable) and **a Host.Tests agreement fact pins it** to `CelFunction.Signature()` for every built-in | B's pattern: every Admin restatement of a core rule is pinned in Host.Tests, the one suite that sees both | none |
| E14 | **`math` is reserved as a host function name** (C1 X3's reasoning: a word an agent reads as CEL syntax) | `math(x)` beside `math.abs(x)` reads as a namespace collision | a host wanting `math` renames |
| E15 | **`cel.md`'s built-in table is drift-tested** against the catalog (a Host.Tests fact reading the table's first column) | the table is the host developer's and operator's reference; the skill region is drift-tested already, the doc was not | none |
| E16 | **The embedded sample registers a host function** (D-3): `SampleHost.CreateBuilder`'s `AddAlvo` chain gains `.AddCelFunction("normalizeVin", …)`; the README gains "Registering a CEL function"; an integration fact boots the sample over a **test-local** copy of `vehicles.alvo.json` with one mutate hook calling it, asserts `IAlvoManagement.GetCelFunctionsAsync` lists it with provenance `Host`, and a Data API write stores its result. `examples/vehicle-registry/vehicles.alvo.json` is **unchanged** | the e2e adapter (E10) is test code nobody copies; the sample is the documented answer to "how do I embed Alvo". The shared descriptor stays hook-free because the standalone image (built-ins only, C1 G5) serves the same file and `Both_modes_generate_the_same_routes` pins that parity | the sample carries a function no shipped descriptor calls — its README says why, and the fact proves it works |
| E17 | **`math.round(x: Decimal, digits: Int) -> Decimal`**, `digits` 0–28, halves away from zero like the one-argument form (D-5, F17). A literal `digits` outside 0–28 is refused at apply even when `x` is a field; a computed one fails closed | "round a price to cents" is the most common money rule in a hook and needs no operator; `Math.Round(decimal, int, MidpointRounding.AwayFromZero)` is exactly this and 28 is `decimal`'s own scale limit | one overload kept forever; C2 must reproduce it (PG `round(numeric, int)` rounds half away from zero; SQLite's `round` is REAL — measure) |
| E18 | **`string()` over a `date` field is refused at apply** (D-9), with a fix | a `date` value reaches CEL as midnight UTC, so `string(due)` would pin `2026-10-05T00:00:00Z`; a later Date type would want `2026-10-05`, and changing an output a hook already stores is breaking. Refusing now keeps either answer additive | an author who wants a date's text cannot have it in D; the fix names `datetime` or a client-written text field |
| E19 | **A built-in's failure on the caller's data answers 500 `function-failed`**, like a host function's (D-8, Q7): `substring` past the end, `int('x')`, `timestamp(bad)`, an operator's overflow or division by zero | one failure contract for every evaluation failure in a hook; a 422 split (Alvo-authored reason vs host exception) is a contract change that deserves its own decision — recorded for the maintainer | an agent reading a 500 may retry a write that can never succeed; the detail names the function and the reason, so it can tell |

## 5. Built-ins v2 — the catalog (interpreter now; C2 must produce the same in SQL)

All functions below: profiles **Condition + Mutate** (except `now`), every parameter non-nullable so **any null argument →
null** (C1 F3; SQL's `NULL` in, `NULL` out), never culture-sensitive, `IsHost = false`. "Standard" = CEL langdef;
"ext" = cel-go extension library; "Alvo" = an Alvo shape or overload. Overloads of one name share parameter names by
position (pinned, so the dashboard's insert template is stable).

### 5.1 Text

| Function | Signature(s) | Origin | Semantics | Edge cases pinned by tests | Note for C2 |
|---|---|---|---|---|---|
| `lowerAscii` | `(text: String) -> String` | ext (member form → F11) | `A`–`Z` → `a`–`z`, nothing else (the existing `FoldAsciiUpperCase` loop, `CelInterpreter.cs:301`) | `'ÄBC'` → `'Äbc'`; `'ẞ'` unchanged; now takes any String expression: `lowerAscii(trim(new.email))` | PG `lower()` is collation/ICU-dependent → `translate(t, 'ABC…Z', 'abc…z')`; SQLite `lower()` is ASCII-only without ICU |
| `upperAscii` | `(text: String) -> String` | ext (F11) | `a`–`z` → `A`–`Z`, nothing else | `'äbc'` → `'äBC'`; `'ß'` unchanged | as `lowerAscii`, mirrored |
| `trim` | `(text: String) -> String` | ext (F1, F5) | unchanged from C1 | unchanged | unchanged |
| `replace` | `(text: String, search: String, replacement: String) -> String` | ext (F1, F6) | unchanged from C1 | unchanged (incl. the 1,048,576 growth cap) | unchanged |
| `substring` | `(text: String, start: Int) -> String`, `(text: String, start: Int, end: Int) -> String` | ext (F11) | code points, 0-based, `start` inclusive, `end` exclusive; the one-argument form runs to the end; the cut copies the original UTF-16 code units | `substring('héllo', 1, 3)` = `'él'`; `substring('😀ab', 1)` = `'ab'`; `start == size` → `''`; **fails closed** when `start < 0`, `end < start`, or either exceeds the text's code points (cel-go: an error) — reason "a position is outside the text", no positions or sizes in it | PG `substr(t, start + 1, end - start)` counts characters; SQL does not raise on a range past the end → C2 must guard or refuse |
| `size` | `(text: String) -> Int` | standard (conformant) | unchanged from C1 | unchanged | unchanged |
| `contains` | `(text: String, search: String) -> Bool` | standard (F11) | ordinal; `search == ''` → `true` | `contains('abc', '')` = `true`; `contains('ABC', 'b')` = `false` (no case folding: write `contains(lowerAscii(x), 'b')`) | PG `strpos(t, s) > 0` (empty → 1, true); SQLite `instr(t, s) > 0`; binary collation needed (PG `COLLATE "C"`) |
| `startsWith` | `(text: String, prefix: String) -> Bool` | standard (F11) | ordinal; empty prefix → `true` | `startsWith('é', 'e')` = `true` (no normalisation, F16) | PG `left(t, char_length(p)) = p`; SQLite `substr(t, 1, length(p)) = p` |
| `endsWith` | `(text: String, suffix: String) -> Bool` | standard (F11) | ordinal; empty suffix → `true` | `endsWith('a@kros.sk', '@kros.sk')` = `true` | PG `right(t, char_length(s)) = s` |

### 5.2 Numbers

| Function | Signature(s) | Origin | Semantics | Edge cases pinned by tests | Note for C2 |
|---|---|---|---|---|---|
| `math.abs` | `(x: Int) -> Int`, `(x: Decimal) -> Decimal` | ext (conformant name; F8) | unchanged from C1 `abs` | unchanged (smallest Int fails closed) | unchanged |
| `math.round` | `(x: Int) -> Int`, `(x: Decimal) -> Decimal`, `(x: Decimal, digits: Int) -> Decimal` | ext (conformant name; F8); the `digits` overload is Alvo's (F17) | one argument: unchanged from C1 `round`, halves away from zero, Int is the identity. With `digits`: `x` rounded to `digits` fractional digits, halves away from zero (`Math.Round(x, digits, MidpointRounding.AwayFromZero)`); **fails closed** when `digits` < 0 or > 28 — and a literal `digits` out of range is refused at apply (§6.4) | `math.round(2.345, 2)` = `2.35`; `math.round(-2.345, 2)` = `-2.35`; `math.round(2.5, 0)` = `3`; `math.round(1.2, 5)` = `1.2` (no padding); `math.round(qty, 2)` binds the Decimal overload (F7 widening); `digits` 29 → fails | PG `round(numeric, int)` rounds half away from zero ✓; SQLite `round(x, n)` works on REAL → **measure** |
| `math.ceil` | `(x: Int) -> Int`, `(x: Decimal) -> Decimal` | ext (F9) | smallest whole number ≥ `x`; scale 0; Int is the identity | `1.2` → `2`; `-1.5` → `-1`; `2.0` → `2` | PG `ceil(numeric)`; SQLite `ceil` exists only when the bundled SQLite has math functions — **measure** |
| `math.floor` | `(x: Int) -> Int`, `(x: Decimal) -> Decimal` | ext (F9) | largest whole number ≤ `x`; scale 0; Int is the identity | `1.8` → `1`; `-1.2` → `-2` | as `ceil` |
| `math.greatest` | `(a: Int, b: Int) -> Int`, `(a: Decimal, b: Decimal) -> Decimal` | ext (F10) | the larger; `a` when equal; Int + Decimal binds the Decimal overload (C1 F7 widening) | `math.greatest(1, 2.5)` = `2.5`; `math.greatest(1.0, 1.00)` = `1.0` | PG `greatest` **ignores** NULLs → C2 wraps `CASE WHEN a IS NULL OR b IS NULL THEN NULL …`; SQLite `max(a, b)` is NULL-strict |
| `math.least` | `(a: Int, b: Int) -> Int`, `(a: Decimal, b: Decimal) -> Decimal` | ext (F10) | the smaller; `a` when equal | `math.least(size(t), 40)` — the truncation recipe below | as `greatest` (`least` / `min`) |

**The truncation recipe** the docs and the skill teach (Ruling V refuses an overlong mutate value with 403, so this is a
real need): `substring(new.title, 0, math.least(size(new.title), 40))`.

### 5.3 Conversions

| Function | Signature(s) | Origin | Semantics | Edge cases pinned by tests | Note for C2 |
|---|---|---|---|---|---|
| `string` | `(value: Int)`, `(value: Decimal)`, `(value: Bool)`, `(value: Uuid)`, `(value: Timestamp)` `-> String` | standard (F12) | Int: invariant digits, `-` for negative. Decimal: invariant, `.` separator, no exponent, **no trailing fractional zeros**, no trailing `.`, zero is `'0'`. Bool: `'true'`/`'false'`. Uuid: lower-case `8-4-4-4-12`. Timestamp: RFC 3339 in UTC, `Z`, fractional seconds only when non-zero and without trailing zeros (cel-go's `RFC3339Nano` output) | `string(1.50)` = `'1.5'`; `string(2.0)` = `'2'`; `string(-0.0)` = `'0'`; `string(due)` over a `date` field is **refused at apply** (E18, §6.5) | engine text casts keep scale (`1.50`) → C2 must trim; SQLite REAL loses precision past 15 digits — measure |
| `int` | `(value: Decimal) -> Int`, `(value: String) -> Int` | standard (F14) | Decimal: truncate toward zero; String: `^[+-]?[0-9]+$`, invariant, base 10. **Fails closed** out of the 64-bit range or on any other text (CEL: an error) | `int(2.9)` = `2`; `int(-2.9)` = `-2`; `int('+7')` = `7`; `int(' 7')` fails (write `int(trim(x))`); `int('1e3')` fails | PG `trunc(x)::bigint`, `t::bigint` raises on bad text ✓ (refusal semantics already match); SQLite `CAST` silently yields 0 → C2 must guard |
| `timestamp` | `(text: String) -> Timestamp` | standard (F13) | RFC 3339: `YYYY-MM-DDTHH:MM:SS[.f{1,9}](Z|±HH:MM)`, upper-case `T` and `Z`, year 0001–9999; digits past the seventh fraction digit are truncated (100 ns). **Fails closed** on anything else | `timestamp('2026-10-05T12:00:00Z')`; `timestamp('2026-10-05T14:00:00+02:00') == timestamp('2026-10-05T12:00:00Z')` is `true`; `'2026-10-05'` fails (no time); `'2026-10-05 12:00:00Z'` fails | PG `t::timestamptz` is more lenient → C2 must validate the literal (it is a literal in practice, already checked at apply by E5) |

### 5.4 Time

| Function | Signature | Origin | Semantics |
|---|---|---|---|
| `now` | `() -> Timestamp` | Alvo (legacy grammar, unchanged) | the instant this write is stamped with; **Mutate only** (E6) |

### 5.5 Failure reasons (exact text; the 500 detail and the apply-time refusal quote them)

| Function | Reason |
|---|---|
| `substring` | `a position is outside the text` |
| `int` (Decimal) | `the value is outside the range of an Int` |
| `int` (String) | `the text is not a whole number such as 42 or -7` |
| `timestamp` | `the text is not an RFC 3339 timestamp such as 2026-10-05T12:00:00Z` |
| `math.round` (`digits`) | `digits must be from 0 to 28` |
| `_+_`, `_-_`, `_*_`, `-_` (Int) | `the result is outside the range of an Int` |
| `_+_`, `_-_`, `_*_`, `_/_` (Decimal) | `the result is outside the range of a Decimal` |
| `_/_` | `the divisor is zero`; Int `/` of the smallest Int by `-1`: `the result is outside the range of an Int` |
| `math.abs`, `replace` | unchanged from C1 |

No reason carries a value from the row (C1 §6: "text Alvo wrote, never the host's", and never the caller's data).
An operator's failure names it by CEL's own overload name (`_/_`), so the problem detail reads "The CEL function '_/_'
failed: the divisor is zero. Nothing was written." (`AlvoExceptionHandler.FunctionFailedDetail`, unchanged).

### 5.6 Operators in hook slots (D-6, D-7)

| Operator | Profiles after D | Operands → result | Null | Fails closed (Condition, Mutate only) |
|---|---|---|---|---|
| `+ - *` | Computed, **Condition, Mutate** | Int, Int → Int (checked 64-bit); any Decimal → Decimal | a null operand → null (unchanged) | Int overflow; Decimal overflow |
| `/` | Computed, **Condition, Mutate** | Int, Int → Int, **truncated toward zero** (`7 / 2` = `3`, `-7 / 2` = `-3`); any Decimal → Decimal (28 significant digits) | null → null | divisor zero (Int or Decimal); smallest Int `/ -1` |
| unary `-` | Computed, **Condition, Mutate** | Int → Int; Decimal → Decimal | null → null | `-` of the smallest Int |
| string `+` | Computed, **Mutate** | String, String → String; no implicit conversion (write `string(x)`) | **Mutate: null → null** (F18); Computed: an operand that may be null is still refused at compile (unchanged) | never (Ruling V refuses an overlong stored value with 403) |

* **The flag.** `EvalState` gains `FailClosed`, set from `CompiledExpression.Profile is Condition or Mutate`. Only those
  two profiles throw; `EvaluateScalar` (Computed) and a Rule's `EvaluatePredicate` keep answering `null` exactly as
  today, so no generated column and no rule changes behaviour.
* **Int vs Decimal at run time** is decided by the operands' CLR values (a record's Integer column arrives as an
  integral type, a literal `2` as `long`): two integral operands take the checked `long` path, anything else the
  `decimal` path. In Computed the `decimal` path stays for every operand pair, as today.
* **Not in D:** `%` (Alvo's lexer has no `%`, and adding it grows the public `CelBinaryOperator` enum and needs an SQL
  rendering that differs per engine — SQLite's `%` casts to integer), Double (Alvo has no `double` type), comparison and
  ternary in Mutate, string `+` in Condition (§12).
* **Apply-time folding** (E5) stays call-only: `1 / 0` over literals is not refused at apply in D; it fails the write.

## 6. Parser and type checker

### 6.1 Qualified names (E2)

`CelParser.ParseIdentifierExpression` (`CelParser.cs:397` at C1 HEAD `3a3c31f`) gains one branch **before** a field reference is resolved:

```
Primary       := … | Identifier CallTail | QualifiedCall | Identifier [ '.' Identifier ]
QualifiedCall := Identifier '.' Identifier CallTail      (only when "<first>.<second>" is a catalogued name)
```

The lookahead is three tokens (`Identifier`, `Dot`, `Identifier`, `LeftParen`) and the catalog decides, so `new.total`,
`old.status` and a field named `math` without a call are untouched; `math.nope(x)` (not catalogued) keeps today's "no
nested field access" refusal, now with a "did you mean" over the `math.` names. The argument list is parsed by the
existing `ParseCatalogCall` (`:440`, one `MaxDepth` unit per level). `CelCall.Name` is the dotted name, which every walker
already treats as an opaque string; `FindPosition` (`CelTypeChecker.cs:1172`) searches it ordinally and finds it.

**Precedence, stated for when receiver syntax lands (E3):** a catalogued dotted name wins over every other reading of
`a.b(` — cel-go's own namespace-first resolution. So if `x.trim()` is ever admitted, `math.abs(x)` still means the
namespaced global and never "`abs` called on a field named `math`"; `new.`/`old.` are never namespaces.

### 6.2 Receiver spellings (E3)

`NestedAccessFix` (`CelParser.cs:584`) already answers `x.trim()` with the global form. D adds the same fix to the
second refusal, `new.title.trim()` (`ParseFieldPath`, `CelParser.cs:597`, the throw at `:613`: "no nested field access beyond old./new."),
which today has no fix: `Write trim(new.title): Alvo calls a function with the value as its first argument, never as
new.title.trim().` Messages (and so the corpus) do not move; only fixes do.

### 6.3 `lowerAscii` as a catalogued function (E4)

| Before (C1) | After (D) |
|---|---|
| `ParseCall` arm `CelCall.LowerAscii => ParseLowerAsciiCall()` (field-only) | arm removed; the catalog arm parses it like any function |
| `CheckLegacyCall` handles `lowerAscii` and `now` under the `Call` construct row (Mutate only) | handles `now` only; `lowerAscii` goes through `CheckCatalogCall` and the `FunctionCall` row |
| `CelBuiltInFunctions.LowerAscii` has `Body: null`, `MutateOnly`, `ResultNullable: true` | a body (`FoldAsciiUpperCase`, moved), `ConditionAndMutate`, `ResultNullable: false` (null in → null out by R3) |
| `CelInterpreter.EvaluateCall` arm by name | removed; the bound overload is invoked |
| `FieldOnlyCallFix` for `lowerAscii(trim(x))` | gone (it parses); `has`/`changed` fixes keep their text |

`CelCall.LowerAscii` stays as a constant (other code names it); `cel.md` "The two legacy calls" becomes "The legacy
call". The corpus rows that move are exactly those whose source names `lowerAscii` and that the new path judges
differently (arity errors instead of syntax errors; profile messages from the function gate instead of the `Call` row;
acceptance in Condition); plan Task 2 regenerates them by rule and lists them in the commit.

### 6.4 Apply-time evaluation of constant calls (E5)

In `CelTypeChecker.Bind` (`:973`), after an overload is bound and **only when** the profile gate passed, the overload is a
built-in with a body (`!IsHost && !IsLegacy`) and every argument node is a `CelLiteral`: invoke it with the literal
values. A `CelFunctionException` adds one `CelCompilationError` at the call's position:

`'{name}(...)' always fails with these constant arguments: {reason}.` — fix: `Correct the constant, or pass a field
instead of a literal.`

Any other outcome changes nothing (the tree is not rewritten — no folding, the value is computed again at run time).
Host functions are never invoked at apply: purity is by contract only (C1 X7), and an apply must not run host code.

**Literal arguments checked alone (E17).** A built-in may declare a *constant check* — an internal
`CelFunction.ConstantCheck`, given each argument's literal value or `null` for a non-literal one, answering a reason or
`null`. `Bind` runs it whenever any argument is a literal, so `math.round(new.price, 30)` is refused at apply although
`new.price` is a field, with the same message and fix as above. In D only the `digits` overload of `math.round`
declares one; its body throws the same reason when `digits` is computed.

### 6.5 `string()` over a `date` field (E18)

In `Bind`, when the bound overload is `string` with a `Timestamp` parameter and its argument is a field reference
(`f`, `old.f`, `new.f`) whose declared type is `date`, one error at the call:

`'string(...)' cannot take the date field 'due' yet: its text form is not settled, and a hook that stored one could not
change it later.` — fix: `Store the date's text from the client, or make 'due' a datetime field, whose text is an RFC
3339 instant.`

No other expression can carry a `date` value in Condition or Mutate (no ternary, no operator yields one), so the field
reference is the whole surface. A comparison with `timestamp(…)` stays legal.

### 6.6 Operators (E6, §5.6)

| Before (C1 HEAD) | After (D) |
|---|---|
| `_allowedProfiles[Arithmetic] = _computedOnly` | `{ Computed, Condition, Mutate }` (covers binary `+ - * /` and unary `-`) |
| `_allowedProfiles[Concatenation] = _computedOnly` | `{ Computed, Mutate }` |
| `CheckConcatenation` runs `RequireNeverNull` in every profile that passed the gate | only in `_sqlRenderedProfiles` (Computed's SQL `||` and CEL's missing null overload disagree; Mutate is interpreter-only and propagates null, F18) |
| `RequireTwoStrings` fix: "A computed field has no string() conversion" | in Condition/Mutate: `Write string(x) to join a number, a flag, an id or an instant.`; Computed's fix unchanged |
| messages "… legal only in the Computed profile" (arithmetic, negation, concatenation) | name the profiles that admit it: `Arithmetic is legal only in the Computed, Condition and Mutate profiles; '+' is not allowed here.`, `Arithmetic negation ('-') is legal only in the Computed, Condition and Mutate profiles.`, `String concatenation ('+' over two strings) is legal only in the Computed and Mutate profiles.` |

The message change moves every corpus row that carries one of the three (Rule and Access rows too — only their text);
the Condition/Mutate rows additionally lose the error or become accepted. 272 rows carry one at C1 HEAD (68 per
non-Computed profile); none names `lowerAscii`, so the operator regeneration and plan Task 2's are disjoint.

## 7. Per-profile legality (after D)

| Function | Rule | Computed | Condition | Mutate | Access |
|---|---|---|---|---|---|
| `now()` (legacy grammar) | ✗ | ✗ | ✗ | ✓ | ✗ |
| the 18 other built-ins (incl. `lowerAscii`) | ✗ | ✗ | ✓ | ✓ | ✗ |
| same — after C2 | ✓ where SQL reproduces §5 | ✓ where SQL reproduces §5 | ✓ | ✓ | ✗ |
| host function | ✗ | ✗ | ✓ | ✓ | ✗ |

| Construct | Rule | Computed | Condition | Mutate | Access |
|---|---|---|---|---|---|
| arithmetic `+ - * /`, unary `-` | ✗ | ✓ (null on failure) | ✓ (fails closed) | ✓ (fails closed) | ✗ |
| string `+` | ✗ | ✓ (nullable operand refused) | ✗ | ✓ (null → null) | ✗ |
| comparison, ternary | as today | as today | as today | ✗ | as today |

`cel.md`'s construct table row "Legacy call (`lowerAscii(field)`, `now()`)" becomes "Legacy call (`now()`)", and its
Arithmetic and Concatenation rows gain the profiles above.

## 8. Errors (exact first sentences; the corpus pins message prefixes)

| Case | Message | Fix |
|---|---|---|
| receiver spelling after `old.`/`new.` | `Alvo has no nested field access beyond old./new.; use a single field name.` (unchanged) | `Write trim(new.title): Alvo calls a function with the value as its first argument, never as new.title.trim().` (new; only when the member is catalogued) |
| unknown qualified name | `Alvo has no nested field access; use a single field name.` (unchanged) | `Did you mean 'math.round'? Known functions: …` (when the first part is a catalogued namespace) |
| constant call that always fails | `'timestamp(...)' always fails with these constant arguments: the text is not an RFC 3339 timestamp such as 2026-10-05T12:00:00Z.` | `Correct the constant, or pass a field instead of a literal.` |
| host name `math` | `ArgumentException`: `The CEL function name 'math' is reserved: it is the namespace of CEL's math functions.` | — |
| runtime failure of a new built-in | 500 `…/errors/function-failed`, detail names the function and the §5.5 reason | — |
| `math.round` with a literal `digits` out of range | `'math.round(...)' always fails with these constant arguments: digits must be from 0 to 28.` | `Correct the constant, or pass a field instead of a literal.` |
| `string()` over a `date` field | `'string(...)' cannot take the date field 'due' yet: its text form is not settled, and a hook that stored one could not change it later.` | `Store the date's text from the client, or make 'due' a datetime field, whose text is an RFC 3339 instant.` |
| arithmetic in Rule/Access | `Arithmetic is legal only in the Computed, Condition and Mutate profiles; '+' is not allowed here.` | `Move this calculation into a computed field.` (unchanged) |
| string `+` in Condition | `String concatenation ('+' over two strings) is legal only in the Computed and Mutate profiles.` | `Join the text in a computed field, and compare that field here instead.` (unchanged) |
| `'#' + new.qty` in Mutate | `'+' joins two strings or adds two numbers; found String and Int, and CEL converts neither implicitly.` (unchanged) | `Write string(x) to join a number, a flag, an id or an instant.` |
| operator overflow or division by zero (Condition/Mutate) | 500 `…/errors/function-failed`, detail `The CEL function '_/_' failed: the divisor is zero. Nothing was written.` | — |

## 9. The dashboard offers the catalog (E8)

### 9.1 Where and what

```
Set field 1 to
[ normalizeFrameNumber(new.frame_number)                       ]
  (the cel/check sentences, unchanged)
A CEL expression in the Mutate profile, stored as {"$cel": "…"}.
▸ Functions you can call here (20)
  ┌───────────────────────────────────────────────────────────────┐
  │ normalizeFrameNumber(value: String) -> String?      this host │
  │ Upper-cases a frame number and drops every character that is  │
  │ not a letter or a digit.                            [Insert]  │
  │ math.round(x: Int) -> Int                            built-in │
  │ math.round(x: Decimal) -> Decimal                             │
  │ x rounded to a whole number, halves away from zero …  [Insert]│
  └───────────────────────────────────────────────────────────────┘
```

* **Placement.** Under the expression box of a mutate row in expression mode (B Task 9 `MutateValue(row, i)`,
  `#hook-mutate-value-{i}`), after its hint; under the condition box in **text** mode (B Task 19 `ConditionField`,
  `#hook-condition`). Not in guided mode (the rows emit their own CEL), not under literal mutate values, not under the
  payload or `to` boxes (JSONata templates, not CEL).
* **The disclosure** is the dashboard's existing `<details class="a-disclosure">` (`FieldEditor.razor:336`),
  collapsed, summary `Functions you can call here ({n})`, where `n` counts names, not overloads. Test id
  `fn-list-{inputId}`.
* **One row per name** (`ListRow`, test id `fn-{name}`): every overload's signature in mono (`a-fn__signature`, wraps
  anywhere), the summary as text, a badge — `built-in` (`a-badge`) or `this host` (`a-badge a-badge--accent`) — and an
  **Insert** button (`AlvoButton` ghost small, test id `fn-insert-{name}`, `aria-label="Insert {name} into {box label}"`).
  Names in ordinal order, as the catalog lists them. A test id such as `fn-math.round` holds a dot, so it is only ever
  reached through `GetByTestId` (an attribute match), never written as a CSS `#id` or class selector.
* **Filter by profile:** a function is offered in a box when its `Profiles` contain the box's profile — `Mutate` for a
  mutate value, `Condition` for a condition. So `now` is offered under a mutate value and not under a condition.
* **The hint under the summary line** (inside the disclosure): *"A built-in works in every Alvo build; a host function
  exists only in this host, and an import into another build refuses it. Insert writes the call with its parameter
  names — replace each with a field or a value; the check under the box says what is still wrong."*

### 9.2 Insert

1. The browser is asked for the box's caret (`selectionStart`, `selectionEnd`; an input keeps them after the button
   takes focus). No caret known → the end of the text.
2. `FunctionOffer.Insert(text, start, end, template)` (pure, unit-tested) replaces the selection with the template and
   answers the new text plus the range to select next.
3. The new text goes through the box's own change handler (B Task 9 `TypeMutateText(i, text)`, B Task 8
   `TypeCondition(text)`), so dirty tracking and the live `cel/check` run exactly as for typing.
4. After the render the browser focuses the box and selects the range: the **first placeholder** to replace.

**The template** is `name(p1, p2, …)` from the overload with the most parameters (first in catalog order on a tie).
**Mutate prefill:** in a mutate row, when the box is empty, the row has a field, the point has a `new.` image (every
point a mutate is offered at) and the first parameter's type accepts the field's CEL type (`CelFieldType.Of`, with
Int→Decimal widening), the first argument is `new.{field}` instead of its parameter name — so a one-parameter function
inserts a complete, checkable call (`normalizeFrameNumber(new.frame_number)`) and the next placeholder, if any, is
selected. `now` inserts `now()` with the caret after it.

### 9.3 Data

`ManagementGateway.CelFunctionsAsync(ct)` → `IReadOnlyList<CelFunctionInfo>`, cached in a `Slot` like capabilities
and dropped by `Invalidate()`. A failure to ask (`ManagementRequestException`, `ManagementForbiddenException`,
`HttpRequestException`, cancellation) answers an empty list and caches nothing — the disclosure is then simply absent:
a helper must never be the reason an operator cannot edit (the gateway's `CheckExpressionAsync` rule). `HooksTab` loads
the list once per component, when the hook sheet first opens.

### 9.4 What the dashboard must never do (security)

Evaluate a function (it inserts text; `cel/check` and apply judge it); render a summary as markup (host-authored text
goes through Razor's encoding — an e2e registers a summary containing `<b>`); offer a function in a profile its
`Profiles` do not list; hide that a function is host code (the badge is always shown).

### 9.5 Accessibility and phone

The summary element is keyboard-operable natively; each Insert is a named button; the inserted range is announced by
the box's existing `aria-describedby` check sentence. At 375 px the row stacks (signature, summary, badge + Insert),
signatures wrap at any character, and with the list open under a mutate value and under a condition three checks pass
(AC 5): `AssertNoHorizontalScrollAsync` (the document and `main.a-content`), a new `AssertSheetFitsAsync` (the
`AlvoEditor` sheet's own scroll container, `.a-editor__body`, which the first check never measured), and
`AssertNoVerticalTextAsync` (a word broken one character per line). The CSS is written for the last one: no
`overflow-wrap: anywhere` element sits in a flex row unless its container has `min-width: 0` and stretches it to the
row's width — the exact defect that assertion was written for.

## 10. The guided condition's function operators (E9)

Three rows in B's `ConditionTable` (B Task 1, as built with `ConditionFieldKind`):

| Operator (words) | CEL (canonical) | Kinds | Legal at | When the field is empty |
|---|---|---|---|---|
| starts with | `startsWith({f}, {v})` | Text | image points | false: a call with an empty argument is empty, and an empty condition does not fire |
| ends with | `endsWith({f}, {v})` | Text | image points | false |
| contains | `contains({f}, {v})` | Text | image points | false |

* **Value required:** `ConditionText.Refusal` (B Task 16) refuses an empty value for these three — "every text starts
  with nothing; write the text it starts with" (and the matching words for the other two). Not `Choice`: an enum has
  declared values, compared with `is`.
* The generator, the strict recognizer's patterns (`PatternOf` escapes the format, B Task 17) and the conformance fact
  (B Task 18, every offered cell against the real validator) read the table, so they cover the new rows without new
  code; their tests gain the rows (the conformance count rises).
* No negated forms ("does not start with"): `!startsWith(null, …)` is `true` for an empty field, the opposite of what
  "does not start with X" reads as; text mode writes it when meant.

## 11. Set up from code, end to end (E10, E11)

### 11.1 The world

`AdminWorld` (`test/MMLib.Alvo.Admin.Tests.EndToEnd/AdminWorld.cs:91-158`) gains one seam,
`protected virtual void Configure(IAlvoBuilder alvo)`, called right after `Configure(IServiceCollection)` with a private
adapter `AlvoServices(IServiceCollection services) : IAlvoBuilder`. A new `HostFunctionWorld : AdminWorld` boots the
bike-workshop descriptor, configures one dev key, decorates `CheckExpressionAsync` to record every verdict by source
(so a scenario can wait for *the* check of the text it inserted and assert it is clean — the deterministic form of
"green"), and registers:

```csharp
alvo.AddCelFunction(
    "normalizeFrameNumber",
    (string value) => new string([.. value.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant)]),
    "Upper-cases a frame number and drops every character that is not a letter or a digit.");
```

A function no built-in composition can express (no built-in removes "every non-alphanumeric"), pure, thread-safe.
Every scenario that asserts an *absence* of the list first waits for a signal that the list was loaded (it is shown in
the same session, or the world records the `cel/functions` call), so no absence can pass because the load had not
finished; every scenario that asserts a clean check waits for the recorded verdict of that exact source rather than
counting sentences right after typing.

### 11.3 The embedded sample (E16)

`samples/MMLib.Alvo.Samples.EmbeddedHost/SampleHost.cs` registers, in its one `AddAlvo` chain:

```csharp
.AddCelFunction(
    "normalizeVin",
    (string vin) => new string([.. vin.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant)]),
    "Upper-cases a vehicle identification number and drops every character that is not a letter or a digit.")
```

The README gains "Registering a CEL function": the registration, the hook a descriptor would write
(`"vin": { "$cel": "normalizeVin(new.vin)" }` in a `beforeCreate` mutate), why `vehicles.alvo.json` itself carries no
such hook (the standalone image serves the same file and knows built-ins only; `Both_modes_generate_the_same_routes`
pins the parity), and the purity contract. `EmbeddedSampleTests` gains one fact: the sample booted over a **test-local**
descriptor — `vehicles.alvo.json` read at run time with that one hook added, written to a temp file, so it can never
drift from the shared file — lists `normalizeVin` with provenance `Host` through `IAlvoManagement` (as a bootstrap
administrator the test registers), and `POST /api/alvo/vehicles` with `vin: "1hgcm82633a004352"` stores
`1HGCM82633A004352`.

### 11.2 Scenarios (real browser, real host, real SQLite)

| Scenario | Steps | Asserts |
|---|---|---|
| `A_host_function_is_offered_inserted_applied_and_stores_its_result` | `bikes` → On write → New hook → beforeCreate → mutate → Field 1 `frame_number` → an expression → open the list → Insert `normalizeFrameNumber` → Add → Preview → apply → `POST /api/bikes` with `frame_number: "wtu 123-456 x"` | the row shows the summary and `this host`; the box reads `normalizeFrameNumber(new.frame_number)`; the recorded check of that source has no findings; apply succeeds; 201 and the stored `frame_number` is `WTU123456X` (from the response and from `GET`) |
| `A_built_in_inserted_into_a_condition_refuses_the_write_it_names` | `customers` → New hook → beforeCreate → reject → condition in Text → open the list → Insert `endsWith` (box reads `endsWith(text, suffix)`, `text` selected) → type `new.email` → write the suffix → reason → Add → apply → `POST /api/customers` twice | the condition list offers `endsWith` and **not** `now`; typing replaced the selected placeholder; the clean check; `x@example.com` → 403 with the reject text; `x@kros.sk` → 201 |
| `A_host_summary_is_text_never_markup` | a second registered function whose summary holds `<b>bold</b>` | the row's text contains `<b>bold</b>` and the row has no `b` element |
| `A_condition_offers_condition_functions_and_insert_lands_at_the_caret` (AC 4) | `customers` → condition in Text → ` > 3` → caret at Home → wait until the list is loaded → Insert `size` | the box reads `size(text) > 3` with `text` selected; the check sentence for `text` (not a field) appears **within 3 s** and focus is still in the box (`document.activeElement.id` = `hook-condition`); typing `new.last_name` replaces the placeholder and the recorded verdict for that source is clean |
| `The_function_list_fits_a_phone` | width 375, list open under a mutate value, then under a condition | `AssertNoHorizontalScrollAsync`, `AssertSheetFitsAsync`, `AssertNoVerticalTextAsync` both times |
| `Guided_rows_and_literal_values_offer_no_list` | the list is first **shown** (text mode; expression value), then the box switched back (guided; literal) | the list was there, and is gone — never an absence asserted before the list could have loaded |
| `Ends_with_is_a_guided_row_that_writes_the_built_in` (B's `GuidedConditionScenarios` world) | guided row: `email` · ends with · `@example.com` | readout `endsWith(new.email, '@example.com')`; Add; Preview has no error panel |

## 12. Forecloses / keeps open

| Later | How it lands without breaking |
|---|---|
| Receiver syntax (`x.trim()`) | a postfix production desugaring to the same `CelCall`; accepts strictly more source; Admin's `CelNames` learns `.name(` |
| Comparison and ternary in Mutate, string `+` in Condition | construct-table rows; the interpreter already evaluates both, and D's fail-closed flag covers what they contain |
| `now()` in Condition | an instant threaded through `EvaluatePredicate` (after-hooks have no write stamp); the legacy arm is the only change |
| `%` | a `CelBinaryOperator.Modulo` member (public, Abstractions — a reviewed `PublicApi` growth), a lexer token, and per-engine SQL for Computed (SQLite's `%` casts to integer); the fail-closed flag already covers modulus by zero |
| `1 / 0` over literals refused at apply | E5's folding extended from calls to operators; additive (it only refuses what always fails) |
| `@tenant` / `@user` in Mutate | the `ContextRef*` rows gain Mutate; until then a mutate passes `new.tenant_id` (a column of a tenant-scoped row), and a condition may pass `@tenant.id`; the refusal's wording is tracked in #310 |
| A Date type (`string(dateField)` = `2026-10-05`) | a `CelValueType` member and a marshaller arm; D refuses `string()` over a `date` field (E18), so no stored text pins either form |
| Descriptor-defined CEL functions, edited in the portal (D-1, Q6) | a third `CelFunctionProvenance` member (string-serialized, additive) and a descriptor block of their own — never the frozen schema's `functions` block, which means csx functions; the catalog already merges two sources, so a third is a registration, not a redesign |
| 422 for a built-in's failure on caller data (Q7) | a second problem type beside `function-failed`; `CelFunctionException.Reason` already tells an Alvo-authored reason from a host exception |
| `matches`, accessors, durations, `indexOf`, `split`/`join`, `math.trunc`/`sign` | catalog entries; each a new name (additive); a `Duration` type is a new `CelValueType` member (string-serialized, additive) |
| C2 SQL for the new built-ins | every §5 row has its note; `RenderFunction` returns `null` for any name an engine cannot reproduce, so that name stays Condition/Mutate-only on that engine |
| Function offering in Rules / Computed (after C2) | extract the `HooksTab` fragment into a component, profile-parameterized |
| A filter box over a long host list | additive in the fragment |
| A custom standalone image with host functions | a public `AlvoHost` seam when a packable host exists to carry it |

Foreclosed deliberately: two spellings of one function (no `abs` alias beside `math.abs`); a guided row per host function.

## 13. Risks

* **The `lowerAscii` corpus move** hides an unintended change among intended ones — mitigated by regenerating *only*
  rows whose source names `lowerAscii`, and by the commit listing each moved row's old and new first message.
* **`math` namespace vs a field named `math`** — `math.abs(x)` parses as the call; a bare `math` stays a field. Admin's
  `CelNames.IsColumn` treated `math` in `math.abs(` as a column (a rename of a field `math` would have rewritten the
  call); fixed in plan Task 11: a name followed by `.` is never a column (`old`/`new` are images, not columns).
* **Caret insertion** relies on the input keeping `selectionStart` after blur (true for text inputs in every engine
  Playwright drives) — the fallback appends at the end.
* **SQLite math functions for C2** (`ceil`, `floor`) may be absent from the bundled build *(unverified)* — a C2 measure,
  not a D risk.
* **Data-dependent fail-closed** (`substring` past the end, `int('x')` from a text field, a division by a field that
  is zero) answers 500 for that write (E19, Q7) — the docs teach `startsWith` for tests, the `math.least` recipe for
  cuts, and a guard in the condition (`new.qty != 0 && …`) before dividing.
* **The fail-closed flag reaching a Rule or Computed path** would turn a deny-by-null or a null column into a 500 —
  mitigated by gating on `CompiledExpression.Profile` in one place (`EvalState`), and by facts that `EvaluateScalar` and
  a Rule's `EvaluatePredicate` still answer `null`/`false` on overflow and division by zero (plan Task 7).
* **The operator corpus move** (272 rows) is large — mitigated by regenerating only rows that carry one of the three gate
  messages, asserting every other line is byte-identical, and listing each moved row in the commit.

## 14. Public API

**No baseline grows.** `CelFunctionInfo`/`ManagementCelFunctions` are unchanged (a dotted `Name` is a string); no core,
Abstractions, Admin, Host or Ai symbol is added. The Admin offering is fragments plus internal types; the e2e seam is
test code; `CelFunction.ConstantCheck` and the interpreter's `FailClosed` flag are internal; the sample is
`IsPackable=false` and has no baseline. No `CelBinaryOperator` member is added — which is exactly why `%` waits (§12). A
`PublicApi.*.verified.txt` that moves in this slice is a defect.

## 15. Test strategy

| Layer | What | Where |
|---|---|---|
| unit (core) | qualified-name parsing; receiver fixes; every §5 row and edge case; `lowerAscii` path; constant-call refusal and the `digits` constant check; `string()` over a `date` field refused; overload parameter names; summaries | `CelQualifiedNameParsingTests`, `CelBuiltInFunctionTests` (+ `CelTextBuiltInTests`, `CelMathBuiltInTests`, `CelConversionBuiltInTests`), `CelConstantCallTests`, `CelFunctionCatalogTests` |
| unit (core, operators) | every §5.6 row: Int stays Int, truncating division, each overflow and division by zero throws in Condition and Mutate and still answers `null`/`false` in Computed and Rule; string `+` in Mutate, null in → null out; the new messages and fix | `CelHookArithmeticTests`, `CelMutateConcatenationTests` |
| property (CsCheck) | `substring` agrees with a rune-index reference and never throws inside range; `upperAscii∘lowerAscii` touches only ASCII letters; `string(int(x))` round-trips for whole Decimals; `timestamp(string(t))` = `t` | `CelBuiltInPropertyTests` |
| corpus | only `lowerAscii` rows (plan Task 2) and rows carrying an arithmetic or concatenation gate message (plan Tasks 7, 8) move; each commit lists them | `CelAcceptanceCorpusTests` |
| parity | `cel/check` and dry-run apply agree on every new built-in in every slot kind, and on the constant-call refusal | `ExpressionCheckAgreementTests` |
| HTTP | a condition with `startsWith` refuses a write; a mutate with `upperAscii(trim(…))` stores the value; `substring` past the end answers `function-failed`; a mutate with `math.round(new.price * 1.2, 2)` and `'#' + new.code` stores both; a division by a zero field answers `function-failed` and writes nothing | `CelBuiltInWriteTests` |
| sample | `normalizeVin` listed with provenance `Host`; a write through `/api/alvo` stores its result | `EmbeddedSampleTests` |
| Admin unit | grouping, profile filter, signature text, template, prefill, `Insert` ranges; `CelNames` namespace rule; the three guided rows | `FunctionOfferTests`, `CelNamesTests`, `ConditionTableTests`, `ConditionTextTests` |
| Host.Tests | signature text = `CelFunction.Signature()`; every template, placeholders replaced by typed literals, compiles in its profile; conformance covers the three rows; `cel.md` table = catalog; both refuse an enum non-member and a required-null mutate (B pre-flight C7) | `FunctionOfferAgreementTests`, `GuidedConditionConformanceTests`, `CelReferenceDocTests`, `HooksEditorAgreementTests` |
| skills | `mutate-functions` region = catalog; examples compile where stated; size ≤ 6,144 bytes | `SkillCoreClaimsTests`, `SkillConformanceTests` |
| e2e | §11.2 | `HostFunctionScenarios`, `BuiltInConditionScenarios`, `FunctionOfferScenarios`, `FunctionListLayoutScenarios`, `FunctionListUnavailableScenarios`, `GuidedConditionScenarios` |

## 16. Deviations (one place, so a reader tells a decision from an oversight)

Continuing C1's F-series (`cel.md` deviations 25–36):

* **F9 (cel.md 25)** — `math.ceil`/`math.floor` take and return `Decimal` (cel-go: `double`) and have an `Int` identity
  overload; extends F8. Reason: Alvo has no `double`; money is decimal.
* **F10 (26)** — `math.greatest`/`math.least` take exactly two arguments (cel-go: one or more, or a list). Reason: the
  catalog has no variadic parameter and Alvo no list literal; nesting composes three.
* **F11 (27)** — `lowerAscii`, `upperAscii`, `substring`, `contains`, `startsWith`, `endsWith` take the global call
  form (standard and cel-go: member form). F1 extended; receiver syntax is deferred, additively (E3).
* **F12 (28)** — `string(Decimal)` writes the shortest form (no trailing fractional zeros). CEL has no decimal; this is
  `string(double)`'s behaviour and the only engine-agnostic answer (a column's scale differs per engine).
  `string(Timestamp)` always writes UTC (`Z`). cel-go keeps the parsed offset; Alvo writes UTC because it stores
  instants, not offsets. (Read in Task 5, cel-go v0.29.2: `String.ConvertToType(TimestampType)` is
  `time.Parse(time.RFC3339, …)`, which keeps the text's offset, and `Timestamp.ConvertToType(StringType)` is
  `t.Format(time.RFC3339Nano)` with no `UTC()`, so `string(timestamp('2026-10-05T14:00:00+02:00'))` is
  `'2026-10-05T14:00:00+02:00'` there and `'2026-10-05T12:00:00Z'` in Alvo. Carried into cel.md deviation 28 in Task 17.)
* **F13 (29)** — `timestamp(text)` admits upper-case `T`/`Z` only and keeps 100 ns of the fraction (CEL: nanoseconds).
  .NET's resolution; Go's RFC 3339 parser is case-sensitive too.
* **F14 (30)** — `int(String)` admits `[+-]?[0-9]+` only (no whitespace, no other base) = Go's `ParseInt(s, 10, 64)`.
* **F15 (31)** — a built-in call over literals that always fails is refused at apply (E5). An addition, not a change of
  any value: CEL defines no compile-time evaluation; cel-go's constant folding is the precedent.
* **F16 (32)** — `contains`/`startsWith`/`endsWith` compare UTF-16 code units ordinally (= code points for well-formed
  text); a lone-surrogate search can match half a pair, as C1 recorded for `replace`.
* **F17 (33)** — `math.round(x: Decimal, digits: Int)`: cel-go's `math.round` has one argument. Reason: "round a price
  to cents" is the commonest money rule a hook has, it needs no operator, and the one-argument form cannot express it.
  Halves away from zero, like the one-argument form; `digits` 0–28 (`decimal`'s scale limit); a literal out of range is
  refused at apply, a computed one fails closed.
* **F18 (34)** — string `+` in Mutate answers `null` when an operand is null (CEL: `+` has no null overload, so an
  error). Reason: the same null policy every Alvo function has (C1 F3: null in, null out), and the same answer SQL's
  `||` gives, so a later SQL rendering of a mutate needs no guard. Computed keeps refusing a nullable operand at compile.
* **F19 (35)** — Decimal arithmetic: CEL has no decimal. `+ - * /` over Decimal follow `System.Decimal` (28 significant
  digits); overflow and division by zero fail closed in Condition and Mutate, as CEL's `int` errors do. A **null**
  operand still makes the result null rather than an error (Computed's long-standing rule, now shared by the hook
  profiles: `baas-analyza.md` §3.3 "null-safe operators"; a condition reading null does not fire). Int arithmetic is
  CEL-conformant: checked 64-bit, `/` truncates toward zero, overflow and division by zero are errors.
* **F20 (36)** — `string()` refuses a `date` field (E18). CEL's core has no date type; Alvo's `date` reaches CEL as
  midnight UTC, and pinning that text now would make a future Date type's natural `2026-10-05` a breaking change.

From the brief and house rules: **E3** (no receiver syntax, though the brief asked to consider it), **E6** (operators in hook slots are in, fail-closed — but
no comparison or ternary in Mutate, no `%`), **E7** (`matches` and accessors deferred, though listed as prior art
to consider), **E10** (no public host seam; registration after `AddAlvo`'s callback), **E12** (no eval case).

From the product spec: none new. `baas-analyza.md` §3.3's "null-safe operators, not silent reject" is honoured by the
null policy C1 already pinned (a null argument makes the call null; a condition reading null does not fire).

## 17. Open questions (ruled autonomously; each the maintainer's to overturn in the PR)

| # | Question | Ruling | Cost if wrong |
|---|---|---|---|
| **Q6** | **"Be able to edit them in the admin portal": the hooks that call functions, or the functions themselves?** (§1, D-1) | the hooks: D offers every function in the hook editor and inserts calls; a host function is C# and cannot be edited in a browser. Portal-editable functions = descriptor-defined CEL functions, a recorded follow-up (§12) in a block of their own — never the frozen schema's `functions` (csx) block | if the maintainer meant functions authored in the portal, that is the next slice's headline; nothing in D has to be undone — the provenance enum and the catalog's merge take a third source |
| **Q7** | **Should a built-in's failure on the caller's own data be 422, not 500?** (`substring` past the end, `int('x')`, `timestamp(bad)`, an operator's overflow or division by zero; E19, D-8) | no, not in D: 500 `function-failed` for every evaluation failure, host or built-in, one consistent contract | an agent may retry a write that can never succeed; the change is additive (a second problem type keyed on `CelFunctionException.Reason`), and the controller files an issue for it |
| Q1 | Rename `abs`/`round` → `math.*` (E2)? | yes | revert one task; the skill and docs follow |
| Q2 | Convert `lowerAscii` to an ordinary function (E4)? | yes | the corpus rows move back |
| Q3 | Operators in hook slots in this slice (E6)? | yes, fail-closed: arithmetic in Condition and Mutate, string `+` in Mutate (D-6, D-7); comparison and ternary in Mutate wait | the operator rows and the flag revert; the corpus rows move back |
| Q4 | Guided rows for the three text tests (E9)? | yes | three table rows to drop |
| Q5 | Should the standalone image carry a demo host function? | no (C1 G5) | `scripts/demo-admin` shows built-ins only |

## 18. Acceptance criteria (numbers only where a source or a ruling gives one)

1. The catalog lists exactly the 19 names and 32 overloads of §5, each with the §5 semantics and every listed edge case;
   every built-in except `now` is in Condition and Mutate only.
2. `CelAcceptanceBaseline.jsonl` moves only in rows whose source names `lowerAscii` and rows that carry an arithmetic or
   concatenation gate message (272 at C1 HEAD), each set listed in its commit; 2,000 characters, `MaxDepth` 32 and
   `MaxTreeDepth` 128 are unchanged.
3. A literal-only built-in call that always fails is refused at apply, and so is a literal `digits` outside 0–28;
   `cel/check` answers the same. `string()` over a `date` field is refused at apply.
3a. In Condition and Mutate, Int overflow, Decimal overflow and division by zero answer 500 `function-failed` and write
   nothing; `7 / 2` is `3`; in Computed and Rule the same inputs answer exactly what they answer at C1 HEAD.
4. Under a mutate value (expression mode) and a condition (text mode) the hook editor lists every function of that
   profile with its signatures, summary and provenance; Insert writes the template at the caret and selects the first
   placeholder; the check sentence appears within 3 s (slice A §5.4) and never takes focus — asserted by an e2e that
   reads `document.activeElement.id` after the sentence renders.
5. At 375 px, `AssertNoHorizontalScrollAsync`, `AssertSheetFitsAsync` and `AssertNoVerticalTextAsync` pass with the
   function list open under a mutate value and under a condition (`baas-analyza.md` §2.8).
6. The §11.2 scenarios pass in `scripts/test-admin-e2e`; the stored frame number is the function's result.
7. The guided form offers "starts with", "ends with", "contains" for Text fields; round trip 2,000 cases, 0 failures;
   the conformance fact accepts every new cell.
8. No `PublicApi.*.verified.txt` changes; the hooks skill ≤ 6,144 bytes; `AlwaysInContextBudget` = 22,758, unchanged.
9. `docs/architecture/extensibility.md` has the host-developer how-to; `cel.md` lists every built-in (drift-tested).
10. The embedded sample registers `normalizeVin`; its README says how and why the shared descriptor stays hook-free;
    `EmbeddedSampleTests` proves it is listed with provenance `Host` and evaluated by a Data API write;
    `examples/vehicle-registry/vehicles.alvo.json` is byte-identical.

## 19. As built

(To be written when the slice lands: commits, deviations from the plan, measured numbers.)

## 20. Decision log (rulings on the validation verdict of 2026-10-06)

The validation returned GO WITH CHANGES: twelve required changes and seven optional ones. Each ruling below is the
controller's; the form is *what — why — cost if wrong*.

* **Ruling D-1: "edit them in the admin portal" means editing the hooks that call functions** (§1, Q6, first among the
  open questions) — host functions are C# and cannot be edited in a browser; portal-authored functions would be
  descriptor-defined CEL functions, recorded in §12 with what keeps them open (a third `CelFunctionProvenance` member, a
  descriptor block of their own, never the frozen schema's csx `functions` block) — if the maintainer meant the
  functions themselves, that is the next slice's headline and nothing in D is undone.
* **Ruling D-2: the guided "ends with" empty-field test is split** — `!endsWith(new.name, 'x')` over a null field is
  `true` (`!AsBoolean(null)`, `CelInterpreter.cs:339,405`, as §10 says), so it gets its own fact asserting `true` beside
  the facts asserting `false` for the un-negated tests (plan Task 3) — if wrong, a test pins the opposite of the
  interpreter and of §10, and the "no negated guided rows" reasoning loses its proof.
* **Ruling D-3: the embedded sample registers a host function** (E16, §11.3, plan Task 16) — the e2e adapter is test
  code nobody copies, and the sample is the documented answer to "how do I embed Alvo"; `vehicles.alvo.json` stays
  unchanged because the standalone image serves it and `Both_modes_generate_the_same_routes` pins that parity, so the
  hook lives in a test-local descriptor derived from it at run time — if wrong, the sample carries one unused
  registration; removing it is one line and one fact.
* **Ruling D-4: the tenant guidance is "in a condition, or pass `new.tenant_id`"** (§12, plan Task 17) — `@tenant` and
  `@user` are not admitted in Mutate (`ContextRefTenant` is Rule + Condition), while `new.tenant_id` is a column of a
  tenant-scoped row and reads in a mutate; the refusal's wording is tracked in #310 — if wrong, a host developer follows
  a how-to into a refusal; the extensibility text is one paragraph.
* **Ruling D-5: `math.round(x: Decimal, digits: Int)`, digits 0–28, halves away from zero** (E17, F17, plan Tasks 4 and
  6) — round-to-cents is the commonest money rule and needs no operator; a literal out of range is refused at apply
  through an internal constant check, a computed one fails closed — if wrong, one overload is kept forever and C2 must
  reproduce it (SQLite's REAL `round` is a measure).
* **Ruling D-6: string `+` is admitted in Mutate** (E6, F18, §5.6, plan Task 8) — no fail-open path exists for it; null
  in gives null out (C1 F3, SQL `||`), Ruling V caps the stored length through facets, and Computed's renderer already
  joins strings (`SqlPredicateRenderer.IsStringJoin`, `bc8e9f2` on `main`) with its nullable-operand refusal unchanged —
  if wrong, prefixing and code building revert to host functions and the Concatenation row loses Mutate.
* **Ruling D-7: fail-closed arithmetic is in D, in Condition and Mutate, as one plan task** (E6, F19, §5.6, plan Task
  7) — the `null`-on-overflow (`CelInterpreter.cs:607-622`, "a generated column must never make a write crash") turns a
  reject off when it sits in a condition; one `EvalState` flag from `CompiledExpression.Profile` makes overflow,
  division by zero and the smallest Int negated throw the internal function failure (rollback, `function-failed`, like
  Ruling U) only there, Rule and Computed keep their semantics, and Int `/` Int truncates toward zero and stays Int
  (CEL). `%` and Double are not in Alvo's grammar — `%` needs a public `CelBinaryOperator` member and per-engine SQL, so
  it is recorded in §12, not built. 272 corpus rows move by rule and are listed — if wrong, a hook dividing by a zero
  field answers 500 instead of skipping, and the flag, the rows and the messages revert together.
* **Ruling D-8: a built-in's failure on caller data stays 500 `function-failed` in D** (E19, Q7) — one contract for
  every evaluation failure, host or built-in; whether Alvo-authored reasons should answer 422 is the maintainer's call
  and the controller files an issue — if wrong, agents retry writes that cannot succeed until the additive 422 lands.
* **Ruling D-9: `string()` over a `date` field is refused at apply, with a fix** (E18, F20, §6.5, plan Task 5) — the
  midnight-UTC text it would produce pins a form a future Date type would want to change; refusing keeps both answers
  additive — if wrong, an author waits for a date's text, and lifting the refusal is additive.
* **Ruling D-10: the e2e waits for what it asserts** (§11.2, plan Tasks 12 and 13) — an absence of the list is asserted
  only after the list was shown in the same session (or the refused call was recorded), a clean check is the recorded
  verdict for that source, and AC 4 is asserted: the sentence within 3 s, focus still in the box — if wrong, the suite
  stays green on a list that never loads and a box that loses focus.
* **Ruling D-11: the 375 px check measures the sheet and vertical text** (§9.5, AC 5, plan Task 13) — `AssertNoHorizontalScrollAsync`
  measures the document and `main.a-content`, never `.a-editor__body`; `AssertSheetFitsAsync` is added and
  `AssertNoVerticalTextAsync` is called, and the CSS keeps every `overflow-wrap: anywhere` element inside a
  `min-width: 0` container stretched to the row — if wrong, a phone shows signatures as a column of letters behind a
  green check.
* **Ruling D-12: references point at C1 HEAD `3a3c31f`**, and `lowerAscii` is stated as shipped on `main` and only
  widened (§0, E2, E4, plan Files lines) — stale line numbers send an implementer to the wrong method, and "unshipped"
  would hide that E4 must stay non-breaking — if wrong, an implementer edits by name anyway; the cost is time.

Optional improvements, adopted where cheap and clearly right:

* **Ruling D-13: the hook-editor task is split** into the offering UI (plan Task 12) and its layout and failure
  scenarios (plan Task 13) — the original was too large for one implementer — if wrong, two commits instead of one.
* **Ruling D-14: `HostCelFunction.ReservedReason` checks the built-in catalog first** (plan Task 5), so a host
  registering `contains`, `int`, `string` or `timestamp` hears "it is a built-in function", not "a standard CEL name" —
  if wrong, only a refusal's wording changes.
* **Ruling D-15: cel.md deviation 8 records that `CelNames`' "a name before `.` is never a column" would silently skip
  renames if JSON paths ever land** (plan Task 17) — if wrong, one sentence.
* **Ruling D-16: a dotted test id (`fn-math.round`) is reached only through `GetByTestId`** (§9.1) — a CSS `#id` with a
  dot selects nothing — if wrong, nothing breaks; the rule only stops a future selector from silently matching nothing.
* **Ruling D-17: the docs add the recipe "a value derived from other fields of the row → a computed field"** (plan Task
  17) — a mutate now can compute a total, but a computed field stays true on every write while a mutate stamps once — if
  wrong, one recipe line.
* **Not adopted:** ordering the offered functions by the mutate field's type (more UI logic for an unmeasured gain;
  ordinal order is what the catalog and `cel.md` show), and a `demo-admin` variant that shows the "this host" badge (the
  standalone image stays built-ins only, C1 G5; the e2e and the sample are where a host function is seen).
