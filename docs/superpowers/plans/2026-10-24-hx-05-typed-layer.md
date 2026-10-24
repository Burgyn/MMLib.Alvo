# HX-05 — Typed layer and lambda translation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A host registers event handlers over its own classes — `Entity<Booking>(b => b.OnUpdated<SendConfirmation>("send-confirmation", when: e => e.Changed(x => x.Status) && e.New.Status == BookingStatus.Confirmed))` — and reads typed records — `ctx.Data.QueryAsync<Booking>(x => x.Guest == guest, limit: 50)` — with every lambda translated by one internal translator: a `when` into the same canonical CEL text an untyped registration carries (so the catalog, the boot compiler and the apply guard see no difference), a query into the public `AlvoFilter` the driver already binds as parameters; every refused construct throws `AlvoTranslationException` naming the construct, the handler or entity, and a fix; a typed record that does not fit the descriptor (missing or mistyped field, an enum member with no declared value, a field `events.exclude` leaves out) refuses boot and refuses the apply that would break it; `As<T>()` turns any `AlvoRecord` into a host type and names the field it lacks.

**Architecture:** One CEL writer (`CelText`, `src/MMLib.Alvo/Expressions/Internal`) owns canonical spelling and the one string-literal escaping rule, proven by `parse(render(s)) == s`. A per-type `RecordShape` (`src/MMLib.Alvo/Data/Internal`) reads a host type once — field per property (`[AlvoField]`, else `JsonNamingPolicy.SnakeCaseLower`), value type, nullability, enum values (`[AlvoEnumValue]`, else snake_case), the constructor to call — and lists the problems no descriptor can cure; `ShapeMapping` checks it against an `EntitySchema`; `RecordMaterializer`/`RecordValue` build instances (`As<T>()`). `ConditionTranslator` (`Extensions/Internal`) walks `Expression<Func<EventCondition<T>, bool>>` into `CelNode`s (strict constants only) and writes them with `CelText` at registration; `FilterTranslator` (`Data/Internal`) walks `Expression<Func<T, bool>>` into `AlvoFilter` per call (captured values evaluated per call, bound by the driver). `AlvoEntityBuilder<T>` stores the translated text in the existing `HandlerRegistration.When` plus a `TypedHandler` (shape + message factory), so `HandlerBinding` adds only the mapping checks, `HandlerMessage` builds `AlvoUpdated<T>` from the images, and `ExtensionApplyRefusals` treats mapped fields as reads. Typed reads are extension methods over `IAlvoSession`/`IAlvoData` that build an `AlvoQuery` — the port's policy, tenancy and hidden-field rules apply unchanged.

