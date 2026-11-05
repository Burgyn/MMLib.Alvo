# Analyzers

Alvo's Roslyn analyzers: what they are, where they ship, what `ALVO1001` decides and what it cannot, and how a rule is
added. The user-facing page is *Analyzer rules* (`website/src/content/docs/extending/analyzer-rules.mdx`); the design is
`docs/superpowers/specs/2026-10-14-host-extensibility-design.md` §4.4.5 and E6, settled in *HX-10 settled details* there
and in `docs/superpowers/plans/2026-11-05-hx-10-hook-guards.md` (Rulings 10–18, 25, 31–33, 39, 45, 46).

## A project, not a package

`src/MMLib.Alvo.Analyzers` is `IsPackable=false` and targets `netstandard2.0` — the one target a compiler loads an
analyzer from (`SolutionConventionTests` exempts a project marked `<IsRoslynComponent>true</IsRoslynComponent>` from the
inherited `TargetFramework` for exactly that value). `MMLib.Alvo`'s pack places `MMLib.Alvo.Analyzers.dll` at
`analyzers/dotnet/cs/` through a `TargetsForTfmSpecificContentInPackage` target, behind a `ProjectReference` with
`ReferenceOutputAssembly="false"` and `PrivateAssets="all"`: build order only, no runtime reference and no nuspec
dependency (`Core_depends_only_on_Abstractions` still holds). This is how `System.Text.Json` and `xunit.v3` ship theirs.
Package-boundary rule (a) names Roslyn because a *runtime* Roslyn dependency is heavy; an analyzer asset adds nothing to a
consumer's runtime closure (Ruling 15, [`package-boundary.md`](package-boundary.md)).

The analyzer class, `InTransactionIoAnalyzer`, is `public` — Roslyn instantiates it by reflection, and every Microsoft
analyzer is public — and is the whole of `PublicApi.MMLib.Alvo.Analyzers.verified.txt`. The assembly is never a compile
reference of a consumer, so the type is not API anyone calls.

