---
name: alvo-dotnet-conventions
description: Use when adding a NuGet package, choosing a library, or writing C# in MMLib.Alvo — packaging, licensing, test stack, and code-style conventions.
---

# Alvo .NET conventions

MMLib.Alvo is Apache-2.0 from the first commit, forever — a license
constraint that rules out otherwise-reasonable dependency choices. These
conventions keep packaging, dependency licensing, the test stack, and code
style consistent across the solution; check them before adding a package,
picking a library, or writing C#.

## Packaging

- **Central Package Management.** All package versions live in
  `Directory.Packages.props` at the repo root. `PackageReference` entries in
  individual `.csproj` files carry no `Version` attribute — the version comes
  from the central file. Adding a new dependency means adding/adjusting an
  entry there, not pinning a version inline.
- **Shared MSBuild settings** live in `Directory.Build.props` (target
  framework, nullable/implicit-usings, warnings-as-errors, NuGet metadata like
  author/license/repository URL). New projects inherit these automatically —
  don't redeclare them per-project.
- **Target framework: `net10.0`.** The SDK version is pinned in `global.json`
  (currently `10.0.100`, `rollForward: latestFeature`) so every contributor
  and CI build the same SDK.

## Licensing bans (hard rule, not a preference)

- **No MediatR.** It went commercial in April 2025; using it would put a
  commercial dependency inside an Apache-2.0 core.
- **No FluentAssertions v8+.** Same reasoning — v8+ is commercially licensed.
  Older FluentAssertions versions aren't the fix either; **use Shouldly** for
  all assertions, full stop.
- If you need a mediator/outbox pattern, **Wolverine** is the suggested
  alternative: it's MIT-licensed and covers both the transactional outbox and
  an in-process mediator, which is exactly the shape principle 9 (vertical
  slice, no MediatR) calls for.

## Test stack

Tests run on **Microsoft.Testing.Platform (MTP)**, not VSTest — this is
selected via the `test.runner` key in `global.json`, so `dotnet test` already
uses MTP without extra flags. On top of that runtime, the stack is:

- **xUnit v3** — the test framework itself.
- **NSubstitute** — fakes/mocks for interfaces and ports.
- **CsCheck** — property-based testing (e.g. the SQL-injection and
  round-trip properties the security-core and schema-testing skills call
  for).
- **Verify** — snapshot testing (generated schema, generated OpenAPI, …).
- **NetArchTest** — architecture tests; this is how the package-dependency
  rules (e.g. "`Abstractions` depends on nothing") are enforced as code
  instead of relying on code review to catch a stray reference. See
  `ArchitectureTests.cs` in `MMLib.Alvo.Abstractions.Tests` for the existing
  pattern.
- **PublicApiGenerator** — public-API approval tests, so a breaking change to
  a shipped package's public surface fails a test and forces a conscious
  decision (major bump or revert) instead of shipping silently.
- **Testcontainers** — integration tests against real database engines
  (Postgres, SQL Server) rather than mocks, for anything that depends on
  engine-specific behavior.

`Directory.Packages.props` currently carries `xunit.v3`, `NetArchTest.Rules`,
and `Shouldly` — add the rest of the stack there as the corresponding test
types are actually written, following the same "no inline version" pattern.

## Code style

- **Default to zero inline comments; make the code say it instead.** Reaching
  for a `//` comment to explain *what* or *why* a line does something is the
  signal to refactor — lift the value into a well-named constant, the
  expression into a well-named method, the branch into a named predicate. A
  comment is the last resort, only for genuinely non-obvious rationale a name
  cannot carry (a workaround for an external bug, a subtle concurrency
  invariant). "It reads more clearly with a note" is not that — rename instead.

  ```csharp
  // ❌ a comment justifying a choice
  // Regex, not Containing: ".Internal" as a whole segment, so ".Internals" isn't a false positive.
  .ResideInNamespaceMatching(@"\.Internal(\.|$)")

  // ✅ the name carries the intent, the comment disappears
  private const string InternalNamespaceSegmentPattern = @"\.Internal(\.|$)";
  ...
  .ResideInNamespaceMatching(InternalNamespaceSegmentPattern)
  ```

  Red flags that mean *rename, don't comment*: `// X, not Y because…`,
  `// compare … not …`, a comment restating the next statement, a comment
  naming what a magic value / regex / flag is for. This holds in **every**
  language in the repo — C#, MSBuild XML, and shell scripts alike.
- **Code is written in English** — identifiers, string literals, log and test
  `Skip` messages, and the rare surviving comment. No other natural language
  belongs in code (docs and commit messages are a separate matter).
- **XML doc comments (`/// <summary>`) are required on public API members**
  of shipped library projects — public types/methods/properties on the ports
  (`Abstractions`) and the core. This is the published API surface
  (IntelliSense, generated docs), so it's the one place comments are expected
  by default; everything internal follows the "why, not what" rule above
  instead.
- Tests rely on descriptive test names, not comment blocks, to communicate
  intent.