**Tech Stack:** .NET 10 (C# 14), `System.Linq.Expressions`, xUnit v3 on Microsoft.Testing.Platform, Shouldly, NSubstitute, CsCheck, Verify + PublicApiGenerator, Testcontainers (PostgreSQL leg), Microsoft.Playwright (admin e2e), Astro/Starlight (docs).

**Spec:** `docs/superpowers/specs/2026-10-14-host-extensibility-design.md` — §7 *HX-05* (scope, docs, dashboard, acceptance (1)–(7)); §4.3 (the translation table, `AlvoTranslationException`'s message, the one escaping writer); §4.2 (`Entity<T>` registration, boot validation "a typed property with no field or an incompatible `FieldClrType`", "a typed mapping reading an `events.exclude` field", the apply guard); §3.1 Q3; §3.2 D1, D2; §3.3 E7; §2.3 X7, X18; §5 rows marked `05`; §6.2 (HX-05 is security core: translator injection, "captured strings never in SQL"); §4.8 *HX-03 settled details* (9), (13), (39), (53); §4.13 *HX-04 settled details* (6), (16).

## Global Constraints

- Branch `hx/05-typed-layer` in the worktree `/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-wt-hx05`, stacked on `hx/04-extension-catalog` (PR #412, not yet merged). Issue **#382**, epic #370. One writer per worktree; never `git add -A` (stage each task's files by path); verify `git branch --show-current` prints `hx/05-typed-layer` before every commit and before the push.
- **Scope is §7 HX-05 only.** No write hooks, `WriteCondition<T>`, `Set(x => x.F, v)` (HX-09); no typed writes, `IAlvoPatch<T>`, `IAlvoTransaction`, and **no `AlvoFilterOperator.StartsWith/EndsWith/Contains`** (§5 assigns them to HX-07); no `now()` (HX-11); no generator or generated classes (HX-17); no `IQueryNarrowing<T>` (HX-16); no typed access lambda for commands (HX-08); no assistant or skill change (the catalog's `when` stays CEL text).
- **Public surface added is exactly** (core `MMLib.Alvo` unless noted):
  - `MMLib.Alvo.Data`: `AlvoEntityAttribute` (ctor `(string name)`, `Name`), `AlvoFieldAttribute` (ctor `(string name)`, `Name`), `AlvoEnumValueAttribute` (ctor `(string value)`, `Value`), `IAlvoEntity` (`AlvoRecord Record { get; }`), `AlvoRecordExtensions.As<T>(this AlvoRecord record) where T : class`, `AlvoTranslationException` (the four constructors of `AlvoStartupRefusedException`'s shape, `FixSuggestion`), `AlvoTypedReadExtensions` (`GetAsync<T>(this IAlvoSession, Guid, CancellationToken = default)`, `QueryAsync<T>(this IAlvoSession, Expression<Func<T, bool>>, int limit, CancellationToken = default)`, `GetAsync<T>(this IAlvoData, Guid, AlvoContext, CancellationToken = default)`, `QueryAsync<T>(this IAlvoData, Expression<Func<T, bool>>, int limit, AlvoContext, CancellationToken = default)`).
  - `MMLib.Alvo.Events`: `EventCondition<T>` (`New`, `Old`, `Caller`, `Changed(params Expression<Func<T, object?>>[])`), `AlvoConditionCaller` (`User`, `Tenant`, `HasRole(string)`), `AlvoEventAttribute` (ctor `(string type)`, `Type`), `AlvoEventContext.Create(AlvoEvent, IAlvoSession, string subscriber, int attempt = 1)`.
  - `MMLib.Alvo.Extensions`: `AlvoEntityBuilder<T>` (`OnCreated`, `OnUpdated`, `OnDeleted`, `OnSaved`, each in four overloads — delegate or class handler × lambda or CEL-string `when`).
  - `Microsoft.Extensions.DependencyInjection.AlvoExtensionBuilderExtensions`: `Entity<T>(this IAlvoBuilder, Action<AlvoEntityBuilder<T>>)`, `AddEventHandler<TMessage, THandler>(this IAlvoBuilder, string id, string? when = null, Action<AlvoHandlerOptions>? configure = null)`.
  - `MMLib.Alvo.TestHost`: `AlvoTestHost.EventContext(AlvoContext caller, string subscriber = "test-handler", int attempt = 1)`.
  Everything else is `internal`. Abstractions' baseline (`test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`) and the Testing baseline (`*TestingLibraryPublicApiTests`) must not move.
- **Translation rules (verbatim, Rulings 1–4):** `when` text is canonical CEL — one space around every binary operator, `, ` between call arguments, parentheses only where precedence needs them (a looser child, or a right operand of equal precedence). `x == null` → `!has(new.x)`, `x != null`/`HasValue` → `has(new.x)`; `list.Contains(x)` → `new.x == 'a' || new.x == 'b'`; a `Guid` constant → `string(new.x) == '<guid D>'`; a `DateTimeOffset` constant → `timestamp('<yyyy-MM-ddTHH:mm:ss.FFFFFFF>Z')`; a decimal literal always carries a `.` (`10.0`). The refusal message is `Cannot translate '<construct>' in extension '<id>' (when): <reason>. Fix: <fix>` and `Cannot translate '<construct>' in a typed query on '<entity>': <reason>. Fix: <fix>`; the default fixes are `write the condition as a CEL string — when: "…" — or move the check into the handler body.` and `build an AlvoQuery with an AlvoFilter yourself, or filter the rows after reading them.`
- **SQL text scan.** `SqlTextConfinedToRendererArchitectureTests` scans `src/MMLib.Alvo/{Expressions,Rules,Events}`: files this plan creates or edits there — `Expressions/Internal/CelText.cs`, `Events/EventCondition.cs`, `Events/AlvoConditionCaller.cs`, `Events/AlvoEventAttribute.cs`, `Events/AlvoEventContext.cs`, `Events/Internal/HandlerMessage.cs`, `Events/Internal/DeliveryRunner.cs` — may not contain `SELECT `, `WHERE `, `ILIKE`, `COALESCE(`, `IS NOT NULL`, `CASE WHEN `, ` IN (` or the whole upper-case words `AND`, `OR`, `NOT` (code, strings or XML docs). Keep their prose lower-case.
- Public XML docs cite no `§` and no `#issue` (DocsGen's `RemarksFilterTests`). XML docs on every public member.
- C# style: `alvo-dotnet-conventions` — no inline comments, methods ≲ 25 lines, English only, `ConfigureAwait(false)` in `src/`, never in tests (xUnit1030); every test call passes `TestContext.Current.CancellationToken` (xUnit1051). `.cs` files are **UTF-8 with BOM + CRLF**: after creating or editing `.cs` files run `dotnet format whitespace MMLib.Alvo.slnx --include <file> <file> …` (file paths, never a directory).
- Single-test runs: `dotnet test --project <test project> --filter-class '*<ClassName>'` (MTP). Public-API baselines: `*PublicApiApprovalTests` (`test/MMLib.Alvo.Tests` for the core, `test/MMLib.Alvo.TestHost.Tests` for the TestHost). Rings: `scripts/test-ring0` after every task, `scripts/test-ring1` after each task marked **[slice end]**, `scripts/test-ring2` before the PR.
- A grown `PublicApi.*.verified.txt` or a new `*.verified.*` trips the turn-review gate: dispatch `alvo-snapshot-judge`, and justify each added public symbol against `alvo-architecture-rules` (*public is the contract*) and the public-surface list above.
- **CI parity:** rings are Debug on the local SDK; CI builds Release with `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended` and `EnforceCodeStyleInBuild`. Translation-only members throw `NotSupportedException` (allowed in getters by CA1065). Task 13 runs the Release build in the CI SDK container before anything is pushed.
- **C# 14 first-class spans:** `array.Contains(x)` in an expression tree binds to `MemoryExtensions.Contains(ReadOnlySpan<T>, T[, IEqualityComparer<T>?])` with the array wrapped in an `op_Implicit` call or a `Convert`; both translators accept that shape (`LambdaShape.IsContains`), as EF Core 10 does.
- **Security core:** §6.2 lists HX-05 — `alvo-security-core-review` checklist, a labelled `/security-review` substitute and the `needs-deep-review` label (Task 13).
- Commits: Conventional Commits, one per task, each message ending with `Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`. No `Closes` keyword in a commit message; the PR body carries `Closes #382` once.

## Review Focus

1. **A nullable enum property compared with a member** (`e.New.Previous == OrderStatus.InReview`, compiled as `Convert(member, int?) == Constant(1, int?)`) must translate to the member's declared value, not to `1` — rows `nullable enum` in `ConditionTranslationTests` (Task 4) and `nullable enum` in `FilterTranslationTests` (Task 5).
2. **`array.Contains(x)` bound by C# 14 to `MemoryExtensions.Contains` through an implicit span conversion** must translate like `Enumerable.Contains`, for a `static readonly` enum array, an inline string array and a captured `long[]` — rows `list of enum members`, `list of texts` (Task 4), `constant enum list`, `inline text list`, `too many candidates` (Task 5).
3. **A string constant whose escaped text pushes the condition past `CelParser.MaxSourceLength` (2 000)** must be refused at registration with a translation message, not reach the boot as a `CelSyntaxException` — fact `A_condition_longer_than_cel_allows_is_refused_at_registration` (Task 4).
4. **A whole decimal in an event image** (`12.00` arrives as the JSON number `12`, read back as a `long`) must fill a `decimal` property, and a `Guid`, a date and an instant arriving as strings must fill theirs — facts `A_whole_decimal_read_back_as_an_integer_fills_a_decimal_property` and `A_record_fills_every_mapped_property` (Task 3).
5. **A typed query called twice with a changed captured variable** must send the new value the second time — no cache keyed by the expression's shape may freeze the first — fact `A_captured_value_is_read_again_on_every_call` (Task 8).

---

## File structure

Created:

| File | Responsibility |
|---|---|
| `src/MMLib.Alvo/Expressions/Internal/CelText.cs` | the one CEL writer: canonical text of a `CelNode` tree, the one string-literal escape |
| `src/MMLib.Alvo/Data/AlvoEntityAttribute.cs`, `AlvoFieldAttribute.cs`, `AlvoEnumValueAttribute.cs`, `IAlvoEntity.cs`, `AlvoRecordExtensions.cs`, `AlvoTranslationException.cs`, `AlvoTypedReadExtensions.cs` | public typed-data surface |
| `src/MMLib.Alvo/Data/Internal/RecordShape.cs` (+ `MappedProperty`), `EnumMap.cs`, `ShapeProblems.cs`, `ShapeMapping.cs`, `RecordMaterializer.cs`, `RecordValue.cs` | host type ↔ fields, checks, building instances |
| `src/MMLib.Alvo/Data/Internal/LambdaShape.cs`, `LambdaValues.cs`, `TranslationRefusal.cs`, `FilterTranslator.cs`, `TypedEntity.cs` | what both translators share; the query translator; typed-read helpers |
| `src/MMLib.Alvo/Events/EventCondition.cs`, `AlvoConditionCaller.cs`, `AlvoEventAttribute.cs` | what a typed `when` reads; the typed application-event marker |
| `src/MMLib.Alvo/Extensions/AlvoEntityBuilder{T}.cs` | typed registrations |
| `src/MMLib.Alvo/Extensions/Internal/ConditionTranslator.cs`, `TypedHandler.cs` | `when` → CEL; shape + typed message factory |
| `test/MMLib.Alvo.Tests/Expressions/CelTextTests.cs` | writer facts and the round-trip properties (acceptance (3)) |
| `test/MMLib.Alvo.Tests/Data/TypedFixtures.cs`, `RecordShapeTests.cs`, `AlvoRecordAsTests.cs`, `FilterText.cs`, `FilterTranslationTests.cs`, `TypedReadTests.cs` | shapes, `As<T>()` (acceptance (7)), the query half of acceptance (1) |
| `test/MMLib.Alvo.Tests/Extensions/ConditionTranslationTests.cs`, `TypedRegistrationTests.cs`, `TypedBindingTests.cs` | the `when` half of acceptance (1), (4), (6), (5) unit |
| `test/MMLib.Alvo.Tests/Events/AlvoEventContextTests.cs`, `test/MMLib.Alvo.TestHost.Tests/AlvoTestHostEventContextTests.cs` | the unit-test seam (HX-03 (53)) |
| `test/_shared/testhost/TypedLayerFacts.cs`, `test/_shared/testhost/descriptors/typed-desk.alvo.json`, `test/MMLib.Alvo.TestHost.Tests/SqliteTypedLayerTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlTypedLayerTests.cs` | both engines: typed handlers, boot refusal (acceptance (5)), typed reads, two tenants, acceptance (2) |
| `website/src/content/docs/extending/typed-or-cel.mdx`; `website/src/snippets/extending/typed/{BookingDeskHandlers.cs,BookingDeskHandlersTests.cs,booking-desk.alvo.json}` | *Typed or CEL* |

Modified: `src/MMLib.Alvo/Extensions/AlvoExtensionBuilderExtensions.cs`; `src/MMLib.Alvo/Extensions/Internal/{ExtensionRegistration.cs,ExtensionRegistrations.cs,HandlerBinding.cs,ExtensionApplyRefusals.cs}`; `src/MMLib.Alvo/Events/AlvoEventContext.cs`, `Events/Internal/{HandlerMessage.cs,DeliveryRunner.cs}`; `src/MMLib.Alvo.TestHost/AlvoTestHost.cs`; `test/MMLib.Alvo.Tests/MMLib.Alvo.Tests.csproj`, `test/MMLib.Alvo.TestHost.Tests/MMLib.Alvo.TestHost.Tests.csproj`; `test/MMLib.Alvo.Tests/Extensions/ExtensionApplyGuardTests.cs`; `test/MMLib.Alvo.Admin.Tests.EndToEnd/{ExtensionWorld.cs,ExtensionScenarios.cs}`; the core and TestHost `PublicApi.*.verified.txt`; docs per Task 12.

---

## Task 1: `CelText` — the one CEL writer

**Files:**
- Create: `src/MMLib.Alvo/Expressions/Internal/CelText.cs`
- Modify: `test/MMLib.Alvo.Tests/MMLib.Alvo.Tests.csproj` (link `../_shared/literals/LiteralText.cs`)
- Test: `test/MMLib.Alvo.Tests/Expressions/CelTextTests.cs`

**Interfaces:**
- Consumes: `CelNode` records (`CelLiteral`, `CelFieldRef`, `CelContextRef`, `CelUnary`, `CelBinary`, `CelHas`, `CelChanged`) and the internal `CelCall(string Name, IReadOnlyList<CelNode> Arguments)`; `CelParser.Parse(string)`, `CelParser.MaxSourceLength` (2000); `LiteralText.Any(int maxPieces = 24)` (`MMLib.Alvo.Tests.Data`).
- Produces: `internal static class CelText` with `string Write(CelNode node)` and `string StringLiteral(string value)`. A `CelLiteral` of type `Uuid`, `Null` or `Timestamp`, or a `CelConditional`, throws `ArgumentException` (the translator never hands one over); `long.MinValue` throws `ArgumentOutOfRangeException`.

- [ ] **Step 1: Link the literal space and write the failing tests**

In `test/MMLib.Alvo.Tests/MMLib.Alvo.Tests.csproj`, inside the `ItemGroup` holding the two existing `_shared` links, add:

```xml
    <Compile Include="$(MSBuildThisFileDirectory)../_shared/literals/LiteralText.cs" Link="_shared/LiteralText.cs" />
```

Create `test/MMLib.Alvo.Tests/Expressions/CelTextTests.cs`:

```csharp
using CsCheck;

using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The one CEL writer (spec 2026-10-14 §4.3): what it writes reads back as what it was given — for every string over the
/// whole code space (HX-05 acceptance (3)), every number, and every canonical condition.
/// </summary>
public sealed class CelTextTests
{
    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("it's", @"'it\'s'")]
    [InlineData(@"back\slash", @"'back\\slash'")]
    [InlineData("line\nbreak\r\ttab", @"'line\nbreak\r\ttab'")]
    [InlineData("\"double\"", "'\"double\"'")]
    [InlineData("čaj 中  ", "'čaj 中  '")]
    public void A_string_literal_escapes_exactly_what_the_lexer_unescapes(string value, string literal) =>
        CelText.StringLiteral(value).ShouldBe(literal);

    [Fact]
    public void Every_string_reads_back_as_itself()
    {
        var sampled = 0;
        LiteralText.Any().Sample(
            value =>
            {
                Interlocked.Increment(ref sampled);
                return CelParser.Parse(CelText.StringLiteral(value)) == new CelLiteral(CelValueType.String, value);
            },
            iter: 10_000);

        sampled.ShouldBe(10_000);
    }

    [Fact]
    public void Every_number_reads_back_as_its_own_text()
    {
        var numbers = Gen.OneOf(
            Gen.Long.Where(number => number != long.MinValue).Select(number => new CelLiteral(CelValueType.Int, number)),
            Gen.Decimal.Select(number => new CelLiteral(CelValueType.Decimal, number)));

        numbers.Sample(literal => CelText.Write(CelParser.Parse(CelText.Write(literal))) == CelText.Write(literal), iter: 5_000);
    }

    [Theory]
    [InlineData("new.status == 'resolved'")]
    [InlineData("changed(status) && new.status == 'resolved'")]
    [InlineData("(changed(status) || changed(total)) && new.is_paid")]
    [InlineData("a || b && c")]
    [InlineData("a && (b && c)")]
    [InlineData("!(new.is_paid && new.quantity > 1)")]
    [InlineData("!has(new.code)")]
    [InlineData("(new.quantity + 1) * 2 > 10")]
    [InlineData("new.quantity - -5 > 0")]
    [InlineData("'manager' in @user.roles")]
    [InlineData("new.owner_id == @user.id || old.tenant_id == @tenant.id")]
    [InlineData("string(new.owner_id) == '3fa85f64-5717-4562-b3fc-2c963f66afa6'")]
    [InlineData("new.due_at < timestamp('2026-01-01T00:00:00Z')")]
    [InlineData("startsWith(new.note, 'ab') || contains(new.note, 'x')")]
    [InlineData("new.total >= 10.0")]
    public void A_canonical_condition_is_written_back_as_itself(string condition) =>
        CelText.Write(CelParser.Parse(condition)).ShouldBe(condition);

    [Fact]
    public void A_decimal_always_carries_a_point_so_it_never_reads_back_as_an_integer() =>
        CelText.Write(new CelLiteral(CelValueType.Decimal, 10m)).ShouldBe("10.0");

    [Fact]
    public void The_smallest_long_has_no_spelling() =>
        Should.Throw<ArgumentOutOfRangeException>(() => CelText.Write(new CelLiteral(CelValueType.Int, long.MinValue)));

    [Fact]
    public void A_literal_the_lexer_has_no_spelling_for_is_refused() =>
        Should.Throw<ArgumentException>(() => CelText.Write(new CelLiteral(CelValueType.Uuid, Guid.Empty)));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelTextTests'`
Expected: FAIL — compile error, `CelText` does not exist.

- [ ] **Step 3: Write the writer**

Create `src/MMLib.Alvo/Expressions/Internal/CelText.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// The one writer of CEL text: a tree as the canonical source the parser reads back, and every string literal through
/// one escaping rule — the exact inverse of <see cref="CelLexer"/>'s six escapes — so parsing what it writes gives back
/// what it was given.
/// </summary>
/// <remarks>
/// Canonical spelling: one space around each binary operator, a call's arguments separated by a comma and a space, and
/// parentheses only where precedence needs them — around a child that binds looser than its parent, and around a right
/// operand of equal precedence, which the parser would otherwise regroup to the left. Two spellings of one condition
/// are therefore one text, which is what lets a typed registration and an untyped one show the same condition.
/// </remarks>
internal static class CelText
{
    private const int OrLevel = 1;
    private const int AndLevel = 2;
    private const int RelationLevel = 3;
    private const int AdditiveLevel = 4;
    private const int MultiplicativeLevel = 5;
    private const int UnaryLevel = 6;
    private const int PrimaryLevel = 7;

    /// <summary>The canonical text of <paramref name="node"/>.</summary>
    /// <param name="node">A tree of literals, field and context references, operators, <c>has</c>, <c>changed</c> and calls.</param>
    /// <returns>The text <see cref="CelParser"/> reads back as the same tree.</returns>
    internal static string Write(CelNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return Text(node);
    }

    /// <summary>A single-quoted CEL string literal holding exactly <paramref name="value"/>.</summary>
    /// <param name="value">Any text; a quote, a backslash, a line feed, a carriage return and a tab are escaped.</param>
    /// <returns>The literal.</returns>
    internal static string StringLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var character in value)
        {
            builder.Append(Escaped(character));
        }

        return builder.Append('\'').ToString();
    }

    private static string Escaped(char character) => character switch
    {
        '\\' => @"\\",
        '\'' => @"\'",
        '\n' => @"\n",
        '\r' => @"\r",
        '\t' => @"\t",
        _ => character.ToString(),
    };

    private static string Text(CelNode node) => node switch
    {
        CelLiteral literal => Literal(literal),
        CelFieldRef field => Field(field),
        CelContextRef context => Context(context.Value),
        CelHas has => $"has({Field(has.Field)})",
        CelChanged changed => $"changed({changed.FieldName})",
        CelCall call => $"{call.Name}({string.Join(", ", call.Arguments.Select(Text))})",
        CelUnary unary => (unary.Operator == CelUnaryOperator.Not ? "!" : "-") + Child(unary.Operand, UnaryLevel, rightOfSame: false),
        CelBinary binary => Binary(binary),
        _ => throw new ArgumentException($"A {node.GetType().Name} has no canonical text here.", nameof(node)),
    };

    private static string Binary(CelBinary binary)
    {
        var level = Level(binary);

        return $"{Child(binary.Left, level, rightOfSame: false)} {Operator(binary.Operator)} {Child(binary.Right, level, rightOfSame: true)}";
    }

    private static string Child(CelNode child, int parentLevel, bool rightOfSame)
    {
        var level = Level(child);
        var text = Text(child);

        return level < parentLevel || (rightOfSame && level == parentLevel) ? $"({text})" : text;
    }

    private static int Level(CelNode node) => node switch
    {
        CelBinary { Operator: CelBinaryOperator.Or } => OrLevel,
        CelBinary { Operator: CelBinaryOperator.And } => AndLevel,
        CelBinary { Operator: CelBinaryOperator.Add or CelBinaryOperator.Subtract } => AdditiveLevel,
        CelBinary { Operator: CelBinaryOperator.Multiply or CelBinaryOperator.Divide } => MultiplicativeLevel,
        CelBinary => RelationLevel,
        CelUnary or CelLiteral { Value: < 0L } or CelLiteral { Value: < 0m } => UnaryLevel,
        _ => PrimaryLevel,
    };

    private static string Literal(CelLiteral literal) => literal.Value switch
    {
        string text => StringLiteral(text),
        bool flag => flag ? "true" : "false",
        long.MinValue => throw new ArgumentOutOfRangeException(nameof(literal), "long.MinValue has no CEL spelling: its magnitude is not an Int."),
        long number => number.ToString(CultureInfo.InvariantCulture),
        decimal number => DecimalText(number),
        _ => throw new ArgumentException($"A {literal.Type} literal has no CEL spelling here.", nameof(literal)),
    };

    private static string DecimalText(decimal number)
    {
        var text = number.ToString(CultureInfo.InvariantCulture);

        return text.Contains('.', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static string Field(CelFieldRef field) => field.State switch
    {
        CelRecordState.New => "new." + field.FieldName,
        CelRecordState.Old => "old." + field.FieldName,
        _ => field.FieldName,
    };

    private static string Context(CelContextValue value) => value switch
    {
        CelContextValue.UserId => "@user.id",
        CelContextValue.UserRoles => "@user.roles",
        _ => "@tenant.id",
    };

    private static string Operator(CelBinaryOperator op) => op switch
    {
        CelBinaryOperator.Equal => "==",
        CelBinaryOperator.NotEqual => "!=",
        CelBinaryOperator.Less => "<",
        CelBinaryOperator.LessOrEqual => "<=",
        CelBinaryOperator.Greater => ">",
        CelBinaryOperator.GreaterOrEqual => ">=",
        CelBinaryOperator.And => "&&",
        CelBinaryOperator.Or => "||",
        CelBinaryOperator.In => "in",
        CelBinaryOperator.Add => "+",
        CelBinaryOperator.Subtract => "-",
        CelBinaryOperator.Multiply => "*",
        _ => "/",
    };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelTextTests'`
Expected: PASS. A failing canonical row is a precedence or spacing defect in `Level`/`Child`, never a reason to change the row: the rows are the spelling Ruling 1 fixes. `a || b && c` parses as `a || (b && c)` and is written back without parentheses; `a && (b && c)` keeps them.

- [ ] **Step 5: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Expressions/Internal/CelText.cs test/MMLib.Alvo.Tests/Expressions/CelTextTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Expressions/Internal/CelText.cs test/MMLib.Alvo.Tests/Expressions/CelTextTests.cs test/MMLib.Alvo.Tests/MMLib.Alvo.Tests.csproj
git commit -m "feat(cel): one writer for CEL text

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 2: Mapping attributes, `IAlvoEntity` and `RecordShape`

**Files:**
- Create: `src/MMLib.Alvo/Data/AlvoEntityAttribute.cs`, `AlvoFieldAttribute.cs`, `AlvoEnumValueAttribute.cs`, `IAlvoEntity.cs`; `src/MMLib.Alvo/Events/AlvoEventAttribute.cs`; `src/MMLib.Alvo/Data/Internal/RecordShape.cs`, `EnumMap.cs`, `ShapeProblems.cs`
- Test: `test/MMLib.Alvo.Tests/Data/TypedFixtures.cs`, `test/MMLib.Alvo.Tests/Data/RecordShapeTests.cs`
- Baseline: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Produces (public): the four attributes (each constructor throws `ArgumentException` on a blank argument) and `IAlvoEntity`, as listed in Global Constraints.
- Produces (internal, `MMLib.Alvo.Data.Internal`):
  - `sealed class RecordShape` — `static RecordShape Of(Type type)` (cached per type); `Type Type`; `string Name` (the type's name); `string? Entity` (`[AlvoEntity]`); `string? EventType` (`[AlvoEvent]`); `bool IsWrapper` (implements `IAlvoEntity`); `IReadOnlyList<MappedProperty> Properties`; `ConstructorInfo? Constructor`; `IReadOnlyList<ShapeProblem> Problems`; `MappedProperty? Mapped(MemberInfo member)`; `static MappedProperty? ParameterProperty(ParameterInfo parameter, IReadOnlyList<MappedProperty> properties)`.
  - `sealed record MappedProperty(PropertyInfo Property, string Field, Type ValueType, bool AcceptsNull, EnumMap? Enum)`.
  - `sealed class EnumMap` — `static EnumMap Of(Type type)`; `Type Type`; `IReadOnlyList<EnumMember> Members`; `bool IsFlags`; `string? ValueOf(object member)`; `object? MemberOf(string value)`; `sealed record EnumMember(string Name, string Value, object Member)`.
  - `sealed record ShapeProblem(string Message, string Fix)`; `static class ShapeProblems` with `IReadOnlyList<ShapeProblem> Of(RecordShape shape)`.
- Produces (tests, `MMLib.Alvo.Tests.Data`): `Order`, `OrderStatus`, `OrderView`, `OrderLine`, `TypedTicket`, `TicketStatus`, the misfit types, `TicketEscalated`, `TypedTicketResolved`, `EscalationHandler`, `TypedFixtures.OrdersSchema`, `TypedFixtures.OrderRecord(...)`, `TypedFixtures.Absent`.

- [ ] **Step 1: Write the fixtures and the failing tests**

Create `test/MMLib.Alvo.Tests/Data/TypedFixtures.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Schema;

using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Data;

[AlvoEntity("orders")]
public sealed class Order
{
    public Guid Id { get; init; }

    public Guid? OwnerId { get; init; }

    public OrderStatus Status { get; init; }

    public OrderStatus? Previous { get; init; }

    public decimal Total { get; init; }

    public long Quantity { get; init; }

    public int? Rank { get; init; }

    public bool IsPaid { get; init; }

    public required string Note { get; init; }

    public string? Code { get; init; }

    public DateTimeOffset? DueAt { get; init; }

    public DateOnly? ShipOn { get; init; }

    [AlvoField("ref_no")]
    public string? Reference { get; init; }

    public string Summary => $"{Note} ({Status})";
}

public enum OrderStatus
{
    Draft,
    InReview,
    [AlvoEnumValue("ok")]
    Approved,
}

[AlvoEntity("orders")]
public sealed class OrderView(AlvoRecord record) : IAlvoEntity
{
    public AlvoRecord Record { get; } = record;

    [AlvoField("note")]
    public string? Note => (string?)Record["note"];
}

public sealed record OrderLine(Guid Id, string Note, decimal Total);

[AlvoEntity("tickets")]
public sealed class TypedTicket
{
    public Guid Id { get; init; }

    public required string Title { get; init; }

    public string? Code { get; init; }

    public TicketStatus Status { get; init; }
}

public enum TicketStatus
{
    Open,
    Resolved,
}

[AlvoEntity("tickets")]
public sealed class TicketWithVat
{
    public string? VatId { get; init; }
}

[AlvoEntity("tickets")]
public sealed class TicketWithNumericCode
{
    public long? Code { get; init; }
}

[AlvoEntity("tickets")]
public sealed class TicketWithSecret
{
    public string? Secret { get; init; }
}

[AlvoEntity("tickets")]
public sealed class TicketWithStrictCode
{
    public required string Code { get; init; }
}

[AlvoEntity("tickets")]
public sealed class TicketWithArchive
{
    public ArchivedTicketStatus Status { get; init; }
}

public enum ArchivedTicketStatus
{
    Open,
    Resolved,
    Archived,
}

[AlvoEntity("tickets")]
public sealed class TicketWithScore
{
    public double Score { get; init; }
}

public sealed class Unmarked
{
    public string? Title { get; init; }
}

public sealed class Uncallable(int seed)
{
    public string? Title { get; init; } = seed.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class TwoNames
{
    public string? Title { get; init; }

    [AlvoField("title")]
    public string? Heading { get; init; }
}

[Flags]
public enum Flagged
{
    None = 0,
    One = 1,
}

public sealed class WithFlags
{
    public Flagged Flags { get; init; }
}

[AlvoEvent("desk.escalated")]
public sealed record TicketEscalated(string Reason, long Level);

public sealed class TypedTicketResolved : IAlvoEventHandler<AlvoUpdated<TypedTicket>>
{
    public Task HandleAsync(AlvoUpdated<TypedTicket> message, AlvoEventContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class EscalationHandler : IAlvoEventHandler<TicketEscalated>
{
    public Task HandleAsync(TicketEscalated message, AlvoEventContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Host types, the <c>orders</c> entity they map, and records shaped as event images carry them.</summary>
public static class TypedFixtures
{
    /// <summary>Removes a field from <see cref="OrderRecord"/> when given as its value.</summary>
    public static readonly object Absent = new();

    public static EntitySchema OrdersSchema { get; } = new()
    {
        Name = "orders",
        Tenancy = TenancyMode.Global,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid },
            new FieldSchema { Name = "owner_id", Type = FieldType.Uuid, Nullable = true },
            new FieldSchema { Name = "status", Type = FieldType.Enum, EnumValues = ["draft", "in_review", "ok"] },
            new FieldSchema { Name = "previous", Type = FieldType.Enum, EnumValues = ["draft", "in_review", "ok"], Nullable = true },
            new FieldSchema { Name = "total", Type = FieldType.Decimal, Precision = 18, Scale = 2 },
            new FieldSchema { Name = "quantity", Type = FieldType.Integer },
            new FieldSchema { Name = "rank", Type = FieldType.Integer, Nullable = true },
            new FieldSchema { Name = "is_paid", Type = FieldType.Boolean },
            new FieldSchema { Name = "note", Type = FieldType.String },
            new FieldSchema { Name = "code", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "due_at", Type = FieldType.DateTime, Nullable = true },
            new FieldSchema { Name = "ship_on", Type = FieldType.Date, Nullable = true },
            new FieldSchema { Name = "ref_no", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "legacy_code", Type = FieldType.String, Nullable = true },
        ],
    };

    /// <summary>An <c>orders</c> record shaped as an event image carries it: ids, days and instants as text.</summary>
    /// <param name="changes">Fields to replace; <see cref="Absent"/> as a value removes the field.</param>
    public static AlvoRecord OrderRecord(params (string Field, object? Value)[] changes)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "0b6f6a7e-4a8e-4bdb-9f3e-0f2b9b6e1c11",
            ["owner_id"] = null,
            ["status"] = "ok",
            ["previous"] = "in_review",
            ["total"] = 12.5m,
            ["quantity"] = 3L,
            ["rank"] = 7L,
            ["is_paid"] = true,
            ["note"] = "fragile",
            ["code"] = null,
            ["due_at"] = "2026-11-02T08:30:00Z",
            ["ship_on"] = "2026-11-03",
            ["ref_no"] = "R-1",
        };
        foreach (var (field, value) in changes)
        {
            Apply(values, field, value);
        }

        return new AlvoRecord(values);
    }

    private static void Apply(Dictionary<string, object?> values, string field, object? value)
    {
        if (ReferenceEquals(value, Absent))
        {
            values.Remove(field);
            return;
        }

        values[field] = value;
    }
}
```

Create `test/MMLib.Alvo.Tests/Data/RecordShapeTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;

namespace MMLib.Alvo.Tests.Data;

/// <summary>How a host type maps to fields (spec 2026-10-14 §4.3): names, enum values, constructors, and the problems no descriptor cures.</summary>
public sealed class RecordShapeTests
{
    [Fact]
    public void A_property_maps_to_its_snake_case_name_unless_it_names_its_field()
    {
        var shape = RecordShape.Of(typeof(Order));

        Field(shape, nameof(Order.IsPaid)).ShouldBe("is_paid");
        Field(shape, nameof(Order.DueAt)).ShouldBe("due_at");
        Field(shape, nameof(Order.Reference)).ShouldBe("ref_no");
        shape.Entity.ShouldBe("orders");
        shape.Problems.ShouldBeEmpty();
    }

    [Fact]
    public void A_get_only_property_without_a_field_attribute_is_not_mapped() =>
        RecordShape.Of(typeof(Order)).Properties.Select(property => property.Property.Name).ShouldNotContain(nameof(Order.Summary));

    [Fact]
    public void Nullability_is_read_from_the_declaration()
    {
        var shape = RecordShape.Of(typeof(Order));

        Mapped(shape, nameof(Order.Code)).AcceptsNull.ShouldBeTrue();
        Mapped(shape, nameof(Order.Note)).AcceptsNull.ShouldBeFalse();
        Mapped(shape, nameof(Order.Rank)).AcceptsNull.ShouldBeTrue();
        Mapped(shape, nameof(Order.Rank)).ValueType.ShouldBe(typeof(int));
        Mapped(shape, nameof(Order.Total)).AcceptsNull.ShouldBeFalse();
    }

    [Fact]
    public void An_enum_member_maps_to_its_snake_case_name_unless_it_names_its_value()
    {
        var map = EnumMap.Of(typeof(OrderStatus));

        map.ValueOf(OrderStatus.InReview).ShouldBe("in_review");
        map.ValueOf(OrderStatus.Approved).ShouldBe("ok");
        map.MemberOf("draft").ShouldBe(OrderStatus.Draft);
        map.MemberOf("approved").ShouldBeNull();
    }

    [Fact]
    public void A_wrapper_is_built_from_its_record_and_maps_its_field_attributes()
    {
        var shape = RecordShape.Of(typeof(OrderView));

        shape.IsWrapper.ShouldBeTrue();
        shape.Constructor!.GetParameters().ShouldHaveSingleItem().ParameterType.ShouldBe(typeof(AlvoRecord));
        Field(shape, nameof(OrderView.Note)).ShouldBe("note");
        shape.Properties.Select(property => property.Property.Name).ShouldNotContain(nameof(OrderView.Record));
    }

    [Fact]
    public void A_positional_record_is_built_through_its_constructor() =>
        RecordShape.Of(typeof(OrderLine)).Constructor!.GetParameters().Length.ShouldBe(3);

    [Theory]
    [InlineData(typeof(TicketWithScore), "TicketWithScore.Score is a Double, which no field type holds.")]
    [InlineData(typeof(Uncallable), "Uncallable has no public constructor Alvo can call")]
    [InlineData(typeof(TwoNames), "TwoNames.Title and TwoNames.Heading map the same field 'title'.")]
    [InlineData(typeof(WithFlags), "Flagged is a [Flags] enum")]
    public void A_type_no_descriptor_can_map_carries_its_problem(Type type, string problem) =>
        RecordShape.Of(type).Problems.ShouldContain(candidate => candidate.Message.StartsWith(problem, StringComparison.Ordinal));

    [Fact]
    public void A_blank_field_name_is_refused_where_it_is_written() =>
        Should.Throw<ArgumentException>(() => new AlvoFieldAttribute(" "));

    [Fact]
    public void One_type_is_read_once() =>
        RecordShape.Of(typeof(Order)).ShouldBeSameAs(RecordShape.Of(typeof(Order)));

    private static MappedProperty Mapped(RecordShape shape, string property) =>
        shape.Properties.Single(candidate => candidate.Property.Name == property);

    private static string Field(RecordShape shape, string property) => Mapped(shape, property).Field;
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*RecordShapeTests'`
Expected: FAIL — compile errors (`AlvoEntityAttribute`, `RecordShape` … do not exist).

- [ ] **Step 3: Write the public types**

`src/MMLib.Alvo/Data/AlvoEntityAttribute.cs`:

```csharp
namespace MMLib.Alvo.Data;

/// <summary>Names the entity a host type maps, for <c>Entity&lt;T&gt;</c> and the typed reads.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class AlvoEntityAttribute : Attribute
{
    /// <summary>Initializes the attribute.</summary>
    /// <param name="name">The entity's name, as the descriptor declares it.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
    public AlvoEntityAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Gets the entity's name.</summary>
    public string Name { get; }
}
```

`src/MMLib.Alvo/Data/AlvoFieldAttribute.cs` — the same shape with `[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]`, summary `Names the field a property maps when it is not the property's snake_case name; on a get-only property it also makes the property mapped.`, parameter `name` — `The field's name, as the descriptor declares it.`, property `Name` — `Gets the field's name.`

`src/MMLib.Alvo/Data/AlvoEnumValueAttribute.cs` — the same shape with `[AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]`, summary `Names the value an enum member stands for in an enum field when it is not the member's snake_case name.`, parameter `value` — `One of the field's declared values.`, property `Value` — `Gets the declared value.`

`src/MMLib.Alvo/Events/AlvoEventAttribute.cs` — the same shape in `namespace MMLib.Alvo.Events;` with `[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]`, summary `Names the application event a payload type is, for AddEventHandler&lt;TMessage, THandler&gt;.`, parameter `type` — `The exact event type, such as orders.approved.`, property `Type` — `Gets the event type.` (`[AlvoEvent("…")]` binds to this class although the record `AlvoEvent` exists: only one of the two derives from `Attribute`.)

`src/MMLib.Alvo/Data/IAlvoEntity.cs`:

```csharp
namespace MMLib.Alvo.Data;

/// <summary>
/// A host type that wraps a record instead of copying it: <c>As&lt;T&gt;()</c> hands the record to the type's public
/// constructor taking one <see cref="AlvoRecord"/>, so fields the type does not declare survive the round trip.
/// </summary>
/// <remarks>
/// Its properties marked <see cref="AlvoFieldAttribute"/> read from <see cref="Record"/>, and a condition or query may
/// name any field through <c>x.Record["field"]</c>. A wrapper without that constructor is refused when the host starts.
/// </remarks>
public interface IAlvoEntity
{
    /// <summary>Gets the record this instance wraps, every field included.</summary>
    AlvoRecord Record { get; }
}
```

- [ ] **Step 4: Write the shape**

`src/MMLib.Alvo/Data/Internal/EnumMap.cs`:

```csharp
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace MMLib.Alvo.Data.Internal;

/// <summary>One enum type's members and the declared values they stand for: <see cref="AlvoEnumValueAttribute"/>, else snake_case.</summary>
internal sealed class EnumMap
{
    private static readonly ConcurrentDictionary<Type, EnumMap> _maps = new();

    private readonly Dictionary<string, object> _byValue;

    private EnumMap(Type type)
    {
        Type = type;
        Members = [.. type.GetFields(BindingFlags.Public | BindingFlags.Static).Select(Member)];
        _byValue = Members.GroupBy(member => member.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Member, StringComparer.Ordinal);
    }

    internal Type Type { get; }

    internal IReadOnlyList<EnumMember> Members { get; }

    internal bool IsFlags => Type.IsDefined(typeof(FlagsAttribute), inherit: false);

    internal static EnumMap Of(Type type) => _maps.GetOrAdd(type, static enumType => new EnumMap(enumType));

    internal string? ValueOf(object member) => Members.FirstOrDefault(entry => entry.Member.Equals(member))?.Value;

    internal object? MemberOf(string value) => _byValue.GetValueOrDefault(value);

    private static EnumMember Member(FieldInfo field) => new(
        field.Name,
        field.GetCustomAttribute<AlvoEnumValueAttribute>()?.Value ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(field.Name),
        field.GetValue(null)!);
}

/// <summary>One enum member, by name, the value it stands for, and itself.</summary>
internal sealed record EnumMember(string Name, string Value, object Member);
```

`src/MMLib.Alvo/Data/Internal/RecordShape.cs`:

```csharp
using MMLib.Alvo.Events;

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace MMLib.Alvo.Data.Internal;

/// <summary>
/// How one host type maps to an entity's fields, read once per type: each mapped property's field
/// (<see cref="AlvoFieldAttribute"/>, else snake_case), the value it holds, whether it takes null, its enum's values, the
/// constructor to call — and the problems no descriptor can cure.
/// </summary>
/// <remarks>
/// A property is mapped when it is public, readable and not an indexer, and has a public setter (<c>set</c> or <c>init</c>)
/// or carries <see cref="AlvoFieldAttribute"/>. A plain type is built through its parameterless constructor, else through
/// the public constructor with the most parameters that all name mapped properties (a positional record's); a wrapper
/// (<see cref="IAlvoEntity"/>) through its constructor taking one <see cref="AlvoRecord"/>.
/// </remarks>
internal sealed class RecordShape
{
    private static readonly ConcurrentDictionary<Type, RecordShape> _shapes = new();

    private readonly Dictionary<string, MappedProperty> _byName;

    private RecordShape(Type type)
    {
        Type = type;
        Entity = type.GetCustomAttribute<AlvoEntityAttribute>()?.Name;
        EventType = type.GetCustomAttribute<AlvoEventAttribute>()?.Type;
        IsWrapper = typeof(IAlvoEntity).IsAssignableFrom(type);
        var nullability = new NullabilityInfoContext();
        Properties = [.. Candidates(type).Select(property => MappedProperty.Of(property, nullability))];
        _byName = Properties.GroupBy(property => property.Property.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        Constructor = IsWrapper ? type.GetConstructor([typeof(AlvoRecord)]) : Callable(type, Properties);
        Problems = ShapeProblems.Of(this);
    }

    internal Type Type { get; }

    internal string Name => Type.Name;

    internal string? Entity { get; }

    internal string? EventType { get; }

    internal bool IsWrapper { get; }

    internal IReadOnlyList<MappedProperty> Properties { get; }

    internal ConstructorInfo? Constructor { get; }

    internal IReadOnlyList<ShapeProblem> Problems { get; }

    internal static RecordShape Of(Type type) => _shapes.GetOrAdd(type, static shaped => new RecordShape(shaped));

    internal static MappedProperty? ParameterProperty(ParameterInfo parameter, IReadOnlyList<MappedProperty> properties) =>
        properties.FirstOrDefault(property => string.Equals(property.Property.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));

    internal MappedProperty? Mapped(MemberInfo member) =>
        member is PropertyInfo property && _byName.TryGetValue(property.Name, out var mapped) ? mapped : null;

    private static IEnumerable<PropertyInfo> Candidates(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0 && property.GetMethod is { IsPublic: true })
            .Where(property => property.SetMethod is { IsPublic: true } || property.IsDefined(typeof(AlvoFieldAttribute), inherit: true));

    private static ConstructorInfo? Callable(Type type, IReadOnlyList<MappedProperty> properties)
    {
        var constructors = type.GetConstructors();

        return constructors.FirstOrDefault(constructor => constructor.GetParameters().Length == 0)
            ?? constructors
                .Where(constructor => constructor.GetParameters().All(parameter => ParameterProperty(parameter, properties) is not null))
                .MaxBy(constructor => constructor.GetParameters().Length);
    }
}

/// <summary>One mapped property: its field, the value it holds (nullable unwrapped), whether it takes null, and its enum.</summary>
internal sealed record MappedProperty(PropertyInfo Property, string Field, Type ValueType, bool AcceptsNull, EnumMap? Enum)
{
    internal static MappedProperty Of(PropertyInfo property, NullabilityInfoContext nullability)
    {
        var lifted = Nullable.GetUnderlyingType(property.PropertyType);
        var valueType = lifted ?? property.PropertyType;

        return new(property, FieldName(property), valueType, lifted is not null || TakesNull(property, nullability), valueType.IsEnum ? EnumMap.Of(valueType) : null);
    }

    private static string FieldName(PropertyInfo property) =>
        property.GetCustomAttribute<AlvoFieldAttribute>(inherit: true)?.Name ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name);

    private static bool TakesNull(PropertyInfo property, NullabilityInfoContext nullability) =>
        !property.PropertyType.IsValueType && nullability.Create(property).ReadState != NullabilityState.NotNull;
}
```

`src/MMLib.Alvo/Data/Internal/ShapeProblems.cs`:

```csharp
namespace MMLib.Alvo.Data.Internal;

/// <summary>One thing wrong with a host type or its mapping, and how to fix it.</summary>
internal sealed record ShapeProblem(string Message, string Fix);

/// <summary>The problems a host type has whatever the descriptor says: no constructor to call, one field mapped twice, a value no field holds.</summary>
internal static class ShapeProblems
{
    private static readonly HashSet<Type> _held =
        [typeof(string), typeof(long), typeof(int), typeof(decimal), typeof(bool), typeof(Guid), typeof(DateOnly), typeof(DateTimeOffset)];

    internal static IReadOnlyList<ShapeProblem> Of(RecordShape shape) =>
        [.. Construction(shape), .. SharedFields(shape), .. shape.Properties.SelectMany(property => Values(shape, property))];

    private static IEnumerable<ShapeProblem> Construction(RecordShape shape)
    {
        if (shape.Constructor is not null)
        {
            yield break;
        }

        yield return shape.IsWrapper
            ? new($"{shape.Name} implements IAlvoEntity but has no public constructor taking an AlvoRecord.", $"Add public {shape.Name}(AlvoRecord record).")
            : new($"{shape.Name} has no public constructor Alvo can call: none is parameterless, and none takes only properties it maps.",
                "Add a parameterless constructor, or make it a positional record over the mapped properties.");
    }

    private static IEnumerable<ShapeProblem> SharedFields(RecordShape shape) =>
        shape.Properties
            .GroupBy(property => property.Field, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => new ShapeProblem(
                $"{string.Join(" and ", group.Select(property => $"{shape.Name}.{property.Property.Name}"))} map the same field '{group.Key}'.",
                "Map each field once: name one of them with [AlvoField(\"…\")]."));

    private static IEnumerable<ShapeProblem> Values(RecordShape shape, MappedProperty property)
    {
        if (property.Enum is { } map)
        {
            return EnumProblems(map);
        }

        return _held.Contains(property.ValueType) ? [] : [Unheld(shape, property)];
    }

    private static ShapeProblem Unheld(RecordShape shape, MappedProperty property) => new(
        $"{shape.Name}.{property.Property.Name} is a {property.ValueType.Name}, which no field type holds.",
        property.ValueType == typeof(DateTime)
            ? "Declare it as DateTimeOffset: a datetime field holds one instant."
            : "Declare it as string, long, int, decimal, bool, Guid, DateOnly, DateTimeOffset or an enum.");

    private static IEnumerable<ShapeProblem> EnumProblems(EnumMap map)
    {
        if (map.IsFlags)
        {
            yield return new($"{map.Type.Name} is a [Flags] enum, but an enum field holds one value.", "Map a plain enum.");
        }

        foreach (var shared in map.Members.GroupBy(member => member.Value, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            yield return new(
                $"{string.Join(" and ", shared.Select(member => $"{map.Type.Name}.{member.Name}"))} map the same value '{shared.Key}'.",
                "Give one of them [AlvoEnumValue(\"…\")].");
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*RecordShapeTests'`
Expected: PASS.

- [ ] **Step 6: Approve the core baseline**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*PublicApiApprovalTests'`
Expected: FAIL with a `.received.txt`. The only additions are `AlvoEntityAttribute`, `AlvoFieldAttribute`, `AlvoEnumValueAttribute` (each with its `AttributeUsage`, constructor and one property) and `IAlvoEntity` in `MMLib.Alvo.Data`, and `AlvoEventAttribute` in `MMLib.Alvo.Events`. Move the received file over `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`, re-run (PASS), dispatch `alvo-snapshot-judge`.

- [ ] **Step 7: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Data/AlvoEntityAttribute.cs src/MMLib.Alvo/Data/AlvoFieldAttribute.cs src/MMLib.Alvo/Data/AlvoEnumValueAttribute.cs src/MMLib.Alvo/Data/IAlvoEntity.cs src/MMLib.Alvo/Events/AlvoEventAttribute.cs src/MMLib.Alvo/Data/Internal/RecordShape.cs src/MMLib.Alvo/Data/Internal/EnumMap.cs src/MMLib.Alvo/Data/Internal/ShapeProblems.cs test/MMLib.Alvo.Tests/Data/TypedFixtures.cs test/MMLib.Alvo.Tests/Data/RecordShapeTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Data src/MMLib.Alvo/Events/AlvoEventAttribute.cs test/MMLib.Alvo.Tests/Data/TypedFixtures.cs test/MMLib.Alvo.Tests/Data/RecordShapeTests.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(data): map host types to entity fields

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 3: `ShapeMapping` and `As<T>()` **[slice end]**

**Files:**
- Create: `src/MMLib.Alvo/Data/Internal/ShapeMapping.cs`, `RecordMaterializer.cs`, `RecordValue.cs`; `src/MMLib.Alvo/Data/AlvoRecordExtensions.cs`
- Modify: `src/MMLib.Alvo/Data/Internal/RecordShape.cs` (`Materialize<T>`)
- Test: `test/MMLib.Alvo.Tests/Data/AlvoRecordAsTests.cs`; four facts appended to `test/MMLib.Alvo.Tests/Data/RecordShapeTests.cs`
- Baseline: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Consumes: `RecordShape`, `MappedProperty`, `EnumMap`, `ShapeProblem` (Task 2); `FieldClrType.Of(FieldType)`; `NameSuggestion.Closest(string, IEnumerable<string>)` (`MMLib.Alvo.Internal`).
- Produces:
  - `internal static class ShapeMapping` — `IEnumerable<ShapeProblem> Problems(RecordShape shape, EntitySchema entity, bool fromEventImages)` (missing field, a value type the field does not hold, an enum member without a declared value, a non-nullable property on a nullable field, and — when `fromEventImages` — a field `events.exclude` leaves out); `bool Holds(MappedProperty property, FieldSchema field)`.
  - `internal static class RecordMaterializer` — `object Create(RecordShape shape, AlvoRecord record)`; `RecordShape.Materialize<T>(AlvoRecord? record) where T : class` (a null record reads as `AlvoRecord.Empty`).
  - `internal static class RecordValue` — `object? Read(RecordShape shape, AlvoRecord record, MappedProperty property)`; `InvalidOperationException Missing(RecordShape shape, MappedProperty property)`.
  - `public static T As<T>(this AlvoRecord record) where T : class` in `AlvoRecordExtensions`; throws `InvalidOperationException` naming the field (missing, null for a property that cannot hold null, a value the property cannot take) or the shape problem.

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Tests/Data/AlvoRecordAsTests.cs`:

```csharp
using MMLib.Alvo.Data;

using static MMLib.Alvo.Tests.Data.TypedFixtures;

namespace MMLib.Alvo.Tests.Data;

/// <summary><c>As&lt;T&gt;()</c> (spec 2026-10-14 §5, HX-05 acceptance (7)): a record as a host type, or an error naming the field.</summary>
public sealed class AlvoRecordAsTests
{
    [Fact]
    public void A_record_fills_every_mapped_property()
    {
        var order = OrderRecord().As<Order>();

        order.Id.ShouldBe(Guid.Parse("0b6f6a7e-4a8e-4bdb-9f3e-0f2b9b6e1c11"));
        order.OwnerId.ShouldBeNull();
        order.Status.ShouldBe(OrderStatus.Approved);
        order.Previous.ShouldBe(OrderStatus.InReview);
        order.Total.ShouldBe(12.5m);
        order.Quantity.ShouldBe(3L);
        order.Rank.ShouldBe(7);
        order.IsPaid.ShouldBeTrue();
        order.Note.ShouldBe("fragile");
        order.DueAt.ShouldBe(new DateTimeOffset(2026, 11, 2, 8, 30, 0, TimeSpan.Zero));
        order.ShipOn.ShouldBe(new DateOnly(2026, 11, 3));
        order.Reference.ShouldBe("R-1");
    }

    [Fact]
    public void A_missing_field_throws_naming_it_and_the_property()
    {
        var failure = Should.Throw<InvalidOperationException>(() => OrderRecord(("note", Absent)).As<Order>());

        failure.Message.ShouldContain("'note'");
        failure.Message.ShouldContain("Order.Note");
    }

    [Fact]
    public void A_null_in_a_property_that_cannot_hold_null_throws_naming_the_field()
    {
        var failure = Should.Throw<InvalidOperationException>(() => OrderRecord(("total", null)).As<Order>());

        failure.Message.ShouldContain("'total'");
        failure.Message.ShouldContain("Order.Total cannot hold null");
    }

    [Fact]
    public void A_whole_decimal_read_back_as_an_integer_fills_a_decimal_property() =>
        OrderRecord(("total", 12L)).As<Order>().Total.ShouldBe(12m);

    [Fact]
    public void An_enum_value_the_type_has_no_member_for_throws_without_echoing_it()
    {
        var failure = Should.Throw<InvalidOperationException>(() => OrderRecord(("status", "archived")).As<Order>());

        failure.Message.ShouldContain("'status'");
        failure.Message.ShouldNotContain("archived");
    }

    [Fact]
    public void A_value_the_property_cannot_take_throws_naming_the_field() =>
        Should.Throw<InvalidOperationException>(() => OrderRecord(("quantity", "three")).As<Order>()).Message.ShouldContain("'quantity'");

    [Fact]
    public void A_positional_record_is_built_through_its_constructor()
    {
        var line = OrderRecord().As<OrderLine>();

        (line.Note, line.Total).ShouldBe(("fragile", 12.5m));
    }

    [Fact]
    public void A_wrapper_keeps_fields_it_does_not_declare()
    {
        var view = OrderRecord(("legacy_code", "L-9")).As<OrderView>();

        view.Note.ShouldBe("fragile");
        view.Record["legacy_code"].ShouldBe("L-9");
    }

    [Fact]
    public void A_wrapper_missing_a_field_it_declares_throws_naming_it() =>
        Should.Throw<InvalidOperationException>(() => OrderRecord(("note", Absent)).As<OrderView>()).Message.ShouldContain("'note'");

    [Fact]
    public void A_type_with_a_shape_problem_cannot_be_read() =>
        Should.Throw<InvalidOperationException>(() => AlvoRecord.Empty.As<TicketWithScore>()).Message.ShouldContain("Double");
}
```

Append to `RecordShapeTests` (add `using FieldType = MMLib.Alvo.Schema.FieldType;` at the top):

```csharp
    [Fact]
    public void A_type_that_fits_its_entity_has_no_mapping_problem() =>
        ShapeMapping.Problems(RecordShape.Of(typeof(Order)), TypedFixtures.OrdersSchema, fromEventImages: true).ShouldBeEmpty();

    [Fact]
    public void A_property_whose_field_the_entity_lacks_is_named()
    {
        var entity = TypedFixtures.OrdersSchema with { Fields = [.. TypedFixtures.OrdersSchema.Fields.Where(field => field.Name != "ref_no")] };

        ShapeMapping.Problems(RecordShape.Of(typeof(Order)), entity, fromEventImages: false).ShouldHaveSingleItem()
            .Message.ShouldBe("Order.Reference maps to 'ref_no', which 'orders' does not declare.");
    }

    [Fact]
    public void A_property_of_another_type_than_its_field_holds_is_a_problem()
    {
        var entity = TypedFixtures.OrdersSchema with
        {
            Fields = [.. TypedFixtures.OrdersSchema.Fields.Select(field => field.Name == "quantity" ? field with { Type = FieldType.String } : field)],
        };

        ShapeMapping.Problems(RecordShape.Of(typeof(Order)), entity, fromEventImages: false).ShouldHaveSingleItem()
            .Message.ShouldBe("Order.Quantity is a Int64, but 'quantity' is a string field, which holds String.");
    }

    [Fact]
    public void A_field_left_out_of_the_event_images_is_a_problem_only_for_a_handler()
    {
        var entity = TypedFixtures.OrdersSchema with { EventImageExclusions = ["note"] };

        ShapeMapping.Problems(RecordShape.Of(typeof(Order)), entity, fromEventImages: false).ShouldBeEmpty();
        ShapeMapping.Problems(RecordShape.Of(typeof(Order)), entity, fromEventImages: true).ShouldHaveSingleItem()
            .Message.ShouldContain("leaves out of its event images (events.exclude)");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AlvoRecordAsTests'`
Expected: FAIL — compile error (`As<T>`, `ShapeMapping` do not exist).

- [ ] **Step 3: Write the mapping check**

`src/MMLib.Alvo/Data/Internal/ShapeMapping.cs`:

```csharp
using MMLib.Alvo.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.Internal;

/// <summary>
/// A host type against one entity (spec 2026-10-14 §4.2): every mapped property names a declared field of a type it can
/// hold, every enum member a declared value, a property on an optional field takes null, and — for a handler, whose record
/// is filled from the event images — no mapped field is one <c>events.exclude</c> leaves out.
/// </summary>
internal static class ShapeMapping
{
    internal static IEnumerable<ShapeProblem> Problems(RecordShape shape, EntitySchema entity, bool fromEventImages) =>
        shape.Properties.SelectMany(property => PropertyProblems(shape, property, entity, fromEventImages));

    internal static bool Holds(MappedProperty property, FieldSchema field) => field.Type switch
    {
        FieldType.Enum => property.ValueType == typeof(string) || property.ValueType.IsEnum,
        FieldType.Integer => property.ValueType == typeof(long) || property.ValueType == typeof(int),
        _ => !property.ValueType.IsEnum && property.ValueType == FieldClrType.Of(field.Type),
    };

    private static IEnumerable<ShapeProblem> PropertyProblems(RecordShape shape, MappedProperty property, EntitySchema entity, bool fromEventImages)
    {
        var owner = $"{shape.Name}.{property.Property.Name}";
        if (Field(entity, property.Field) is not { } field)
        {
            return [Missing(owner, property, entity)];
        }

        if (!Holds(property, field))
        {
            return [Mistyped(owner, property, field)];
        }

        return [.. EnumValues(property, field), .. Nullability(owner, property, field), .. Excluded(owner, property, entity, fromEventImages)];
    }

    private static FieldSchema? Field(EntitySchema entity, string name) =>
        entity.Fields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal));

    private static ShapeProblem Missing(string owner, MappedProperty property, EntitySchema entity)
    {
        var names = entity.Fields.Select(field => field.Name).Order(StringComparer.Ordinal).ToList();
        var fix = NameSuggestion.Closest(property.Field, names) is { } closest
            ? $"Did you mean '{closest}'? Map it with [AlvoField(\"{closest}\")], or remove the property."
            : $"Map it to a field of '{entity.Name}' with [AlvoField(\"…\")], or remove the property. Fields: {string.Join(", ", names)}.";

        return new($"{owner} maps to '{property.Field}', which '{entity.Name}' does not declare.", fix);
    }

    private static ShapeProblem Mistyped(string owner, MappedProperty property, FieldSchema field) => new(
        $"{owner} is a {property.ValueType.Name}, but '{field.Name}' is a {field.Type.ToString().ToLowerInvariant()} field, which holds {Held(field)}.",
        $"Declare {owner} as {Held(field)}.");

    private static string Held(FieldSchema field) => field.Type switch
    {
        FieldType.Enum => "String or an enum",
        FieldType.Integer => "Int64 or Int32",
        _ => FieldClrType.Of(field.Type).Name,
    };

    private static IEnumerable<ShapeProblem> EnumValues(MappedProperty property, FieldSchema field)
    {
        var declared = field.EnumValues ?? [];

        return property.Enum is not { } map
            ? []
            : map.Members.Where(member => !declared.Contains(member.Value, StringComparer.Ordinal)).Select(member => new ShapeProblem(
                $"{map.Type.Name}.{member.Name} maps to '{member.Value}', which is not a value of '{field.Name}' ({string.Join(", ", declared)}).",
                $"Name its value with [AlvoEnumValue(\"…\")], or add '{member.Value}' to the field's values."));
    }

    private static IEnumerable<ShapeProblem> Nullability(string owner, MappedProperty property, FieldSchema field) =>
        field.Nullable && !property.AcceptsNull
            ? [new($"'{field.Name}' may be empty, but {owner} cannot hold null.", $"Declare {owner} as {property.ValueType.Name}?.")]
            : [];

    private static IEnumerable<ShapeProblem> Excluded(string owner, MappedProperty property, EntitySchema entity, bool fromEventImages) =>
        fromEventImages && entity.EventImageExclusions.Contains(property.Field, StringComparer.Ordinal)
            ? [new(
                $"{owner} maps to '{property.Field}', which '{entity.Name}' leaves out of its event images (events.exclude): the handler's record could never be filled.",
                $"Map a type without {property.Property.Name} for this handler, or remove '{property.Field}' from entities.{entity.Name}.events.exclude.")]
            : [];
}
```

- [ ] **Step 4: Write the value reader, the materializer and `As<T>()`**

`src/MMLib.Alvo/Data/Internal/RecordValue.cs`:

```csharp
using System.Globalization;

namespace MMLib.Alvo.Data.Internal;

/// <summary>
/// One field's value as a mapped property holds it — from what a read or an event image carries: an id, a day or an
/// instant as text, a whole number as a long, a decimal as a decimal or a whole long, an enum value as its declared text.
/// </summary>
/// <remarks>A missing field is not a null one: the message says which, and never echoes the value.</remarks>
internal static class RecordValue
{
    internal static object? Read(RecordShape shape, AlvoRecord record, MappedProperty property)
    {
        if (!record.TryGetValue(property.Field, out var raw))
        {
            throw Missing(shape, property);
        }

        if (raw is null)
        {
            return property.AcceptsNull ? null : throw Null(shape, property);
        }

        return Held(raw, property) ?? throw Unheld(shape, property, raw);
    }

    internal static InvalidOperationException Missing(RecordShape shape, MappedProperty property) => new(
        $"The record has no field '{property.Field}', which {shape.Name}.{property.Property.Name} maps to. A read leaves out a "
        + "field the caller may not see, and an event image leaves out what events.exclude names: map a type without it, or "
        + "read the record untyped.");

    private static InvalidOperationException Null(RecordShape shape, MappedProperty property) => new(
        $"Field '{property.Field}' is null, but {shape.Name}.{property.Property.Name} cannot hold null: declare it as {property.ValueType.Name}?.");

    private static InvalidOperationException Unheld(RecordShape shape, MappedProperty property, object raw) => new(
        property.Enum is { } map
            ? $"Field '{property.Field}' holds a value {map.Type.Name} has no member for: name the member's value with [AlvoEnumValue(\"…\")]."
            : $"Field '{property.Field}' holds a {raw.GetType().Name}, which {shape.Name}.{property.Property.Name} ({property.ValueType.Name}) cannot take.");

    private static object? Held(object raw, MappedProperty property)
    {
        if (property.Enum is { } map)
        {
            return raw is string text ? map.MemberOf(text) : raw.GetType() == map.Type ? raw : null;
        }

        return Scalar(raw, property.ValueType);
    }

    private static object? Scalar(object raw, Type target)
    {
        if (raw.GetType() == target)
        {
            return raw;
        }

        return target == typeof(long) ? Whole(raw)
            : target == typeof(int) ? Whole(raw) is { } whole && whole is >= int.MinValue and <= int.MaxValue ? (int)whole : null
            : target == typeof(decimal) ? Exact(raw)
            : target == typeof(Guid) ? raw is string text && Guid.TryParse(text, out var id) ? id : null
            : target == typeof(DateOnly) ? Day(raw)
            : target == typeof(DateTimeOffset) ? Instant(raw)
            : null;
    }

    private static long? Whole(object raw) => raw switch
    {
        long number => number,
        int number => number,
        decimal number when number == decimal.Truncate(number) && number >= long.MinValue && number <= long.MaxValue => (long)number,
        _ => null,
    };

    private static decimal? Exact(object raw) => raw switch
    {
        decimal number => number,
        long number => number,
        int number => number,
        double number when double.IsFinite(number) && Math.Abs(number) < 7.9e28 => (decimal)number,
        _ => null,
    };

    private static DateOnly? Day(object raw) => raw switch
    {
        DateTime instant => DateOnly.FromDateTime(instant),
        string text when DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) => day,
        _ => null,
    };

    private static DateTimeOffset? Instant(object raw) => raw switch
    {
        DateTime instant => new DateTimeOffset(instant.Kind == DateTimeKind.Local ? instant.ToUniversalTime() : DateTime.SpecifyKind(instant, DateTimeKind.Utc)),
        string text when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant) => instant,
        _ => null,
    };
}
```

`src/MMLib.Alvo/Data/Internal/RecordMaterializer.cs`:

```csharp
using System.Reflection;

namespace MMLib.Alvo.Data.Internal;

/// <summary>Builds a host type from a record through the constructor its shape chose, then its remaining public setters.</summary>
internal static class RecordMaterializer
{
    internal static object Create(RecordShape shape, AlvoRecord record)
    {
        if (shape.Problems.Count > 0)
        {
            throw new InvalidOperationException($"{shape.Name} cannot be read from a record: {shape.Problems[0].Message} {shape.Problems[0].Fix}");
        }

        return shape.IsWrapper ? Wrapped(shape, record) : Built(shape, record);
    }

    private static object Wrapped(RecordShape shape, AlvoRecord record)
    {
        var missing = shape.Properties.FirstOrDefault(property => !record.TryGetValue(property.Field, out _));

        return missing is null ? shape.Constructor!.Invoke([record]) : throw RecordValue.Missing(shape, missing);
    }

    private static object Built(RecordShape shape, AlvoRecord record)
    {
        var parameters = shape.Constructor!.GetParameters();
        var instance = shape.Constructor.Invoke([.. parameters.Select(parameter => RecordValue.Read(shape, record, RecordShape.ParameterProperty(parameter, shape.Properties)!))]);
        foreach (var property in shape.Properties.Where(property => property.Property.SetMethod is { IsPublic: true } && !Passed(property, parameters)))
        {
            property.Property.SetValue(instance, RecordValue.Read(shape, record, property));
        }

        return instance;
    }

    private static bool Passed(MappedProperty property, ParameterInfo[] parameters) =>
        parameters.Any(parameter => string.Equals(parameter.Name, property.Property.Name, StringComparison.OrdinalIgnoreCase));
}
```

Add to `RecordShape`, below `Mapped`:

```csharp
    internal T Materialize<T>(AlvoRecord? record)
        where T : class =>
        (T)RecordMaterializer.Create(this, record ?? AlvoRecord.Empty);
```

`src/MMLib.Alvo/Data/AlvoRecordExtensions.cs`:

```csharp
using MMLib.Alvo.Data.Internal;

namespace MMLib.Alvo.Data;

/// <summary>Reads a record as a host type.</summary>
public static class AlvoRecordExtensions
{
    /// <summary>
    /// The record as a <typeparamref name="T"/>: each mapped property from its field (<see cref="AlvoFieldAttribute"/>, else
    /// the property's snake_case name), an enum from its declared value (<see cref="AlvoEnumValueAttribute"/>, else
    /// snake_case); an <see cref="IAlvoEntity"/> wraps the record instead, keeping every field.
    /// </summary>
    /// <typeparam name="T">A class with a parameterless constructor, a positional record, or an <see cref="IAlvoEntity"/>.</typeparam>
    /// <param name="record">The record, from a read or an event.</param>
    /// <returns>A new <typeparamref name="T"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// A mapped field is missing from the record (a read leaves out what the caller may not see, an event image what
    /// <c>events.exclude</c> names), null where the property cannot hold null, or a value the property cannot take — the
    /// message names the field; or <typeparamref name="T"/> cannot be mapped at all.
    /// </exception>
    public static T As<T>(this AlvoRecord record)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(record);

        return RecordShape.Of(typeof(T)).Materialize<T>(record);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AlvoRecordAsTests'` and `--filter-class '*RecordShapeTests'`
Expected: PASS.

- [ ] **Step 6: Approve the core baseline**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*PublicApiApprovalTests'`
Expected: FAIL; the received diff adds exactly `AlvoRecordExtensions` with `public static T As<T>(this MMLib.Alvo.Data.AlvoRecord record) where T : class`. Accept, re-run (PASS), dispatch `alvo-snapshot-judge`.

- [ ] **Step 7: Format, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Data/Internal/ShapeMapping.cs src/MMLib.Alvo/Data/Internal/RecordValue.cs src/MMLib.Alvo/Data/Internal/RecordMaterializer.cs src/MMLib.Alvo/Data/Internal/RecordShape.cs src/MMLib.Alvo/Data/AlvoRecordExtensions.cs test/MMLib.Alvo.Tests/Data/AlvoRecordAsTests.cs test/MMLib.Alvo.Tests/Data/RecordShapeTests.cs
scripts/test-ring1
git add src/MMLib.Alvo/Data test/MMLib.Alvo.Tests/Data/AlvoRecordAsTests.cs test/MMLib.Alvo.Tests/Data/RecordShapeTests.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(data): read a record as a host type with As<T>()

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 4: `EventCondition<T>` and the `when` translator

**Files:**
- Create: `src/MMLib.Alvo/Events/EventCondition.cs`, `src/MMLib.Alvo/Events/AlvoConditionCaller.cs`, `src/MMLib.Alvo/Data/AlvoTranslationException.cs`, `src/MMLib.Alvo/Data/Internal/LambdaShape.cs`, `LambdaValues.cs`, `TranslationRefusal.cs`, `src/MMLib.Alvo/Extensions/Internal/ConditionTranslator.cs`
- Test: `test/MMLib.Alvo.Tests/Extensions/ConditionTranslationTests.cs`
- Baseline: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Consumes: `CelText.Write` (Task 1); `RecordShape.Of`, `.Mapped`, `.IsWrapper`, `.Name`, `EnumMap.Of`, `.ValueOf` (Task 2); `HandlerVerb` (`Created`, `Updated`, `Deleted`, `Saved`); `CelParser.MaxSourceLength`; `ICelCompiler.Compile(string, CelProfile, EntitySchema)` and `CelFixtures.Compiler` (tests).
- Produces:
  - Public `EventCondition<T>`, `AlvoConditionCaller`, `AlvoTranslationException` (Global Constraints).
  - `internal static class LambdaShape` — `bool Touches(Expression node, ParameterExpression parameter)`; `Expression? FindClock(Expression node)`; `bool IsClock(Expression node)`; `Expression Unwrapped(Expression node)` (strips `Quote` and transparent `Convert`: nullable lift, numeric widening, to `object`, between reference types, enum ↔ underlying — never a narrowing conversion); `Type? EnumBehind(Expression node)`; `bool IsNullable(Expression? node)`; `Type Underlying(Type type)`; `string? RecordKey(MethodCallExpression call)` (`AlvoRecord`'s indexer with a constant key); `bool IsContains(MethodCallExpression call, out Expression? source, out Expression? value)`.
  - `internal static class LambdaValues` — `bool TryConstant(Expression node, out object? value, out string reason)` (strict: a constant, a `static readonly` or `const` field, a conversion or negation of one, an array initializer of them); `object? Evaluate(Expression node)` (per call).
  - `internal static class TranslationRefusal` — `const string WhenFix`, `const string QueryFix`; `AlvoTranslationException When(string id, Expression node, string reason, string fix = WhenFix)`; `AlvoTranslationException Query(string entity, Expression node, string reason, string fix = QueryFix)`.
  - `internal sealed class ConditionTranslator` — `static string Translate<T>(Expression<Func<EventCondition<T>, bool>> when, string id, HandlerVerb verb) where T : class`.

- [ ] **Step 1: Write the failing table suite**

Create `test/MMLib.Alvo.Tests/Extensions/ConditionTranslationTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Events;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Tests.Data;
using MMLib.Alvo.Tests.Expressions;

using System.Linq.Expressions;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>
/// The <c>when</c> column of spec 2026-10-14 §4.3 as data (HX-05 acceptance (1)): each lambda's CEL, which reads back as
/// itself and compiles against the entity; every refused construct throws naming the extension and a fix (acceptance (4)).
/// </summary>
public sealed class ConditionTranslationTests
{
    private const decimal Ceiling = 1000m;

    private static readonly Guid Fleet = Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6");
    private static readonly DateTimeOffset Cutoff = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly decimal Threshold = 250.5m;
    private static readonly OrderStatus[] Open = [OrderStatus.Draft, OrderStatus.InReview];
    private static readonly string[] NoNotes = [];
    private static readonly string LongNote = new('x', CelParser.MaxSourceLength);

    public static TheoryData<string, string, Expression<Func<EventCondition<Order>, bool>>, string> Translated()
    {
        const long limit = 99;

        return new()
        {
            { "snake_case field", "Updated", e => e.New.Note == "x", "new.note == 'x'" },
            { "[AlvoField] name", "Updated", e => e.New.Reference == "R-1", "new.ref_no == 'R-1'" },
            { "both images", "Updated", e => e.Old.Quantity < e.New.Quantity, "old.quantity < new.quantity" },
            { "changed, several", "Updated", e => e.Changed(x => x.Status, x => x.Total), "changed(status) || changed(total)" },
            { "changed and a value", "Updated", e => e.Changed(x => x.Status) && e.New.Status == OrderStatus.Approved, "changed(status) && new.status == 'ok'" },
            { "changed inside and", "Updated", e => e.Changed(x => x.Status, x => x.Total) && e.New.IsPaid, "(changed(status) || changed(total)) && new.is_paid" },
            { "relations", "Updated", e => e.New.Total >= 10m && e.New.Quantity != 3, "new.total >= 10.0 && new.quantity != 3" },
            { "negation", "Updated", e => !e.New.IsPaid || e.New.Total > 0.5m, "!new.is_paid || new.total > 0.5" },
            { "negated group", "Updated", e => !(e.New.IsPaid && e.New.Quantity > 1), "!(new.is_paid && new.quantity > 1)" },
            { "arithmetic", "Updated", e => e.New.Quantity + 1 > 5 && e.New.Total * 2m > 100m, "new.quantity + 1 > 5 && new.total * 2.0 > 100.0" },
            { "arithmetic precedence", "Updated", e => (e.New.Quantity + 1) * 2 > 10, "(new.quantity + 1) * 2 > 10" },
            { "null test", "Updated", e => e.New.Code == null, "!has(new.code)" },
            { "not-null tests", "Updated", e => e.New.Code != null && e.New.DueAt.HasValue, "has(new.code) && has(new.due_at)" },
            { "null on the left", "Updated", e => null == e.New.Code, "!has(new.code)" },
            { "text tests", "Updated", e => e.New.Note.StartsWith("ab") || e.New.Note.EndsWith("yz", StringComparison.Ordinal) || e.New.Note.Contains("mid"), "startsWith(new.note, 'ab') || endsWith(new.note, 'yz') || contains(new.note, 'mid')" },
            { "list of enum members", "Updated", e => Open.Contains(e.New.Status), "new.status == 'draft' || new.status == 'in_review'" },
            { "list of texts", "Updated", e => new[] { "a", "b" }.Contains(e.New.Note), "new.note == 'a' || new.note == 'b'" },
            { "enum member", "Updated", e => e.New.Status != OrderStatus.Draft, "new.status != 'draft'" },
            { "nullable enum", "Updated", e => e.New.Previous == OrderStatus.InReview, "new.previous == 'in_review'" },
            { "enum images", "Updated", e => e.Old.Status != e.New.Status, "old.status != new.status" },
            { "role", "Updated", e => e.Caller.HasRole("manager"), "'manager' in @user.roles" },
            { "caller id", "Updated", e => e.New.OwnerId == e.Caller.User, "new.owner_id == @user.id" },
            { "static readonly and const", "Updated", e => e.New.Total > Threshold && e.New.Total < Ceiling, "new.total > 250.5 && new.total < 1000.0" },
            { "local const", "Updated", e => e.New.Quantity < limit, "new.quantity < 99" },
            { "id constant", "Updated", e => e.New.OwnerId == Fleet, "string(new.owner_id) == '3fa85f64-5717-4562-b3fc-2c963f66afa6'" },
            { "instant constant", "Updated", e => e.New.DueAt < Cutoff, "new.due_at < timestamp('2026-01-01T00:00:00Z')" },
            { "escaped text", "Updated", e => e.New.Note == "it's \\ \"quoted\"\n", @"new.note == 'it\'s \\ ""quoted""\n'" },
            { "negative constant", "Updated", e => e.New.Quantity > -5, "new.quantity > -5" },
            { "bool literal", "Updated", e => e.New.IsPaid == true, "new.is_paid == true" },
            { "value of a nullable", "Updated", e => e.New.Rank!.Value > 2, "new.rank > 2" },
            { "created", "Created", e => e.New.Total > 0m, "new.total > 0.0" },
            { "deleted", "Deleted", e => e.Old.IsPaid, "old.is_paid" },
            { "saved", "Saved", e => e.New.Quantity >= 1, "new.quantity >= 1" },
        };
    }

    public static TheoryData<string, string, Expression<Func<EventCondition<Order>, bool>>, string> Refused()
    {
        var threshold = 5m;

        return new()
        {
            { "method on a field", "Updated", e => e.New.Note.GetHashCode() == 1, "method 'GetHashCode' has no CEL equivalent" },
            { "clock", "Updated", e => e.New.DueAt < DateTimeOffset.UtcNow, "conditions have no clock yet" },
            { "local clock", "Updated", e => e.New.DueAt < DateTime.Now, "conditions have no clock yet" },
            { "captured variable", "Updated", e => e.New.Total > threshold, "'threshold' is a captured variable" },
            { "static property", "Updated", e => e.New.Note == Environment.NewLine, "'NewLine' is a static property" },
            { "pre-image on create", "Created", e => e.Old.Total > 0m, "carries no record before the write" },
            { "post-image on delete", "Deleted", e => e.New.Total > 0m, "carries no record after the write" },
            { "changed on save", "Saved", e => e.Changed(x => x.Status), "changed() compares the record before and after the write" },
            { "culture comparison", "Updated", e => e.New.Note.StartsWith("a", StringComparison.OrdinalIgnoreCase), "only the ordinal comparison translates" },
            { "conditional", "Updated", e => (e.New.IsPaid ? 1 : 0) == 1, "a conditional (?:) has no CEL equivalent" },
            { "relational enum", "Updated", e => e.New.Status > OrderStatus.Draft, "an enum field compares with == and != only" },
            { "smallest long", "Updated", e => e.New.Quantity > long.MinValue, "long.MinValue has no CEL literal" },
            { "unmapped property", "Updated", e => e.New.Summary == "x", "Order.Summary maps to no field" },
            { "empty list", "Updated", e => NoNotes.Contains(e.New.Note), "an empty list matches nothing" },
            { "computed constant", "Updated", e => e.New.Note == "a".Trim(), "it is computed when the condition runs" },
        };
    }

    [Theory]
    [MemberData(nameof(Translated))]
    public void A_condition_translates_to_its_cel(string row, string verb, Expression<Func<EventCondition<Order>, bool>> when, string cel)
    {
        var text = ConditionTranslator.Translate(when, "row-handler", Enum.Parse<HandlerVerb>(verb));

        text.ShouldBe(cel, row);
        CelText.Write(CelParser.Parse(text)).ShouldBe(text, $"{row}: the writer's text reads back as itself");
        CelFixtures.Compiler.Compile(text, CelProfile.Condition, TypedFixtures.OrdersSchema).IsSuccess.ShouldBeTrue($"{row}: {text} compiles");
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void A_construct_with_no_cel_equivalent_is_refused_naming_the_extension_and_a_fix(
        string row, string verb, Expression<Func<EventCondition<Order>, bool>> when, string reason)
    {
        var refusal = Should.Throw<AlvoTranslationException>(() => ConditionTranslator.Translate(when, "row-handler", Enum.Parse<HandlerVerb>(verb)));

        refusal.Message.ShouldContain("in extension 'row-handler' (when): ", customMessage: row);
        refusal.Message.ShouldContain(reason, customMessage: row);
        refusal.Message.ShouldContain(". Fix: ", customMessage: row);
        refusal.FixSuggestion.ShouldNotBeNullOrWhiteSpace(row);
    }

    [Fact]
    public void The_refusal_names_the_construct_the_extension_and_the_fix()
    {
        var refusal = Should.Throw<AlvoTranslationException>(() =>
            ConditionTranslator.Translate<Order>(e => e.New.Note.GetHashCode() == 1, "send-confirmation", HandlerVerb.Updated));

        refusal.Message.ShouldBe(
            "Cannot translate 'e.New.Note.GetHashCode()' in extension 'send-confirmation' (when): method 'GetHashCode' has no CEL "
            + "equivalent. Fix: write the condition as a CEL string — when: \"…\" — or move the check into the handler body.");
        refusal.FixSuggestion.ShouldBe(TranslationRefusal.WhenFix);
    }

    [Fact]
    public void A_condition_longer_than_cel_allows_is_refused_at_registration() =>
        Should.Throw<AlvoTranslationException>(() => ConditionTranslator.Translate<Order>(e => e.New.Note == LongNote, "long", HandlerVerb.Updated))
            .Message.ShouldContain($"past the {CelParser.MaxSourceLength} a condition may hold");

    [Fact]
    public void A_wrapper_reads_any_field_through_its_record() =>
        ConditionTranslator.Translate<OrderView>(e => (string?)e.New.Record["legacy_code"] == "L-9" && e.New.Note != null, "row", HandlerVerb.Updated)
            .ShouldBe("new.legacy_code == 'L-9' && has(new.note)");
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ConditionTranslationTests'`
Expected: FAIL — compile errors (`EventCondition<T>`, `ConditionTranslator`, `AlvoTranslationException` do not exist).

- [ ] **Step 3: Write the public types**

`src/MMLib.Alvo/Events/EventCondition.cs`:

```csharp
using System.Linq.Expressions;

namespace MMLib.Alvo.Events;

/// <summary>
/// What a typed handler's <c>when</c> lambda reads. Alvo translates the lambda into a CEL condition when the handler is
/// registered and never runs it, so every member throws if called.
/// </summary>
/// <typeparam name="T">The record type the handler is registered for.</typeparam>
/// <remarks>
/// A condition compares mapped properties of <see cref="New"/> and <see cref="Old"/> — as the verb's event carries them —
/// with each other, with constants, <c>static readonly</c> fields and enum members; it may call <see cref="Changed"/> on
/// an update, test <see cref="Caller"/>, use comparisons, <c>&amp;&amp;</c>, <c>||</c>, <c>!</c>, arithmetic,
/// <c>== null</c>, <c>HasValue</c>, the ordinal <c>StartsWith</c>, <c>EndsWith</c> and <c>Contains</c>, and
/// <c>Contains</c> over a constant list. Anything else is refused with an
/// <see cref="Data.AlvoTranslationException"/> when the handler is registered.
/// </remarks>
public sealed class EventCondition<T>
    where T : class
{
    private const string TranslationOnly = "An EventCondition is read by Alvo's translator and never run: pass the lambda to when:.";

    private EventCondition()
    {
    }

    /// <summary>Gets the record after the write (<c>new.</c>); a delete carries none.</summary>
    public T New => throw new NotSupportedException(TranslationOnly);

    /// <summary>Gets the record before the write (<c>old.</c>); an update and a delete carry it.</summary>
    public T Old => throw new NotSupportedException(TranslationOnly);

    /// <summary>Gets who caused the event.</summary>
    public AlvoConditionCaller Caller => throw new NotSupportedException(TranslationOnly);

    /// <summary>Whether any of <paramref name="fields"/> changed in the write (<c>changed(field)</c>); on an update only.</summary>
    /// <param name="fields">Mapped properties of the record, such as <c>x =&gt; x.Status</c>.</param>
    /// <returns>Never returns: the call is translated.</returns>
    public bool Changed(params Expression<Func<T, object?>>[] fields) => throw new NotSupportedException(TranslationOnly);
}
```

`src/MMLib.Alvo/Events/AlvoConditionCaller.cs`:

```csharp
namespace MMLib.Alvo.Events;

/// <summary>The caller a typed condition tests — translated into <c>@user</c> and <c>@tenant</c>, never run.</summary>
public sealed class AlvoConditionCaller
{
    private const string TranslationOnly = "An AlvoConditionCaller is read by Alvo's translator and never run: pass the lambda to when:.";

    private AlvoConditionCaller()
    {
    }

    /// <summary>Gets the caller's user id (<c>@user.id</c>).</summary>
    public Guid? User => throw new NotSupportedException(TranslationOnly);

    /// <summary>Gets the caller's tenant id (<c>@tenant.id</c>).</summary>
    public Guid? Tenant => throw new NotSupportedException(TranslationOnly);

    /// <summary>Whether the caller holds <paramref name="role"/> (<c>'role' in @user.roles</c>).</summary>
    /// <param name="role">A role the descriptor declares, as a constant.</param>
    /// <returns>Never returns: the call is translated.</returns>
    public bool HasRole(string role) => throw new NotSupportedException(TranslationOnly);
}
```

If a build reports CA1822 on these members, add `[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "A translation-only member is an instance member so that e.New and e.Caller read as member accesses.")]` on both classes.

`src/MMLib.Alvo/Data/AlvoTranslationException.cs`:

```csharp
namespace MMLib.Alvo.Data;

/// <summary>
/// A lambda Alvo cannot translate: a typed handler's condition when the handler is registered, a typed query when it is
/// called. The message names the construct, the handler or entity, and why; <see cref="FixSuggestion"/> says what to
/// write instead.
/// </summary>
public sealed class AlvoTranslationException : Exception
{
    /// <summary>Initializes an exception with no message.</summary>
    public AlvoTranslationException() => FixSuggestion = string.Empty;

    /// <summary>Initializes an exception with a message.</summary>
    /// <param name="message">What could not be translated, and why.</param>
    public AlvoTranslationException(string message)
        : base(message) => FixSuggestion = string.Empty;

    /// <summary>Initializes an exception with a message and its cause.</summary>
    /// <param name="message">What could not be translated, and why.</param>
    /// <param name="innerException">The cause.</param>
    public AlvoTranslationException(string message, Exception innerException)
        : base(message, innerException) => FixSuggestion = string.Empty;

    /// <summary>Initializes an exception with a message and what to write instead.</summary>
    /// <param name="message">What could not be translated, and why.</param>
    /// <param name="fixSuggestion">What to write instead.</param>
    public AlvoTranslationException(string message, string fixSuggestion)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(fixSuggestion);

        FixSuggestion = fixSuggestion;
    }

    /// <summary>Gets what to write instead.</summary>
    public string FixSuggestion { get; }
}
```

- [ ] **Step 4: Write what both translators share**

`src/MMLib.Alvo/Data/Internal/TranslationRefusal.cs`:

```csharp
using System.Linq.Expressions;

namespace MMLib.Alvo.Data.Internal;

/// <summary>The one shape of a translation refusal (spec 2026-10-14 §4.3): the construct, where it was written, why, and the fix.</summary>
internal static class TranslationRefusal
{
    internal const string WhenFix = "write the condition as a CEL string — when: \"…\" — or move the check into the handler body.";

    internal const string QueryFix = "build an AlvoQuery with an AlvoFilter yourself, or filter the rows after reading them.";

    internal static AlvoTranslationException When(string id, Expression node, string reason, string fix = WhenFix) =>
        new($"Cannot translate '{node}' in extension '{id}' (when): {reason}. Fix: {fix}", fix);

    internal static AlvoTranslationException Query(string entity, Expression node, string reason, string fix = QueryFix) =>
        new($"Cannot translate '{node}' in a typed query on '{entity}': {reason}. Fix: {fix}", fix);
}
```

`src/MMLib.Alvo/Data/Internal/LambdaShape.cs`:

```csharp
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace MMLib.Alvo.Data.Internal;

/// <summary>What both translators ask of a lambda's tree: what reads the parameter, what reads a clock, what a conversion hides.</summary>
internal static class LambdaShape
{
    private static readonly HashSet<Type> _clockTypes = [typeof(DateTime), typeof(DateTimeOffset), typeof(DateOnly), typeof(TimeOnly)];
    private static readonly HashSet<string> _clockMembers = ["Now", "UtcNow", "Today"];
    private static readonly HashSet<Type> _integral = [typeof(sbyte), typeof(byte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long)];
    private static readonly HashSet<Type> _small = [typeof(sbyte), typeof(byte), typeof(short), typeof(ushort)];

    internal static bool Touches(Expression node, ParameterExpression parameter) => new ParameterFinder(parameter).Finds(node);

    internal static Expression? FindClock(Expression node) => new ClockFinder().Find(node);

    internal static bool IsClock(Expression node) => node switch
    {
        MemberExpression { Expression: null, Member: var member } => _clockTypes.Contains(member.DeclaringType!) && _clockMembers.Contains(member.Name),
        MethodCallExpression { Method: var method } => method.DeclaringType == typeof(TimeProvider) && method.Name is "GetUtcNow" or "GetLocalNow",
        _ => false,
    };

    internal static Expression Unwrapped(Expression node)
    {
        while (node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.Quote } unary && IsTransparent(unary))
        {
            node = unary.Operand;
        }

        return node;
    }

    internal static Type? EnumBehind(Expression node) =>
        node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert && Underlying(convert.Operand.Type) is { IsEnum: true } type
            ? type
            : null;

    internal static bool IsNullable(Expression? node) => node is not null && Nullable.GetUnderlyingType(node.Type) is not null;

    internal static Type Underlying(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    internal static string? RecordKey(MethodCallExpression call) =>
        call is { Method.Name: "get_Item", Object: { } record, Arguments: [ConstantExpression { Value: string key }] } && record.Type == typeof(AlvoRecord) ? key : null;

    internal static bool IsContains(MethodCallExpression call, [NotNullWhen(true)] out Expression? source, [NotNullWhen(true)] out Expression? value)
    {
        (source, value) = call switch
        {
            { Method.Name: "Contains", Object: null, Arguments: [var items, var item] } when IsSequence(call.Method.DeclaringType) => (Spanless(items), item),
            { Method.Name: "Contains", Object: null, Arguments: [var items, var item, ConstantExpression { Value: null }] } when call.Method.DeclaringType == typeof(MemoryExtensions) => (Spanless(items), item),
            { Method.Name: "Contains", Object: { } items, Arguments: [var item] } when items.Type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(items.Type) => (items, item),
            _ => (null, null),
        };

        return source is not null;
    }

    private static bool IsSequence(Type? type) => type == typeof(Enumerable) || type == typeof(MemoryExtensions);

    private static Expression Spanless(Expression items) => items switch
    {
        MethodCallExpression { Method.Name: "op_Implicit", Arguments: [var inner] } => inner,
        UnaryExpression { NodeType: ExpressionType.Convert } convert => convert.Operand,
        _ => items,
    };

    private static bool IsTransparent(UnaryExpression unary)
    {
        if (unary.NodeType == ExpressionType.Quote)
        {
            return true;
        }

        var from = Underlying(unary.Operand.Type);
        var to = Underlying(unary.Type);

        return from == to || to == typeof(object) || (!from.IsValueType && !to.IsValueType)
            || (from.IsEnum && Enum.GetUnderlyingType(from) == to) || Widens(from, to);
    }

    private static bool Widens(Type from, Type to) =>
        ((to == typeof(long) || to == typeof(decimal)) && _integral.Contains(from)) || (to == typeof(int) && _small.Contains(from));

    private sealed class ParameterFinder(ParameterExpression parameter) : ExpressionVisitor
    {
        private bool _found;

        internal bool Finds(Expression node)
        {
            Visit(node);

            return _found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            _found |= node == parameter;

            return node;
        }
    }

    private sealed class ClockFinder : ExpressionVisitor
    {
        private Expression? _clock;

        internal Expression? Find(Expression node)
        {
            Visit(node);

            return _clock;
        }

        public override Expression? Visit(Expression? node)
        {
            if (_clock is null && node is not null && IsClock(node))
            {
                _clock = node;
            }

            return base.Visit(node);
        }
    }
}
```

`src/MMLib.Alvo/Data/Internal/LambdaValues.cs`:

```csharp
using System.Linq.Expressions;
using System.Reflection;

namespace MMLib.Alvo.Data.Internal;

/// <summary>
/// Values in a lambda that do not read its parameter: strictly, for a condition translated once (spec 2026-10-14 §4.3:
/// only a constant, a <c>static readonly</c> field or an enum member), and per call, for a query (any value).
/// </summary>
internal static class LambdaValues
{
    internal static bool TryConstant(Expression node, out object? value, out string reason)
    {
        (value, reason) = (null, string.Empty);
        switch (node)
        {
            case ConstantExpression constant:
                value = constant.Value;
                return true;
            case MemberExpression { Expression: null, Member: FieldInfo field } when field.IsInitOnly || field.IsLiteral:
                value = field.GetValue(null);
                return true;
            case NewArrayExpression { NodeType: ExpressionType.NewArrayInit } array:
                return TryArray(array, out value, out reason);
            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.Negate or ExpressionType.NegateChecked } unary:
                return TryFolded(unary, out value, out reason);
            default:
                reason = Why(node);
                return false;
        }
    }

    internal static object? Evaluate(Expression node) => node switch
    {
        ConstantExpression constant => constant.Value,
        MemberExpression { Member: FieldInfo field, Expression: null or ConstantExpression } member => field.GetValue((member.Expression as ConstantExpression)?.Value),
        _ => Expression.Lambda<Func<object?>>(Expression.Convert(node, typeof(object))).Compile(preferInterpretation: true)(),
    };

    private static bool TryFolded(UnaryExpression unary, out object? value, out string reason)
    {
        value = null;
        if (!TryConstant(unary.Operand, out _, out reason))
        {
            return false;
        }

        value = Evaluate(unary);
        return true;
    }

    private static bool TryArray(NewArrayExpression array, out object? value, out string reason)
    {
        var items = new object?[array.Expressions.Count];
        for (var index = 0; index < items.Length; index++)
        {
            if (!TryConstant(array.Expressions[index], out items[index], out reason))
            {
                value = null;
                return false;
            }
        }

        (value, reason) = (items, string.Empty);
        return true;
    }

    private static string Why(Expression node) => node switch
    {
        MemberExpression { Expression: ConstantExpression, Member.Name: var name } =>
            $"'{name}' is a captured variable, but a condition is translated once, when the handler is registered, so it may hold only constants, static readonly fields and enum members",
        MemberExpression { Expression: null, Member: FieldInfo field } => $"'{field.Name}' is a static field that can change after the handler is registered",
        MemberExpression { Expression: null, Member: PropertyInfo property } => $"'{property.Name}' is a static property, computed again whenever it is read",
        _ => "it is computed when the condition runs, not a constant",
    };
}
```

- [ ] **Step 5: Write the `when` translator**

`src/MMLib.Alvo/Extensions/Internal/ConditionTranslator.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Events;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

using System.Collections;
using System.Collections.Frozen;
using System.Globalization;
using System.Linq.Expressions;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// Translates a typed handler's <c>when</c> into the CEL text the untyped core compiles (spec 2026-10-14 §4.3), once, at
/// registration — so the boot, the catalog and the apply guard see exactly what a CEL-string registration gives them.
/// </summary>
internal sealed class ConditionTranslator
{
    private const string ConstantFix = "make it a const or a static readonly field, or write the condition as a CEL string — when: \"…\".";
    private const string ClockReason = "a condition is judged after the commit, and conditions have no clock yet";
    private const string ClockFix = "compare against a field the write stored, or check the time inside the handler body.";

    private static readonly FrozenDictionary<ExpressionType, CelBinaryOperator> _comparisons = new Dictionary<ExpressionType, CelBinaryOperator>
    {
        [ExpressionType.Equal] = CelBinaryOperator.Equal,
        [ExpressionType.NotEqual] = CelBinaryOperator.NotEqual,
        [ExpressionType.LessThan] = CelBinaryOperator.Less,
        [ExpressionType.LessThanOrEqual] = CelBinaryOperator.LessOrEqual,
        [ExpressionType.GreaterThan] = CelBinaryOperator.Greater,
        [ExpressionType.GreaterThanOrEqual] = CelBinaryOperator.GreaterOrEqual,
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<ExpressionType, CelBinaryOperator> _arithmetic = new Dictionary<ExpressionType, CelBinaryOperator>
    {
        [ExpressionType.Add] = CelBinaryOperator.Add,
        [ExpressionType.AddChecked] = CelBinaryOperator.Add,
        [ExpressionType.Subtract] = CelBinaryOperator.Subtract,
        [ExpressionType.SubtractChecked] = CelBinaryOperator.Subtract,
        [ExpressionType.Multiply] = CelBinaryOperator.Multiply,
        [ExpressionType.MultiplyChecked] = CelBinaryOperator.Multiply,
        [ExpressionType.Divide] = CelBinaryOperator.Divide,
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<string, string> _textTests = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["StartsWith"] = "startsWith",
        ["EndsWith"] = "endsWith",
        ["Contains"] = "contains",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly ParameterExpression _event;
    private readonly string _id;
    private readonly HandlerVerb _verb;
    private readonly RecordShape _shape;

    private ConditionTranslator(ParameterExpression @event, string id, HandlerVerb verb, RecordShape shape) =>
        (_event, _id, _verb, _shape) = (@event, id, verb, shape);

    internal static string Translate<T>(Expression<Func<EventCondition<T>, bool>> when, string id, HandlerVerb verb)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(when);
        var translator = new ConditionTranslator(when.Parameters[0], id, verb, RecordShape.Of(typeof(T)));
        var text = CelText.Write(translator.Predicate(when.Body));

        return text.Length <= CelParser.MaxSourceLength
            ? text
            : throw translator.Refuse(when.Body, $"it translates to {text.Length} characters of CEL, past the {CelParser.MaxSourceLength} a condition may hold", "shorten it, or move part of the check into the handler body.");
    }

    private CelNode Predicate(Expression node) => node switch
    {
        BinaryExpression { NodeType: ExpressionType.AndAlso } both => new CelBinary(CelBinaryOperator.And, Predicate(both.Left), Predicate(both.Right)),
        BinaryExpression { NodeType: ExpressionType.OrElse } either => new CelBinary(CelBinaryOperator.Or, Predicate(either.Left), Predicate(either.Right)),
        UnaryExpression { NodeType: ExpressionType.Not } not => new CelUnary(CelUnaryOperator.Not, Predicate(not.Operand)),
        BinaryExpression comparison when _comparisons.TryGetValue(comparison.NodeType, out var op) => Comparison(comparison, op),
        MethodCallExpression call => Call(call),
        MemberExpression { Member.Name: "HasValue" } has when LambdaShape.IsNullable(has.Expression) => new CelHas(Field(has.Expression!)),
        _ => Operand(node),
    };

    private CelNode Comparison(BinaryExpression comparison, CelBinaryOperator op) =>
        NullTest(comparison, op) ?? EnumComparison(comparison, op) ?? Typed(comparison, op, Operand(comparison.Left), Operand(comparison.Right));

    private CelNode? NullTest(BinaryExpression comparison, CelBinaryOperator op)
    {
        var tested = IsNull(comparison.Right) ? comparison.Left : IsNull(comparison.Left) ? comparison.Right : null;
        if (tested is null)
        {
            return null;
        }

        if (op is not (CelBinaryOperator.Equal or CelBinaryOperator.NotEqual))
        {
            throw Refuse(comparison, "only == and != can test for null", TranslationRefusal.WhenFix);
        }

        var has = new CelHas(Field(tested));
        return op == CelBinaryOperator.NotEqual ? has : new CelUnary(CelUnaryOperator.Not, has);
    }

    private static bool IsNull(Expression node) => LambdaShape.Unwrapped(node) is ConstantExpression { Value: null };

    private CelNode? EnumComparison(BinaryExpression comparison, CelBinaryOperator op)
    {
        var (field, constant, type) = EnumSides(comparison);
        if (field is null)
        {
            return null;
        }

        if (op is not (CelBinaryOperator.Equal or CelBinaryOperator.NotEqual))
        {
            throw Refuse(comparison, "an enum field compares with == and != only", TranslationRefusal.WhenFix);
        }

        if (!LambdaValues.TryConstant(LambdaShape.Unwrapped(constant!), out var raw, out var reason) || raw is null)
        {
            throw Refuse(constant!, reason.Length > 0 ? reason : "null is tested with == null against a field", ConstantFix);
        }

        return new CelBinary(op, Operand(field), EnumText(constant!, (Enum)Enum.ToObject(type!, raw)));
    }

    private (Expression? Field, Expression? Constant, Type? Type) EnumSides(BinaryExpression comparison)
    {
        if (LambdaShape.EnumBehind(comparison.Left) is { } left && LambdaShape.Touches(comparison.Left, _event) && !LambdaShape.Touches(comparison.Right, _event))
        {
            return (((UnaryExpression)comparison.Left).Operand, comparison.Right, left);
        }

        return LambdaShape.EnumBehind(comparison.Right) is { } right && LambdaShape.Touches(comparison.Right, _event) && !LambdaShape.Touches(comparison.Left, _event)
            ? (((UnaryExpression)comparison.Right).Operand, comparison.Left, right)
            : (null, null, null);
    }

    private CelNode Typed(Expression node, CelBinaryOperator op, CelNode left, CelNode right)
    {
        if (left is not CelLiteral { Type: CelValueType.Uuid } && right is not CelLiteral { Type: CelValueType.Uuid })
        {
            return new CelBinary(op, left, right);
        }

        return op is CelBinaryOperator.Equal or CelBinaryOperator.NotEqual
            ? new CelBinary(op, AsText(left), AsText(right))
            : throw Refuse(node, "an id compares with == and != only", TranslationRefusal.WhenFix);
    }

    private static CelNode AsText(CelNode node) => node is CelLiteral { Type: CelValueType.Uuid, Value: Guid id }
        ? new CelLiteral(CelValueType.String, id.ToString("D"))
        : new CelCall("string", [node]);

    private CelNode Operand(Expression node)
    {
        var unwrapped = LambdaShape.Unwrapped(node);
        if (!LambdaShape.Touches(unwrapped, _event))
        {
            return Constant(unwrapped);
        }

        return unwrapped switch
        {
            BinaryExpression arithmetic when _arithmetic.TryGetValue(arithmetic.NodeType, out var op) => new CelBinary(op, Operand(arithmetic.Left), Operand(arithmetic.Right)),
            UnaryExpression { NodeType: ExpressionType.Negate or ExpressionType.NegateChecked } negate => new CelUnary(CelUnaryOperator.Negate, Operand(negate.Operand)),
            MemberExpression member when CallerValue(member) is { } caller => caller,
            _ => Field(unwrapped),
        };
    }

    private CelContextRef? CallerValue(MemberExpression member) => member switch
    {
        { Expression: MemberExpression { Member.Name: "Caller", Expression: var owner }, Member.Name: "User" } when owner == _event => new(CelContextValue.UserId, CelValueType.Uuid),
        { Expression: MemberExpression { Member.Name: "Caller", Expression: var owner }, Member.Name: "Tenant" } when owner == _event => new(CelContextValue.TenantId, CelValueType.Uuid),
        _ => null,
    };

    private CelFieldRef Field(Expression node)
    {
        var unwrapped = LambdaShape.Unwrapped(node);

        return unwrapped switch
        {
            MemberExpression { Member.Name: "Value" } value when LambdaShape.IsNullable(value.Expression) => Field(value.Expression!),
            MemberExpression member when Image(member.Expression) is { } state => MappedField(member, state),
            MethodCallExpression call when LambdaShape.RecordKey(call) is { } key && RecordImage(call.Object!) is { } state => new CelFieldRef(key, CelValueType.Null, state),
            _ => throw Refuse(unwrapped, Describe(unwrapped), TranslationRefusal.WhenFix),
        };
    }

    private CelRecordState? Image(Expression? node)
    {
        if (node is not MemberExpression { Expression: ParameterExpression parameter, Member.Name: "New" or "Old" } image || parameter != _event)
        {
            return null;
        }

        var state = image.Member.Name == "New" ? CelRecordState.New : CelRecordState.Old;
        return Carries(state) ? state : throw Refuse(image, ImageReason(state), TranslationRefusal.WhenFix);
    }

    private bool Carries(CelRecordState state) => (_verb, state) switch
    {
        (HandlerVerb.Deleted, CelRecordState.New) => false,
        (HandlerVerb.Created or HandlerVerb.Saved, CelRecordState.Old) => false,
        _ => true,
    };

    private string ImageReason(CelRecordState state) => state == CelRecordState.Old
        ? $"On{_verb} carries no record before the write (old.); register OnUpdated for a condition on the change"
        : "OnDeleted carries no record after the write (new.)";

    private CelRecordState? RecordImage(Expression record) =>
        Image(record) ?? (record is MemberExpression { Member.Name: nameof(IAlvoEntity.Record) } wrapped && _shape.IsWrapper ? Image(wrapped.Expression) : null);

    private CelFieldRef MappedField(MemberExpression member, CelRecordState state) => _shape.Mapped(member.Member) is { } mapped
        ? new CelFieldRef(mapped.Field, CelValueType.Null, state)
        : throw Refuse(member, $"{_shape.Name}.{member.Member.Name} maps to no field: it has neither a public setter nor [AlvoField]", TranslationRefusal.WhenFix);

    private CelNode Call(MethodCallExpression call) => call switch
    {
        { Method.Name: "Changed", Object: var owner } when owner == _event => Changed(call),
        { Method.Name: nameof(AlvoConditionCaller.HasRole), Object: { } caller } when caller.Type == typeof(AlvoConditionCaller) => HasRole(call),
        { Object: { } text } when text.Type == typeof(string) && _textTests.TryGetValue(call.Method.Name, out var name) => TextTest(call, name),
        _ when LambdaShape.IsContains(call, out var source, out var value) => AnyOf(call, source, value),
        _ when LambdaShape.IsClock(call) => throw Refuse(call, ClockReason, ClockFix),
        _ => throw Refuse(call, $"method '{call.Method.Name}' has no CEL equivalent", TranslationRefusal.WhenFix),
    };

    private CelNode Changed(MethodCallExpression call)
    {
        if (_verb != HandlerVerb.Updated)
        {
            throw Refuse(call, $"changed() compares the record before and after the write, and On{_verb} carries one of them", "register the handler with OnUpdated.");
        }

        var fields = call.Arguments[0] is NewArrayExpression array
            ? array.Expressions.Select(ChangedField).ToList()
            : throw Refuse(call, "changed() takes its properties inline", "write e.Changed(x => x.Status, x => x.Total).");
        return fields.Count > 0
            ? fields.Aggregate((left, right) => new CelBinary(CelBinaryOperator.Or, left, right))
            : throw Refuse(call, "changed() names no property", "name at least one: e.Changed(x => x.Status).");
    }

    private CelNode ChangedField(Expression argument)
    {
        var body = LambdaShape.Unwrapped(argument) is LambdaExpression lambda ? LambdaShape.Unwrapped(lambda.Body) : null;

        return body is MemberExpression { Expression: ParameterExpression } member && _shape.Mapped(member.Member) is { } mapped
            ? new CelChanged(mapped.Field)
            : throw Refuse(argument, "changed() takes a mapped property of the record, such as x => x.Status", TranslationRefusal.WhenFix);
    }

    private CelNode HasRole(MethodCallExpression call) =>
        LambdaValues.TryConstant(call.Arguments[0], out var role, out var reason) && role is string { Length: > 0 } name
            ? new CelBinary(CelBinaryOperator.In, new CelLiteral(CelValueType.String, name), new CelContextRef(CelContextValue.UserRoles, CelValueType.StringList))
            : throw Refuse(call, reason.Length > 0 ? reason : "a role is a non-empty constant", ConstantFix);

    private CelNode TextTest(MethodCallExpression call, string name)
    {
        var ordinal = call.Arguments.Count == 1 || call.Arguments is [_, ConstantExpression { Value: StringComparison.Ordinal }];
        if (!ordinal || call.Arguments[0].Type != typeof(string))
        {
            throw Refuse(call, $"only the ordinal comparison translates: CEL's {name} compares characters exactly", "call the one-argument overload, or pass StringComparison.Ordinal.");
        }

        return new CelCall(name, [Operand(call.Object!), Operand(call.Arguments[0])]);
    }

    private CelNode AnyOf(MethodCallExpression call, Expression source, Expression value)
    {
        if (LambdaShape.Touches(source, _event) || !LambdaShape.Touches(value, _event))
        {
            throw Refuse(call, "Contains translates only as a constant list containing a field", TranslationRefusal.WhenFix);
        }

        if (!LambdaValues.TryConstant(source, out var items, out var reason) || items is not IEnumerable list)
        {
            throw Refuse(source, reason.Length > 0 ? reason : "the list is null", ConstantFix);
        }

        var tests = list.Cast<object?>().Select(item => Typed(call, CelBinaryOperator.Equal, Operand(value), Literal(source, item))).ToList();
        return tests.Count > 0
            ? tests.Aggregate((left, right) => new CelBinary(CelBinaryOperator.Or, left, right))
            : throw Refuse(call, "an empty list matches nothing", "remove the condition, or name at least one value.");
    }

    private CelNode Constant(Expression node)
    {
        if (LambdaShape.FindClock(node) is { } clock)
        {
            throw Refuse(clock, ClockReason, ClockFix);
        }

        return LambdaValues.TryConstant(node, out var value, out var reason) ? Literal(node, value) : throw Refuse(node, reason, ConstantFix);
    }

    private CelNode Literal(Expression node, object? value) => value switch
    {
        null => throw Refuse(node, "null is tested with == null or != null against a field", TranslationRefusal.WhenFix),
        string text => new CelLiteral(CelValueType.String, text),
        bool flag => new CelLiteral(CelValueType.Bool, flag),
        Enum member => EnumText(node, member),
        long.MinValue => throw Refuse(node, "long.MinValue has no CEL literal", ConstantFix),
        sbyte or byte or short or ushort or int or uint or long => new CelLiteral(CelValueType.Int, Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        ulong big when big <= long.MaxValue => new CelLiteral(CelValueType.Int, (long)big),
        decimal number => new CelLiteral(CelValueType.Decimal, number),
        Guid id => new CelLiteral(CelValueType.Uuid, id),
        DateTimeOffset instant => new CelCall("timestamp", [new CelLiteral(CelValueType.String, Rfc3339(instant))]),
        double or float => throw Refuse(node, "a binary floating-point constant has no exact CEL literal", "use a decimal constant, such as 99.5m."),
        DateTime => throw Refuse(node, "a DateTime has no offset, so it names no single instant", "use a DateTimeOffset."),
        _ => throw Refuse(node, $"a {value.GetType().Name} constant has no CEL literal", TranslationRefusal.WhenFix),
    };

    private static string Rfc3339(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture) + "Z";

    private CelLiteral EnumText(Expression node, Enum member) => EnumMap.Of(member.GetType()).ValueOf(member) is { } text
        ? new CelLiteral(CelValueType.String, text)
        : throw Refuse(node, $"{member.GetType().Name} has no member {member}", TranslationRefusal.WhenFix);

    private string Describe(Expression node) => node switch
    {
        MethodCallExpression call => $"method '{call.Method.Name}' has no CEL equivalent",
        ConditionalExpression => "a conditional (?:) has no CEL equivalent",
        UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert => $"the conversion to {convert.Type.Name} has no CEL equivalent",
        MemberExpression member when LambdaShape.IsClock(member) => ClockReason,
        MemberExpression member => $"'{member.Member.Name}' is not a field of {_shape.Name} a condition can read",
        _ => $"a {node.NodeType} expression has no CEL equivalent",
    };

    private AlvoTranslationException Refuse(Expression node, string reason, string fix) => TranslationRefusal.When(_id, node, reason, fix);
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ConditionTranslationTests'`
Expected: PASS (33 translated rows, 15 refused rows, 3 facts). Diagnose by the row label. Two traps the rows are built to catch: an enum comparison arriving as `Convert(member, Int32) == Constant(2)` (the `nullable enum` row as `Convert(member, Int32?)`), handled by `EnumComparison` before the general path; and `Open.Contains(…)`/`new[] { … }.Contains(…)` arriving as `MemoryExtensions.Contains` over `op_Implicit(…)`, possibly with a third `null` argument — `LambdaShape.IsContains` must accept both. If `CelFixtures.Compiler.Compile(...)` rejects a translated text, fix the translator, never the row: the row is legal Alvo CEL by construction. The `instant constant` row depends on `.FFFFFFF` dropping the period with a zero fraction — the same format `CelBuiltInFunctions.StringOf` uses for `string(timestamp)`.

- [ ] **Step 7: Approve the core baseline**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*PublicApiApprovalTests'`
Expected: FAIL; the received diff adds exactly `AlvoTranslationException` (four constructors, `FixSuggestion`) in `MMLib.Alvo.Data`, and `EventCondition<T>` (`New`, `Old`, `Caller`, `Changed`) and `AlvoConditionCaller` (`User`, `Tenant`, `HasRole`) in `MMLib.Alvo.Events`. Accept, re-run (PASS), dispatch `alvo-snapshot-judge`.

- [ ] **Step 8: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Events/EventCondition.cs src/MMLib.Alvo/Events/AlvoConditionCaller.cs src/MMLib.Alvo/Data/AlvoTranslationException.cs src/MMLib.Alvo/Data/Internal/LambdaShape.cs src/MMLib.Alvo/Data/Internal/LambdaValues.cs src/MMLib.Alvo/Data/Internal/TranslationRefusal.cs src/MMLib.Alvo/Extensions/Internal/ConditionTranslator.cs test/MMLib.Alvo.Tests/Extensions/ConditionTranslationTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Events/EventCondition.cs src/MMLib.Alvo/Events/AlvoConditionCaller.cs src/MMLib.Alvo/Data/AlvoTranslationException.cs src/MMLib.Alvo/Data/Internal/LambdaShape.cs src/MMLib.Alvo/Data/Internal/LambdaValues.cs src/MMLib.Alvo/Data/Internal/TranslationRefusal.cs src/MMLib.Alvo/Extensions/Internal/ConditionTranslator.cs test/MMLib.Alvo.Tests/Extensions/ConditionTranslationTests.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(extensions): translate a typed handler's when into CEL

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 5: The query translator

**Files:**
- Create: `src/MMLib.Alvo/Data/Internal/FilterTranslator.cs`
- Test: `test/MMLib.Alvo.Tests/Data/FilterText.cs`, `test/MMLib.Alvo.Tests/Data/FilterTranslationTests.cs`

**Interfaces:**
- Consumes: `LambdaShape`, `LambdaValues`, `TranslationRefusal` (Task 4); `RecordShape`, `EnumMap` (Task 2); `AlvoFilter`, `AlvoComparison`, `AlvoAnd`, `AlvoOr`, `AlvoNot`, `AlvoFilterOperator`, `AlvoFilter.MaxInCandidates` (1000).
- Produces: `internal sealed class FilterTranslator` — `static AlvoFilter Translate<T>(Expression<Func<T, bool>> where, string entity) where T : class`. Captured values are evaluated on every call; an enum value becomes its declared text; `&&`/`||` chains are flattened into one `AlvoAnd`/`AlvoOr`; an empty list is `new AlvoOr([])` (matches nothing). Test helper `FilterText.Of(AlvoFilter)`.

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Tests/Data/FilterText.cs`:

```csharp
using MMLib.Alvo.Data;

using System.Collections;
using System.Globalization;

namespace MMLib.Alvo.Tests.Data;

/// <summary>A filter tree as one line of text, so a translation is asserted against a readable expectation.</summary>
internal static class FilterText
{
    internal static string Of(AlvoFilter filter) => filter switch
    {
        AlvoComparison comparison => $"{comparison.Field} {comparison.Operator.ToString().ToLowerInvariant()} {Value(comparison.Value)}",
        AlvoAnd and => $"and({string.Join(", ", and.Filters.Select(Of))})",
        AlvoOr or => $"or({string.Join(", ", or.Filters.Select(Of))})",
        AlvoNot not => $"not({Of(not.Filter)})",
        _ => throw new ArgumentException("An unknown filter case.", nameof(filter)),
    };

    private static string Value(object? value) => value switch
    {
        null => "null",
        string text => $"'{text}'",
        bool flag => flag ? "true" : "false",
        DateTimeOffset instant => instant.ToString("O", CultureInfo.InvariantCulture),
        DateOnly day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IEnumerable items => $"[{string.Join(", ", items.Cast<object?>().Select(Value))}]",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
```

Create `test/MMLib.Alvo.Tests/Data/FilterTranslationTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;

using System.Linq.Expressions;

namespace MMLib.Alvo.Tests.Data;

/// <summary>
/// The query column of spec 2026-10-14 §4.3 as data (HX-05 acceptance (1)): each lambda's <c>AlvoFilter</c>, with values
/// placed as typed values for the driver to bind; every refused construct throws naming the entity and a fix.
/// </summary>
public sealed class FilterTranslationTests
{
    private static readonly OrderStatus[] Open = [OrderStatus.Draft, OrderStatus.InReview];
    private static readonly string[] NoNotes = [];
    private static readonly DateTimeOffset Cutoff = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 11, 2);

    public static TheoryData<string, Expression<Func<Order, bool>>, string> Translated()
    {
        var note = "it's";
        List<Guid> ids = [Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222")];

        return new()
        {
            { "equality", x => x.Note == "a", "note eq 'a'" },
            { "[AlvoField] name", x => x.Reference == "R-1", "ref_no eq 'R-1'" },
            { "greater", x => x.Quantity > 3, "quantity gt 3" },
            { "value on the left", x => 3 < x.Quantity, "quantity gt 3" },
            { "less or equal", x => x.Total <= 10.5m, "total lte 10.5" },
            { "a flat and", x => x.Quantity != 2 && x.Total >= 1m && x.Rank < 9, "and(quantity neq 2, total gte 1, rank lt 9)" },
            { "bool field and null", x => x.IsPaid || x.Code == null, "or(is_paid eq true, code is null)" },
            { "negated bool field", x => !x.IsPaid, "not(is_paid eq true)" },
            { "not null and HasValue", x => x.Code != null && x.DueAt.HasValue, "and(not(code is null), not(due_at is null))" },
            { "enum member", x => x.Status == OrderStatus.Approved, "status eq 'ok'" },
            { "nullable enum", x => x.Previous == OrderStatus.Draft, "previous eq 'draft'" },
            { "constant enum list", x => Open.Contains(x.Status), "status in ['draft', 'in_review']" },
            { "captured id list", x => ids.Contains(x.Id), "id in [11111111-1111-1111-1111-111111111111, 22222222-2222-2222-2222-222222222222]" },
            { "inline text list", x => new[] { "a", "b" }.Contains(x.Note), "note in ['a', 'b']" },
            { "empty list", x => NoNotes.Contains(x.Note), "or()" },
            { "instant", x => x.DueAt < Cutoff, "due_at lt 2026-01-01T00:00:00.0000000+00:00" },
            { "captured text", x => x.Note == note, "note eq 'it's'" },
            { "day", x => x.ShipOn == Day, "ship_on eq 2026-11-02" },
            { "nested groups", x => x.Note == "a" && (x.Quantity > 1 || x.IsPaid), "and(note eq 'a', or(quantity gt 1, is_paid eq true))" },
        };
    }

    public static TheoryData<string, Expression<Func<Order, bool>>, string> Refused()
    {
        var many = Enumerable.Range(0, 1001).Select(number => (long)number).ToArray();

        return new()
        {
            { "text operator", x => x.Note.StartsWith("a"), "'StartsWith' has no filter operator yet" },
            { "arithmetic", x => x.Total * 2m > 10m, "arithmetic has no filter equivalent" },
            { "two fields", x => x.Quantity > x.Rank, "it compares two fields" },
            { "method on a field", x => x.Note.Trim() == "a", "method 'Trim' has no filter equivalent" },
            { "clock", x => DateTimeOffset.UtcNow > x.DueAt, "a query has no clock" },
            { "relational enum", x => x.Status > OrderStatus.Draft, "an enum field compares with == and != only" },
            { "too many candidates", x => many.Contains(x.Quantity), "it lists 1001 candidates" },
            { "unmapped property", x => x.Summary == "a", "Order.Summary maps to no field" },
        };
    }

    [Theory]
    [MemberData(nameof(Translated))]
    public void A_predicate_translates_to_its_filter(string row, Expression<Func<Order, bool>> where, string filter) =>
        FilterText.Of(FilterTranslator.Translate(where, "orders")).ShouldBe(filter, row);

    [Theory]
    [MemberData(nameof(Refused))]
    public void A_construct_with_no_filter_equivalent_is_refused_naming_the_entity_and_a_fix(string row, Expression<Func<Order, bool>> where, string reason)
    {
        var refusal = Should.Throw<AlvoTranslationException>(() => FilterTranslator.Translate(where, "orders"));

        refusal.Message.ShouldContain("in a typed query on 'orders': ", customMessage: row);
        refusal.Message.ShouldContain(reason, customMessage: row);
        refusal.FixSuggestion.ShouldNotBeNullOrWhiteSpace(row);
    }

    [Fact]
    public void A_captured_value_is_placed_as_a_typed_value_never_as_text()
    {
        var payload = "x'; DROP TABLE orders; --";

        var comparison = FilterTranslator.Translate<Order>(x => x.Note == payload, "orders").ShouldBeOfType<AlvoComparison>();

        comparison.Value.ShouldBeSameAs(payload);
    }

    [Fact]
    public void A_wrapper_filters_any_field_through_its_record() =>
        FilterText.Of(FilterTranslator.Translate<OrderView>(x => (string?)x.Record["legacy_code"] == "L-9", "orders")).ShouldBe("legacy_code eq 'L-9'");
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*FilterTranslationTests'`
Expected: FAIL — compile error (`FilterTranslator` does not exist).

- [ ] **Step 3: Write the query translator**

`src/MMLib.Alvo/Data/Internal/FilterTranslator.cs`:

```csharp
using System.Collections;
using System.Collections.Frozen;
using System.Linq.Expressions;

namespace MMLib.Alvo.Data.Internal;

/// <summary>
/// Translates a typed query's predicate into the public <see cref="AlvoFilter"/> tree (spec 2026-10-14 X7, §4.3), on every
/// call: a captured value is read then and placed as a typed value the driver binds as a parameter, never as text.
/// </summary>
internal sealed class FilterTranslator
{
    private const string NoClockFix = "read the clock before the query and capture it: var now = DateTimeOffset.UtcNow; then x => x.DueAt < now.";
    private const string NoTextOperatorFix = "match the whole value with ==, or filter the rows after reading them.";

    private static readonly FrozenDictionary<ExpressionType, AlvoFilterOperator> _operators = new Dictionary<ExpressionType, AlvoFilterOperator>
    {
        [ExpressionType.Equal] = AlvoFilterOperator.Eq,
        [ExpressionType.NotEqual] = AlvoFilterOperator.Neq,
        [ExpressionType.LessThan] = AlvoFilterOperator.Lt,
        [ExpressionType.LessThanOrEqual] = AlvoFilterOperator.Lte,
        [ExpressionType.GreaterThan] = AlvoFilterOperator.Gt,
        [ExpressionType.GreaterThanOrEqual] = AlvoFilterOperator.Gte,
    }.ToFrozenDictionary();

    private static readonly FrozenSet<string> _textTests = new[] { "StartsWith", "EndsWith", "Contains" }.ToFrozenSet(StringComparer.Ordinal);

    private readonly ParameterExpression _row;
    private readonly string _entity;
    private readonly RecordShape _shape;

    private FilterTranslator(ParameterExpression row, string entity, RecordShape shape) => (_row, _entity, _shape) = (row, entity, shape);

    internal static AlvoFilter Translate<T>(Expression<Func<T, bool>> where, string entity)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(where);

        return new FilterTranslator(where.Parameters[0], entity, RecordShape.Of(typeof(T))).Filter(where.Body);
    }

    private AlvoFilter Filter(Expression node) => node switch
    {
        BinaryExpression { NodeType: ExpressionType.AndAlso } => new AlvoAnd([.. Terms(node, ExpressionType.AndAlso).Select(Filter)]),
        BinaryExpression { NodeType: ExpressionType.OrElse } => new AlvoOr([.. Terms(node, ExpressionType.OrElse).Select(Filter)]),
        UnaryExpression { NodeType: ExpressionType.Not } not => new AlvoNot(Filter(not.Operand)),
        BinaryExpression comparison when _operators.TryGetValue(comparison.NodeType, out var op) => Comparison(comparison, op),
        MethodCallExpression call => Call(call),
        MemberExpression { Member.Name: "HasValue" } has when LambdaShape.IsNullable(has.Expression) => new AlvoNot(IsNull(has.Expression!)),
        _ when node.Type == typeof(bool) && LambdaShape.Touches(node, _row) => new AlvoComparison(Field(node), AlvoFilterOperator.Eq, true),
        _ => throw Refuse(node, Describe(node)),
    };

    private static IEnumerable<Expression> Terms(Expression node, ExpressionType connective) =>
        node is BinaryExpression binary && binary.NodeType == connective ? Terms(binary.Left, connective).Concat(Terms(binary.Right, connective)) : [node];

    private AlvoFilter Comparison(BinaryExpression comparison, AlvoFilterOperator op)
    {
        var (field, value, flipped) = Sides(comparison);
        var effective = flipped ? Flipped(op) : op;
        var operand = Value(value, field);
        if (operand is null)
        {
            return Null(comparison, effective, field);
        }

        return EnumOf(field) is not null && effective is not (AlvoFilterOperator.Eq or AlvoFilterOperator.Neq)
            ? throw Refuse(comparison, "an enum field compares with == and != only")
            : new AlvoComparison(Field(field), effective, operand);
    }

    private (Expression Field, Expression Value, bool Flipped) Sides(BinaryExpression comparison) =>
        (LambdaShape.Touches(comparison.Left, _row), LambdaShape.Touches(comparison.Right, _row)) switch
        {
            (true, false) => (comparison.Left, comparison.Right, false),
            (false, true) => (comparison.Right, comparison.Left, true),
            (true, true) => throw Refuse(comparison, "it compares two fields, and a filter compares a field with a value", "compare each field with a value, or filter the rows after reading them."),
            _ => throw Refuse(comparison, "it compares two values and reads no field", "compute it before the query."),
        };

    private static AlvoFilterOperator Flipped(AlvoFilterOperator op) => op switch
    {
        AlvoFilterOperator.Lt => AlvoFilterOperator.Gt,
        AlvoFilterOperator.Lte => AlvoFilterOperator.Gte,
        AlvoFilterOperator.Gt => AlvoFilterOperator.Lt,
        AlvoFilterOperator.Gte => AlvoFilterOperator.Lte,
        _ => op,
    };

    private AlvoFilter Null(BinaryExpression comparison, AlvoFilterOperator op, Expression field) => op switch
    {
        AlvoFilterOperator.Eq => IsNull(field),
        AlvoFilterOperator.Neq => new AlvoNot(IsNull(field)),
        _ => throw Refuse(comparison, "only == and != can test for null"),
    };

    private AlvoComparison IsNull(Expression field) => new(Field(field), AlvoFilterOperator.Is, null);

    private object? Value(Expression value, Expression field)
    {
        if (LambdaShape.FindClock(value) is { } clock)
        {
            throw Refuse(clock, "a query has no clock", NoClockFix);
        }

        return ValueFor(LambdaValues.Evaluate(LambdaShape.Unwrapped(value)), EnumOf(field), value);
    }

    private object? ValueFor(object? raw, Type? enumType, Expression node) => raw switch
    {
        null => null,
        Enum member => EnumValue(member, node),
        _ when enumType is not null => EnumValue((Enum)Enum.ToObject(enumType, raw), node),
        _ => raw,
    };

    private string EnumValue(Enum member, Expression node) => EnumMap.Of(member.GetType()).ValueOf(member)
        ?? throw Refuse(node, $"{member.GetType().Name} has no member {member}");

    private static Type? EnumOf(Expression field) => LambdaShape.Underlying(LambdaShape.Unwrapped(field).Type) is { IsEnum: true } type ? type : null;

    private string Field(Expression node)
    {
        var unwrapped = LambdaShape.Unwrapped(node);

        return unwrapped switch
        {
            MemberExpression { Member.Name: "Value" } value when LambdaShape.IsNullable(value.Expression) => Field(value.Expression!),
            MemberExpression { Expression: ParameterExpression parameter } member when parameter == _row => FieldOf(member),
            MethodCallExpression call when LambdaShape.RecordKey(call) is { } key && IsRowRecord(call.Object!) => key,
            _ => throw Refuse(unwrapped, Describe(unwrapped)),
        };
    }

    private string FieldOf(MemberExpression member) => _shape.Mapped(member.Member)?.Field ?? throw Refuse(member, Describe(member));

    private bool IsRowRecord(Expression record) =>
        record == _row || (record is MemberExpression { Member.Name: nameof(IAlvoEntity.Record), Expression: var owner } && owner == _row && _shape.IsWrapper);

    private AlvoFilter Call(MethodCallExpression call)
    {
        if (call.Object?.Type == typeof(string) && _textTests.Contains(call.Method.Name))
        {
            throw Refuse(call, $"'{call.Method.Name}' has no filter operator yet: AlvoFilter offers no literal prefix, suffix or substring test", NoTextOperatorFix);
        }

        return LambdaShape.IsContains(call, out var source, out var value) && !LambdaShape.Touches(source, _row) && LambdaShape.Touches(value, _row)
            ? Membership(call, source, value)
            : throw Refuse(call, $"method '{call.Method.Name}' has no filter equivalent");
    }

    private AlvoFilter Membership(MethodCallExpression call, Expression source, Expression value)
    {
        if (LambdaShape.FindClock(source) is { } clock)
        {
            throw Refuse(clock, "a query has no clock", NoClockFix);
        }

        var enumType = EnumOf(value);
        var candidates = (LambdaValues.Evaluate(source) as IEnumerable ?? Array.Empty<object?>()).Cast<object?>().Select(item => ValueFor(item, enumType, source)).ToList();
        if (candidates.Count > AlvoFilter.MaxInCandidates)
        {
            throw Refuse(call, $"it lists {candidates.Count} candidates, past the {AlvoFilter.MaxInCandidates} one filter may bind", "split the list across queries.");
        }

        return candidates.Count == 0 ? new AlvoOr([]) : new AlvoComparison(Field(value), AlvoFilterOperator.In, candidates);
    }

    private string Describe(Expression node) => node switch
    {
        BinaryExpression { NodeType: ExpressionType.Add or ExpressionType.AddChecked or ExpressionType.Subtract or ExpressionType.SubtractChecked or ExpressionType.Multiply or ExpressionType.MultiplyChecked or ExpressionType.Divide or ExpressionType.Modulo } => "arithmetic has no filter equivalent",
        MethodCallExpression call => $"method '{call.Method.Name}' has no filter equivalent",
        MemberExpression member when LambdaShape.IsClock(member) => "a query has no clock",
        MemberExpression { Expression: ParameterExpression } member => $"{_shape.Name}.{member.Member.Name} maps to no field: it has neither a public setter nor [AlvoField]",
        ConditionalExpression => "a conditional (?:) has no filter equivalent",
        _ => $"a {node.NodeType} expression has no filter equivalent",
    };

    private AlvoTranslationException Refuse(Expression node, string reason, string fix = TranslationRefusal.QueryFix) =>
        TranslationRefusal.Query(_entity, node, reason, fix);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*FilterTranslationTests'`
Expected: PASS (19 translated rows, 8 refused rows, 2 facts).

- [ ] **Step 5: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Data/Internal/FilterTranslator.cs test/MMLib.Alvo.Tests/Data/FilterText.cs test/MMLib.Alvo.Tests/Data/FilterTranslationTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Data/Internal/FilterTranslator.cs test/MMLib.Alvo.Tests/Data/FilterText.cs test/MMLib.Alvo.Tests/Data/FilterTranslationTests.cs
git commit -m "feat(data): translate a typed query into an AlvoFilter

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 6: Typed registration — `Entity<T>`, `AlvoEntityBuilder<T>`, `AddEventHandler<TMessage, THandler>`

**Files:**
- Create: `src/MMLib.Alvo/Extensions/AlvoEntityBuilder{T}.cs`, `src/MMLib.Alvo/Extensions/Internal/TypedHandler.cs`
- Modify: `src/MMLib.Alvo/Extensions/AlvoExtensionBuilderExtensions.cs`, `src/MMLib.Alvo/Extensions/Internal/ExtensionRegistration.cs` (`HandlerRegistration`), `src/MMLib.Alvo/Extensions/Internal/ExtensionRegistrations.cs`, `src/MMLib.Alvo/Events/Internal/HandlerMessage.cs`, `src/MMLib.Alvo/Events/Internal/DeliveryRunner.cs` (line 272)
- Test: `test/MMLib.Alvo.Tests/Extensions/TypedRegistrationTests.cs`
- Baseline: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Consumes: `ConditionTranslator.Translate<T>` (Task 4); `RecordShape.Of`, `.Entity`, `.EventType`, `.Materialize<T>` (Tasks 2–3); `ExtensionRegistrations.AddHandler`, `HandlerInvoker.FromDelegate(Delegate, Type)`, `HandlerInvoker.FromClass<THandler, TMessage>()`, `HandledEventType.Ensure(string)`, `EntityEvents`, `NamedEvent`, `HandlerVerb`; `HandlerCatalog.Registry(Action<IAlvoBuilder>)` and `HandlerCatalog.OnUpdated` (tests); `ExtensionCatalog(ExtensionRegistry, IOptions<AlvoExtensionOptions>).Catalog()` (HX-04).
- Produces:
  - `internal sealed record TypedHandler(RecordShape Shape, Func<AlvoEventData, object> Message)` with `static TypedHandler ForEntity<T>(HandlerVerb verb) where T : class` and `static TypedHandler ForPayload<T>() where T : class`.
  - `HandlerRegistration` gains a trailing `TypedHandler? Typed = null`; `ExtensionRegistrations.AddHandler(..., HandlerInvoker invoker, TypedHandler? typed = null)`.
  - `HandlerMessage.For(AlvoEvent @event, HandlerRegistration handler)` (was `(AlvoEvent, HandlerSource)`).
  - Public `AlvoEntityBuilder<T>`, `Entity<T>`, `AddEventHandler<TMessage, THandler>` (Global Constraints). `Entity<T>` and `AddEventHandler<TMessage, THandler>` throw `ArgumentException` (parameter name `T` / `TMessage`) when the attribute is missing; an untranslatable `when` throws `AlvoTranslationException` from the registration call.

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Tests/Extensions/TypedRegistrationTests.cs`:

```csharp
using Microsoft.Extensions.Options;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Tests.Data;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>Typed registrations (spec 2026-10-14 §4.2, D1, D2): the untyped core's registration, with a translated condition and typed messages.</summary>
public sealed class TypedRegistrationTests
{
    [Fact]
    public void A_typed_handler_registers_its_translated_condition_its_entity_and_its_message()
    {
        var handler = Registry(alvo => alvo.Entity<TypedTicket>(tickets => tickets.OnUpdated<TypedTicketResolved>(
            "typed-resolved", when: e => e.Changed(x => x.Status) && e.New.Status == TicketStatus.Resolved))).Handlers.ShouldHaveSingleItem();

        handler.When.ShouldBe("changed(status) && new.status == 'resolved'");
        handler.Source.ShouldBe(new EntityEvents(["tickets"], HandlerVerb.Updated));
        handler.Invoker.MessageType.ShouldBe(typeof(AlvoUpdated<TypedTicket>));
        handler.Typed.ShouldNotBeNull().Shape.Type.ShouldBe(typeof(TypedTicket));
    }

    [Fact]
    public void Typed_and_untyped_registrations_of_one_handler_describe_the_same_condition()
    {
        var registry = Registry(alvo => alvo
            .Entity<TypedTicket>(tickets => tickets.OnUpdated<TypedTicketResolved>(
                "typed", when: e => e.Changed(x => x.Status) && e.New.Status == TicketStatus.Resolved))
            .Entity("tickets", tickets => tickets.OnUpdated("untyped", OnUpdated, when: "changed(status) && new.status == 'resolved'")));

        var items = new ExtensionCatalog(registry, Options.Create(new AlvoExtensionOptions())).Catalog().Items;

        items.Single(item => item.Id == "typed").When.ShouldBe(items.Single(item => item.Id == "untyped").When);
    }

    [Fact]
    public void A_typed_entity_takes_a_cel_string_too() =>
        Registry(alvo => alvo.Entity<TypedTicket>(tickets => tickets.OnCreated(
            "typed-cel", (AlvoCreated<TypedTicket> _) => Task.CompletedTask, when: "new.title == 'x'"))).Handlers.ShouldHaveSingleItem().When.ShouldBe("new.title == 'x'");

    [Fact]
    public void A_type_without_an_entity_attribute_is_refused_at_registration() =>
        Should.Throw<ArgumentException>(() => Registry(alvo => alvo.Entity<Unmarked>(_ => { })))
            .Message.ShouldContain("[AlvoEntity(");

    [Fact]
    public void An_untranslatable_condition_is_refused_at_registration_naming_the_handler() =>
        Should.Throw<AlvoTranslationException>(() => Registry(alvo => alvo.Entity<TypedTicket>(tickets => tickets.OnUpdated<TypedTicketResolved>(
            "typed-bad", when: e => e.New.Title.GetHashCode() == 1)))).Message.ShouldContain("in extension 'typed-bad' (when)");

    [Fact]
    public void A_typed_application_event_handler_reads_its_type_from_the_attribute()
    {
        var handler = Registry(alvo => alvo.AddEventHandler<TicketEscalated, EscalationHandler>("escalate")).Handlers.ShouldHaveSingleItem();

        handler.Source.ShouldBe(new NamedEvent("desk.escalated"));
        handler.Typed.ShouldNotBeNull();
    }

    [Fact]
    public void A_payload_type_without_an_event_attribute_is_refused_at_registration() =>
        Should.Throw<ArgumentException>(() => Registry(alvo => alvo.AddEventHandler<TypedTicket, UnusedHandler>("no-event")))
            .Message.ShouldContain("[AlvoEvent(");

    [Fact]
    public void The_typed_message_is_built_from_the_event_images()
    {
        var handler = Registry(alvo => alvo.Entity<TypedTicket>(tickets => tickets.OnUpdated<TypedTicketResolved>("typed"))).Handlers.ShouldHaveSingleItem();
        var data = new AlvoEventData { OldRecord = Ticket("open"), Record = Ticket("resolved") };

        var message = handler.Typed!.Message(data).ShouldBeOfType<AlvoUpdated<TypedTicket>>();

        (message.Old.Status, message.New.Status).ShouldBe((TicketStatus.Open, TicketStatus.Resolved));
    }

    [Fact]
    public void A_typed_payload_is_built_from_the_event_data() =>
        Registry(alvo => alvo.AddEventHandler<TicketEscalated, EscalationHandler>("escalate")).Handlers.ShouldHaveSingleItem()
            .Typed!.Message(new AlvoEventData { Record = new AlvoRecord(new Dictionary<string, object?> { ["reason"] = "late", ["level"] = 2L }) })
            .ShouldBe(new TicketEscalated("late", 2));

    private static AlvoRecord Ticket(string status) => new(new Dictionary<string, object?>
    {
        ["id"] = "7d1a3c38-9f0b-4c39-9c51-5f2a8d0e6b21",
        ["title"] = "Printer jam",
        ["code"] = null,
        ["status"] = status,
    });

    private sealed class UnusedHandler : IAlvoEventHandler<TypedTicket>
    {
        public Task HandleAsync(TypedTicket message, AlvoEventContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*TypedRegistrationTests'`
Expected: FAIL — compile errors (`Entity<T>`, `HandlerRegistration.Typed` do not exist).

- [ ] **Step 3: Carry the typed handler through the registration**

`src/MMLib.Alvo/Extensions/Internal/TypedHandler.cs`:

```csharp
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>What a typed registration adds to the untyped core's: the host type's shape, and how its message is built from an event.</summary>
/// <param name="Shape">The host type's mapping, checked against the entity at boot.</param>
/// <param name="Message">Builds the handler's message from the event's images or payload.</param>
internal sealed record TypedHandler(RecordShape Shape, Func<AlvoEventData, object> Message)
{
    internal static TypedHandler ForEntity<T>(HandlerVerb verb)
        where T : class
    {
        var shape = RecordShape.Of(typeof(T));
        Func<AlvoEventData, object> message = verb switch
        {
            HandlerVerb.Created => data => new AlvoCreated<T> { New = shape.Materialize<T>(data.Record) },
            HandlerVerb.Updated => data => new AlvoUpdated<T> { Old = shape.Materialize<T>(data.OldRecord), New = shape.Materialize<T>(data.Record) },
            HandlerVerb.Deleted => data => new AlvoDeleted<T> { Old = shape.Materialize<T>(data.OldRecord) },
            _ => data => new AlvoSaved<T> { New = shape.Materialize<T>(data.Record), Old = data.OldRecord is { } old ? shape.Materialize<T>(old) : null },
        };

        return new(shape, message);
    }

    internal static TypedHandler ForPayload<T>()
        where T : class
    {
        var shape = RecordShape.Of(typeof(T));

        return new(shape, data => shape.Materialize<T>(data.Record));
    }
}
```

In `src/MMLib.Alvo/Extensions/Internal/ExtensionRegistration.cs`, replace the `HandlerRegistration` declaration with:

```csharp
/// <summary>An event handler; <paramref name="Typed"/> is set when it was registered over a host type.</summary>
internal sealed record HandlerRegistration(
    string Id, int Sequence, HandlerSource Source, string? When, HandlerSettings Settings, HandlerInvoker Invoker, TypedHandler? Typed = null)
    : ExtensionRegistration(Id, Sequence);
```

In `src/MMLib.Alvo/Extensions/Internal/ExtensionRegistrations.cs`, give `AddHandler` a trailing parameter `TypedHandler? typed = null` and pass it on: `new HandlerRegistration(id, sequence, source, when, HandlerSettings.From(options), invoker, typed)`.

In `src/MMLib.Alvo/Events/Internal/HandlerMessage.cs`, replace `For` with the following, and its summary with `What a handler is handed for one event: the verb's images or the payload, over the host type a typed registration names, else over AlvoRecord.`:

```csharp
    internal static object For(AlvoEvent @event, HandlerRegistration handler) =>
        handler.Typed is { } typed ? typed.Message(@event.Data) : Untyped(@event, handler.Source);

    private static object Untyped(AlvoEvent @event, HandlerSource source) => source is EntityEvents entities
        ? ForVerb(@event.Data, entities.Verb)
        : @event.Data.Record ?? AlvoRecord.Empty;
```

In `src/MMLib.Alvo/Events/Internal/DeliveryRunner.cs` line 272: `var message = HandlerMessage.For(@event, handler.Registration);`. A materialization failure — a field missing from an image — throws inside the attempt like any handler exception and is recorded by its type name.

- [ ] **Step 4: Write the typed builder and the two registration verbs**

`src/MMLib.Alvo/Extensions/AlvoEntityBuilder{T}.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;

using System.Linq.Expressions;

namespace MMLib.Alvo.Extensions;

/// <summary>
/// Registers host code for the entity <typeparamref name="T"/> maps, with messages over <typeparamref name="T"/> and
/// conditions written as lambdas over it — or as CEL strings; every registration is checked against the descriptor when
/// the host starts.
/// </summary>
/// <typeparam name="T">A class marked with <c>[AlvoEntity("…")]</c>.</typeparam>
/// <remarks>
/// A lambda <c>when</c> is translated into CEL as it is registered: an untranslatable one throws
/// <see cref="Data.AlvoTranslationException"/> from this call, naming the handler. The catalog shows the translated CEL.
/// </remarks>
public sealed class AlvoEntityBuilder<T>
    where T : class
{
    private readonly IServiceCollection _services;
    private readonly string _entity;

    internal AlvoEntityBuilder(IServiceCollection services, string entity)
    {
        _services = services;
        _entity = entity;
    }

    /// <summary>Runs <paramref name="handler"/> after a record is created, when <paramref name="when"/> holds.</summary>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens: its deliveries are found by it after a restart.</param>
    /// <param name="handler">A delegate taking an <c>AlvoCreated&lt;T&gt;</c>, and optionally the context, a token and services.</param>
    /// <param name="when">A condition over <c>e.New</c> and <c>e.Caller</c>.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> OnCreated(string id, Delegate handler, Expression<Func<EventCondition<T>, bool>> when, Action<AlvoHandlerOptions>? configure = null) =>
        Add(id, HandlerVerb.Created, HandlerInvoker.FromDelegate(handler, typeof(AlvoCreated<T>)), Translated(id, HandlerVerb.Created, when), configure);

    /// <summary>Runs <paramref name="handler"/> after a record is created.</summary>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="handler">A delegate taking an <c>AlvoCreated&lt;T&gt;</c>, and optionally the context, a token and services.</param>
    /// <param name="when">An optional CEL condition over <c>new.</c>.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> OnCreated(string id, Delegate handler, string? when = null, Action<AlvoHandlerOptions>? configure = null) =>
        Add(id, HandlerVerb.Created, HandlerInvoker.FromDelegate(handler, typeof(AlvoCreated<T>)), when, configure);

    /// <summary>Runs a <typeparamref name="THandler"/>, built per delivery, after a record is created, when <paramref name="when"/> holds.</summary>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="when">A condition over <c>e.New</c> and <c>e.Caller</c>.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> OnCreated<THandler>(string id, Expression<Func<EventCondition<T>, bool>> when, Action<AlvoHandlerOptions>? configure = null)
        where THandler : class, IAlvoEventHandler<AlvoCreated<T>> =>
        Add(id, HandlerVerb.Created, HandlerInvoker.FromClass<THandler, AlvoCreated<T>>(), Translated(id, HandlerVerb.Created, when), configure);

    /// <summary>Runs a <typeparamref name="THandler"/>, built per delivery, after a record is created.</summary>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="when">An optional CEL condition over <c>new.</c>.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> OnCreated<THandler>(string id, string? when = null, Action<AlvoHandlerOptions>? configure = null)
        where THandler : class, IAlvoEventHandler<AlvoCreated<T>> =>
        Add(id, HandlerVerb.Created, HandlerInvoker.FromClass<THandler, AlvoCreated<T>>(), when, configure);

    private string Translated(string id, HandlerVerb verb, Expression<Func<EventCondition<T>, bool>> when)
    {
        ArgumentNullException.ThrowIfNull(when);

        return ConditionTranslator.Translate(when, id, verb);
    }

    private AlvoEntityBuilder<T> Add(string id, HandlerVerb verb, HandlerInvoker invoker, string? when, Action<AlvoHandlerOptions>? configure)
    {
        ExtensionRegistrations.AddHandler(_services, id, new EntityEvents([_entity], verb), when, configure, invoker, TypedHandler.ForEntity<T>(verb));

        return this;
    }
}
```

Then add the same four overloads (same order, same doc shape) for:
- `OnUpdated` — `HandlerVerb.Updated`, message `AlvoUpdated<T>`, summaries "…after a record is updated or replaced…", lambda `when` doc "A condition over `e.New`, `e.Old`, `e.Changed(…)` and `e.Caller`.", string `when` doc "An optional CEL condition over both images.";
- `OnDeleted` — `HandlerVerb.Deleted`, `AlvoDeleted<T>`, "…after a record is deleted…", "A condition over `e.Old` and `e.Caller`.", "An optional CEL condition over the deleted record (`old.`).";
- `OnSaved` — `HandlerVerb.Saved`, `AlvoSaved<T>`, "…after a record is created or updated…", "A condition over `e.New` and `e.Caller`.", "An optional CEL condition over the stored record (`new.`)."

Sixteen public methods in all, in the order Created, Updated, Deleted, Saved. (A call with a lambda `when` binds the `Expression` overload — a lambda does not convert to `string`; a call without `when` binds the `string?` overload, the only one whose `when` is optional.)

In `src/MMLib.Alvo/Extensions/AlvoExtensionBuilderExtensions.cs` add `using MMLib.Alvo.Data.Internal;` and, after `Entities`:

```csharp
    /// <summary>Registers host code for the entity <typeparamref name="T"/> maps, with conditions written as lambdas over it.</summary>
    /// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>; its mapped properties are checked against the entity when the host starts.</typeparam>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="configure">Registers the entity's handlers.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentException"><typeparamref name="T"/> carries no <see cref="AlvoEntityAttribute"/>.</exception>
    public static IAlvoBuilder Entity<T>(this IAlvoBuilder builder, Action<AlvoEntityBuilder<T>> configure)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var entity = RecordShape.Of(typeof(T)).Entity
            ?? throw new ArgumentException($"Mark {typeof(T).Name} with [AlvoEntity(\"<entity>\")], so Alvo knows which entity it maps.", nameof(T));
        configure(new AlvoEntityBuilder<T>(builder.Services, entity));

        return builder;
    }
```

and, after the existing `AddEventHandler<THandler>`:

```csharp
    /// <summary>Runs a <typeparamref name="THandler"/>, built per delivery, for every application event <typeparamref name="TMessage"/> names, handing it the payload as a <typeparamref name="TMessage"/>.</summary>
    /// <typeparam name="TMessage">The payload type, marked with <see cref="AlvoEventAttribute"/>.</typeparam>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="when">An optional CEL condition over <c>@user</c> and <c>@tenant</c>.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentException"><typeparamref name="TMessage"/> carries no <see cref="AlvoEventAttribute"/>.</exception>
    public static IAlvoBuilder AddEventHandler<TMessage, THandler>(
        this IAlvoBuilder builder, string id, string? when = null, Action<AlvoHandlerOptions>? configure = null)
        where TMessage : class
        where THandler : class, IAlvoEventHandler<TMessage>
    {
        ArgumentNullException.ThrowIfNull(builder);
        var eventType = RecordShape.Of(typeof(TMessage)).EventType
            ?? throw new ArgumentException($"Mark {typeof(TMessage).Name} with [AlvoEvent(\"<event type>\")], so Alvo knows which event it is.", nameof(TMessage));
        HandledEventType.Ensure(eventType);
        ExtensionRegistrations.AddHandler(
            builder.Services, id, new NamedEvent(eventType), when, configure, HandlerInvoker.FromClass<THandler, TMessage>(), TypedHandler.ForPayload<TMessage>());

        return builder;
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*TypedRegistrationTests'`, then `--filter-class '*Extension*'` and `--filter-class '*Handler*'`
Expected: PASS — the HX-03/HX-04 handler suites unchanged (the untyped path's message is built exactly as before).

- [ ] **Step 6: Approve the core baseline**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*PublicApiApprovalTests'`
Expected: FAIL; the received diff adds exactly `AlvoEntityBuilder<T>` with its sixteen methods, and on `AlvoExtensionBuilderExtensions` `Entity<T>` and `AddEventHandler<TMessage, THandler>`. Accept, re-run (PASS), dispatch `alvo-snapshot-judge`.

- [ ] **Step 7: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include "src/MMLib.Alvo/Extensions/AlvoEntityBuilder{T}.cs" src/MMLib.Alvo/Extensions/Internal/TypedHandler.cs src/MMLib.Alvo/Extensions/AlvoExtensionBuilderExtensions.cs src/MMLib.Alvo/Extensions/Internal/ExtensionRegistration.cs src/MMLib.Alvo/Extensions/Internal/ExtensionRegistrations.cs src/MMLib.Alvo/Events/Internal/HandlerMessage.cs src/MMLib.Alvo/Events/Internal/DeliveryRunner.cs test/MMLib.Alvo.Tests/Extensions/TypedRegistrationTests.cs
scripts/test-ring0
git add "src/MMLib.Alvo/Extensions/AlvoEntityBuilder{T}.cs" src/MMLib.Alvo/Extensions/Internal/TypedHandler.cs src/MMLib.Alvo/Extensions/AlvoExtensionBuilderExtensions.cs src/MMLib.Alvo/Extensions/Internal/ExtensionRegistration.cs src/MMLib.Alvo/Extensions/Internal/ExtensionRegistrations.cs src/MMLib.Alvo/Events/Internal/HandlerMessage.cs src/MMLib.Alvo/Events/Internal/DeliveryRunner.cs test/MMLib.Alvo.Tests/Extensions/TypedRegistrationTests.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(extensions): register typed handlers with Entity<T>

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 7: A typed record that does not fit refuses boot and the apply that would break it **[slice end]**

**Files:**
- Modify: `src/MMLib.Alvo/Extensions/Internal/HandlerBinding.cs`, `src/MMLib.Alvo/Extensions/Internal/ExtensionApplyRefusals.cs`
- Test: `test/MMLib.Alvo.Tests/Extensions/TypedBindingTests.cs`; two facts and a helper appended to `test/MMLib.Alvo.Tests/Extensions/ExtensionApplyGuardTests.cs`

**Interfaces:**
- Consumes: `HandlerRegistration.Typed` (Task 6); `RecordShape.Problems`, `ShapeMapping.Problems(shape, entity, fromEventImages: true)`, `ShapeMapping.Holds` (Task 3); `HandlerCatalog.Compile`, `Registry`, `EveryService`, `ExtensionBindingCompiler.Check(registry, compiler, services)` and the guard test's `Guard`, `Refused`, `Precise`, `Replaced`, `WithoutField`, `FieldFix` (tests).
- Produces: problems at locator `extensions/<id>/record` — catalog-free ones (`RecordShape.Problems`) from `HandlerBinding.CheckRegistration`, so an `Awaiting` boot refuses them too (HX-03 (39)); mapping ones from `Bind`. A handler with either never binds. `ExtensionApplyRefusals` points at a removed, excluded or retyped **mapped** field with the sentence "its typed record `<Type>` maps it".

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Tests/Extensions/TypedBindingTests.cs`:

```csharp
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Tests.Data;
using MMLib.Alvo.Tests.Expressions;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>
/// Boot validation of typed mappings (spec 2026-10-14 §4.2, E7; HX-05 acceptance (5)): a property with no field, of a type
/// its field does not hold, reading a field left out of the event images, or an enum member with no declared value refuses
/// boot at <c>extensions/&lt;id&gt;/record</c>, all at once.
/// </summary>
public sealed class TypedBindingTests
{
    [Fact]
    public void A_typed_handler_that_fits_binds()
    {
        var result = Compile(alvo => alvo.Entity<TypedTicket>(t => t.OnUpdated<TypedTicketResolved>("typed", when: e => e.New.Status == TicketStatus.Resolved)));

        result.Problems.ShouldBeEmpty();
        result.Bindings.Handler("typed").ShouldNotBeNull();
    }

    [Fact]
    public void A_property_with_no_field_refuses_boot_naming_the_property() =>
        Problem<TicketWithVat>().Message.ShouldBe("TicketWithVat.VatId maps to 'vat_id', which 'tickets' does not declare.");

    [Fact]
    public void A_mistyped_property_refuses_boot() =>
        Problem<TicketWithNumericCode>().Message.ShouldBe("TicketWithNumericCode.Code is a Int64, but 'code' is a string field, which holds String.");

    [Fact]
    public void A_property_reading_a_field_left_out_of_the_event_images_refuses_boot() =>
        Problem<TicketWithSecret>().Message.ShouldContain("leaves out of its event images (events.exclude)");

    [Fact]
    public void A_property_that_cannot_hold_null_on_an_optional_field_refuses_boot() =>
        Problem<TicketWithStrictCode>().Message.ShouldBe("'code' may be empty, but TicketWithStrictCode.Code cannot hold null.");

    [Fact]
    public void An_enum_member_with_no_declared_value_refuses_boot() =>
        Problem<TicketWithArchive>().Message.ShouldStartWith("ArchivedTicketStatus.Archived maps to 'archived', which is not a value of 'status'");

    [Fact]
    public void A_property_no_field_type_holds_refuses_even_before_a_descriptor()
    {
        var problems = ExtensionBindingCompiler.Check(
            Registry(alvo => alvo.Entity<TicketWithScore>(t => t.OnCreated("scored", (AlvoCreated<TicketWithScore> _) => Task.CompletedTask))),
            CelFixtures.Compiler,
            EveryService()).Problems;

        problems.ShouldHaveSingleItem().Locator.ShouldBe("extensions/scored/record");
    }

    [Fact]
    public void Every_typed_problem_is_reported_at_once()
    {
        var result = Compile(alvo => alvo
            .Entity<TicketWithVat>(t => t.OnCreated("missing", (AlvoCreated<TicketWithVat> _) => Task.CompletedTask))
            .Entity<TicketWithNumericCode>(t => t.OnCreated("mistyped", (AlvoCreated<TicketWithNumericCode> _) => Task.CompletedTask))
            .Entity<TicketWithSecret>(t => t.OnCreated("excluded", (AlvoCreated<TicketWithSecret> _) => Task.CompletedTask)));

        result.Problems.Select(problem => problem.Id).Distinct().Order().ShouldBe(["excluded", "missing", "mistyped"]);
        result.Bindings.Handlers.ShouldBeEmpty();
    }

    private static ExtensionProblem Problem<TRecord>()
        where TRecord : class
    {
        var problem = Compile(alvo => alvo.Entity<TRecord>(t => t.OnCreated("typed", (AlvoCreated<TRecord> _) => Task.CompletedTask)))
            .Problems.ShouldHaveSingleItem();
        problem.Locator.ShouldBe("extensions/typed/record");
        problem.Kind.ShouldBe("eventHandler");

        return problem;
    }
}
```

Append to `ExtensionApplyGuardTests` (add `using MMLib.Alvo.Tests.Data;`):

```csharp
    [Fact]
    public void Removing_a_field_a_typed_record_maps_is_refused_at_the_field()
    {
        var precise = Precise(Refused(Guard(Typed("typed")), WithoutField("tickets", "title"))).ShouldHaveSingleItem();

        precise.Path.ShouldBe("/entities/tickets/fields/title");
        precise.Message.ShouldContain("its typed record TypedTicket maps it");
        precise.FixSuggestion.ShouldBe(FieldFix);
    }

    [Fact]
    public void Excluding_a_field_a_typed_record_maps_is_refused_at_the_exclusion() =>
        Precise(Refused(Guard(Typed("typed")), Replaced("tickets", entity => entity with { EventImageExclusions = ["secret", "title"] })))
            .ShouldHaveSingleItem().Path.ShouldBe("/entities/tickets/events/exclude/1");

    private static Action<IAlvoBuilder> Typed(string id) =>
        alvo => alvo.Entity<TypedTicket>(t => t.OnUpdated<TypedTicketResolved>(id));
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*TypedBindingTests'`
Expected: FAIL — the misfits bind (no `record` problems yet).

- [ ] **Step 3: Check the mapping where the handler binds**

In `HandlerBinding` (add `using MMLib.Alvo.Data.Internal;`): call `CheckShape();` as the first line of `CheckRegistration` (before `ResolveServices(services);`), and add:

```csharp
    private void CheckShape()
    {
        foreach (var problem in handler.Typed?.Shape.Problems ?? [])
        {
            Add("record", problem.Message, problem.Fix);
        }
    }

    private void TypedMapping(EntitySchema entity)
    {
        if (handler.Typed is not { Shape: { Problems.Count: 0 } shape })
        {
            return;
        }

        foreach (var problem in ShapeMapping.Problems(shape, entity, fromEventImages: true))
        {
            Add("record", problem.Message, problem.Fix);
        }
    }
```

In `EntityConditions`, replace the `else if (Condition(entity, source.Verb) is { } condition) { conditions[name] = condition; }` arm with:

```csharp
            else
            {
                if (Condition(entity, source.Verb) is { } condition)
                {
                    conditions[name] = condition;
                }

                TypedMapping(entity);
            }
```

(`Bind` already returns `null` whenever a problem was added, so a misfit never binds.)

- [ ] **Step 4: Point the apply refusal at a mapped field**

In `ExtensionApplyRefusals` (add `using MMLib.Alvo.Data.Internal;`), replace the last statement of `ForEntity` (`return [.. FieldsRead(handler.When, before, compiler) …];`) with:

```csharp
        var read = FieldsRead(handler.When, before, compiler);
        return [.. read.Select(field => FieldRefusal(handler, before, after, field, compiler)).OfType<DescriptorValidationError>(),
            .. Mapped(handler, read).Select(mapped => MappedRefusal(handler, mapped.Shape, mapped.Property, after)).OfType<DescriptorValidationError>()];
```

and add:

```csharp
    private static IEnumerable<(RecordShape Shape, MappedProperty Property)> Mapped(HandlerRegistration handler, string[] read) =>
        handler.Typed is { } typed
            ? typed.Shape.Properties.Where(property => !read.Contains(property.Field, StringComparer.Ordinal)).Select(property => (typed.Shape, property))
            : [];

    private static DescriptorValidationError? MappedRefusal(HandlerRegistration handler, RecordShape shape, MappedProperty property, EntitySchema after)
    {
        var path = $"/entities/{after.Name}/fields/{property.Field}";
        if (Field(after, property.Field) is not { } now)
        {
            return Error(path, $"Field '{property.Field}' of '{after.Name}' is bound by extension '{handler.Id}' ({Kind}, in this host): its typed record {shape.Name} maps it, so without it the handler would no longer bind, and every event it would have handled would be recorded dead.", FixPrefix + "field.");
        }

        var index = after.EventImageExclusions.ToList().IndexOf(property.Field);
        if (index >= 0)
        {
            return FieldExcluded(handler.Id, after.Name, property.Field, index);
        }

        return ShapeMapping.Holds(property, now)
            ? null
            : Error(path, $"Field '{property.Field}' of '{after.Name}' is read by extension '{handler.Id}' ({Kind}, in this host): its typed record {shape.Name} maps it and cannot hold its new type, so the handler would no longer bind.", FixPrefix + "field's type.");
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*TypedBindingTests'`, `--filter-class '*ExtensionApplyGuardTests'`, `--filter-class '*ExtensionBindingCompilerTests'`
Expected: PASS — the existing guard and compiler facts unchanged.

- [ ] **Step 6: Format, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Extensions/Internal/HandlerBinding.cs src/MMLib.Alvo/Extensions/Internal/ExtensionApplyRefusals.cs test/MMLib.Alvo.Tests/Extensions/TypedBindingTests.cs test/MMLib.Alvo.Tests/Extensions/ExtensionApplyGuardTests.cs
scripts/test-ring1
git add src/MMLib.Alvo/Extensions/Internal/HandlerBinding.cs src/MMLib.Alvo/Extensions/Internal/ExtensionApplyRefusals.cs test/MMLib.Alvo.Tests/Extensions/TypedBindingTests.cs test/MMLib.Alvo.Tests/Extensions/ExtensionApplyGuardTests.cs
git commit -m "feat(extensions): refuse a typed record that does not fit at boot and at apply

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 8: Typed reads on `IAlvoSession` and `IAlvoData`

**Files:**
- Create: `src/MMLib.Alvo/Data/AlvoTypedReadExtensions.cs`, `src/MMLib.Alvo/Data/Internal/TypedEntity.cs`
- Test: `test/MMLib.Alvo.Tests/Data/TypedReadTests.cs`
- Baseline: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Consumes: `FilterTranslator.Translate<T>` (Task 5); `RecordShape.Of(...).Entity`, `As<T>()` (Tasks 2–3); `IAlvoSession.GetAsync/QueryAsync`, `IAlvoData.GetAsync/QueryAsync`; `AlvoQuery { Entity, Filter, Limit }`, `AlvoPage.Items`.
- Produces: `internal static class TypedEntity` — `string NameOf<T>()` (throws `InvalidOperationException` naming the missing `[AlvoEntity]`), `AlvoQuery Query<T>(Expression<Func<T, bool>> where, int limit) where T : class` (throws `ArgumentOutOfRangeException` for `limit < 1`), `IReadOnlyList<T> Items<T>(AlvoPage page) where T : class`; the public `AlvoTypedReadExtensions` (Global Constraints).

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Tests/Data/TypedReadTests.cs`:

```csharp
using MMLib.Alvo.Data;

using NSubstitute;

using static MMLib.Alvo.Tests.Data.TypedFixtures;

namespace MMLib.Alvo.Tests.Data;

/// <summary>Typed reads (spec 2026-10-14 §5): the entity the type names, the translated filter, the caller's rules, typed rows.</summary>
public sealed class TypedReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_typed_get_reads_the_entity_the_type_names_and_maps_the_record()
    {
        var session = Substitute.For<IAlvoSession>();
        var id = Guid.NewGuid();
        session.GetAsync("orders", id, Ct).Returns(Task.FromResult<AlvoRecord?>(OrderRecord()));

        var order = await session.GetAsync<Order>(id, Ct);

        order.ShouldNotBeNull().Status.ShouldBe(OrderStatus.Approved);
    }

    [Fact]
    public async Task A_typed_get_of_a_record_the_caller_cannot_see_is_null()
    {
        var session = Substitute.For<IAlvoSession>();
        session.GetAsync("orders", Arg.Any<Guid>(), Ct).Returns(Task.FromResult<AlvoRecord?>(null));

        (await session.GetAsync<Order>(Guid.NewGuid(), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_typed_query_sends_the_translated_filter_and_the_limit_and_maps_the_rows()
    {
        var session = Session(out var sent);

        var orders = await session.QueryAsync<Order>(x => x.Status == OrderStatus.Approved && x.Total > 10m, limit: 25, Ct);

        orders.ShouldHaveSingleItem().Note.ShouldBe("fragile");
        var query = sent.ShouldHaveSingleItem();
        (query.Entity, query.Limit).ShouldBe(("orders", 25));
        FilterText.Of(query.Filter!).ShouldBe("and(status eq 'ok', total gt 10)");
    }

    [Fact]
    public async Task A_captured_value_is_read_again_on_every_call()
    {
        var session = Session(out var sent);
        var note = "first";

        await session.QueryAsync<Order>(x => x.Note == note, limit: 5, Ct);
        note = "second";
        await session.QueryAsync<Order>(x => x.Note == note, limit: 5, Ct);

        sent.Select(query => FilterText.Of(query.Filter!)).ShouldBe(["note eq 'first'", "note eq 'second'"]);
    }

    [Fact]
    public async Task A_typed_query_needs_a_positive_limit() =>
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Session(out _).QueryAsync<Order>(x => x.IsPaid, limit: 0, Ct));

    [Fact]
    public async Task A_type_without_an_entity_attribute_cannot_be_read() =>
        (await Should.ThrowAsync<InvalidOperationException>(() => Substitute.For<IAlvoSession>().GetAsync<Unmarked>(Guid.NewGuid(), Ct)))
            .Message.ShouldContain("[AlvoEntity(");

    [Fact]
    public async Task An_untranslatable_query_is_refused_at_its_call_naming_the_entity() =>
        (await Should.ThrowAsync<AlvoTranslationException>(() => Session(out _).QueryAsync<Order>(x => x.Note.Trim() == "a", limit: 5, Ct)))
            .Message.ShouldContain("in a typed query on 'orders'");

    [Fact]
    public async Task The_data_port_s_typed_query_reads_as_the_caller_it_is_given()
    {
        var data = Substitute.For<IAlvoData>();
        var caller = new AlvoContext { User = new UserId(Guid.NewGuid()), Roles = new HashSet<Role> { Role.Authenticated } };
        data.QueryAsync(Arg.Any<AlvoQuery>(), caller, Ct).Returns(Task.FromResult(new AlvoPage { Items = [OrderRecord()] }));

        var orders = await data.QueryAsync<Order>(x => x.IsPaid, limit: 10, caller, Ct);

        orders.ShouldHaveSingleItem();
        await data.Received(1).QueryAsync(Arg.Is<AlvoQuery>(query => query.Entity == "orders" && query.Limit == 10), caller, Ct);
    }

    private static IAlvoSession Session(out List<AlvoQuery> sent)
    {
        var queries = new List<AlvoQuery>();
        var session = Substitute.For<IAlvoSession>();
        session.QueryAsync(Arg.Do<AlvoQuery>(queries.Add), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AlvoPage { Items = [OrderRecord()] }));
        sent = queries;

        return session;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*TypedReadTests'`
Expected: FAIL — compile errors (`GetAsync<T>`, `QueryAsync<T>` do not exist).

- [ ] **Step 3: Write the typed reads**

`src/MMLib.Alvo/Data/Internal/TypedEntity.cs`:

```csharp
using System.Linq.Expressions;

namespace MMLib.Alvo.Data.Internal;

/// <summary>What a typed read needs: the entity its type names, its query, and its rows as the type.</summary>
internal static class TypedEntity
{
    internal static string NameOf<T>() => RecordShape.Of(typeof(T)).Entity
        ?? throw new InvalidOperationException($"{typeof(T).Name} carries no [AlvoEntity(\"…\")], so a typed read cannot name its entity: mark it with the entity it maps.");

    internal static AlvoQuery Query<T>(Expression<Func<T, bool>> where, int limit)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(where);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var entity = NameOf<T>();

        return new AlvoQuery { Entity = entity, Filter = FilterTranslator.Translate(where, entity), Limit = limit };
    }

    internal static IReadOnlyList<T> Items<T>(AlvoPage page)
        where T : class =>
        [.. page.Items.Select(item => item.As<T>())];
}
```

`src/MMLib.Alvo/Data/AlvoTypedReadExtensions.cs`:

```csharp
using MMLib.Alvo.Data.Internal;

using System.Linq.Expressions;

namespace MMLib.Alvo.Data;

/// <summary>
/// Reads typed records through the data port: the entity a type's <see cref="AlvoEntityAttribute"/> names, a predicate
/// lambda translated into an <see cref="AlvoFilter"/>, and the same rules, tenancy and hidden fields as an untyped read.
/// </summary>
/// <remarks>
/// A predicate compares mapped properties with values — captured variables are read on every call and bound as
/// parameters — combined with <c>&amp;&amp;</c>, <c>||</c> and <c>!</c>, tests <c>== null</c> and <c>HasValue</c>, and
/// <c>Contains</c> over a list of values. Anything else throws <see cref="AlvoTranslationException"/> when the read is
/// called. A typed read returns one bounded page; page through more with an untyped <see cref="AlvoQuery"/>.
/// </remarks>
public static class AlvoTypedReadExtensions
{
    /// <summary>Reads one record as a <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>.</typeparam>
    /// <param name="session">Data access as the session's caller.</param>
    /// <param name="id">The record's id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The record, or <see langword="null"/> when it does not exist or the caller may not see it.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> names no entity, or the record lacks a field it maps.</exception>
    public static async Task<T?> GetAsync<T>(this IAlvoSession session, Guid id, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        var record = await session.GetAsync(TypedEntity.NameOf<T>(), id, cancellationToken).ConfigureAwait(false);

        return record?.As<T>();
    }

    /// <summary>Reads at most <paramref name="limit"/> records matching <paramref name="where"/>, as <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>.</typeparam>
    /// <param name="session">Data access as the session's caller.</param>
    /// <param name="where">The predicate, translated into a filter on every call.</param>
    /// <param name="limit">The most records to return, at least 1.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The matching records the caller may see.</returns>
    /// <exception cref="AlvoTranslationException"><paramref name="where"/> holds a construct with no filter equivalent.</exception>
    public static async Task<IReadOnlyList<T>> QueryAsync<T>(this IAlvoSession session, Expression<Func<T, bool>> where, int limit, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        var page = await session.QueryAsync(TypedEntity.Query(where, limit), cancellationToken).ConfigureAwait(false);

        return TypedEntity.Items<T>(page);
    }

    /// <summary>Reads one record as a <typeparamref name="T"/>, as <paramref name="context"/>.</summary>
    /// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>.</typeparam>
    /// <param name="data">The data port.</param>
    /// <param name="id">The record's id.</param>
    /// <param name="context">The caller performing the read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The record, or <see langword="null"/> when it does not exist or the caller may not see it.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> names no entity, or the record lacks a field it maps.</exception>
    public static async Task<T?> GetAsync<T>(this IAlvoData data, Guid id, AlvoContext context, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(data);
        var record = await data.GetAsync(TypedEntity.NameOf<T>(), id, context, cancellationToken).ConfigureAwait(false);

        return record?.As<T>();
    }

    /// <summary>Reads at most <paramref name="limit"/> records matching <paramref name="where"/>, as <typeparamref name="T"/>, as <paramref name="context"/>.</summary>
    /// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>.</typeparam>
    /// <param name="data">The data port.</param>
    /// <param name="where">The predicate, translated into a filter on every call.</param>
    /// <param name="limit">The most records to return, at least 1.</param>
    /// <param name="context">The caller performing the read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The matching records the caller may see.</returns>
    /// <exception cref="AlvoTranslationException"><paramref name="where"/> holds a construct with no filter equivalent.</exception>
    public static async Task<IReadOnlyList<T>> QueryAsync<T>(this IAlvoData data, Expression<Func<T, bool>> where, int limit, AlvoContext context, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(data);
        var page = await data.QueryAsync(TypedEntity.Query(where, limit), context, cancellationToken).ConfigureAwait(false);

        return TypedEntity.Items<T>(page);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*TypedReadTests'`
Expected: PASS (8).

- [ ] **Step 5: Approve the core baseline**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*PublicApiApprovalTests'`
Expected: FAIL; the received diff adds exactly `AlvoTypedReadExtensions` with its four methods. Accept, re-run (PASS), dispatch `alvo-snapshot-judge`.

- [ ] **Step 6: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Data/AlvoTypedReadExtensions.cs src/MMLib.Alvo/Data/Internal/TypedEntity.cs test/MMLib.Alvo.Tests/Data/TypedReadTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Data/AlvoTypedReadExtensions.cs src/MMLib.Alvo/Data/Internal/TypedEntity.cs test/MMLib.Alvo.Tests/Data/TypedReadTests.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(data): typed reads on IAlvoSession and IAlvoData

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 9: An event context for a class handler's unit test

**Files:**
- Modify: `src/MMLib.Alvo/Events/AlvoEventContext.cs`, `src/MMLib.Alvo.TestHost/AlvoTestHost.cs`
- Test: `test/MMLib.Alvo.Tests/Events/AlvoEventContextTests.cs`, `test/MMLib.Alvo.TestHost.Tests/AlvoTestHostEventContextTests.cs`
- Baselines: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`, `test/MMLib.Alvo.TestHost.Tests/PublicApi.MMLib.Alvo.TestHost.verified.txt`

**Interfaces:**
- Consumes: `AlvoEventContext`'s internal constructor `(AlvoEvent, string subscriber, int attempt, IAlvoSession data)`; `IAlvoSessions.For(AlvoContext)`; `AlvoEvent` (`required` `Id`, `Source`, `Type`, `Time` (UTC), `Subject`, `PartitionKey`, `AuthType`, `CorrelationId`, `Data`), `AlvoEvent.DefaultSource`, `AlvoEventAuthType.Unknown`, `AlvoEventData`.
- Produces: `public static AlvoEventContext AlvoEventContext.Create(AlvoEvent @event, IAlvoSession data, string subscriber, int attempt = 1)` (throws on a null argument, a blank subscriber, an attempt below 1); `public AlvoEventContext AlvoTestHost.EventContext(AlvoContext caller, string subscriber = "test-handler", int attempt = 1)` — a synthetic event of type `test.delivery` with a fresh version-7 id at the host's clock, and data access through the host's real pipeline as `caller`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/Events/AlvoEventContextTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

using NSubstitute;

namespace MMLib.Alvo.Tests.Events;

/// <summary>The public factory a class handler's unit test calls (spec 2026-10-14 HX-03 settled details (53)).</summary>
public sealed class AlvoEventContextTests
{
    private static readonly AlvoEvent _event = new()
    {
        Id = Guid.CreateVersion7(),
        Source = AlvoEvent.DefaultSource,
        Type = "test.delivery",
        Time = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero),
        Subject = "test",
        PartitionKey = "test",
        AuthType = AlvoEventAuthType.Unknown,
        CorrelationId = "c-1",
        Data = new AlvoEventData(),
    };

    [Fact]
    public void Create_hands_back_what_it_was_given()
    {
        var session = Substitute.For<IAlvoSession>();

        var context = AlvoEventContext.Create(_event, session, "send-confirmation", attempt: 3);

        (context.Event, context.EventId, context.Data, context.Subscriber, context.Attempt).ShouldBe((_event, _event.Id, session, "send-confirmation", 3));
    }

    [Fact]
    public void An_attempt_below_one_is_refused() =>
        Should.Throw<ArgumentOutOfRangeException>(() => AlvoEventContext.Create(_event, Substitute.For<IAlvoSession>(), "x", attempt: 0));

    [Fact]
    public void A_blank_subscriber_is_refused() =>
        Should.Throw<ArgumentException>(() => AlvoEventContext.Create(_event, Substitute.For<IAlvoSession>(), " "));
}
```

`test/MMLib.Alvo.TestHost.Tests/AlvoTestHostEventContextTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>Calling a class handler yourself (spec 2026-10-14 HX-03 settled details (53)): the context reads and writes through the real pipeline.</summary>
public sealed class AlvoTestHostEventContextTests
{
    [Fact]
    public async Task A_class_handler_called_directly_writes_as_the_caller()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(
            builder => builder.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", "handler-desk.alvo.json")), cancellationToken: ct);
        var agent = AlvoTestHost.Caller("agent");
        var context = alvo.EventContext(agent);

        await new CopyTitle().HandleAsync(new AlvoCreated<AlvoRecord> { New = new AlvoRecord(new Dictionary<string, object?> { ["title"] = "copied" }) }, context, ct);

        (context.Caller, context.Attempt, context.Subscriber, context.Event.Type).ShouldBe((agent, 1, "test-handler", "test.delivery"));
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "tickets" }, agent, ct)).Items.ShouldHaveSingleItem()["title"].ShouldBe("copied");
    }

    private sealed class CopyTitle : IAlvoEventHandler<AlvoCreated<AlvoRecord>>
    {
        public Task HandleAsync(AlvoCreated<AlvoRecord> message, AlvoEventContext context, CancellationToken cancellationToken) =>
            context.Data.CreateAsync("tickets", new Dictionary<string, object?> { ["title"] = message.New["title"] }, cancellationToken);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AlvoEventContextTests'`
Expected: FAIL — compile error (`AlvoEventContext.Create` does not exist).

- [ ] **Step 3: Write the factory and the helper**

In `src/MMLib.Alvo/Events/AlvoEventContext.cs`, after the constructor:

```csharp
    /// <summary>
    /// A context for calling a class handler's <c>HandleAsync</c> yourself — in a unit test, say; deliveries build their own.
    /// </summary>
    /// <param name="event">The event the handler sees.</param>
    /// <param name="data">Data access as the handler's caller, such as <c>IAlvoSessions.For(caller)</c>.</param>
    /// <param name="subscriber">The handler's id.</param>
    /// <param name="attempt">Which attempt this is, from 1.</param>
    /// <returns>The context.</returns>
    public static AlvoEventContext Create(AlvoEvent @event, IAlvoSession data, string subscriber, int attempt = 1)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriber);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        return new(@event, subscriber, attempt, data);
    }
```

In `src/MMLib.Alvo.TestHost/AlvoTestHost.cs` (add `using MMLib.Alvo.Data;` and `using MMLib.Alvo.Events;` if absent), a constant `private const string TestEventType = "test.delivery";` and, after `Caller`:

```csharp
    /// <summary>
    /// An event context for calling a class handler's <c>HandleAsync</c> yourself: data access as <paramref name="caller"/>
    /// through this host's real pipeline, and a synthetic event of type <c>test.delivery</c> with a fresh id.
    /// </summary>
    /// <param name="caller">The caller the handler acts as; <see cref="Caller"/> builds one.</param>
    /// <param name="subscriber">The handler id the context reports.</param>
    /// <param name="attempt">The attempt the context reports, from 1.</param>
    /// <returns>The context to pass to the handler.</returns>
    /// <exception cref="ObjectDisposedException">The host was disposed.</exception>
    public AlvoEventContext EventContext(AlvoContext caller, string subscriber = "test-handler", int attempt = 1)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var services = Services;
        var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();

        return AlvoEventContext.Create(TestEvent(now), services.GetRequiredService<IAlvoSessions>().For(caller), subscriber, attempt);
    }

    private static AlvoEvent TestEvent(DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        Source = AlvoEvent.DefaultSource,
        Type = TestEventType,
        Time = now.ToUniversalTime(),
        Subject = TestEventType,
        PartitionKey = TestEventType,
        AuthType = AlvoEventAuthType.Unknown,
        CorrelationId = Guid.NewGuid().ToString("N"),
        Data = new AlvoEventData(),
    };
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AlvoEventContextTests'` and `dotnet test --project test/MMLib.Alvo.TestHost.Tests --filter-class '*AlvoTestHostEventContextTests'`
Expected: PASS.

- [ ] **Step 5: Approve both baselines**

Run `--filter-class '*PublicApiApprovalTests'` in `test/MMLib.Alvo.Tests` (adds exactly `AlvoEventContext.Create(...)`) and in `test/MMLib.Alvo.TestHost.Tests` (adds exactly `AlvoTestHost.EventContext(...)`). Accept both, re-run (PASS), dispatch `alvo-snapshot-judge`.

- [ ] **Step 6: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Events/AlvoEventContext.cs src/MMLib.Alvo.TestHost/AlvoTestHost.cs test/MMLib.Alvo.Tests/Events/AlvoEventContextTests.cs test/MMLib.Alvo.TestHost.Tests/AlvoTestHostEventContextTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Events/AlvoEventContext.cs src/MMLib.Alvo.TestHost/AlvoTestHost.cs test/MMLib.Alvo.Tests/Events/AlvoEventContextTests.cs test/MMLib.Alvo.TestHost.Tests/AlvoTestHostEventContextTests.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt test/MMLib.Alvo.TestHost.Tests/PublicApi.MMLib.Alvo.TestHost.verified.txt
git commit -m "feat(events): build an event context for a class handler's unit test

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 10: The typed layer on both engines **[slice end]**

**Files:**
- Create: `test/_shared/testhost/descriptors/typed-desk.alvo.json`, `test/_shared/testhost/TypedLayerFacts.cs`, `test/MMLib.Alvo.TestHost.Tests/SqliteTypedLayerTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlTypedLayerTests.cs`
- Modify: `test/MMLib.Alvo.TestHost.Tests/MMLib.Alvo.TestHost.Tests.csproj` (CsCheck, and a link to `../_shared/ef/SqlCapture.cs`)

**Interfaces:**
- Consumes: everything above; `AlvoTestHost.StartAsync`, `.Data`, `.Services`, `.DrainEventsAsync`, `.Hooks.AssertRan`, `AlvoTestHost.Caller`; `SqlCapture(string marker)` (`MMLib.Alvo.Tests.Data`, `.Statements`, `.Clear()`, `IDisposable`); `PostgresFixture.ConnectionString`, `DispatchedEventCollection.Name` (PG leg; both already used by `PostgreSqlEventHandlerTests`).
- Produces: `public abstract class TypedLayerFacts` (namespace `MMLib.Alvo.TestHost.Tests`) with `protected abstract Task<TypedDatabase> DatabaseAsync()` and `protected sealed record TypedDatabase(Action<IAlvoBuilder> Use, string Marker)` — `Marker` is the substring `SqlCapture` matches in the connection string (the SQLite file name, the PostgreSQL database name). `_shared/testhost/*.cs` and `descriptors/*.alvo.json` are already compiled and copied by both projects.

- [ ] **Step 1: Write the descriptor and the shared facts**

In `test/MMLib.Alvo.TestHost.Tests/MMLib.Alvo.TestHost.Tests.csproj`, add a new `ItemGroup` holding `<PackageReference Include="CsCheck" />` (the PostgreSQL integration project already references it), and beside the `_shared/testhost` link:

```xml
    <Compile Include="$(MSBuildThisFileDirectory)../_shared/ef/SqlCapture.cs" Link="_shared/SqlCapture.cs" />
```

`test/_shared/testhost/descriptors/typed-desk.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "typed-desk",
  "tenancy": { "enabled": true },
  "auth": { "roles": ["agent"] },
  "entities": {
    "orders": {
      "tenancy": "scoped",
      "events": { "exclude": ["internal_note"] },
      "fields": {
        "reference": { "type": "string", "required": true, "maxLength": 60 },
        "status": { "type": "enum", "required": true, "values": ["draft", "in_review", "ok"] },
        "total": { "type": "decimal", "precision": 12, "scale": 2 },
        "quantity": { "type": "integer" },
        "is_paid": { "type": "boolean" },
        "due_at": { "type": "datetime" },
        "ship_on": { "type": "date" },
        "owner_id": { "type": "uuid" },
        "internal_note": { "type": "string", "maxLength": 200 }
      },
      "rules": {
        "list": "'agent' in @user.roles || 'admin' in @user.roles",
        "get": "'agent' in @user.roles || 'admin' in @user.roles",
        "create": "'agent' in @user.roles",
        "update": "'agent' in @user.roles"
      }
    }
  }
}
```

`test/_shared/testhost/TypedLayerFacts.cs`:

```csharp
using CsCheck;

using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Data;

using System.Collections.Concurrent;

namespace MMLib.Alvo.TestHost.Tests;

[AlvoEntity("orders")]
public sealed class DeskOrder
{
    public Guid Id { get; init; }

    public required string Reference { get; init; }

    public DeskStatus Status { get; init; }

    public decimal? Total { get; init; }

    public long? Quantity { get; init; }

    public bool? IsPaid { get; init; }

    public DateTimeOffset? DueAt { get; init; }

    public DateOnly? ShipOn { get; init; }

    public Guid? OwnerId { get; init; }
}

public enum DeskStatus
{
    Draft,
    InReview,
    [AlvoEnumValue("ok")]
    Approved,
}

[AlvoEntity("orders")]
public sealed class DeskOrderWithVat
{
    public string? VatId { get; init; }
}

[AlvoEntity("orders")]
public sealed class DeskOrderMistyped
{
    public string? Quantity { get; init; }
}

[AlvoEntity("orders")]
public sealed class DeskOrderWithNote
{
    public string? InternalNote { get; init; }
}

[AlvoEvent("desk.shipped")]
public sealed record DeskShipped(string Reference, long Boxes);

public sealed class ShipmentLog
{
    public ConcurrentQueue<DeskShipped> Shipped { get; } = new();
}

public sealed class ShipmentRecorder(ShipmentLog log) : IAlvoEventHandler<DeskShipped>
{
    public Task HandleAsync(DeskShipped message, AlvoEventContext context, CancellationToken cancellationToken)
    {
        log.Shipped.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The typed layer over the real pipeline (spec 2026-10-14 §7 HX-05), on whichever engine the deriving class supplies —
/// the SQLite leg in <c>MMLib.Alvo.TestHost.Tests</c>, the PostgreSQL leg in <c>MMLib.Alvo.Data.PostgreSql.Tests.Integration</c>.
/// </summary>
public abstract class TypedLayerFacts
{
    private const string TypedDesk = "typed-desk.alvo.json";

    private static readonly Gen<string> _hostile = Gen.Char["abcXYZ01_ '\"%;-()\\é中"].Array[1, 24].Select(characters => new string(characters));

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_typed_handler_is_handed_typed_images_and_its_translated_condition_selects()
    {
        var seen = new ConcurrentQueue<AlvoUpdated<DeskOrder>>();
        await using var alvo = await StartAsync(await DatabaseAsync(), builder => builder.Entity<DeskOrder>(orders => orders.OnUpdated(
            "on-approved",
            (AlvoUpdated<DeskOrder> approved) =>
            {
                seen.Enqueue(approved);
                return Task.CompletedTask;
            },
            when: e => e.Changed(x => x.Status) && e.New.Status == DeskStatus.Approved)));
        var agent = Agent(Guid.NewGuid());
        var id = await CreateAsync(alvo, agent, "R-1", "draft");
        await UpdateAsync(alvo, agent, id, "in_review");
        await UpdateAsync(alvo, agent, id, "ok");

        await alvo.DrainEventsAsync(Ct);

        var approved = seen.ShouldHaveSingleItem();
        (approved.Old.Status, approved.New.Status, approved.New.Reference, approved.New.Total).ShouldBe((DeskStatus.InReview, DeskStatus.Approved, "R-1", 12.5m));
        approved.New.ShipOn.ShouldBe(new DateOnly(2026, 11, 3));
        alvo.Hooks.AssertRan("on-approved", times: 1);
    }

    [Fact]
    public async Task A_typed_application_event_handler_is_handed_its_typed_payload()
    {
        var log = new ShipmentLog();
        await using var alvo = await StartAsync(await DatabaseAsync(), builder =>
        {
            builder.AddEventHandler<DeskShipped, ShipmentRecorder>("record-shipment");
            builder.Services.AddSingleton(log);
        });

        await alvo.Services.GetRequiredService<IAlvoEvents>().PublishAsync(
            "desk.shipped", "desk/1", new Dictionary<string, object?> { ["reference"] = "R-9", ["boxes"] = 3L }, Agent(Guid.NewGuid()), Ct);
        await alvo.DrainEventsAsync(Ct);

        log.Shipped.ShouldHaveSingleItem().ShouldBe(new DeskShipped("R-9", 3));
    }

    [Fact]
    public async Task Typed_records_that_do_not_fit_refuse_boot_all_at_once()
    {
        var database = await DatabaseAsync();

        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => StartAsync(database, builder => builder
            .Entity<DeskOrderWithVat>(o => o.OnCreated("typed-missing", (AlvoCreated<DeskOrderWithVat> _) => Task.CompletedTask))
            .Entity<DeskOrderMistyped>(o => o.OnCreated("typed-mistyped", (AlvoCreated<DeskOrderMistyped> _) => Task.CompletedTask))
            .Entity<DeskOrderWithNote>(o => o.OnCreated("typed-excluded", (AlvoCreated<DeskOrderWithNote> _) => Task.CompletedTask))));

        refusal.Message.ShouldContain("'typed-missing'");
        refusal.Message.ShouldContain("'vat_id'");
        refusal.Message.ShouldContain("'typed-mistyped'");
        refusal.Message.ShouldContain("'typed-excluded'");
        refusal.Message.ShouldContain("events.exclude");
    }

    [Fact]
    public async Task A_typed_read_returns_typed_records_through_the_caller_s_rules()
    {
        await using var alvo = await StartAsync(await DatabaseAsync(), _ => { });
        var agent = Agent(Guid.NewGuid());
        var approved = await CreateAsync(alvo, agent, "R-1", "ok");
        await CreateAsync(alvo, agent, "R-2", "draft");

        (await alvo.Data.GetAsync<DeskOrder>(approved, agent, Ct)).ShouldNotBeNull().Reference.ShouldBe("R-1");
        (await alvo.Data.QueryAsync<DeskOrder>(x => x.Status == DeskStatus.Approved && x.Total > 10m, 10, agent, Ct))
            .ShouldHaveSingleItem().Id.ShouldBe(approved);
    }

    [Fact]
    public async Task A_typed_read_never_crosses_tenants()
    {
        await using var alvo = await StartAsync(await DatabaseAsync(), _ => { });
        var tenantA = Agent(Guid.NewGuid());
        var tenantB = Agent(Guid.NewGuid());
        var id = await CreateAsync(alvo, tenantA, "R-1", "ok");

        (await alvo.Data.QueryAsync<DeskOrder>(x => x.Reference == "R-1", 10, tenantB, Ct)).ShouldBeEmpty();
        (await alvo.Data.GetAsync<DeskOrder>(id, tenantB, Ct)).ShouldBeNull();
        (await alvo.Data.QueryAsync<DeskOrder>(x => x.Reference == "R-1", 10, tenantA, Ct)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_hostile_captured_string_is_matched_as_a_value()
    {
        await using var alvo = await StartAsync(await DatabaseAsync(), _ => { });
        var agent = Agent(Guid.NewGuid());
        var payload = "x'; DROP TABLE orders; --";
        await CreateAsync(alvo, agent, payload, "draft");

        (await alvo.Data.QueryAsync<DeskOrder>(x => x.Reference == payload, 10, agent, Ct)).ShouldHaveSingleItem().Reference.ShouldBe(payload);
    }

    [Fact]
    public async Task No_captured_string_reaches_the_sql_text()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, _ => { });
        var agent = Agent(Guid.NewGuid());
        using var capture = new SqlCapture(database.Marker);
        var baseline = await StatementsAsync(alvo, agent, capture, "benign");
        var sampled = 0;

        await _hostile.SampleAsync(
            async captured =>
            {
                (await StatementsAsync(alvo, agent, capture, captured)).ShouldBe(baseline, $"captured {captured}");
                Interlocked.Increment(ref sampled);
            },
            iter: 200,
            threads: 1);

        baseline.ShouldNotBeEmpty();
        sampled.ShouldBe(200);
    }

    protected abstract Task<TypedDatabase> DatabaseAsync();

    private static async Task<IReadOnlyList<string>> StatementsAsync(AlvoTestHost alvo, AlvoContext agent, SqlCapture capture, string captured)
    {
        capture.Clear();
        await alvo.Data.QueryAsync<DeskOrder>(
            x => x.Reference == captured || (x.Reference != captured && new[] { captured, "fixed" }.Contains(x.Reference)), 10, agent, Ct);

        return capture.Statements;
    }

    private static async Task<AlvoTestHost> StartAsync(TypedDatabase database, Action<IAlvoBuilder> configure) =>
        await AlvoTestHost.StartAsync(
            alvo =>
            {
                database.Use(alvo);
                alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", TypedDesk));
                configure(alvo);
            },
            cancellationToken: Ct);

    private static AlvoContext Agent(Guid tenant) => AlvoTestHost.Caller("agent") with { Tenant = new TenantId(tenant) };

    private static async Task<Guid> CreateAsync(AlvoTestHost alvo, AlvoContext agent, string reference, string status)
    {
        var created = await alvo.Data.CreateAsync("orders", new Dictionary<string, object?>
        {
            ["reference"] = reference,
            ["status"] = status,
            ["total"] = 12.5m,
            ["quantity"] = 3L,
            ["ship_on"] = "2026-11-03",
            ["tenant_id"] = agent.Tenant!.Value.Value,
        }, agent, cancellationToken: Ct);

        return (Guid)created[AlvoManagedColumns.Id]!;
    }

    private static Task<AlvoRecord> UpdateAsync(AlvoTestHost alvo, AlvoContext agent, Guid id, string status) =>
        alvo.Data.UpdateAsync("orders", id, new Dictionary<string, object?> { ["status"] = status }, agent, cancellationToken: Ct);

    protected sealed record TypedDatabase(Action<IAlvoBuilder> Use, string Marker);
}
```

- [ ] **Step 2: Write the two legs**

`test/MMLib.Alvo.TestHost.Tests/SqliteTypedLayerTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>The typed-layer facts on SQLite — a temp file per fact, its name the marker the SQL capture matches.</summary>
public sealed class SqliteTypedLayerTests : TypedLayerFacts, IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in _files)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }

        GC.SuppressFinalize(this);
    }

    protected override Task<TypedDatabase> DatabaseAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alvo-typed-{Guid.NewGuid():N}.db");
        _files.Add(path);
        var connectionString = $"Data Source={path}";

        return Task.FromResult(new TypedDatabase(alvo => alvo.UseSqlite(connectionString), Path.GetFileName(path)));
    }
}
```

`test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlTypedLayerTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.TestHost.Tests;
using MMLib.Alvo.Tests.Events;

using Npgsql;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>The typed-layer facts with <c>UsePostgreSql</c> — a fresh database per fact on one container, its name the capture's marker.</summary>
[Collection(DispatchedEventCollection.Name)]
public sealed class PostgreSqlTypedLayerTests(PostgresFixture postgres)
    : TypedLayerFacts, IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly List<string> _databases = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var name in _databases)
        {
            await ExecuteAsync($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }
    }

    protected override async Task<TypedDatabase> DatabaseAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(postgres.ConnectionString), "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");

        var name = $"alvo_typed_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE DATABASE \"{name}\"");
        _databases.Add(name);
        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name }.ToString();

        return new TypedDatabase(alvo => alvo.UsePostgreSql(connectionString), name);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = admin.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
```

- [ ] **Step 3: Run the SQLite leg**

Run: `dotnet test --project test/MMLib.Alvo.TestHost.Tests --filter-class '*SqliteTypedLayerTests'`
Expected: PASS (7). Failure modes that are not the typed layer: a refused create naming `tenant_id` means the scoped write wants it differently — compare with `EventHandlerFacts.CreateNoteAsync` and keep the write honest; `baseline.ShouldNotBeEmpty()` failing means the marker does not occur in the connection string — read `SqlCapture`'s filter and pass what it matches. A statement list that differs from the baseline is a defect to diagnose, never a property to relax: the guarantee is byte-identical text.

- [ ] **Step 4: Run the PostgreSQL leg**

Run: `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlTypedLayerTests'` (Docker required; skips otherwise)
Expected: PASS (7).

- [ ] **Step 5: Format, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include test/_shared/testhost/TypedLayerFacts.cs test/MMLib.Alvo.TestHost.Tests/SqliteTypedLayerTests.cs test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlTypedLayerTests.cs
scripts/test-ring1
git add test/_shared/testhost/TypedLayerFacts.cs test/_shared/testhost/descriptors/typed-desk.alvo.json test/MMLib.Alvo.TestHost.Tests/SqliteTypedLayerTests.cs test/MMLib.Alvo.TestHost.Tests/MMLib.Alvo.TestHost.Tests.csproj test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlTypedLayerTests.cs
git commit -m "test(typed): the typed layer on both engines, and no captured string in SQL

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 11: The dashboard shows a typed handler's translated condition

**Files:**
- Modify: `test/MMLib.Alvo.Admin.Tests.EndToEnd/ExtensionWorld.cs`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/ExtensionScenarios.cs`

**Interfaces:**
- Consumes: `Entity<T>` (Task 6); HX-04's lane test ids `lifecycle-after-commit`, `lane-extension-<id>`, `lane-extension-when`, `entity-extensions-<entity>`.
- Produces: `ExtensionWorld.Completed` (`"notify-completed"`), a typed handler on `work_orders`; the `work_orders` badge moves from 2 to 3.

- [ ] **Step 1: Register a typed handler in the world and pin it**

In `ExtensionWorld.cs`: add `internal const string Completed = "notify-completed";`, chain onto `Configure` before the `"*"` registration:

```csharp
        .Entity<WorkOrder>(orders => orders.OnUpdated(
            Completed,
            (AlvoUpdated<WorkOrder> _) => Task.CompletedTask,
            when: e => e.Changed(x => x.Status) && e.New.Status == WorkOrderStatus.Completed))
```

and at the end of the file:

```csharp
/// <summary>The part of a field-service work order the typed handler maps.</summary>
[AlvoEntity("work_orders")]
public sealed class WorkOrder
{
    public required string Reference { get; init; }

    public WorkOrderStatus Status { get; init; }
}

/// <summary>A work order's life cycle, as <c>examples/field-service</c> declares it.</summary>
public enum WorkOrderStatus
{
    Scheduled,
    InProgress,
    Completed,
    Cancelled,
}
```

Update the class summary to "…three event handlers registered as a host developer would: one on `work_orders` reading `scheduled_for`, a typed one on `work_orders` whose condition is a lambda, and one on every entity."

In `ExtensionScenarios.cs`, `The_entity_list_counts_the_extensions_that_run_on_each_entity`: expect `"⟨/⟩ 3"` and the aria label `"3 extensions from code run on this entity"` for `work_orders` (`regions` stays `⟨/⟩ 1`). Add to `ExtensionCatalogScenarios`:

```csharp
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_lifecycle_tab_shows_a_typed_handler_s_condition_as_cel()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders?tab=lifecycle");

        var item = session.Page.GetByTestId("lifecycle-after-commit").GetByTestId($"lane-extension-{ExtensionWorld.Completed}");
        await item.WaitForAsync();

        (await item.GetByTestId("lane-extension-when").InnerTextAsync()).Trim()
            .ShouldBe("when changed(status) && new.status == 'completed'");
        session.AssertConsoleClean();
    }
```

- [ ] **Step 2: Run the dashboard suite**

Run: `scripts/test-admin-e2e`
Expected: PASS, including HX-04's refusal scenario (the typed handler maps only `reference` and `status`, so removing `scheduled_for` still names `notify-reschedule` alone). A boot refusal naming `notify-completed` means `WorkOrder` does not fit the field-service descriptor — both mapped fields are `required` there, so non-nullable properties are right; read the refusal's sentence before touching the type.

- [ ] **Step 3: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include test/MMLib.Alvo.Admin.Tests.EndToEnd/ExtensionWorld.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/ExtensionScenarios.cs
scripts/test-ring0
git add test/MMLib.Alvo.Admin.Tests.EndToEnd/ExtensionWorld.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/ExtensionScenarios.cs
git commit -m "test(admin): the lifecycle lane shows a typed handler's translated condition

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 12: Docs — *Typed or CEL*, architecture docs, CHANGELOG, PLAN, spec amendment **[slice end]**

**Files:**
- Create: `website/src/content/docs/extending/typed-or-cel.mdx`; `website/src/snippets/extending/typed/BookingDeskHandlers.cs`, `BookingDeskHandlersTests.cs`, `booking-desk.alvo.json`
- Modify: `website/astro.config.mjs`, `website/src/content/docs/extending/index.mdx`, `extending/event-handlers.mdx`, `extending/extension-catalog.mdx` (sidebar order 5), `extending/testing.mdx` (sidebar order 6)
- Modify: `docs/architecture/extensibility.md`, `docs/architecture/cel.md`, `CHANGELOG.md`, `docs/PLAN.md`, `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`

**Interfaces:**
- Consumes: everything above.
- Produces: the page, run by `BookingDeskHandlersTests` in `MMLib.Alvo.DocsSnippets.Tests` (ring0).

- [ ] **Step 1: Write the snippets and their facts**

`website/src/snippets/extending/typed/booking-desk.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "booking-desk",
  "access": { "admin": "'admin' in @user.roles" },
  "auth": { "roles": ["staff"] },
  "entities": {
    "bookings": {
      "fields": {
        "guest": { "type": "string", "required": true, "maxLength": 80 },
        "status": { "type": "enum", "required": true, "values": ["requested", "confirmed", "cancelled"] },
        "nights": { "type": "integer", "required": true },
        "total": { "type": "decimal", "precision": 10, "scale": 2 }
      },
      "rules": {
        "list": "'staff' in @user.roles || 'admin' in @user.roles",
        "get": "'staff' in @user.roles || 'admin' in @user.roles",
        "create": "'staff' in @user.roles",
        "update": "'staff' in @user.roles"
      }
    }
  }
}
```

(`'admin'` on the reads matters: a handler runs as the system, which holds `admin`.)

`website/src/snippets/extending/typed/BookingDeskHandlers.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace BookingDesk;

[AlvoEntity("bookings")]
public sealed class Booking
{
    public Guid Id { get; init; }

    public required string Guest { get; init; }

    public BookingStatus Status { get; init; }

    public long Nights { get; init; }

    public decimal? Total { get; init; }
}

public enum BookingStatus
{
    Requested,
    Confirmed,
    Cancelled,
}

public static class BookingDeskHandlers
{
    public static IAlvoBuilder AddBookingDesk(this IAlvoBuilder alvo) => alvo
        .Entity<Booking>(bookings => bookings
            .OnUpdated<SendConfirmation>(
                "send-confirmation",
                when: e => e.Changed(x => x.Status) && e.New.Status == BookingStatus.Confirmed)
            .OnCreated(
                "long-stay",
                (AlvoCreated<Booking> created, IGuestMail mail, CancellationToken ct) => mail.SendAsync(created.New.Guest, "Enjoy your long stay", ct),
                when: "new.nights >= 7"));
}

public interface IGuestMail
{
    Task SendAsync(string guest, string text, CancellationToken cancellationToken);
}

public sealed class SendConfirmation(IGuestMail mail) : IAlvoEventHandler<AlvoUpdated<Booking>>
{
    public async Task HandleAsync(AlvoUpdated<Booking> confirmed, AlvoEventContext context, CancellationToken cancellationToken)
    {
        var guest = confirmed.New.Guest;
        var stays = await context.Data.QueryAsync<Booking>(x => x.Guest == guest && x.Status == BookingStatus.Confirmed, limit: 50, cancellationToken);
        await mail.SendAsync(guest, $"Booking confirmed: {stays.Count} confirmed stay(s) with us", cancellationToken);
    }
}
```

`website/src/snippets/extending/typed/BookingDeskHandlersTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Management;
using MMLib.Alvo.TestHost;
using Shouldly;
using Xunit;

namespace BookingDesk.Tests;

public sealed class BookingDeskHandlersTests
{
    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "booking-desk.alvo.json");

    [Fact]
    public async Task Confirming_a_booking_sends_one_confirmation_and_the_catalog_shows_its_condition_as_cel()
    {
        var ct = TestContext.Current.CancellationToken;
        var mail = new RecordingMail();
        await using var alvo = await StartAsync(mail, ct);
        var staff = AlvoTestHost.Caller("staff");

        var booking = await alvo.Data.CreateAsync(
            "bookings", new Dictionary<string, object?> { ["guest"] = "Ada", ["status"] = "requested", ["nights"] = 2L }, staff, cancellationToken: ct);
        await alvo.Data.UpdateAsync("bookings", (Guid)booking["id"]!, new Dictionary<string, object?> { ["status"] = "confirmed" }, staff, cancellationToken: ct);
        await alvo.DrainEventsAsync(ct);

        mail.Sent.ShouldBe(["Ada: Booking confirmed: 1 confirmed stay(s) with us"]);
        alvo.Hooks.AssertRan("long-stay", times: 0);
        var catalog = await AsAdministrator(alvo).GetExtensionsAsync("booking-desk", ct);
        catalog.Items.Single(item => item.Id == "send-confirmation").When.ShouldBe("changed(status) && new.status == 'confirmed'");
    }

    [Fact]
    public async Task A_class_handler_can_be_called_directly()
    {
        var ct = TestContext.Current.CancellationToken;
        var mail = new RecordingMail();
        await using var alvo = await StartAsync(mail, ct);
        var staff = AlvoTestHost.Caller("staff");
        var created = await alvo.Data.CreateAsync(
            "bookings", new Dictionary<string, object?> { ["guest"] = "Grace", ["status"] = "confirmed", ["nights"] = 1L }, staff, cancellationToken: ct);
        var stored = (await alvo.Data.GetAsync<Booking>((Guid)created["id"]!, staff, ct))!;

        await new SendConfirmation(mail).HandleAsync(new AlvoUpdated<Booking> { Old = stored, New = stored }, alvo.EventContext(staff), ct);

        mail.Sent.ShouldBe(["Grace: Booking confirmed: 1 confirmed stay(s) with us"]);
    }

    [Fact]
    public void A_condition_with_no_cel_equivalent_is_refused_when_it_is_registered()
    {
        var refusal = Should.Throw<AlvoTranslationException>(() => new ServiceCollection().AddAlvo(alvo => alvo.Entity<Booking>(bookings =>
            bookings.OnUpdated<SendConfirmation>("bad", when: e => e.New.Guest.GetHashCode() == 7))));

        refusal.Message.ShouldStartWith("Cannot translate 'e.New.Guest.GetHashCode()' in extension 'bad' (when)");
    }

    private static Task<AlvoTestHost> StartAsync(RecordingMail mail, CancellationToken ct) =>
        AlvoTestHost.StartAsync(builder =>
        {
            builder.FromDescriptor(Descriptor).AddBookingDesk();
            builder.Services.AddSingleton<IGuestMail>(mail);
        }, cancellationToken: ct);

    private static IAlvoManagement AsAdministrator(AlvoTestHost alvo)
    {
        alvo.Services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = AlvoTestHost.Caller("admin"),
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "docs",
        };
        return alvo.Services.GetRequiredService<IAlvoManagement>();
    }

    private sealed class RecordingMail : IGuestMail
    {
        private readonly List<string> _sent = [];

        public IReadOnlyList<string> Sent => _sent;

        public Task SendAsync(string guest, string text, CancellationToken cancellationToken)
        {
            _sent.Add($"{guest}: {text}");
            return Task.CompletedTask;
        }
    }
}
```

Run: `dotnet test --project test/MMLib.Alvo.DocsSnippets.Tests`
Expected: PASS (`SnippetCoverageTests` finds `BookingDeskHandlers` and `BookingDeskHandlersTests`). A `ManagementForbiddenException` means the `access` block did not admit the principal — keep the descriptor honest (it mirrors HX-04's `fleet-desk`), not the test.

- [ ] **Step 2: Write the page**

`website/src/content/docs/extending/typed-or-cel.mdx` — frontmatter `title: Typed or CEL`, `description: Write a handler's condition as a C# lambda over your own class, or as CEL — Alvo translates the lambda once, checks your class against the descriptor at start-up, and reads typed records for you.`, `sidebar:` / `order: 4`. Imports as `event-handlers.mdx` does (`Code`, `SourceExcerpt`, the three snippets as `descriptor`, `handlers`, `tests`). Excerpt markers carry no quotes (HX-04's `extension-catalog.mdx` precedent). Sections, each a few sentences:

1. *Two ways to write one handler* — `Entity("bookings", …)` with `AlvoRecord` and a CEL string, or `Entity<Booking>(…)` with your class and a lambda; mix them (a typed entity takes a CEL string too). `<Code code={descriptor} lang="json" title="booking-desk.alvo.json" />` and `<SourceExcerpt code={handlers} from="public static IAlvoBuilder AddBookingDesk" to="new.nights >= 7" title="BookingDeskHandlers.cs" />`. Every-entity (`"*"`), `Entities([...])` and application-event conditions stay CEL.
2. *Your class* — `[AlvoEntity("bookings")]`; a property maps to its snake_case field unless `[AlvoField("…")]` names it; an enum member to its snake_case value unless `[AlvoEnumValue("…")]`; mapped = public with a setter (or `[AlvoField]`); a table field type → C# type (`string`/`text`/`json` → `string`; `integer` → `long` or `int`; `decimal` → `decimal`; `boolean` → `bool`; `uuid`/`ref` → `Guid`; `date` → `DateOnly`; `datetime` → `DateTimeOffset`; `enum` → `string` or an enum); an optional field needs a nullable property. At start-up a property with no field, the wrong type, a non-nullable property on an optional field, an enum member with no declared value, or a field `events.exclude` leaves out refuses the start, all problems at once — and an apply that would remove, exclude or retype a mapped field is refused like one that breaks a condition.
3. *What a lambda can say* — the translation table (C# → CEL) with Rulings 2–6 in words: `== null` becomes `!has(…)`, a constant list becomes `||` of equalities, a `Guid` constant compares through `string(…)`, an instant through `timestamp('…')`, `StartsWith`/`EndsWith`/`Contains` compare ordinally; only constants, `const`, `static readonly` fields and enum members — a captured variable is refused, because the condition is translated once. The catalog and the dashboard's **Lifecycle** tab show the CEL. A refusal: the sample message ``Cannot translate 'e.New.Guest.GetHashCode()' in extension 'bad' (when): method 'GetHashCode' has no CEL equivalent. Fix: write the condition as a CEL string — when: "…" — or move the check into the handler body.`` and `<SourceExcerpt code={tests} from="var refusal" to="in extension 'bad' (when)" title="BookingDeskHandlersTests.cs" />`. Conditions have no clock yet.
4. *Typed reads* — `context.Data.QueryAsync<Booking>(x => …, limit: 50)` and `GetAsync<Booking>(id)` (and on `IAlvoData` with a caller): the same rules, tenancy and hidden fields as an untyped read; captured values read on every call and bound as parameters, never written into SQL; one bounded page; `StartsWith`/`EndsWith`/`Contains` on a text, arithmetic and field-to-field comparisons are refused when the read is called. `record.As<Booking>()` for any record; it names the field a record lacks (a hidden field, or one left out of the event images).
5. *Testing a typed handler* — `<SourceExcerpt code={tests} from="public async Task Confirming_a_booking" to="new.status == 'confirmed'" title="BookingDeskHandlersTests.cs" />`, and calling a class handler yourself with `alvo.EventContext(staff)`: `<SourceExcerpt code={tests} from="public async Task A_class_handler_can_be_called_directly" to="mail.Sent.ShouldBe([" title="BookingDeskHandlersTests.cs" />`.
6. *Generated classes* — a class implementing `IAlvoEntity` wraps the record instead of copying it (unknown fields survive; `x.Record["field"]` reaches any field); generated classes arrive in a later release.

`website/astro.config.mjs`: `page('Typed or CEL', 'extending/typed-or-cel'),` after the *React to changes* entry. `extension-catalog.mdx` frontmatter `order: 5`; `testing.mdx` `order: 6`. `extending/index.mdx`: a bullet `- [Typed or CEL](/extending/typed-or-cel/) — handlers over your own classes, conditions as lambdas, typed reads.` after the event-handlers bullet; in *What lands next* drop "Typed handlers, ". `event-handlers.mdx`: replace "`AlvoEventContext` has no public constructor yet, so test a class handler through the test host, as above, rather than by calling `HandleAsync` yourself." with "To call a class handler's `HandleAsync` yourself, pass it `alvo.EventContext(caller)` — data access through the test host's real pipeline as that caller ([example](/extending/typed-or-cel/#testing-a-typed-handler))."; *Not yet* becomes "Redeliver, replay and pause arrive in the next releases."

- [ ] **Step 3: Architecture docs, CHANGELOG, PLAN**

`docs/architecture/extensibility.md`, after *Registering an event handler*, a section **Registering typed code**: `Entity<T>` adds the same `HandlerRegistration`, its `When` the CEL text `ConditionTranslator` wrote at registration, plus a `TypedHandler` (the type's `RecordShape` and its message factory); the boot adds only the mapping checks at `extensions/<id>/record` (`RecordShape.Problems` catalog-free, `ShapeMapping` against the entity); the apply guard counts mapped fields as reads; typed reads are extension methods that build an `AlvoQuery` through `FilterTranslator` — the port is unchanged, and every policy, tenancy and hidden-field rule applies as to an untyped read. Point at *Typed or CEL* on the docs site.

`docs/architecture/cel.md`, a section **One writer** before *Deliberate deviations from CEL*: `CelText` is the only code that writes CEL text — canonical spelling (Ruling 1) and the one string-literal escape, the inverse of `CelLexer`'s six escapes — proven by `CelTextTests.Every_string_reads_back_as_itself` over the whole code space; Alvo's CEL has no list literal and refuses `== null`, so the translator writes `||` chains and `has()` instead.

`CHANGELOG.md`, `## [Unreleased]` → `### Added`, as the first entry: **Typed layer and lambda translation** (HX-05, #382). `Entity<T>` / `AlvoEntityBuilder<T>` register handlers over a host class with conditions as lambdas over `EventCondition<T>` (`New`, `Old`, `Changed`, `Caller` — `AlvoConditionCaller`), translated once into the CEL the catalog shows; `AddEventHandler<TMessage, THandler>` with `[AlvoEvent]`; `[AlvoEntity]`, `[AlvoField]`, `[AlvoEnumValue]`, `IAlvoEntity`; `AlvoRecord.As<T>()`; typed reads `GetAsync<T>`/`QueryAsync<T>` on `IAlvoSession` and `IAlvoData`, translated into `AlvoFilter`; `AlvoTranslationException`; a typed record that does not fit the descriptor refuses start-up and the apply that would break it; `AlvoEventContext.Create` and `AlvoTestHost.EventContext` for calling a class handler in a unit test. Docs: *Typed or CEL*. No breaking change.

`docs/PLAN.md`, *Host extensibility* line: "…; HX-04 (extension catalog, apply guard, dashboard and assistant) the fourth; HX-05 (typed layer and lambda translation) the fifth."

- [ ] **Step 4: Spec amendment**

In `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`, after the refusal-message paragraph of §4.3 (before `### 4.4`), add **HX-05 settled details.** — "Rulings and deviations taken while planning (1–19, verbatim from `docs/superpowers/plans/2026-10-24-hx-05-typed-layer.md`), so a later reader can tell a decision from an oversight. Where a ruling and §4.3's table disagree, the ruling is what shipped." — followed by this plan's *Rulings and deviations* (1)–(19), numbered, verbatim. In §5, append to the `05` row that lists `Entity<T>(…)`: "`AlvoConditionCaller`, `AlvoEventContext.Create` and `AlvoTestHost.EventContext` added (§4.3 *HX-05 settled details* (5), (12))."

- [ ] **Step 5: Build the site, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include website/src/snippets/extending/typed/BookingDeskHandlers.cs website/src/snippets/extending/typed/BookingDeskHandlersTests.cs
scripts/test-ring1
(cd website && npm ci && npm run build)
git add website/src/content/docs/extending/typed-or-cel.mdx website/src/snippets/extending/typed website/astro.config.mjs website/src/content/docs/extending/index.mdx website/src/content/docs/extending/event-handlers.mdx website/src/content/docs/extending/extension-catalog.mdx website/src/content/docs/extending/testing.mdx docs/architecture/extensibility.md docs/architecture/cel.md CHANGELOG.md docs/PLAN.md docs/superpowers/specs/2026-10-14-host-extensibility-design.md
git commit -m "docs: typed or CEL

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

Expected: the site builds with link validation green (the new public types appear in the generated C# reference).

---

## Task 13: Rings, reviews, plan-guard, PR report, PR

- [ ] **Step 1: CI-parity Release build, rings and the out-of-ring suites**

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 sh -c 'dotnet build MMLib.Alvo.slnx -c Release -p:HUSKY=0 2>&1 | grep -E "error [A-Z]+[0-9]+" | sort -u'
git clean -fdx -e .superpowers
```

Expected: no output (any `error CAxxxx`/`IDExxxx`/`RCSxxxx` is fixed in a `fix(…): …` commit and this step re-run — CA1822 on the translation-only members takes the suppression Task 4 Step 3 names). Then `scripts/test-ring0`, `scripts/test-ring1`, `scripts/test-ring2` (both engines; includes `PostgreSqlTypedLayerTests`), `scripts/test-admin-e2e`, `(cd website && npm run build)`. Expected: all green. The OpenAPI document, the Vacuum ruleset and the TeaPie path set do not move (no route added). The assistant eval is not run: no skill, tool or instruction changed.

- [ ] **Step 2: Reviews (substitutes) and the security-core checklist**

Freeze the tree. `/code-review` and `/security-review` are user-only: dispatch two reviewer subagents over `git diff hx/04-extension-catalog...HEAD`, labelled "substitute for /code-review high" and "substitute for /security-review". Point the code reviewer at: `LambdaShape.IsTransparent` (no narrowing conversion stripped), the enum and span-`Contains` shapes, `RecordValue`'s conversions, `HandlerBinding`'s new problems never letting a misfit bind, `ExtensionApplyRefusals.Mapped` never duplicating a condition's pointer. Point the security reviewer at: no captured or constant value ever reaching SQL text (`FilterTranslator` places values only in `AlvoComparison.Value`; field names come only from `RecordShape` — host-authored attribute strings — and are still checked by `FilterSqlRenderer` against the schema), typed reads going only through `IAlvoSession`/`IAlvoData` (policy in the `WHERE`, tenancy inside the port — `A_typed_read_never_crosses_tenants`), `As<T>()` and refusal messages never echoing a field's value, `CelText`'s escaping (`parse(render(s)) == s`), and `AlvoEventContext.Create` granting nothing (it carries the session it is given). Then run the `alvo-security-core-review` checklist and record each item's evidence for the PR report. Fix findings, re-run ring0, one `fix(…): …` commit per finding.

- [ ] **Step 3: `alvo-plan-guard`**

Dispatch `alvo-plan-guard` last (read-only); act on drift it reports. The PLAN marker stays where it is.

- [ ] **Step 4: PR report**

Use the `alvo-pr-report` skill (it dispatches `alvo-pr-reporter` and publishes the 8-section page). Evidence: acceptance (1)–(7) → tests (table below), the four public-API diffs against the public-surface list, the both-engine run of `TypedLayerFacts` (acceptance (2) as 200 samples per engine, byte-identical statements), the admin e2e run, the Release build, the security-core checklist and both substitute reviews, and the rulings below — calling out Rulings 2–4 and 7 (translation shapes that differ from §4.3's table) and 5 and 12 (public members §5 does not list).

- [ ] **Step 5: Push and open the PR**

```bash
git branch --show-current   # hx/05-typed-layer
git push -u origin hx/05-typed-layer
```

If #412 (HX-04) is merged: `git fetch origin && git merge origin/main` first (merge-commit style), re-run ring0, push, then `gh pr create --base main`. If #412 is still open: `gh pr create --base main --draft`, the body's first line "Stacked on #412 — review `hx/04-extension-catalog...hx/05-typed-layer`; ready once #412 merges." The body is the skill's five-line pointer, carries `Closes #382` once, and ends with `https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`. Then `gh pr edit --add-label needs-deep-review` (§6.2). After the merge, read #382 back (`gh issue view 382`) to confirm it — and only it — was closed.

---

## Rulings and deviations this plan takes

Each is recorded in the spec amendment (Task 12 Step 4) so a later reader can tell a decision from an oversight.

1. **Canonical CEL.** `CelText` writes one space around every binary operator, `, ` between call arguments, and parentheses only around a looser child or a right operand of equal precedence. A typed registration and an untyped one therefore show the same `When` exactly when the CEL string is written canonically (acceptance (6)); a CEL string spaced differently is shown as the host wrote it — the catalog never rewrites host text.
2. **`== null` and `HasValue` become `has()`.** Alvo's CEL refuses `== null`/`!= null` (every comparison already treats a missing value as false), so `x == null` translates to `!has(new.x)` and `x != null`/`x.HasValue` to `has(new.x)` — a deviation from §4.3's row, same meaning. The query side keeps §4.3's `is null` / `not is null`.
3. **Lists become `||` chains.** Alvo's CEL has no list literal, so `list.Contains(x)` in a `when` translates to `new.x == 'a' || new.x == 'b'` (parenthesised when nested), the spelling the parser's own fix recommends; an empty constant list is refused. `x.In(a, b)` is not shipped: §5 approves no type to carry it, and an array's `Contains` says the same. The query side is §4.3's `in` (≤ `AlvoFilter.MaxInCandidates`; an empty list matches nothing).
4. **Typed constants without a CEL literal.** A `Guid` constant compares as `string(new.x) == '<guid D>'` (Alvo's CEL cannot compare a Uuid with a String and has no uuid literal; `string()` renders a Uuid in the same `D` form); a `DateTimeOffset` constant as `timestamp('<UTC, RFC 3339, up to seven fraction digits>Z')`; a `DateOnly`, `DateTime`, `double`, `float`, `char` and `long.MinValue` constant is refused in a `when` with a fix (no exact literal). The query side places every value as a typed value for the driver to bind.
5. **`e.Caller` is `AlvoConditionCaller`** (`User`, `Tenant`, `HasRole(string)`), a public type §5 does not list: `AlvoContext.HasRole(Role)` cannot serve, because a host cannot construct an application `Role`. Translation-only members throw `NotSupportedException` if called.
6. **Text tests are ordinal.** `StartsWith`/`EndsWith` translate in their one-argument form and with `StringComparison.Ordinal`, `Contains(string)` as is — CEL compares characters exactly; any other `StringComparison` is refused. (The one-argument .NET overloads are culture-sensitive; the condition is judged by CEL, and the docs say so.)
7. **Query text operators wait for HX-07.** §4.3's table maps `StartsWith/EndsWith/Contains(string)` to new `AlvoFilterOperator` members, but §5 assigns those members (and their driver obligation and contract rows) to HX-07; HX-05 refuses them in a typed query with a fix and never falls back to `Like` (N-7). §5 decides because it is the approval list.
8. **An untranslatable `when` throws at registration** (Q3) as `AlvoTranslationException`, first problem only — the lambda is wrong whatever the descriptor says, and the registration call is where the host sees it. Mapping problems are boot problems, reported all at once (§4.2) at `extensions/<id>/record`: catalog-free ones (no constructor, a value type no field holds, a field mapped twice, a `[Flags]` enum) even at an `Awaiting` boot (HX-03 (39)).
9. **No per-shape translation cache** (Q3's "translations cached per expression shape" deferred): a `when` is translated once anyway; a typed query is walked per call and its captured values evaluated per call — a constant or closure field read by reflection, anything else through `Compile(preferInterpretation: true)` — which also rules out a cache serving a stale captured value. A cache waits for a measurement (HX-10's calibration).
10. **Typed `QueryAsync<T>` takes a required `limit` and returns `IReadOnlyList<T>`** — one bounded page, no cursor; paging stays the untyped `AlvoQuery`. A typed page would need an `AlvoPage<T>` §5 does not approve.
11. **Mapping rules.** Field: `[AlvoField]`, else `JsonNamingPolicy.SnakeCaseLower`; enum value: `[AlvoEnumValue]`, else the same policy (prior art: `System.Text.Json`'s naming policy). Mapped: public, readable, not an indexer, with a public setter or `[AlvoField]`. Compatible CLR types per field type as the docs table lists (`integer` takes `long` or `int`; `enum` takes `string` or an enum whose every member names a declared value); nullability is read with `NullabilityInfoContext`, and a non-nullable property on an optional field refuses boot — D2's "no `!`" applied to the mapping.
12. **The unit-test seam is two public members** — `AlvoEventContext.Create(...)` and `AlvoTestHost.EventContext(caller, …)` — closing HX-03 (53). Not on §5's list; a factory carries only the session it is given, so it grants nothing an `IAlvoSessions` caller lacks, and `InternalsVisibleTo` into the published TestHost is refused by the repository's own rule (forgeable).
13. **`IAlvoEntity` is `AlvoRecord Record { get; }` plus a public `(AlvoRecord)` constructor**: `As<T>()` wraps instead of copying, so unknown fields survive (the protocol HX-17's generated classes implement), and `x.Record["field"]` translates to the field in both translators — the reachable form of §4.3's `record["field"]` row, since the untyped builder keeps CEL strings (D1). A wrapper's `[AlvoField]` properties are checked like any mapping, and `As<T>()` refuses a record lacking one.
14. **`As<T>()` throws `InvalidOperationException`** naming the field — missing (distinct from null), null for a property that cannot hold null, or a value the property cannot take — and never echoes the value.
15. **Typed application events.** `AddEventHandler<TMessage, THandler>` reads the event type from `[AlvoEvent]`; its `when` stays a CEL string (an application event's condition tests only `@user`/`@tenant`, HX-03 (9)); `TMessage` is checked catalog-free only (a payload declares no fields), so a missing payload field fails the delivery attempt with the field named.
16. **The apply guard counts mapped fields as reads.** Removing, excluding or retyping a field a typed record maps is refused at its pointer with "its typed record `<Type>` maps it"; a field both read and mapped gets the condition's sentence once.
17. **Constants in a `when`** are literal constants, `const` (folded by the compiler), `static readonly` fields, enum members, conversions and negations of those, and array initializers of those for `Contains`. A captured variable, an instance member, a static property and any call are refused (§4.3); the clock (`DateTime(Offset).Now/UtcNow/Today`, `TimeProvider.GetUtcNow/GetLocalNow`) is refused in both translators — in a `when` until HX-11's `now()`, in a query with "capture it first".
18. **Acceptance (2) is proven through the real pipeline**: `SqlCapture` records every statement of a typed query on SQLite and PostgreSQL (`TypedLayerFacts`, linked into both legs), and 200 hostile captured strings per engine must yield statements byte-identical to a benign baseline. The alphabet is the filter-renderer property's hostile one, not the whole code space — PostgreSQL refuses NUL in a text parameter, which is the engine refusing the value, not text leaking into SQL.
19. **The dashboard slice adds no UI**: the *Lifecycle* lane already renders `When`, which for a typed handler is the translated CEL; the admin e2e world gains a typed handler and pins its condition (the `work_orders` badge moves from 2 to 3).

---

## Acceptance criteria → tasks

| HX-05 criterion | Task(s) | Test |
|---|---|---|
| (1) the §4.3 table as a data-driven unit suite | 4, 5 | `ConditionTranslationTests.A_condition_translates_to_its_cel` (33 rows, each re-parsed and compiled against the entity), `FilterTranslationTests.A_predicate_translates_to_its_filter` (19 rows) |
| (2) CsCheck: random captured strings never in emitted SQL text, both engines | 10 | `TypedLayerFacts.No_captured_string_reaches_the_sql_text` in `SqliteTypedLayerTests` and `PostgreSqlTypedLayerTests`; `A_hostile_captured_string_is_matched_as_a_value`; unit `FilterTranslationTests.A_captured_value_is_placed_as_a_typed_value_never_as_text` |
| (3) CsCheck: `parse(render(s)) == s` | 1 | `CelTextTests.Every_string_reads_back_as_itself` (10 000 over the whole code space), `Every_number_reads_back_as_its_own_text`, `A_canonical_condition_is_written_back_as_itself` |
| (4) every refused construct throws with id and fix; `UtcNow` in `when` refused until HX-11 | 4, 5, 6 | `ConditionTranslationTests.A_construct_with_no_cel_equivalent_is_refused_naming_the_extension_and_a_fix` (incl. `clock`, `local clock`), `The_refusal_names_the_construct_the_extension_and_the_fix`, `FilterTranslationTests.A_construct_with_no_filter_equivalent_is_refused_naming_the_entity_and_a_fix`, `TypedRegistrationTests.An_untranslatable_condition_is_refused_at_registration_naming_the_handler` |
| (5) a typed record with a missing/mistyped field, or reading an excluded field, refuses boot | 3, 7, 10 | `TypedBindingTests` (missing, mistyped, excluded, nullability, enum member, catalog-free, all at once), `TypedLayerFacts.Typed_records_that_do_not_fit_refuse_boot_all_at_once` (both engines) |
| (6) typed and untyped registrations of one handler produce the same catalog `When` | 6, 11 | `TypedRegistrationTests.Typed_and_untyped_registrations_of_one_handler_describe_the_same_condition`; e2e `The_lifecycle_tab_shows_a_typed_handler_s_condition_as_cel` |
| (7) `As<T>()` with a missing field throws naming it | 3 | `AlvoRecordAsTests.A_missing_field_throws_naming_it_and_the_property`, `A_wrapper_missing_a_field_it_declares_throws_naming_it` |
| Scope: `Entity<T>`, attributes, `IAlvoEntity`, typed messages | 2, 6, 10 | `RecordShapeTests`, `TypedRegistrationTests`, `TypedLayerFacts.A_typed_handler_is_handed_typed_images_and_its_translated_condition_selects`, `A_typed_application_event_handler_is_handed_its_typed_payload` |
| Scope: typed reads | 8, 10 | `TypedReadTests`, `TypedLayerFacts.A_typed_read_returns_typed_records_through_the_caller_s_rules`, `A_typed_read_never_crosses_tenants` |
| Scope: boot mapping validation incl. `events.exclude` (E7), and the apply guard | 7 | `TypedBindingTests.A_property_reading_a_field_left_out_of_the_event_images_refuses_boot`, `ExtensionApplyGuardTests.Removing_a_field_a_typed_record_maps_is_refused_at_the_field`, `Excluding_a_field_a_typed_record_maps_is_refused_at_the_exclusion` |
| Handed over by HX-03 (13): `EventCondition<T>`, `AlvoEventAttribute`, `AddEventHandler<TMessage,THandler>` | 2, 4, 6 | public-API baseline; `TypedRegistrationTests.A_typed_application_event_handler_reads_its_type_from_the_attribute` |
| Handed over by HX-03 (53): a public test factory for `AlvoEventContext` | 9 | `AlvoEventContextTests`, `AlvoTestHostEventContextTests.A_class_handler_called_directly_writes_as_the_caller`, snippet `A_class_handler_can_be_called_directly` |
| Docs: *Typed or CEL*; Dashboard: catalog shows translated `when` | 11, 12 | `BookingDeskHandlersTests` in `MMLib.Alvo.DocsSnippets.Tests`; website build; admin e2e |
| Security core (§6.2) | 13 | `alvo-security-core-review` checklist, `/security-review` substitute, `needs-deep-review` |