**The Roslyn floor.** `Microsoft.CodeAnalysis.CSharp` is pinned to **4.14.0**, older than the compiler of the minimum SDK
in `global.json` (10.0.100): a compiler loads an analyzer built against a Roslyn no newer than its own. The pin is scoped
to exactly `MMLib.Alvo.Analyzers` and `MMLib.Alvo.Analyzers.Tests` by an item condition on the project name (Ruling 45,
replacing Ruling 40's repository-wide pin): `CentralPackageTransitivePinningEnabled` makes a `PackageVersion` a
transitive pin for every project, and two suites already reach Roslyn transitively at versions no single pin satisfies —
`MMLib.Alvo.Schema.Tests` through Corvus.Json.Validator (>= 5.3.0) and the EF suites through
`Microsoft.EntityFrameworkCore.Design` (= 4.14.0). Unscoped, restore fails one of them (NU1109/NU1107). A new Roslyn
component joins the condition by name, deliberately; raising the floor re-runs `scripts/test-packed-analyzer`.

## Who it reaches

Every project that references `MMLib.Alvo` **directly**. NuGet does not flow an analyzer asset transitively: a package
that depends on the core gets `exclude="Build,Analyzers"` by default, and a `PackageReference`'s default
`PrivateAssets` keeps `analyzers` from flowing on. So a project that reaches the core only through another package — a
test project that references only `MMLib.Alvo.TestHost`, a class library built on a package that wraps the core — is
**not** checked. That is acceptable because the code the rule judges is registered against `IAlvoBuilder`, which a host
gets from `MMLib.Alvo` itself; *Analyzer rules* tells the reader to keep hook code in a project that references the core.
Spec E6's "the analyzer must reach every hook author" is amended to this (*HX-10 settled details* (15)).

## `ALVO1001`

Id `ALVO1001`, category `Reliability`, severity **Warning**, help link
`https://alvo.burgyn.online/extending/analyzer-rules/#alvo1001` (`Alvo1001DocsTests` holds the link and the page's
heading together).

**Scope (Rulings 10, 33, 39).** (a) Every operation block whose nearest containing type implementing
`IBeforeWriteHook`, `IAfterWriteHook`, `IBeforeDeleteHook` or `IAfterDeleteHook` (generic or not) — the whole type,
constructors, field initializers and nested types included, matching the boot ban's "in-transaction hook type";
(b) the delegate passed as `hook` to `AlvoEntityBuilder`/`AlvoEntityBuilder<T>`'s `BeforeWrite`, `AfterWrite`,
`BeforeDelete`, `AfterDelete`; (c) the delegate passed as `function` to `AddCelFunction`. For (b) and (c) a lambda's body
is analysed, and a method group whose method is declared in the same file (`IOperation.SemanticModel`). Validate hooks,
event handlers and commands are out of scope: they run outside any transaction.

**Reach (Rulings 11, 32).** A type is reached when it — or an array element, a generic argument, a base type or an
implemented interface of it — is in `System.Net.Http`, `System.Net.Sockets`, `System.Net.WebSockets`, `System.Net.Mail`,
`System.Net.Quic`; is `WebClient`, `WebRequest`, `HttpWebRequest` or `FtpWebRequest`; starts with
`Microsoft.AspNetCore.Identity` or `MMLib.Alvo.Identity`; is named `IEmailSender`; is one of `HookDependencies`' types
(`DbConnection`, `DbDataSource`, `IAlvoEvents`, `IAlvoData`, `IAlvoSessions`, `IAlvoTransactions`, `IOutboxStore`,
`IDeliveryStore`, `IAlvoCommands`, `IAlvoEventPump`, `IAlvoManagement`, `IAlvoOperations`, `IAlvoUserStore`,
`IAlvoUserAdministration`, `IAlvoBootstrapAdmin`, `IAlvoAssistant`); or is `System.Data.IDbConnection`,
`System.IO.{File,FileInfo,FileStream,Directory,DirectoryInfo}`, `Microsoft.EntityFrameworkCore.DbContext` or
`IDbContextFactory<T>` — the analyzer may be wider than the boot ban, never narrower. Reported operations: an object
creation, a field/property/parameter/local reference, a static call, a generic argument (`GetRequiredService<HttpClient>()`),
a `typeof` (`GetService(typeof(IAlvoData))`).

**Awaits (Rulings 12, 25).** In a hook scope, an `await` (through `ConfigureAwait`) of a call is reported unless the
method belongs to `IAlvoSession` (so `IAlvoTransaction`), is an extension method whose `this` is an `IAlvoSession` (the
typed `QueryAsync<T>`/`CreateAsync<T>`), is declared in `MMLib.Alvo.Hooks` (a context's `PublishAsync`), or is the hook's
own code — declared in the scope's hook type or a type nested in it, or a local function inside the analysed body.
Everything else the host wrote — a typed HTTP client, a repository, a helper class — is reported when awaited. An await
of a local, field or property is not judged (no call to judge). This is §4.4.5's "any `await` on a task not produced by
`write.Transaction`", narrowed to what an operation tree decides without data flow.

**CEL functions (Ruling 13).** In an `AddCelFunction` delegate any call returning `Task`, `Task<T>`, `ValueTask` or
`ValueTask<T>` is reported: the delegate is synchronous (async delegates are refused at registration), so it can only
block on it — `Microsoft.VisualStudio.Threading.Analyzers`' VSTHRD002 reasoning.

**One diagnostic per use (Rulings 14, 31).** A call on a reached receiver is reported once, at the receiver. A reached
field, property, parameter or local is reported once per operation block (per lambda body for a delegate scope), at its
first use; constants and static fields of value or enum types (`SocketType.Stream`) are not reported. Creations, static
calls, generic arguments, `typeof`, awaits and task calls are reported where they occur.

**Prior art.** Microsoft's `BannedApiAnalyzers` (RS0030): symbol-based matching, base types, interfaces and generic
arguments included — adopted; its consumer-configurable list — deviated from: the list is fixed (the boot ban's), so the
analyzer and the boot refusal cannot disagree.

## Parity with the boot ban

`BootBanParityTests` (core `InternalsVisibleTo` the analyzer's tests) holds the analyzer's list a superset of
`HookDependencies`': every banned type, network namespace, web-request type and identity prefix the start refuses is one
`ALVO1001` reports, and `IEmailSender` is matched by name on both sides. A type added to the boot ban without the analyzer
fails ring0.

## How acceptance (2) is proven

`scripts/test-packed-analyzer` (Ruling 16; in no ring — it needs Docker and nuget.org) packs `MMLib.Alvo.Abstractions`
and `MMLib.Alvo`, or takes CI's `artifacts/`, checks the nupkg layout (`analyzers/dotnet/cs/MMLib.Alvo.Analyzers.dll`)
and that the nuspec names neither the analyzer nor Roslyn (`grep -c 'MMLib\.Alvo\.Analyzers\|CodeAnalysis'` must be 0 —
Ruling 46: the planned `Analyzers\|CodeAnalysis` also matched NuGet's `exclude="Build,Analyzers"` on every dependency), then builds a
consumer generated in a temp directory inside `mcr.microsoft.com/dotnet/sdk:<global.json version>`, pinned to the local
feed by `packageSourceMapping`, and asserts `warning ALVO1001` on exactly the lines `scripts/fixtures/packed-consumer/Hooks.cs`
marks — the before-write hook and the `AddCelFunction` delegate, not the event handler — and a successful build. CI runs
it on the Linux leg right after `Pack`, inside the required **Build & test** check.

## In-repo wiring

The analyzer is applied in this repository **only to the docs snippets** (Ruling 17): `MMLib.Alvo.DocsSnippets.Tests`
references it with `OutputItemType="Analyzer"`, so every `website/src/snippets/extending/**` snippet compiles with
`ALVO1001` as an error (`TreatWarningsAsErrors`) — the code a reader copies is clean, and a false positive fails the
build. No other project gets it: test suites deliberately inject banned types into hooks to prove the boot ban, and the
analyzer's own suite compiles consumer sources through `AnalyzerHarness`.

## Not mutated

Every Stryker shard pins one `project`; mutating the analyzer needs a new shard with probed `mutants:`/`suite:` budgets,
which HX-10 could not probe (Ruling 18). Declared gap: the analyzer is advisory (a warning); the enforcement is the boot
ban and the time budget, both in mutated shards. Follow-up issue recorded with the PR.

## Blind spots

Documented on *Analyzer rules* under *What it cannot see* (Rulings 25, 32, 39):

- a **synchronous** call into the host's own code (`rates.Euro()`), and whatever another class the hook calls
  synchronously does — only an *await* of host code is judged;
- synchronous use of a client the list does not name (Redis `IDatabase`, gRPC `ClientBase`, `new StreamReader(path)`);
- a method group declared in another file;
- a delegate held in a local or returned by a factory before it reaches `AddCelFunction` or a hook verb.

The boot refusal covers a hook's constructor and delegate parameters; the time budget covers a CEL function whatever it
does, except one that never returns.

**Owed: `IAlvoScheduler` on the await allow-list (HX-13).** §4.4.5 allows scheduling a timer inside the write's
transaction. When HX-13 adds `IAlvoScheduler`, its methods must join the allowed awaits in `AlvoSymbols` (beside
`IAlvoSession` and `MMLib.Alvo.Hooks`), or every scheduler call in a hook becomes a false positive that breaks a
`TreatWarningsAsErrors` build (Ruling 39).

## Adding a rule

- **Id** `ALVO1xxx`, the next free number; category and severity chosen deliberately (a warning unless the rule can
  never be wrong).
- A `DiagnosticDescriptor` **field** in `AlvoDiagnostics` (RS2002 matches release tracking against field initializers
  only), with `helpLinkUri: HelpRoot + "#alvo1xxx"`.
- A row in `AnalyzerReleases.Unshipped.md` (RS2008); it moves to `Shipped.md` at release.
- A section on *Analyzer rules* headed exactly `## ALVO1xxx` (Starlight slugs the whole heading, and the help link is
  `#alvo1xxx`), with the rule's title as a bold first sentence, and a help-link fact beside `Alvo1001DocsTests`.
- Scope and reach tests in `test/MMLib.Alvo.Analyzers.Tests`, a false-positive fact for every documented-compliant
  shape, and a marked line in `scripts/fixtures/packed-consumer/Hooks.cs` if the rule must be proven on a packed
  consumer.
- `netstandard2.0` constraints: no `ArgumentNullException.ThrowIfNull`, no `string.Contains(char)`, no records, ordinal
  comparisons only; symbols via `IOperation.SemanticModel`, never `Compilation.GetSemanticModel` (RS1030).
