# F3 PR1 — Security core (caller context, CEL, policy engine) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the unbypassable half of the F3 vertical slice — a strongly-typed caller
context, a scoped dev-auth key model, and a CEL compiler whose Rule profile compiles to
*both* a two-valued SQL predicate and an in-memory delegate behind `IPolicyEngine` — so the
data port in PR2 is born with policy enforcement inside it rather than retrofitted around it.

**Architecture:** One hand-written CEL parser produces a validated, typed AST
(`CompiledExpression`); nothing in the core ever emits a column name or a dialect keyword.
`IPredicateRenderer` walks that AST into `SqlPredicate` (SQL text + named parameters),
delegating every field expression, dialect operator and boolean literal to the driver's
`IFieldSqlRenderer` — which is what keeps F7's dynamic entities from becoming a rewrite of
the security core. The same AST is interpreted in memory for `create` / `update`
post-image checks (`WITH CHECK`), and a differential property test proves the two backends
never disagree. `IPolicyEngine` resolves `(entity, operation, context)` into a
`PolicyDecision` — deny, or a `USING` / `WITH CHECK` pair plus the auto-injected tenant
scope — from a `PolicyCatalog` compiled once at apply time, so a rule referencing a
nonexistent column fails at save, not at request time.

**Tech Stack:** .NET `net10.0`, xUnit v3 on Microsoft.Testing.Platform, Shouldly, CsCheck
(property + fuzz), Verify (golden SQL), NSubstitute, NetArchTest, PublicApiGenerator.

**Source of truth:** [`docs/superpowers/specs/2026-07-25-f3-crud-vertical-slice-design.md`](../specs/2026-07-25-f3-crud-vertical-slice-design.md).
Read its sections *Caller context*, *Roles are a set*, *Minimal dev auth*, *CEL: one parser,
three profiles, two backends*, *The core compiles, the provider renders*, *Null semantics*,
*Default-deny*, *Field-level hidden/readOnly* before Task 1. Closes issue **#74** (`[15a]`)
and the front half of **#20**.

## Global Constraints

- Target framework `net10.0`; tests run on Microsoft.Testing.Platform (not VSTest).
  `TreatWarningsAsErrors=true` — a warning is a build failure.
- `MMLib.Alvo.Abstractions` is **EF-free, ADO.NET-free and ASP.NET-free** — pure model +
  ports. `RootNamespace` is `MMLib.Alvo`, so a file under `Expressions/` is namespace
  `MMLib.Alvo.Expressions`, never `MMLib.Alvo.Abstractions.Expressions`.
- `MMLib.Alvo` (core) references **only** `MMLib.Alvo.Abstractions` among family assemblies,
  and never EF Core or Npgsql — enforced by `test/_shared/SharedArchitectureRules.cs`.
- **No SQL string is emitted by the core without going through `IFieldSqlRenderer`** for
  fields, dialect operators and boolean literals. No user-supplied literal is ever
  interpolated into SQL — every literal becomes a named parameter.
- **Default-deny everywhere:** a missing rule denies, a missing context denies, a missing
  tenant on a scoped entity denies, an empty scope set denies.
- Public API members of shipped projects carry `/// <summary>` XML docs.
- Methods stay short and single-purpose (~25-line ceiling); extract aggressively. Zero
  inline comments — name things instead (`.claude/skills/alvo-dotnet-conventions`).
- Central package versions live in `Directory.Packages.props`; `.csproj` references carry
  no `Version=`.
- DI registration is idempotent (`TryAdd*`); each feature owns a `Setup.cs` with an
  internal `Add<Feature>` extension, registered explicitly from `AddAlvo` — no scanning.
- Every shipped-package change updates its `PublicApi.<assembly>.verified.txt` baseline in
  `test/_shared/`. A moved `*.verified.*` baseline triggers the turn gate → dispatch
  `alvo-snapshot-judge`; that is expected here, not a problem to route around.
- Run `scripts/test-ring0` after each implementation step, `scripts/test-ring1` at the end
  of each task, `scripts/test-ring2` before the PR. Conventional Commits, commit per task.
- Branch `f3/pr1-security-core`. **Never push to `main`.**

## Deliberate reading of the spec (record before you start)

Three points where this plan makes the spec's PR1 row concrete. Each is a decision, not an
oversight — if the maintainer disagrees, these are the vetoable spots:

1. **PR1 carries no ASP.NET dependency.** The spec puts
   `FrameworkReference Microsoft.AspNetCore.App` in the core *for the milestone*; PR1 has no
   endpoint to authenticate, so dev auth lands here as a **host-agnostic mechanism** (key
   store → principal, scope gate, tenant resolution) and its HTTP binding (header reading,
   authentication scheme, `IHttpContextAccessor`-backed ambient accessor) lands in PR3 with
   the endpoints. Consequence: `[15a]`'s DoD sentence "a request carries an identity" is
   demonstrated at the mechanism level in PR1 and end-to-end in PR3; the tenant-isolation
   half ("a query with no tenant context fails") is proved by the adversarial suite here
   over the in-memory data fake and again on real SQL in PR2.
2. **The adversarial suite ships green over an in-memory `IAlvoData`, not red.** The spec
   says "written red in PR1, green in PR2"; a red suite cannot be merged, since the PR is
   the gate. The intent — the suite predates and cannot be shaped by the SQL
   implementation — is preserved exactly: the suite is written as an abstract base in
   `MMLib.Alvo.Testing` (Task 12) *before* any implementation, and PR1 binds it to
   `InMemoryAlvoData` (policy enforced through the in-memory backend PR1 delivers). PR2 adds
   the SQLite and PostgreSQL subclasses, which start red there and go green. Within Task 12
   the TDD cycle is still red-first: the subclass is committed-ready before the fake exists,
   so the engineer sees the failures.
3. **The two-valued collapse is rendered once, in the core.** "The provider renders SQL"
   is honoured by the provider owning *field expressions, dialect operators and the
   boolean literals*; the AST walk and the `NULL → FALSE` rule live in one core
   `SqlPredicateRenderer` so the security-critical rule is written, snapshotted and
   mutation-tested once instead of once per engine. Golden per-engine snapshots follow in
   PR2 when real renderers exist; PR1 snapshots against a test renderer.

---

## File Structure

**`src/MMLib.Alvo.Abstractions/`** — ports + pure model (no EF, no ADO, no ASP.NET)

- Create `Identity/UserId.cs` — `readonly record struct UserId(Guid Value)` + JSON converter + `TryParse`.
- Create `Identity/TenantId.cs` — same shape for tenants.
- Create `Identity/Role.cs` — closed value type; `default(Role)` is `anon`.
- Create `Identity/RoleCatalog.cs` — the only mint for application roles.
- Create `Identity/AlvoContext.cs` — `UserId` + `IReadOnlySet<Role>` + `TenantId?`.
- Create `Auth/ApiKeyScope.cs` — `<entity>:<read|write>` scope value type.
- Create `Auth/AlvoPrincipal.cs` — resolved context + its scope set.
- Create `Auth/IAlvoContextResolver.cs` — resolve a principal from a presented key.
- Create `Auth/IAlvoContextAccessor.cs` — the ambient, per-request accessor.
- Create `Auth/ApiKeyRecord.cs` — the key model (hash, roles, tenant, scopes, expiry, revocation, last-used).
- Create `Auth/IApiKeyStore.cs` — key lookup port.
- Create `Expressions/CelProfile.cs` — `Rule` / `Computed` / `Condition`.
- Create `Expressions/CelValueType.cs` — the type lattice used by the checker.
- Create `Expressions/CelNode.cs` — the AST (one file, small records).
- Create `Expressions/CompiledExpression.cs` — validated tree + profile + result type + source.
- Create `Expressions/CelCompilationResult.cs` — success/failure with `CelCompilationError`.
- Create `Expressions/ICelCompiler.cs` — compile source + profile + `EntitySchema` → result.
- Create `Expressions/IFieldSqlRenderer.cs` — the driver's rendering contract.
- Create `Expressions/IPredicateRenderer.cs` — AST + context → `SqlPredicate`.
- Create `Expressions/SqlPredicate.cs` — SQL text + named parameters.
- Create `Rules/DataOperation.cs` — `List/Get/Create/Update/Delete`.
- Create `Rules/PolicyDecision.cs` — deny, or `Using` / `WithCheck` / `TenantScope` / field masks.
- Create `Rules/IPolicyEngine.cs` — the resolution port.
- Create `Data/AlvoRecord.cs` — weakly-typed record values.
- Create `Data/AlvoQuery.cs` + `Data/AlvoFilter.cs` — the query model (tree filter, sort, paging seam).
- Create `Data/IAlvoData.cs` — the data port; context is a required parameter.
- Create `Data/AlvoAuthorizationException.cs`, `Data/AlvoRecordNotFoundException.cs`.

**`src/MMLib.Alvo/`** — core implementations, organized as capability features

- Create `Expressions/Internal/CelLexer.cs`, `Expressions/Internal/CelToken.cs`.
- Create `Expressions/Internal/CelParser.cs` — recursive descent, depth + length caps.
- Create `Expressions/Internal/CelTypeChecker.cs` — gradual typing against `EntitySchema`, profile allow-list.
- Create `Expressions/Internal/CelCompiler.cs` — `ICelCompiler` implementation (lex → parse → check).
- Create `Expressions/Internal/CelInterpreter.cs` — the in-memory backend.
- Create `Expressions/Internal/SqlPredicateRenderer.cs` — the two-valued SQL backend.
- Create `Expressions/Setup.cs` — `AddAlvoExpressions`.
- Create `Rules/Internal/PolicyCatalogBuilder.cs` — compile every rule at apply, fail fast.
- Create `Rules/PolicyCatalog.cs` — compiled rules per (entity, operation) + field masks.
- Create `Rules/Internal/PolicyEngine.cs` — `IPolicyEngine` implementation.
- Create `Rules/Setup.cs` — `AddAlvoRules`.
- Create `Auth/Internal/ApiKeyHash.cs` — SHA-256 + constant-time compare.
- Create `Auth/Internal/ApiKeyContextResolver.cs` — key → `AlvoPrincipal`, default-deny.
- Create `Auth/Internal/InMemoryApiKeyStore.cs` — the dev key store from options.
- Create `Auth/ScopeGate.cs` — `(scopes, entity, operation)` → allowed, checked before policy.
- Create `Auth/TenantResolver.cs` — key tenant + requested tenant → `TenantId?`, mismatch denies.
- Create `Auth/AlvoAuthOptions.cs` — the dev key configuration surface.
- Create `Auth/Setup.cs` — `AddAlvoAuth`.
- Modify `AlvoServiceCollectionExtensions.cs` — call the three feature setups.
- Modify `Descriptor/Internal/DescriptorValidator.cs` — compile `rules.*`, `hidden`, `readOnly` at validate time.

**`src/MMLib.Alvo.Testing/`** — shipped fakes + contract suites

- Create `Expressions/TestFieldSqlRenderer.cs` — ANSI-ish renderer for core snapshots.
- Create `Data/InMemoryAlvoData.cs` — reference `IAlvoData` enforcing policy per row.
- Create `Data/AlvoDataAdversarialTests.cs` — the abstract two-user / two-tenant / default-deny suite.
- Create `Rules/PolicyEngineContractTests.cs` — abstract contract for `IPolicyEngine`.

**`test/`**

- `MMLib.Alvo.Abstractions.Tests/Identity/*` — identity primitives, `Role`, `RoleCatalog`, JSON.
- `MMLib.Alvo.Tests/Expressions/*` — lexer, parser, fuzz, type checker, interpreter, renderer, differential.
- `MMLib.Alvo.Tests/Rules/*` — policy catalog, engine, contract subclass.
- `MMLib.Alvo.Tests/Auth/*` — key resolution, scope gate, tenant resolver.
- `MMLib.Alvo.Tests/Data/InMemoryAlvoDataAdversarialTests.cs` — the concrete adversarial run.
- `test/_shared/PublicApi.*.verified.txt` — updated baselines.

**Repo files**

- Modify `schema/project.schema.json:145,689` — `@user.role` → `@user.roles`.
- Modify `stryker-config.json` — exclude the three `Setup.cs` wiring files.
- Create `docs/architecture/cel.md` — the profile table, the null rule, the rendering seam.

---

## Task 1: Identity primitives — `UserId` and `TenantId`

Strong typing that survives every boundary. A wrapper that serializes as `{"Value":"…"}` or
reaches ADO.NET as a wrapper is worse than a bare `Guid`, so the converter and the
`TryParse` land with the type, not later.

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Identity/UserId.cs`
- Create: `src/MMLib.Alvo.Abstractions/Identity/TenantId.cs`
- Test: `test/MMLib.Alvo.Abstractions.Tests/Identity/IdentityPrimitiveTests.cs`

**Interfaces:**
- Produces: `MMLib.Alvo.UserId` / `MMLib.Alvo.TenantId` — `readonly record struct` over
  `Guid Value`, `IParsable<T>` (`Parse`/`TryParse`), `ToString()` = the bare GUID text,
  `New()` factory, and a `JsonConverter` attached via `[JsonConverter]` that reads/writes a
  bare JSON string. Both expose `Value` so a provider can bind the raw `Guid`.

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Abstractions.Tests/Identity/IdentityPrimitiveTests.cs`:

```csharp
using System.Text.Json;

namespace MMLib.Alvo.Tests.Identity;

public class IdentityPrimitiveTests
{
    private static readonly Guid _guid = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void UserId_serializes_as_a_bare_json_string()
    {
        var json = JsonSerializer.Serialize(new UserId(_guid));

        json.ShouldBe($"\"{_guid}\"");
        JsonSerializer.Deserialize<UserId>(json).ShouldBe(new UserId(_guid));
    }

    [Fact]
    public void TenantId_serializes_as_a_bare_json_string()
    {
        var json = JsonSerializer.Serialize(new TenantId(_guid));

        json.ShouldBe($"\"{_guid}\"");
        JsonSerializer.Deserialize<TenantId>(json).ShouldBe(new TenantId(_guid));
    }

    [Fact]
    public void UserId_round_trips_through_TryParse()
    {
        UserId.TryParse(_guid.ToString(), provider: null, out var parsed).ShouldBeTrue();

        parsed.ShouldBe(new UserId(_guid));
        parsed.ToString().ShouldBe(_guid.ToString());
    }

    [Fact]
    public void TenantId_rejects_text_that_is_not_a_guid()
    {
        TenantId.TryParse("not-a-guid", provider: null, out var parsed).ShouldBeFalse();

        parsed.ShouldBe(default(TenantId));
    }

    [Fact]
    public void Deserializing_a_non_string_token_fails_loudly()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<UserId>("42"));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests`
Expected: FAIL — `UserId`/`TenantId` do not exist (CS0246).

- [ ] **Step 3: Implement `UserId`**

Create `src/MMLib.Alvo.Abstractions/Identity/UserId.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMLib.Alvo;

/// <summary>
/// The internal identifier of a caller. An external subject (an OIDC <c>sub</c>, an API key
/// identifier) is mapped to a <see cref="UserId"/>; the raw external value is never stored in
/// a record, so the framework-managed <c>created_by</c> / <c>updated_by</c> columns stay
/// <c>uuid</c>.
/// </summary>
/// <param name="Value">The underlying identifier.</param>
[JsonConverter(typeof(UserIdJsonConverter))]
public readonly record struct UserId(Guid Value) : IParsable<UserId>
{
    /// <summary>Creates a new, random identifier.</summary>
    public static UserId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();

    /// <inheritdoc />
    public static UserId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out UserId result)
    {
        if (Guid.TryParse(s, out var value))
        {
            result = new UserId(value);
            return true;
        }

        result = default;
        return false;
    }
}

/// <summary>Serializes <see cref="UserId"/> as a bare JSON string.</summary>
internal sealed class UserIdJsonConverter : JsonConverter<UserId>
{
    /// <inheritdoc />
    public override UserId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            ? new UserId(reader.GetGuid())
            : throw new JsonException("Expected a UUID string for a user id.");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, UserId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
```

- [ ] **Step 4: Implement `TenantId`**

Create `src/MMLib.Alvo.Abstractions/Identity/TenantId.cs` with the same shape: type
`TenantId`, converter `TenantIdJsonConverter`, exception message
`"Expected a UUID string for a tenant id."`, and a `<summary>` explaining that a `null`
`TenantId` denies on a tenant-scoped entity rather than widening to every tenant.

- [ ] **Step 5: Run the tests**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests`
Expected: PASS (5 new tests).

- [ ] **Step 6: Accept the public-API baseline**

Run: `scripts/test-ring0`
Expected: the `PublicApi.MMLib.Alvo.Abstractions` approval test fails with the two new
types in the diff. Move the received file over the verified one:
`cp test/_shared/PublicApi.MMLib.Alvo.Abstractions.received.txt test/_shared/PublicApi.MMLib.Alvo.Abstractions.verified.txt`
then re-run `scripts/test-ring0` — green. The turn gate will ask for
`alvo-snapshot-judge`; dispatch it (the baseline moved because two public types were added).

- [ ] **Step 7: Commit**

```bash
git add src/MMLib.Alvo.Abstractions/Identity test/MMLib.Alvo.Abstractions.Tests/Identity test/_shared
git commit -m "feat(identity): add UserId and TenantId strong types"
```

---

## Task 2: `Role` and `RoleCatalog` — a closed set, safe by default

Two properties carry the security value: `default(Role)` is `anon` (a forgotten field fails
safe), and an application role cannot be constructed outside a `RoleCatalog` built from the
descriptor. Note the trap: a `record struct` synthesizes equality over the *backing field*,
so `default(Role)` would not equal `Role.Anon`. Equality is written by hand over `Name`.

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Identity/Role.cs`
- Create: `src/MMLib.Alvo.Abstractions/Identity/RoleCatalog.cs`
- Test: `test/MMLib.Alvo.Abstractions.Tests/Identity/RoleTests.cs`
- Test: `test/MMLib.Alvo.Abstractions.Tests/Identity/RoleCatalogTests.cs`

**Interfaces:**
- Consumes: `AlvoDescriptor.Auth.Roles` (existing).
- Produces:
  - `MMLib.Alvo.Role` — `readonly record struct`, `string Name`, statics `Anon`,
    `Authenticated`, `Admin`, `internal static Role Application(string name)`, ordinal
    equality over `Name`, `ToString()` = `Name`.
  - `MMLib.Alvo.RoleCatalog` — `static RoleCatalog BuiltInOnly { get; }`,
    `static RoleCatalog FromDescriptor(AlvoDescriptor descriptor)`,
    `static RoleCatalog Create(IEnumerable<string> applicationRoles)`,
    `bool TryGet(string name, out Role role)`, `Role Get(string name)` (throws
    `UnknownRoleException`), `IReadOnlySet<Role> All { get; }`,
    `IReadOnlySet<Role> Resolve(IEnumerable<string> names)` (throws on the first unknown).
  - `MMLib.Alvo.UnknownRoleException` — carries `RoleName` and a fix suggestion listing the
    known roles.

- [ ] **Step 1: Write the failing `Role` test**

Create `test/MMLib.Alvo.Abstractions.Tests/Identity/RoleTests.cs`:

```csharp
namespace MMLib.Alvo.Tests.Identity;

public class RoleTests
{
    [Fact]
    public void Default_role_is_anon_so_a_forgotten_initialization_fails_safe()
    {
        default(Role).ShouldBe(Role.Anon);
        default(Role).Name.ShouldBe("anon");
    }

    [Fact]
    public void Default_role_hashes_like_anon()
    {
        default(Role).GetHashCode().ShouldBe(Role.Anon.GetHashCode());
    }

    [Fact]
    public void Built_in_roles_are_distinct_and_named()
    {
        Role.Authenticated.Name.ShouldBe("authenticated");
        Role.Admin.Name.ShouldBe("admin");
        Role.Admin.ShouldNotBe(Role.Authenticated);
        Role.Admin.ShouldNotBe(Role.Anon);
    }

    [Fact]
    public void Role_prints_as_its_name()
    {
        Role.Admin.ToString().ShouldBe("admin");
    }

    [Fact]
    public void Role_has_no_public_constructor_so_an_undeclared_role_cannot_be_minted()
    {
        typeof(Role).GetConstructors()
            .Where(constructor => constructor.GetParameters().Length > 0)
            .ShouldBeEmpty();
    }
}
```

- [ ] **Step 2: Run it, expect failure**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests`
Expected: FAIL — `Role` does not exist.

- [ ] **Step 3: Implement `Role`**

Create `src/MMLib.Alvo.Abstractions/Identity/Role.cs`:

```csharp
namespace MMLib.Alvo;

/// <summary>
/// A role a caller holds. A caller holds a <em>set</em> of roles, and CEL exposes them as
/// <c>@user.roles</c> with membership via <c>in</c> — the built-in trio is not one axis
/// (<c>anon</c> / <c>authenticated</c> describe whether the caller is logged in, <c>admin</c>
/// describes privilege), so a single slot cannot answer "any logged-in user may read".
/// </summary>
/// <remarks>
/// Two deliberate properties: <c>default(Role)</c> is <see cref="Anon"/> — the
/// least-privileged value, so an uninitialized field fails safe instead of open — and an
/// application role can only be minted through a <see cref="RoleCatalog"/> built from the
/// descriptor, so a typo is rejected where it enters rather than silently matching no rule.
/// </remarks>
public readonly record struct Role
{
    private const string AnonName = "anon";

    private readonly string? _name;

    private Role(string name) => _name = name;

    /// <summary>The anonymous, least-privileged role; also the value of <c>default(Role)</c>.</summary>
    public static Role Anon { get; } = new(AnonName);

    /// <summary>Every authenticated caller, regardless of privilege.</summary>
    public static Role Authenticated { get; } = new("authenticated");

    /// <summary>The built-in administrative role.</summary>
    public static Role Admin { get; } = new("admin");

    /// <summary>Gets the role name as it appears in the descriptor and in CEL.</summary>
    public string Name => _name ?? AnonName;

    internal static Role Application(string name) => new(name);

    /// <inheritdoc />
    public bool Equals(Role other) => string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    /// <inheritdoc />
    public override string ToString() => Name;
}
```

- [ ] **Step 4: Run the `Role` tests**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests`
Expected: PASS.

- [ ] **Step 5: Write the failing `RoleCatalog` test**

Create `test/MMLib.Alvo.Abstractions.Tests/Identity/RoleCatalogTests.cs`:

```csharp
using MMLib.Alvo.Descriptor;

namespace MMLib.Alvo.Tests.Identity;

public class RoleCatalogTests
{
    private const string DescriptorWithRoles = """
    {
      "apiVersion": "alvo.dev/v1",
      "name": "demo",
      "auth": { "roles": ["editor", "compliance"] },
      "entities": {
        "orders": { "fields": { "title": { "type": "string" } } }
      }
    }
    """;

    [Fact]
    public void Catalog_always_contains_the_three_built_ins()
    {
        RoleCatalog.BuiltInOnly.All.ShouldBe([Role.Anon, Role.Authenticated, Role.Admin], ignoreOrder: true);
    }

    [Fact]
    public void Catalog_mints_declared_application_roles()
    {
        var catalog = RoleCatalog.FromDescriptor(AlvoDescriptor.Parse(DescriptorWithRoles));

        catalog.TryGet("editor", out var editor).ShouldBeTrue();
        editor.Name.ShouldBe("editor");
        catalog.All.Count.ShouldBe(5);
    }

    [Fact]
    public void Undeclared_role_is_rejected_loudly_with_the_known_names()
    {
        var catalog = RoleCatalog.FromDescriptor(AlvoDescriptor.Parse(DescriptorWithRoles));

        var exception = Should.Throw<UnknownRoleException>(() => catalog.Get("edtior"));

        exception.RoleName.ShouldBe("edtior");
        exception.Message.ShouldContain("editor");
    }

    [Fact]
    public void Resolving_a_set_of_names_yields_roles()
    {
        var catalog = RoleCatalog.FromDescriptor(AlvoDescriptor.Parse(DescriptorWithRoles));

        catalog.Resolve(["authenticated", "editor"])
            .ShouldBe([Role.Authenticated, catalog.Get("editor")], ignoreOrder: true);
    }

    [Fact]
    public void A_descriptor_role_colliding_with_a_built_in_does_not_duplicate_it()
    {
        var catalog = RoleCatalog.Create(["admin", "editor"]);

        catalog.All.Count.ShouldBe(4);
        catalog.Get("admin").ShouldBe(Role.Admin);
    }
}
```

- [ ] **Step 6: Implement `RoleCatalog` + `UnknownRoleException`**

Create `src/MMLib.Alvo.Abstractions/Identity/RoleCatalog.cs`. Shape:

```csharp
using MMLib.Alvo.Descriptor;

namespace MMLib.Alvo;

/// <summary>
/// The closed set of roles this project recognises: the three built-ins plus the
/// descriptor's <c>auth.roles</c>. The only place an application <see cref="Role"/> can be
/// minted, so an undeclared name is rejected at the boundary where it arrives.
/// </summary>
public sealed class RoleCatalog
{
    private readonly Dictionary<string, Role> _byName;

    private RoleCatalog(IEnumerable<string> applicationRoles)
    {
        _byName = new Dictionary<string, Role>(StringComparer.Ordinal)
        {
            [Role.Anon.Name] = Role.Anon,
            [Role.Authenticated.Name] = Role.Authenticated,
            [Role.Admin.Name] = Role.Admin,
        };

        foreach (var name in applicationRoles)
        {
            _byName.TryAdd(name, Role.Application(name));
        }

        All = _byName.Values.ToHashSet();
    }

    /// <summary>A catalog holding only the three built-in roles.</summary>
    public static RoleCatalog BuiltInOnly { get; } = new([]);

    /// <summary>Gets every role this project recognises.</summary>
    public IReadOnlySet<Role> All { get; }

    /// <summary>Builds a catalog from a descriptor's <c>auth.roles</c>.</summary>
    /// <param name="descriptor">The descriptor to read roles from.</param>
    public static RoleCatalog FromDescriptor(AlvoDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return Create(descriptor.Auth?.Roles ?? []);
    }

    /// <summary>Builds a catalog from an explicit list of application role names.</summary>
    /// <param name="applicationRoles">Role names beyond the built-ins.</param>
    public static RoleCatalog Create(IEnumerable<string> applicationRoles) => new(applicationRoles);

    /// <summary>Looks a role up by name.</summary>
    /// <param name="name">The role name.</param>
    /// <param name="role">The resolved role when known.</param>
    /// <returns><see langword="true"/> when the name is a declared role.</returns>
    public bool TryGet(string name, out Role role) => _byName.TryGetValue(name, out role);

    /// <summary>Resolves a role by name.</summary>
    /// <param name="name">The role name.</param>
    /// <exception cref="UnknownRoleException">The name is not a declared role.</exception>
    public Role Get(string name) =>
        TryGet(name, out var role) ? role : throw new UnknownRoleException(name, KnownNames());

    /// <summary>Resolves a set of role names.</summary>
    /// <param name="names">The role names.</param>
    /// <exception cref="UnknownRoleException">Any name is not a declared role.</exception>
    public IReadOnlySet<Role> Resolve(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Select(Get).ToHashSet();
    }

    private IReadOnlyList<string> KnownNames() =>
        _byName.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
}
```

Add `UnknownRoleException` in the same file — `public sealed class UnknownRoleException : Exception`
with `string RoleName { get; }`, the three standard constructors (CA1032) plus
`internal UnknownRoleException(string roleName, IReadOnlyList<string> knownRoles)` whose
message is
`$"Role '{roleName}' is not declared. Declared roles: {string.Join(", ", knownRoles)}. Add it to auth.roles in the descriptor."`

- [ ] **Step 7: Run the tests, then ring1**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests` → PASS.
Run: `scripts/test-ring1` → accept the `PublicApi.MMLib.Alvo.Abstractions` baseline as in
Task 1 Step 6, re-run, green.

- [ ] **Step 8: Commit**

```bash
git add src/MMLib.Alvo.Abstractions/Identity test/MMLib.Alvo.Abstractions.Tests/Identity test/_shared
git commit -m "feat(identity): add Role value type and RoleCatalog"
```

---

## Task 3: `AlvoContext`, scopes, and the auth ports

The currency of every data operation, plus the ports the dev-auth mechanism implements. All
ASP.NET-free: `IAlvoContextResolver` takes the presented key, not an `HttpContext`.

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Identity/AlvoContext.cs`
- Create: `src/MMLib.Alvo.Abstractions/Auth/ApiKeyScope.cs`
- Create: `src/MMLib.Alvo.Abstractions/Auth/AlvoPrincipal.cs`
- Create: `src/MMLib.Alvo.Abstractions/Auth/ApiKeyRecord.cs`
- Create: `src/MMLib.Alvo.Abstractions/Auth/IApiKeyStore.cs`
- Create: `src/MMLib.Alvo.Abstractions/Auth/IAlvoContextResolver.cs`
- Create: `src/MMLib.Alvo.Abstractions/Auth/IAlvoContextAccessor.cs`
- Create: `src/MMLib.Alvo.Abstractions/Rules/DataOperation.cs`
- Test: `test/MMLib.Alvo.Abstractions.Tests/Identity/AlvoContextTests.cs`
- Test: `test/MMLib.Alvo.Abstractions.Tests/Auth/ApiKeyScopeTests.cs`

**Interfaces:**
- Produces:
  - `MMLib.Alvo.AlvoContext` — `sealed record` with `required UserId User`,
    `required IReadOnlySet<Role> Roles`, `TenantId? Tenant`;
    `static AlvoContext Anonymous { get; }` (a fixed all-zero `UserId` with `{ Role.Anon }`);
    `static AlvoContext System(TenantId? tenant)` for post-commit paths (`{ Role.Admin }`, a
    fixed well-known `UserId`), `bool HasRole(Role role)`, and a guard in the initializer
    that rejects an empty role set (`ArgumentException` via a validating `Roles` setter).
  - `MMLib.Alvo.Auth.ApiKeyScope` — `readonly record struct` with `string Entity`,
    `ScopeAccess Access` (`Read`/`Write`); `static bool TryParse(string, out ApiKeyScope)`
    over `"<entity|*>:<read|write>"`; `bool Allows(string entity, DataOperation operation)`
    where `List`/`Get` need `Read` and `Create`/`Update`/`Delete` need `Write`, and `Write`
    does **not** imply `Read` (an explicit pair is required — least privilege).
  - `MMLib.Alvo.Auth.AlvoPrincipal` — `sealed record` `AlvoContext Context`,
    `IReadOnlySet<ApiKeyScope> Scopes`, `string KeyId`.
  - `MMLib.Alvo.Auth.ApiKeyRecord` — `sealed record`: `KeyId`, `Sha256Hash` (base64),
    `UserId User`, `IReadOnlyList<string> RoleNames`, `TenantId? Tenant`,
    `IReadOnlySet<ApiKeyScope> Scopes`, `DateTimeOffset? ExpiresAt`,
    `DateTimeOffset? RevokedAt`, `DateTimeOffset? LastUsedAt`,
    `bool IsUsable(DateTimeOffset now)`.
  - `MMLib.Alvo.Auth.IApiKeyStore` — `ValueTask<ApiKeyRecord?> FindAsync(string keyId, CancellationToken)`,
    `ValueTask TouchAsync(string keyId, DateTimeOffset usedAt, CancellationToken)`.
  - `MMLib.Alvo.Auth.IAlvoContextResolver` —
    `ValueTask<AlvoPrincipal?> ResolveAsync(string? presentedKey, string? requestedTenant, CancellationToken)`;
    documented to return `null` (deny) for absent, malformed, expired, revoked or
    tenant-mismatched credentials, never a partially-trusted principal.
  - `MMLib.Alvo.Auth.IAlvoContextAccessor` — `AlvoPrincipal? Principal { get; set; }`, the
    ambient per-request accessor §4 asks for; documented as *availability, not enforcement*
    (`IAlvoData` still takes the context as a parameter, because the outbox dispatcher,
    after-hooks and automation actions run with no request scope).
  - `MMLib.Alvo.Rules.DataOperation` — `List`, `Get`, `Create`, `Update`, `Delete`.

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Abstractions.Tests/Identity/AlvoContextTests.cs`:

```csharp
namespace MMLib.Alvo.Tests.Identity;

public class AlvoContextTests
{
    [Fact]
    public void Anonymous_context_holds_exactly_the_anon_role_and_no_tenant()
    {
        AlvoContext.Anonymous.Roles.ShouldBe([Role.Anon]);
        AlvoContext.Anonymous.Tenant.ShouldBeNull();
    }

    [Fact]
    public void An_empty_role_set_is_rejected_because_anonymous_is_a_role_not_an_absence()
    {
        Should.Throw<ArgumentException>(() => new AlvoContext
        {
            User = UserId.New(),
            Roles = new HashSet<Role>(),
        });
    }

    [Fact]
    public void HasRole_answers_over_the_whole_set()
    {
        var context = new AlvoContext
        {
            User = UserId.New(),
            Roles = new HashSet<Role> { Role.Authenticated, Role.Admin },
        };

        context.HasRole(Role.Admin).ShouldBeTrue();
        context.HasRole(Role.Anon).ShouldBeFalse();
    }

    [Fact]
    public void System_context_names_its_identity_for_post_commit_paths()
    {
        var tenant = new TenantId(Guid.NewGuid());

        var system = AlvoContext.System(tenant);

        system.Roles.ShouldContain(Role.Admin);
        system.Tenant.ShouldBe(tenant);
        system.User.ShouldBe(AlvoContext.System(null).User);
    }
}
```

Create `test/MMLib.Alvo.Abstractions.Tests/Auth/ApiKeyScopeTests.cs`:

```csharp
using MMLib.Alvo.Auth;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Tests.Auth;

public class ApiKeyScopeTests
{
    [Theory]
    [InlineData("orders:read", DataOperation.List, true)]
    [InlineData("orders:read", DataOperation.Get, true)]
    [InlineData("orders:read", DataOperation.Update, false)]
    [InlineData("orders:write", DataOperation.Create, true)]
    [InlineData("orders:write", DataOperation.Delete, true)]
    [InlineData("orders:write", DataOperation.List, false)]
    [InlineData("*:read", DataOperation.Get, true)]
    public void Scope_gates_the_operation_it_names(string scope, DataOperation operation, bool allowed)
    {
        ApiKeyScope.TryParse(scope, out var parsed).ShouldBeTrue();

        parsed.Allows("orders", operation).ShouldBe(allowed);
    }

    [Fact]
    public void A_scope_for_another_entity_never_allows_this_one()
    {
        ApiKeyScope.TryParse("invoices:read", out var scope).ShouldBeTrue();

        scope.Allows("orders", DataOperation.List).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("orders")]
    [InlineData("orders:admin")]
    [InlineData("orders:read:extra")]
    [InlineData(":read")]
    public void Malformed_scopes_are_refused_rather_than_widened(string scope)
    {
        ApiKeyScope.TryParse(scope, out _).ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run, expect failure**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests`
Expected: FAIL — the types do not exist.

- [ ] **Step 3: Implement the model types**

`AlvoContext` (namespace `MMLib.Alvo`) — validate the role set in the property initializer:

```csharp
namespace MMLib.Alvo;

/// <summary>
/// The identity every data operation is performed as: who the caller is, which roles they
/// hold, and which tenant they act in. Passed explicitly to <c>IAlvoData</c> rather than read
/// from ambient state, because the post-commit paths (outbox dispatcher, after-hooks,
/// automation actions) run with no request scope and are exactly where a wrong or missing
/// tenant is catastrophic.
/// </summary>
public sealed record AlvoContext
{
    private static readonly UserId _systemUser = new(Guid.Parse("00000000-0000-0000-0000-00000000a1v0"));

    private readonly IReadOnlySet<Role> _roles = new HashSet<Role> { Role.Anon };

    /// <summary>Gets the caller's internal identifier.</summary>
    public required UserId User { get; init; }

    /// <summary>
    /// Gets the roles the caller holds; never empty, since anonymous is the role
    /// <see cref="Role.Anon"/> rather than the absence of one.
    /// </summary>
    public required IReadOnlySet<Role> Roles
    {
        get => _roles;
        init => _roles = value is { Count: > 0 }
            ? value
            : throw new ArgumentException(
                "A caller always holds at least one role; use { Role.Anon } for an anonymous caller.",
                nameof(Roles));
    }

    /// <summary>Gets the tenant the caller acts in; <see langword="null"/> denies on a tenant-scoped entity.</summary>
    public TenantId? Tenant { get; init; }

    /// <summary>The anonymous caller: a fixed all-zero identity holding only <see cref="Role.Anon"/>.</summary>
    public static AlvoContext Anonymous { get; } = new()
    {
        User = default,
        Roles = new HashSet<Role> { Role.Anon },
    };

    /// <summary>
    /// The framework's own identity, for actions that run as the system rather than as the
    /// originator of a change (spec §3.3).
    /// </summary>
    /// <param name="tenant">The tenant the action operates in.</param>
    public static AlvoContext System(TenantId? tenant) => new()
    {
        User = _systemUser,
        Roles = new HashSet<Role> { Role.Admin },
        Tenant = tenant,
    };

    /// <summary>Answers whether the caller holds a role.</summary>
    /// <param name="role">The role to test.</param>
    public bool HasRole(Role role) => Roles.Contains(role);
}
```

`Guid.Parse("00000000-0000-0000-0000-00000000a1v0")` is not a valid GUID (`v` is not hex) —
use `00000000-0000-0000-0000-0000000000a1` and name the constant `_systemUser`.

`ApiKeyScope`: parse with `ReadOnlySpan<char>` split on `':'`; reject empty entity, unknown
access, more than one separator. `Allows` compares the entity ordinally (with `*` as the
wildcard) and maps `DataOperation` → required `ScopeAccess` in one small private static.

`ApiKeyRecord.IsUsable(DateTimeOffset now)` returns
`RevokedAt is null && (ExpiresAt is null || ExpiresAt > now)`.

- [ ] **Step 4: Implement the ports**

Three interfaces + `DataOperation`, each with XML docs stating the default-deny contract
verbatim from the **Interfaces** block above. `IAlvoContextAccessor.Principal` has a setter
so the PR3 HTTP layer can populate it once per request.

- [ ] **Step 5: Run tests + ring0, accept baseline, commit**

Run: `scripts/test-ring0` → accept the Abstractions public-API baseline → green.

```bash
git add src/MMLib.Alvo.Abstractions test/MMLib.Alvo.Abstractions.Tests test/_shared
git commit -m "feat(auth): add AlvoContext, api-key scopes and the auth ports"
```

---

## Task 4: Dev auth — key resolution, scope gate, tenant resolution

The mechanism, host-agnostic (see *Deliberate reading of the spec* #1). Scopes are
mandatory: a key with an empty scope set can do nothing, because "a PAT without scopes is
the all-powerful `service_role` anti-pattern renamed". There is **no service-role bypass** —
it is deferred to #42, which delivers the audit that is supposed to log it.

**Files:**
- Create: `src/MMLib.Alvo/Auth/AlvoAuthOptions.cs`
- Create: `src/MMLib.Alvo/Auth/Internal/ApiKeyHash.cs`
- Create: `src/MMLib.Alvo/Auth/Internal/InMemoryApiKeyStore.cs`
- Create: `src/MMLib.Alvo/Auth/Internal/ApiKeyContextResolver.cs`
- Create: `src/MMLib.Alvo/Auth/ScopeGate.cs`
- Create: `src/MMLib.Alvo/Auth/TenantResolver.cs`
- Create: `src/MMLib.Alvo/Auth/Setup.cs`
- Test: `test/MMLib.Alvo.Tests/Auth/ApiKeyContextResolverTests.cs`
- Test: `test/MMLib.Alvo.Tests/Auth/ScopeGateTests.cs`
- Test: `test/MMLib.Alvo.Tests/Auth/TenantResolverTests.cs`

**Interfaces:**
- Consumes: `IApiKeyStore`, `ApiKeyRecord`, `AlvoPrincipal`, `ApiKeyScope`, `RoleCatalog`,
  `TenantId`, `DataOperation`.
- Produces:
  - `AlvoAuthOptions` — `IList<AlvoDevApiKey> DevKeys { get; }` where
    `AlvoDevApiKey` is `{ string KeyId; string Secret; Guid User; IList<string> Roles; Guid? Tenant; IList<string> Scopes; DateTimeOffset? ExpiresAt; }`
    (plain configuration-bindable shape), plus `string HeaderName { get; init; } = "X-Alvo-Api-Key"`
    consumed by PR3.
  - `internal static class ApiKeyHash` — `string Compute(string secret)` (base64 SHA-256),
    `bool Matches(string secret, string expectedHash)` using
    `CryptographicOperations.FixedTimeEquals`.
  - `internal sealed class ApiKeyContextResolver : IAlvoContextResolver` — presented key
    format `"<keyId>.<secret>"`; resolves the record, verifies the hash in constant time,
    checks usability, resolves roles through `RoleCatalog` (an unknown role name denies the
    whole request — F3 roles come from Alvo's own configuration, so rejecting loudly is
    correct), resolves the tenant through `TenantResolver`, touches `LastUsedAt`, and returns
    `null` on every failure path.
  - `public sealed class ScopeGate` — `bool Allows(AlvoPrincipal principal, string entity, DataOperation operation)`;
    an empty scope set denies. Called **before** `IPolicyEngine`.
  - `public sealed class TenantResolver` — `bool TryResolve(ApiKeyRecord key, string? requestedTenant, out TenantId? tenant)`:
    the key's tenant wins; a requested tenant that differs denies; a malformed requested
    tenant denies; a key with no tenant and no request yields `null` (which then denies on
    scoped entities in the policy engine, not here).

- [ ] **Step 1: Write the failing resolver test**

Create `test/MMLib.Alvo.Tests/Auth/ApiKeyContextResolverTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using MMLib.Alvo.Auth;

namespace MMLib.Alvo.Tests.Auth;

public class ApiKeyContextResolverTests
{
    private static readonly Guid _user = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid _tenant = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static IAlvoContextResolver Resolver(Action<AlvoAuthOptions>? configure = null)
    {
        var options = new AlvoAuthOptions();
        options.DevKeys.Add(new AlvoDevApiKey
        {
            KeyId = "dev",
            Secret = "s3cret",
            User = _user,
            Roles = { "authenticated", "editor" },
            Tenant = _tenant,
            Scopes = { "orders:read", "orders:write" },
        });
        configure?.Invoke(options);

        var store = new InMemoryApiKeyStore(Options.Create(options), TimeProvider.System);
        return new ApiKeyContextResolver(store, RoleCatalog.Create(["editor"]), TimeProvider.System);
    }

    [Fact]
    public async Task A_valid_key_resolves_to_its_identity_roles_tenant_and_scopes()
    {
        var principal = await Resolver().ResolveAsync("dev.s3cret", requestedTenant: null, TestContext.Current.CancellationToken);

        principal.ShouldNotBeNull();
        principal.Context.User.ShouldBe(new UserId(_user));
        principal.Context.Roles.ShouldBe([Role.Authenticated, RoleCatalog.Create(["editor"]).Get("editor")], ignoreOrder: true);
        principal.Context.Tenant.ShouldBe(new TenantId(_tenant));
        principal.Scopes.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dev")]
    [InlineData("dev.wrong")]
    [InlineData("unknown.s3cret")]
    [InlineData("dev.s3cret.extra")]
    public async Task Every_bad_credential_denies_rather_than_degrading(string? presented)
    {
        var principal = await Resolver().ResolveAsync(presented, requestedTenant: null, TestContext.Current.CancellationToken);

        principal.ShouldBeNull();
    }

    [Fact]
    public async Task An_expired_key_denies()
    {
        var resolver = Resolver(options => options.DevKeys[0].ExpiresAt = DateTimeOffset.UnixEpoch);

        var principal = await resolver.ResolveAsync("dev.s3cret", requestedTenant: null, TestContext.Current.CancellationToken);

        principal.ShouldBeNull();
    }

    [Fact]
    public async Task A_key_naming_an_undeclared_role_denies_the_whole_request()
    {
        var options = new AlvoAuthOptions();
        options.DevKeys.Add(new AlvoDevApiKey
        {
            KeyId = "dev",
            Secret = "s3cret",
            User = _user,
            Roles = { "edtior" },
            Scopes = { "orders:read" },
        });
        var resolver = new ApiKeyContextResolver(
            new InMemoryApiKeyStore(Options.Create(options), TimeProvider.System),
            RoleCatalog.Create(["editor"]),
            TimeProvider.System);

        var principal = await resolver.ResolveAsync("dev.s3cret", requestedTenant: null, TestContext.Current.CancellationToken);

        principal.ShouldBeNull();
    }

    [Fact]
    public async Task Requesting_another_tenant_than_the_key_owns_denies()
    {
        var principal = await Resolver().ResolveAsync(
            "dev.s3cret", requestedTenant: Guid.NewGuid().ToString(), TestContext.Current.CancellationToken);

        principal.ShouldBeNull();
    }

    [Fact]
    public async Task A_key_with_no_scopes_resolves_but_can_do_nothing()
    {
        var resolver = Resolver(options => options.DevKeys[0].Scopes.Clear());

        var principal = await resolver.ResolveAsync("dev.s3cret", requestedTenant: null, TestContext.Current.CancellationToken);

        principal.ShouldNotBeNull();
        new ScopeGate().Allows(principal, "orders", Rules.DataOperation.List).ShouldBeFalse();
    }
}
```

`InMemoryApiKeyStore` and `ApiKeyContextResolver` are `internal`; add
`[assembly: InternalsVisibleTo("MMLib.Alvo.Tests")]` if
`src/MMLib.Alvo/Properties/AssemblyInfo.cs` does not already carry it (check first — the
migrations tests already reach internals, so it probably does).

- [ ] **Step 2: Run, expect failure**

Run: `dotnet test --project test/MMLib.Alvo.Tests`
Expected: FAIL — types missing.

- [ ] **Step 3: Implement `ApiKeyHash`, `AlvoAuthOptions`, `InMemoryApiKeyStore`**

`ApiKeyHash.Compute` = `Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))`.
`Matches` compares the two base64 strings' bytes with
`CryptographicOperations.FixedTimeEquals`. `InMemoryApiKeyStore` maps each `AlvoDevApiKey`
into an `ApiKeyRecord` once in its constructor (hashing the configured secret), skipping any
entry whose scopes fail `ApiKeyScope.TryParse`, and keeps `LastUsedAt` in a
`ConcurrentDictionary`.

- [ ] **Step 4: Implement `TenantResolver`, `ScopeGate`, `ApiKeyContextResolver`**

Keep each method under ~15 lines; `ApiKeyContextResolver.ResolveAsync` reads as a sequence of
guarded steps (`SplitPresentedKey`, `FindUsableKey`, `VerifySecret`, `ResolveRoles`,
`ResolveTenant`, `Touch`), each returning `null` to deny.

- [ ] **Step 5: Write `ScopeGate` and `TenantResolver` tests**

Create `test/MMLib.Alvo.Tests/Auth/ScopeGateTests.cs` covering: empty scope set denies every
operation; `orders:read` denies `Create`; `*:write` allows `Delete` on any entity;
`orders:read` denies another entity's `List`.

Create `test/MMLib.Alvo.Tests/Auth/TenantResolverTests.cs` covering: key tenant with no
request → key tenant; matching requested tenant → that tenant; differing requested tenant →
deny; malformed requested tenant → deny; no key tenant and no request → `null` with a
`true` return (the denial belongs to the policy engine, which knows whether the entity is
scoped).

- [ ] **Step 6: Wire `Auth/Setup.cs` and register it**

```csharp
namespace MMLib.Alvo.Auth;

internal static class AuthSetup
{
    internal static IServiceCollection AddAlvoAuth(this IServiceCollection services)
    {
        services.AddOptions<AlvoAuthOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IApiKeyStore, Internal.InMemoryApiKeyStore>();
        services.TryAddSingleton<IAlvoContextResolver, Internal.ApiKeyContextResolver>();
        services.TryAddSingleton<ScopeGate>();
        services.TryAddSingleton<TenantResolver>();
        return services;
    }
}
```

`RoleCatalog` is registered by the descriptor pipeline (Task 13); until then register
`RoleCatalog.BuiltInOnly` via `TryAddSingleton`. Call `services.AddAlvoAuth()` from
`AddAlvo`.

- [ ] **Step 7: ring1, baselines, commit**

Run: `scripts/test-ring1` → accept the core public-API baseline → green.

```bash
git add src/MMLib.Alvo/Auth src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs test/MMLib.Alvo.Tests/Auth test/_shared
git commit -m "feat(auth): add scoped dev api-key resolution and tenant resolution"
```

---

## Task 5: CEL lexer and AST

The AST is the product the core hands to a provider — no SQL, no column names. Keep it
small: every node kind here is one the profiles allow, so an out-of-profile construct is
rejected by the parser or the checker, never represented.

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Expressions/CelValueType.cs`
- Create: `src/MMLib.Alvo.Abstractions/Expressions/CelNode.cs`
- Create: `src/MMLib.Alvo.Abstractions/Expressions/CelProfile.cs`
- Create: `src/MMLib.Alvo/Expressions/Internal/CelToken.cs`
- Create: `src/MMLib.Alvo/Expressions/Internal/CelLexer.cs`
- Test: `test/MMLib.Alvo.Tests/Expressions/CelLexerTests.cs`

**Interfaces:**
- Produces:
  - `MMLib.Alvo.Expressions.CelProfile` — `Rule`, `Computed`, `Condition`.
  - `MMLib.Alvo.Expressions.CelValueType` — `Bool`, `Int`, `Decimal`, `String`, `Timestamp`,
    `Uuid`, `Json`, `StringList`, `Null`.
  - `MMLib.Alvo.Expressions.CelNode` (abstract record) and its cases:
    - `CelLiteral(CelValueType Type, object? Value)`
    - `CelFieldRef(string FieldName, CelValueType Type, CelRecordState State)` — `State` is
      `Current` for a Rule/Computed row field, `New`/`Old` in the Condition profile.
    - `CelContextRef(CelContextValue Value, CelValueType Type)` where `CelContextValue` is
      `UserId`, `UserRoles`, `TenantId`.
    - `CelUnary(CelUnaryOperator Operator, CelNode Operand)` — `Not`, `Negate`.
    - `CelBinary(CelBinaryOperator Operator, CelNode Left, CelNode Right)` — `Equal`,
      `NotEqual`, `Less`, `LessOrEqual`, `Greater`, `GreaterOrEqual`, `And`, `Or`, `In`,
      `Add`, `Subtract`, `Multiply`, `Divide`.
    - `CelHas(CelFieldRef Field)` — the standard presence test.
    - `CelConditional(CelNode Condition, CelNode WhenTrue, CelNode WhenFalse)`.
    - `CelChanged(string FieldName)` — Condition profile only.
  - `MMLib.Alvo.Expressions` internals: `CelToken(CelTokenKind Kind, string Text, int Position)`,
    `CelLexer.Tokenize(string source)` → `IReadOnlyList<CelToken>` or throws
    `CelSyntaxException(string message, int position)`.

- [ ] **Step 1: Write the failing lexer test**

Create `test/MMLib.Alvo.Tests/Expressions/CelLexerTests.cs`:

```csharp
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

public class CelLexerTests
{
    private static IReadOnlyList<CelTokenKind> Kinds(string source) =>
        CelLexer.Tokenize(source).Select(token => token.Kind).ToArray();

    [Fact]
    public void Tokenizes_a_row_field_compared_to_a_context_value()
    {
        Kinds("owner_id == @user.id").ShouldBe(
        [
            CelTokenKind.Identifier,
            CelTokenKind.Equal,
            CelTokenKind.ContextReference,
            CelTokenKind.Dot,
            CelTokenKind.Identifier,
            CelTokenKind.EndOfInput,
        ]);
    }

    [Fact]
    public void Tokenizes_membership_over_a_string_literal()
    {
        Kinds("'editor' in @user.roles").ShouldBe(
        [
            CelTokenKind.StringLiteral,
            CelTokenKind.In,
            CelTokenKind.ContextReference,
            CelTokenKind.Dot,
            CelTokenKind.Identifier,
            CelTokenKind.EndOfInput,
        ]);
    }

    [Theory]
    [InlineData("'it''s'", "it's")]
    [InlineData("\"quoted\"", "quoted")]
    [InlineData("'a\\nb'", "a\nb")]
    public void Reads_string_literals_with_escapes(string source, string expected)
    {
        CelLexer.Tokenize(source)[0].Text.ShouldBe(expected);
    }

    [Theory]
    [InlineData("'unterminated")]
    [InlineData("owner_id # 1")]
    [InlineData("@")]
    [InlineData("1.2.3")]
    public void Refuses_input_it_cannot_tokenize(string source)
    {
        Should.Throw<CelSyntaxException>(() => CelLexer.Tokenize(source));
    }

    [Fact]
    public void Reports_the_position_of_the_offending_character()
    {
        var exception = Should.Throw<CelSyntaxException>(() => CelLexer.Tokenize("a && #"));

        exception.Position.ShouldBe(5);
    }
}
```

- [ ] **Step 2: Run, expect failure**

Run: `dotnet test --project test/MMLib.Alvo.Tests` → FAIL (types missing).

- [ ] **Step 3: Implement the AST + profile + value type in Abstractions**

One file per concept as listed; `CelNode` is a single file holding the abstract record and
its cases, each with an XML doc naming which profiles may contain it.

- [ ] **Step 4: Implement `CelToken` and `CelLexer`**

Token kinds: `Identifier`, `ContextReference`, `StringLiteral`, `IntLiteral`,
`DecimalLiteral`, `True`, `False`, `Null`, `In`, `Has`, `Dot`, `Comma`, `LeftParen`,
`RightParen`, `Question`, `Colon`, `Equal`, `NotEqual`, `Less`, `LessOrEqual`, `Greater`,
`GreaterOrEqual`, `And`, `Or`, `Not`, `Plus`, `Minus`, `Star`, `Slash`, `EndOfInput`.
`@user` / `@tenant` lex as one `ContextReference` token carrying the name without the `@`;
a bare `@` or an unknown `@name` throws. Keep `Tokenize` a dispatch loop over small
`Read*` methods (~15 lines each).

- [ ] **Step 5: Run tests, ring0, commit**

```bash
git add src/MMLib.Alvo.Abstractions/Expressions src/MMLib.Alvo/Expressions test/MMLib.Alvo.Tests/Expressions test/_shared
git commit -m "feat(expressions): add the CEL AST and lexer"
```

---

## Task 6: CEL parser — precedence, caps, and fuzz resistance

Recursive descent following CEL's precedence. Two caps make the fuzz criterion reachable:
the schema's own `maxLength: 2000` on `$defs/cel`, and a nesting depth cap so a pathological
input cannot exhaust the stack.

**Files:**
- Create: `src/MMLib.Alvo/Expressions/Internal/CelParser.cs`
- Test: `test/MMLib.Alvo.Tests/Expressions/CelParserTests.cs`
- Test: `test/MMLib.Alvo.Tests/Expressions/CelParserFuzzTests.cs`

**Interfaces:**
- Consumes: `CelLexer`, `CelToken`, the AST records.
- Produces: `internal static class CelParser` with
  `static CelSyntaxNode Parse(string source)` → an **untyped** parse tree
  (`CelParseNode`), because field types are only known to the checker. To avoid a second
  tree, parse into the AST records with `CelValueType.Null` placeholders on `CelFieldRef`
  and let the checker rewrite them — document that the parser's output is *not* a
  `CompiledExpression` and must never be rendered.
  Constants: `MaxSourceLength = 2000`, `MaxDepth = 32`. Both violations throw
  `CelSyntaxException`.

Precedence, lowest to highest: conditional `?:` (right-assoc) → `||` → `&&` → relations
(`== != < <= > >=`, `in`; non-associative — `a == b == c` is a syntax error) → additive
(`+ -`) → multiplicative (`* /`) → unary (`! -`) → primary (literal, identifier,
`@ctx.member`, `has(field)`, parenthesised).

- [ ] **Step 1: Write the failing parser test**

Create `test/MMLib.Alvo.Tests/Expressions/CelParserTests.cs`:

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

public class CelParserTests
{
    [Fact]
    public void And_binds_tighter_than_or()
    {
        var parsed = CelParser.Parse("a == 1 || b == 2 && c == 3");

        var root = parsed.ShouldBeOfType<CelBinary>();
        root.Operator.ShouldBe(CelBinaryOperator.Or);
        root.Right.ShouldBeOfType<CelBinary>().Operator.ShouldBe(CelBinaryOperator.And);
    }

    [Fact]
    public void Parentheses_override_precedence()
    {
        var parsed = CelParser.Parse("(a == 1 || b == 2) && c == 3");

        parsed.ShouldBeOfType<CelBinary>().Operator.ShouldBe(CelBinaryOperator.And);
    }

    [Fact]
    public void Negation_applies_to_the_parenthesised_group()
    {
        var parsed = CelParser.Parse("!(owner_id == @user.id)");

        var unary = parsed.ShouldBeOfType<CelUnary>();
        unary.Operator.ShouldBe(CelUnaryOperator.Not);
        unary.Operand.ShouldBeOfType<CelBinary>().Operator.ShouldBe(CelBinaryOperator.Equal);
    }

    [Fact]
    public void Multiplication_binds_tighter_than_addition()
    {
        var parsed = CelParser.Parse("unit_price * amount + 1");

        var root = parsed.ShouldBeOfType<CelBinary>();
        root.Operator.ShouldBe(CelBinaryOperator.Add);
        root.Left.ShouldBeOfType<CelBinary>().Operator.ShouldBe(CelBinaryOperator.Multiply);
    }

    [Fact]
    public void Conditional_is_right_associative()
    {
        var parsed = CelParser.Parse("a ? b : c ? d : e");

        var root = parsed.ShouldBeOfType<CelConditional>();
        root.WhenFalse.ShouldBeOfType<CelConditional>();
    }

    [Fact]
    public void Context_members_become_typed_context_references()
    {
        CelParser.Parse("@user.roles").ShouldBeOfType<CelContextRef>()
            .Value.ShouldBe(CelContextValue.UserRoles);
        CelParser.Parse("@tenant.id").ShouldBeOfType<CelContextRef>()
            .Value.ShouldBe(CelContextValue.TenantId);
    }

    [Fact]
    public void Has_parses_as_a_presence_test_over_a_field()
    {
        CelParser.Parse("has(owner_id)").ShouldBeOfType<CelHas>()
            .Field.FieldName.ShouldBe("owner_id");
    }

    [Fact]
    public void Changed_parses_as_its_own_node()
    {
        CelParser.Parse("changed(status)").ShouldBeOfType<CelChanged>()
            .FieldName.ShouldBe("status");
    }

    [Fact]
    public void New_and_old_prefixes_become_state_qualified_field_references()
    {
        CelParser.Parse("new.status").ShouldBeOfType<CelFieldRef>().State.ShouldBe(CelRecordState.New);
        CelParser.Parse("old.status").ShouldBeOfType<CelFieldRef>().State.ShouldBe(CelRecordState.Old);
    }

    [Theory]
    [InlineData("a ==")]
    [InlineData("== a")]
    [InlineData("a == b == c")]
    [InlineData("(a == b")]
    [InlineData("a && ")]
    [InlineData("has()")]
    [InlineData("has(a, b)")]
    [InlineData("unknown_macro(a)")]
    [InlineData("a.b.c")]
    [InlineData("@user.unknown")]
    [InlineData("[1, 2]")]
    public void Refuses_input_outside_the_grammar(string source)
    {
        Should.Throw<CelSyntaxException>(() => CelParser.Parse(source));
    }

    [Fact]
    public void Refuses_source_longer_than_the_schema_allows()
    {
        var source = string.Join(" || ", Enumerable.Repeat("a == 1", 400));

        Should.Throw<CelSyntaxException>(() => CelParser.Parse(source))
            .Message.ShouldContain("2000");
    }

    [Fact]
    public void Refuses_pathological_nesting_instead_of_exhausting_the_stack()
    {
        var source = new string('(', 200) + "a" + new string(')', 200);

        Should.Throw<CelSyntaxException>(() => CelParser.Parse(source));
    }
}
```

- [ ] **Step 2: Run, expect failure; then implement `CelParser`**

Run: `dotnet test --project test/MMLib.Alvo.Tests` → FAIL. Implement one method per
precedence level (`ParseConditional`, `ParseOr`, `ParseAnd`, `ParseRelation`,
`ParseAdditive`, `ParseMultiplicative`, `ParseUnary`, `ParsePrimary`), a `_depth` counter
incremented in `ParseConditional` and `ParsePrimary`'s paren branch, and small helpers
(`Expect`, `Match`, `Current`). Re-run → PASS.

- [ ] **Step 3: Write the fuzz property test**

Create `test/MMLib.Alvo.Tests/Expressions/CelParserFuzzTests.cs`:

```csharp
using CsCheck;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

public class CelParserFuzzTests
{
    private const string Alphabet = "abc_ 01'\"()[]{}.,:;?!<>=&|+-*/%@\\\n\t";

    [Fact]
    public void Arbitrary_text_either_parses_or_raises_a_cel_syntax_error()
    {
        Gen.Char[Alphabet].Array[0, 60]
            .Select(characters => new string(characters))
            .Sample(source =>
            {
                try
                {
                    CelParser.Parse(source);
                }
                catch (CelSyntaxException)
                {
                }
            },
            iter: 20_000);
    }

    [Fact]
    public void Deeply_nested_generated_input_never_stack_overflows()
    {
        Gen.Int[1, 400].Sample(
            depth => Should.Throw<CelSyntaxException>(() =>
                CelParser.Parse(new string('!', depth) + "a")).ShouldNotBeNull(),
            iter: 200);
    }
}
```

The first test's contract is the important one: **no exception other than
`CelSyntaxException` may escape** — a `NullReferenceException`,
`IndexOutOfRangeException`, `StackOverflowException` or `ArgumentOutOfRangeException` fails
the property. Adjust the second test if a short `!` chain legitimately parses (then assert
"parses or throws `CelSyntaxException`", never a crash).

- [ ] **Step 4: Run the fuzz suite, fix what it finds, commit**

Run: `dotnet test --project test/MMLib.Alvo.Tests` → PASS.

```bash
git add src/MMLib.Alvo/Expressions test/MMLib.Alvo.Tests/Expressions
git commit -m "feat(expressions): add the CEL parser with depth and length caps"
```

---

## Task 7: Type checker, profiles, and `ICelCompiler`

Where fail-fast lives: an unknown column, an out-of-profile node, a type error and the
singular `@user.role` all become structured errors *at apply*, each with a fix suggestion.
This is what makes "a rule referencing a nonexistent column fails at save, not at request
time" true.

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Expressions/CompiledExpression.cs`
- Create: `src/MMLib.Alvo.Abstractions/Expressions/CelCompilationResult.cs`
- Create: `src/MMLib.Alvo.Abstractions/Expressions/ICelCompiler.cs`
- Create: `src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs`
- Create: `src/MMLib.Alvo/Expressions/Internal/CelCompiler.cs`
- Create: `src/MMLib.Alvo/Expressions/Setup.cs`
- Test: `test/MMLib.Alvo.Tests/Expressions/CelCompilerTests.cs`
- Test: `test/MMLib.Alvo.Tests/Expressions/CelProfileTests.cs`

**Interfaces:**
- Consumes: `CelParser`, `EntitySchema`, `FieldSchema`, `FieldType`.
- Produces:
  - `CompiledExpression` — `sealed record` `{ required CelNode Root; required CelProfile Profile; required CelValueType ResultType; required string Source; required string EntityName; }`.
    XML doc states: a `CompiledExpression` is only ever produced by a successful
    `ICelCompiler.Compile`, so a renderer may assume it is type-checked and in-profile.
  - `CelCompilationError` — `sealed record (string Message, string? FixSuggestion, int Position)`.
  - `CelCompilationResult` — `sealed record` with
    `static CelCompilationResult Success(CompiledExpression expression)`,
    `static CelCompilationResult Failure(params CelCompilationError[] errors)`,
    `bool IsSuccess`, `CompiledExpression? Expression`,
    `IReadOnlyList<CelCompilationError> Errors`.
  - `ICelCompiler` — `CelCompilationResult Compile(string source, CelProfile profile, EntitySchema entity)`.
  - `internal sealed class CelCompiler : ICelCompiler`.
  - `internal static class ExpressionsSetup` — `AddAlvoExpressions` registering
    `ICelCompiler` and (Task 9) `IPredicateRenderer`.

Type rules the checker enforces:
- `&&`, `||`, `!` require `Bool` operands and yield `Bool`.
- Comparisons require **comparable, compatible** operands (`String`↔`String`,
  numeric↔numeric with `Int` widening to `Decimal`, `Uuid`↔`Uuid`, `Timestamp`↔`Timestamp`,
  anything↔`Null`) and yield `Bool`. `Json` operands are rejected with a fix suggestion
  ("compare a scalar field, or defer to a hook").
- `<`, `<=`, `>`, `>=` additionally reject `Bool` and `Uuid`.
- `in` requires `(String, StringList)` and yields `Bool`; the only `StringList` in the
  environment is `@user.roles`.
- Arithmetic requires numeric operands, yields the wider type, and is **Computed-only**.
- `has(field)` yields `Bool` and requires a row field.
- The conditional requires a `Bool` condition and identical branch types.
- The Rule and Condition profiles require the whole expression to be `Bool`; the Computed
  profile requires it to be a non-`Bool` scalar matching the target field's type
  (checked by the caller in Task 13, which knows the field).
- `FieldType` → `CelValueType`: `String`/`Text`/`Enum` → `String`; `Integer` → `Int`;
  `Decimal` → `Decimal`; `Boolean` → `Bool`; `Date`/`DateTime` → `Timestamp`;
  `Uuid`/`Ref` → `Uuid`; `Json` → `Json`.

Profile allow-lists:

| Node | Rule | Computed | Condition |
|---|---|---|---|
| `CelFieldRef` `State=Current` | ✓ | ✓ | ✓ |
| `CelFieldRef` `State=New/Old`, `CelChanged` | ✗ | ✗ | ✓ |
| `CelContextRef` | ✓ | ✗ | ✓ |
| comparisons, `&& \|\| !`, `in`, `has` | ✓ | ✗ | ✓ |
| arithmetic, conditional | ✗ | ✓ | ✗ |

- [ ] **Step 1: Write the failing compiler test**

Create `test/MMLib.Alvo.Tests/Expressions/CelCompilerTests.cs`:

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

public class CelCompilerTests
{
    private static readonly EntitySchema _orders = new()
    {
        Name = "orders",
        Tenancy = TenancyMode.Scoped,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid },
            new FieldSchema { Name = "owner_id", Type = FieldType.Uuid, Nullable = true },
            new FieldSchema { Name = "status", Type = FieldType.Enum, EnumValues = ["draft", "approved"] },
            new FieldSchema { Name = "total", Type = FieldType.Decimal, Precision = 18, Scale = 2 },
            new FieldSchema { Name = "payload", Type = FieldType.Json, Nullable = true },
            new FieldSchema { Name = "tenant_id", Type = FieldType.Uuid },
        ],
    };

    private static readonly ICelCompiler _compiler = new CelCompiler();

    private static CelCompilationResult Compile(string source, CelProfile profile = CelProfile.Rule) =>
        _compiler.Compile(source, profile, _orders);

    [Fact]
    public void Compiles_a_row_field_compared_to_the_caller()
    {
        var result = Compile("owner_id == @user.id");

        result.IsSuccess.ShouldBeTrue();
        result.Expression!.ResultType.ShouldBe(CelValueType.Bool);
        result.Expression.EntityName.ShouldBe("orders");
    }

    [Fact]
    public void Compiles_role_membership()
    {
        Compile("'editor' in @user.roles").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void An_unknown_column_fails_at_compile_time_with_the_known_fields()
    {
        var result = Compile("ownr_id == @user.id");

        result.IsSuccess.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem();
        result.Errors[0].Message.ShouldContain("ownr_id");
        result.Errors[0].FixSuggestion.ShouldContain("owner_id");
    }

    [Fact]
    public void The_singular_user_role_is_rejected_with_the_plural_fix()
    {
        var result = Compile("@user.role == 'editor'");

        result.IsSuccess.ShouldBeFalse();
        result.Errors[0].FixSuggestion.ShouldContain("'editor' in @user.roles");
    }

    [Fact]
    public void Comparing_the_role_list_to_a_string_is_a_type_error_not_a_contains()
    {
        var result = Compile("@user.roles == 'editor'");

        result.IsSuccess.ShouldBeFalse();
        result.Errors[0].FixSuggestion.ShouldContain(" in @user.roles");
    }

    [Fact]
    public void Claims_are_rejected_and_point_at_the_rbac_issue()
    {
        var result = Compile("@user.claims['department'] == status");

        result.IsSuccess.ShouldBeFalse();
        result.Errors[0].FixSuggestion.ShouldContain("#37");
    }

    [Theory]
    [InlineData("status == 1")]
    [InlineData("total == 'x'")]
    [InlineData("owner_id < @user.id")]
    [InlineData("status && total")]
    [InlineData("payload == 'x'")]
    [InlineData("!status")]
    [InlineData("status")]
    public void Type_errors_are_reported_at_compile_time(string source)
    {
        Compile(source).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void A_rule_must_evaluate_to_a_boolean()
    {
        Compile("total").IsSuccess.ShouldBeFalse();
        Compile("true").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Every_error_carries_a_position_so_an_agent_can_point_at_the_source()
    {
        var result = Compile("owner_id == @user.id && ownr_id == @user.id");

        result.Errors[0].Position.ShouldBeGreaterThan(20);
    }
}
```

- [ ] **Step 2: Write the failing profile test**

Create `test/MMLib.Alvo.Tests/Expressions/CelProfileTests.cs` asserting the allow-list table
above: arithmetic in `Rule` fails and in `Computed` succeeds; `@user.id` in `Computed` fails
("a computed column is evaluated by the database with no caller context"); `changed(status)`
and `new.status` fail in `Rule` and succeed in `Condition`; a comprehension macro
(`fields.all(f, f > 0)`) fails in every profile with a suggestion naming
`hooks.beforeUpdate`; the Computed profile rejects `&&`.

- [ ] **Step 3: Run, expect failure; implement the checker and compiler**

Run: `dotnet test --project test/MMLib.Alvo.Tests` → FAIL.

`CelTypeChecker` walks the parsed tree once, rewriting `CelFieldRef` with the resolved
`CelValueType` and collecting errors; the unknown-field suggestion uses the closest field
name by Levenshtein distance ≤ 2, falling back to the sorted field list. `CelCompiler.Compile`
= `Tokenize → Parse → Check → profile filter → result-type check`, converting
`CelSyntaxException` into a `CelCompilationError` so no exception escapes the port.

- [ ] **Step 4: Run, ring1, commit**

```bash
git add src/MMLib.Alvo.Abstractions/Expressions src/MMLib.Alvo/Expressions test/MMLib.Alvo.Tests/Expressions test/_shared
git commit -m "feat(expressions): add the CEL type checker, profiles and ICelCompiler"
```

---

## Task 8: The in-memory backend — `WITH CHECK` semantics

`create` has no stored row to filter, so the Rule profile must also evaluate over a
candidate row. This backend defines Alvo's null semantics, and Task 9's renderer is written
to agree with it.

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Data/AlvoRecord.cs`
- Create: `src/MMLib.Alvo/Expressions/Internal/CelInterpreter.cs`
- Test: `test/MMLib.Alvo.Tests/Expressions/CelInterpreterTests.cs`

**Interfaces:**
- Produces:
  - `MMLib.Alvo.Data.AlvoRecord` — `sealed record` wrapping
    `IReadOnlyDictionary<string, object?> Values` with `object? this[string field]`,
    `bool TryGetValue(string field, out object? value)`, `static AlvoRecord Empty`, and
    `AlvoRecord With(string field, object? value)`.
  - `internal static class CelInterpreter` —
    `static bool EvaluatePredicate(CompiledExpression expression, AlvoRecord current, AlvoRecord? previous, AlvoContext context)`
    and `static object? EvaluateScalar(CompiledExpression expression, AlvoRecord current)`
    (used by Computed tests and PR6).

Documented semantics (identical to the SQL renderer's):
- A comparison with a `null` operand yields **false** (never "unknown").
- `!` applies to the already-collapsed boolean, so `!(null == x)` is `true`.
- `&&` / `||` are the CEL absorbing forms: `false && error` → `false`,
  `true || error` → `true`.
- A field absent from the record is `null` — the same as a present `null`.
- `changed(f)` is `false` when `previous` is `null` (a create changes nothing);
  otherwise it compares `previous[f]` to `current[f]` with `Equals`.
- The top-level result of a Rule/Condition expression is `false` unless it is exactly `true`.

- [ ] **Step 1: Write the failing interpreter test**

Create `test/MMLib.Alvo.Tests/Expressions/CelInterpreterTests.cs`. Cover, with a shared
`Evaluate(string source, AlvoRecord row, AlvoContext context)` helper:

```csharp
[Fact]
public void A_null_field_compares_as_false_not_unknown()
{
    Evaluate("owner_id == @user.id", Row(("owner_id", null)), Alice).ShouldBeFalse();
}

[Fact]
public void Negation_sees_the_collapsed_comparison_so_a_null_owner_is_allowed()
{
    Evaluate("!(owner_id == @user.id)", Row(("owner_id", null)), Alice).ShouldBeTrue();
}

[Fact]
public void Role_membership_reads_the_context_role_set()
{
    Evaluate("'editor' in @user.roles", AlvoRecord.Empty, Editor).ShouldBeTrue();
    Evaluate("'editor' in @user.roles", AlvoRecord.Empty, Alice).ShouldBeFalse();
}

[Fact]
public void Tenant_reference_reads_the_context_tenant()
{
    Evaluate("tenant_id == @tenant.id", Row(("tenant_id", AcmeTenant.Value)), AcmeUser).ShouldBeTrue();
    Evaluate("tenant_id == @tenant.id", Row(("tenant_id", AcmeTenant.Value)), OtherTenantUser).ShouldBeFalse();
}

[Fact]
public void A_missing_tenant_in_the_context_denies()
{
    Evaluate("tenant_id == @tenant.id", Row(("tenant_id", AcmeTenant.Value)), TenantlessUser).ShouldBeFalse();
}

[Fact]
public void Or_absorbs_a_failing_branch()
{
    Evaluate("'admin' in @user.roles || owner_id == @user.id", Row(("owner_id", null)), Admin).ShouldBeTrue();
}

[Fact]
public void Changed_is_false_on_create_and_true_at_a_transition()
{
    EvaluateCondition("changed(status)", Row(("status", "approved")), previous: null).ShouldBeFalse();
    EvaluateCondition("changed(status)", Row(("status", "approved")), Row(("status", "draft"))).ShouldBeTrue();
    EvaluateCondition("changed(status)", Row(("status", "approved")), Row(("status", "approved"))).ShouldBeFalse();
}

[Fact]
public void New_and_old_read_the_two_images()
{
    EvaluateCondition("changed(status) && new.status == 'approved'",
        Row(("status", "approved")), Row(("status", "draft"))).ShouldBeTrue();
    EvaluateCondition("old.status == 'draft'",
        Row(("status", "approved")), Row(("status", "draft"))).ShouldBeTrue();
}

[Fact]
public void Numeric_comparison_widens_int_to_decimal()
{
    Evaluate("total > 5", Row(("total", 10.5m)), Alice).ShouldBeTrue();
}
```

- [ ] **Step 2: Run, expect failure; implement `AlvoRecord` and `CelInterpreter`**

The interpreter is a `switch` over node kinds, one small method per family
(`EvaluateBinary`, `EvaluateComparison`, `EvaluateLogical`, `EvaluateUnary`,
`ResolveContext`, `ResolveField`). Comparison goes through one
`static bool Compare(object? left, object? right, CelBinaryOperator op)` that returns
`false` whenever either side is `null` — the single place the null rule is expressed.

- [ ] **Step 3: Run, ring1, commit**

```bash
git add src/MMLib.Alvo.Abstractions/Data src/MMLib.Alvo/Expressions test/MMLib.Alvo.Tests/Expressions test/_shared
git commit -m "feat(expressions): add the in-memory CEL backend for WITH CHECK"
```

---

## Task 9: The SQL backend — `IFieldSqlRenderer` and two-valued rendering

The subtlest correctness issue in the milestone. CEL is two-valued; SQL is three-valued. The
renderer collapses every subtree that can yield `UNKNOWN` so the two backends cannot
disagree — and every literal leaves as a named parameter, never as text.

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Expressions/IFieldSqlRenderer.cs`
- Create: `src/MMLib.Alvo.Abstractions/Expressions/IPredicateRenderer.cs`
- Create: `src/MMLib.Alvo.Abstractions/Expressions/SqlPredicate.cs`
- Create: `src/MMLib.Alvo/Expressions/Internal/SqlPredicateRenderer.cs`
- Create: `src/MMLib.Alvo.Testing/Expressions/TestFieldSqlRenderer.cs`
- Test: `test/MMLib.Alvo.Tests/Expressions/SqlPredicateRendererTests.cs`
- Test: `test/MMLib.Alvo.Tests/Expressions/SqlPredicateRendererSnapshotTests.cs`
- Test: `test/MMLib.Alvo.Tests/Expressions/NoInterpolationPropertyTests.cs`

**Interfaces:**
- Produces:
  - `IFieldSqlRenderer` — the driver's contract:
    - `string RenderField(EntitySchema entity, string fieldName)` — a quoted column on a
      physical entity, a JSON path (`data->>'owner_id'`) on a dynamic one (F7).
    - `string RenderParameter(string parameterName)` — `@p0`, `$1`, …
    - `string TrueLiteral { get; }` / `string FalseLiteral { get; }` — `TRUE`/`FALSE` on
      PostgreSQL, `1`/`0` on SQLite.
    - `string RenderCaseInsensitiveLike(string left, string right)` — `ILIKE` on
      PostgreSQL, `LIKE` (with the dialect's own collation caveat) on SQLite.
    - XML docs state the invariant: **the core never composes an identifier or a
      dialect keyword itself**, so a new storage driver only implements this interface.
  - `IPredicateRenderer` — `SqlPredicate Render(CompiledExpression expression, AlvoContext context, IFieldSqlRenderer fields)`.
    Documented guarantee: the returned SQL is **two-valued** — it evaluates to true or
    false, never `UNKNOWN` — and contains no value from the expression source.
  - `SqlPredicate` — `sealed record (string Sql, IReadOnlyDictionary<string, object?> Parameters)`
    with `static SqlPredicate AlwaysFalse(IFieldSqlRenderer fields)`.
  - `internal sealed class SqlPredicateRenderer : IPredicateRenderer`.
  - `MMLib.Alvo.Testing.TestFieldSqlRenderer` — `"quoted"` identifiers, `@pN` parameters,
    `TRUE`/`FALSE`, `UPPER(a) LIKE UPPER(b)`; used by core snapshots and by the differential
    test.

Rendering rules (each one is a test below):
- A comparison renders as `COALESCE(<left> <op> <right>, <FalseLiteral>)`.
- `&&` / `||` render as `(<a> AND <b>)` / `(<a> OR <b>)` over already-collapsed operands.
- `!` renders as `(NOT <collapsed>)`.
- The whole predicate is wrapped once more in
  `COALESCE(<predicate>, <FalseLiteral>)`, so a top-level `UNKNOWN` cannot survive.
- `has(field)` renders as `(<field> IS NOT NULL)` — never `UNKNOWN`, so no wrap.
- A context value renders as a **parameter** (`@user.id` → `@p0` bound to a `Guid`).
- `@user.roles` is known at render time, so `'x' in @user.roles` renders as
  `TrueLiteral`/`FalseLiteral` — the role name never reaches the SQL text.
- A `@tenant.id` reference with a `null` context tenant renders `FalseLiteral` (deny), never
  `IS NULL`.
- Every literal from the source becomes a parameter; parameter names are generated
  (`p0`, `p1`, …), never derived from the source text.

- [ ] **Step 1: Write the failing renderer test**

Create `test/MMLib.Alvo.Tests/Expressions/SqlPredicateRendererTests.cs`:

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing;

namespace MMLib.Alvo.Tests.Expressions;

public class SqlPredicateRendererTests
{
    private static readonly IFieldSqlRenderer _fields = new TestFieldSqlRenderer();
    private static readonly IPredicateRenderer _renderer = new SqlPredicateRenderer();

    private static SqlPredicate Render(string source, AlvoContext context) =>
        _renderer.Render(CelFixtures.CompileRule(source), context, _fields);

    [Fact]
    public void A_comparison_is_collapsed_so_null_reads_as_false()
    {
        var predicate = Render("owner_id == @user.id", CelFixtures.Alice);

        predicate.Sql.ShouldBe("COALESCE(COALESCE(\"owner_id\" = @p0, FALSE), FALSE)");
        predicate.Parameters["p0"].ShouldBe(CelFixtures.Alice.User.Value);
    }

    [Fact]
    public void Negation_is_rendered_over_the_collapsed_value()
    {
        Render("!(owner_id == @user.id)", CelFixtures.Alice).Sql
            .ShouldBe("COALESCE((NOT COALESCE(\"owner_id\" = @p0, FALSE)), FALSE)");
    }

    [Fact]
    public void A_string_literal_never_appears_in_the_sql_text()
    {
        var predicate = Render("status == 'approved'", CelFixtures.Alice);

        predicate.Sql.ShouldNotContain("approved");
        predicate.Parameters.Values.ShouldContain("approved");
    }

    [Fact]
    public void Role_membership_is_decided_at_render_time_so_the_role_name_stays_out_of_the_sql()
    {
        Render("'editor' in @user.roles", CelFixtures.Editor).Sql.ShouldNotContain("editor");
        Render("'editor' in @user.roles", CelFixtures.Editor).Sql.ShouldContain("TRUE");
        Render("'editor' in @user.roles", CelFixtures.Alice).Sql.ShouldContain("FALSE");
    }

    [Fact]
    public void A_tenantless_context_renders_a_denial_rather_than_an_is_null_comparison()
    {
        var predicate = Render("tenant_id == @tenant.id", CelFixtures.TenantlessAlice);

        predicate.Sql.ShouldNotContain("IS NULL");
        predicate.Sql.ShouldBe("COALESCE(FALSE, FALSE)");
        predicate.Parameters.ShouldBeEmpty();
    }

    [Fact]
    public void Presence_tests_are_already_two_valued()
    {
        Render("has(owner_id)", CelFixtures.Alice).Sql
            .ShouldBe("COALESCE((\"owner_id\" IS NOT NULL), FALSE)");
    }

    [Fact]
    public void Parameter_names_are_generated_not_taken_from_the_source()
    {
        var predicate = Render("status == 'p0' && owner_id == @user.id", CelFixtures.Alice);

        predicate.Parameters.Keys.ShouldBe(["p0", "p1"], ignoreOrder: true);
        predicate.Parameters["p0"].ShouldBe("p0");
        predicate.Parameters["p1"].ShouldBe(CelFixtures.Alice.User.Value);
    }
}
```

Create `test/MMLib.Alvo.Tests/Expressions/CelFixtures.cs` alongside it: the `_orders`
`EntitySchema` from Task 7, `CompileRule` / `CompileCondition` / `CompileComputed` helpers
that throw with the joined error messages when compilation fails, and the named contexts
(`Alice`, `Bob`, `Editor`, `Admin`, `AcmeUser`, `OtherTenantUser`, `TenantlessAlice`).
Reuse it from Tasks 8, 10, 11 and 12 instead of re-declaring fixtures.

- [ ] **Step 2: Run, expect failure; implement the renderer**

Run: `dotnet test --project test/MMLib.Alvo.Tests` → FAIL. Implement `SqlPredicateRenderer`
with one private method per node family and a `ParameterBag` inner type owning name
generation. Assert the exact SQL strings from the test — if you prefer different but
equivalent SQL, update the test deliberately, never loosen it to `ShouldContain`.

- [ ] **Step 3: Write the golden snapshot test**

Create `test/MMLib.Alvo.Tests/Expressions/SqlPredicateRendererSnapshotTests.cs` — one
`[Fact]` that renders a table of ~12 representative rules (each operator, nesting,
negation, `has`, role membership, tenant scope, a mixed `&&`/`||` tree) and `Verify`s the
`{ source, sql, parameters }` list under a stable file name
(`.UseFileName("cel-to-sql-core")`). This is the artifact PR2's per-engine snapshots are
compared against; when it moves, `alvo-snapshot-judge` decides.

- [ ] **Step 4: Write the no-interpolation property test**

Create `test/MMLib.Alvo.Tests/Expressions/NoInterpolationPropertyTests.cs`:

```csharp
using CsCheck;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Testing;

namespace MMLib.Alvo.Tests.Expressions;

public class NoInterpolationPropertyTests
{
    private static readonly Gen<string> _literals =
        Gen.Char["abcXYZ01_ '\"%;-()"].Array[1, 12].Select(characters => new string(characters));

    [Fact]
    public void No_literal_from_a_rule_ever_appears_in_the_rendered_sql()
    {
        _literals.Sample(literal =>
        {
            var escaped = literal.Replace("'", "''", StringComparison.Ordinal);
            var result = CelFixtures.Compiler.Compile(
                $"status == '{escaped}'", CelProfile.Rule, CelFixtures.Orders);
            if (!result.IsSuccess)
            {
                return true;
            }

            var predicate = new SqlPredicateRenderer()
                .Render(result.Expression!, CelFixtures.Alice, new TestFieldSqlRenderer());

            return !predicate.Sql.Contains(literal, StringComparison.Ordinal)
                && predicate.Parameters.Values.Contains(literal);
        },
        iter: 10_000);
    }

    [Fact]
    public void An_injection_attempt_through_every_operator_stays_inside_a_parameter()
    {
        const string Payload = "x'; DROP TABLE orders; --";
        var operators = new[] { "==", "!=", "<", "<=", ">", ">=" };

        foreach (var op in operators)
        {
            var result = CelFixtures.Compiler.Compile(
                $"status {op} '{Payload.Replace("'", "''", StringComparison.Ordinal)}'",
                CelProfile.Rule,
                CelFixtures.Orders);
            if (!result.IsSuccess)
            {
                continue;
            }

            var predicate = new SqlPredicateRenderer()
                .Render(result.Expression!, CelFixtures.Alice, new TestFieldSqlRenderer());

            predicate.Sql.ShouldNotContain("DROP", Case.Insensitive);
            predicate.Parameters.Values.ShouldContain(Payload);
        }
    }
}
```

Note the ordering-comparison arms on an `enum`-typed field will fail type-checking — that is
why the property skips unsuccessful compilations. Add a second field (`title`, `String`) to
`CelFixtures.Orders` so at least the relational operators do compile and are genuinely
exercised.

- [ ] **Step 5: Run everything, accept the new baselines, commit**

Run: `scripts/test-ring1`, accept `cel-to-sql-core.verified.txt` and the two public-API
baselines (`MMLib.Alvo.Abstractions`, `MMLib.Alvo.Testing`), dispatch
`alvo-snapshot-judge` when the turn gate fires.

```bash
git add src/MMLib.Alvo.Abstractions/Expressions src/MMLib.Alvo/Expressions src/MMLib.Alvo.Testing test/MMLib.Alvo.Tests/Expressions test/_shared
git commit -m "feat(expressions): render two-valued SQL predicates behind IFieldSqlRenderer"
```

---

## Task 10: The differential test — the two backends cannot disagree

The proof obligation for the null-semantics decision: for the same rule and the same row,
the SQL predicate and the in-memory delegate return the same verdict. PR1 evaluates the
rendered SQL with a tiny in-process evaluator over the parameter bag; PR2 replays the same
harness against real SQLite and PostgreSQL.

**Files:**
- Create: `src/MMLib.Alvo.Testing/Expressions/DifferentialRuleCases.cs`
- Test: `test/MMLib.Alvo.Tests/Expressions/DifferentialBackendTests.cs`

**Interfaces:**
- Produces: `MMLib.Alvo.Testing.DifferentialRuleCases` —
  `static IReadOnlyList<DifferentialRuleCase> All { get; }` where
  `DifferentialRuleCase` is `sealed record (string Rule, AlvoRecord Row, string ContextName)`;
  plus `static AlvoContext ContextFor(string name)` so PR2 can drive the identical matrix
  from its provider test projects without duplicating it.

Cases must include, at minimum: a nullable field equal / unequal / null under `==`, `!=`,
`!( … )`, `&&`, `||`, `has`, role membership, tenant match/mismatch/absent, and a nested
`(a || b) && !c` tree — 20+ combinations.

- [ ] **Step 1: Write the differential test**

`test/MMLib.Alvo.Tests/Expressions/DifferentialBackendTests.cs`:

```csharp
[Theory]
[MemberData(nameof(Cases))]
public void The_sql_predicate_and_the_in_memory_delegate_agree(string rule, string contextName, int caseIndex)
{
    var compiled = CelFixtures.CompileRule(rule);
    var context = DifferentialRuleCases.ContextFor(contextName);
    var row = DifferentialRuleCases.All[caseIndex].Row;

    var inMemory = CelInterpreter.EvaluatePredicate(compiled, row, previous: null, context);
    var viaSql = SqlVerdict.Evaluate(
        new SqlPredicateRenderer().Render(compiled, context, new TestFieldSqlRenderer()), row);

    viaSql.ShouldBe(inMemory, $"rule '{rule}' disagreed between the SQL and in-memory backends");
}
```

Implement `SqlVerdict` as a test-local minimal evaluator: it parses the rendered SQL grammar
the renderer itself emits (`COALESCE`, `AND`, `OR`, `NOT`, `IS NOT NULL`, comparisons,
`TRUE`/`FALSE`, `@pN`, quoted identifiers) with three-valued semantics, so it faithfully
models a database rather than reusing `CelInterpreter` (which would make the test tautological).
Keep it in `test/MMLib.Alvo.Tests/Expressions/SqlVerdict.cs`, ~120 lines, and assert its own
three-valued behaviour in two unit tests (`NULL = 1` → unknown; `COALESCE(unknown, FALSE)` →
false) so a bug in the harness cannot silently pass the differential test.

- [ ] **Step 2: Add a CsCheck arm**

A generated-rule arm: build random rule trees from the fixture's fields and operators, and
random rows with a ~30% chance of `null` per field; assert agreement over 5,000 samples.
This is where a missed collapse actually gets caught.

- [ ] **Step 3: Run, commit**

```bash
git add src/MMLib.Alvo.Testing/Expressions test/MMLib.Alvo.Tests/Expressions test/_shared
git commit -m "test(expressions): prove the SQL and in-memory backends never disagree"
```

---

## Task 11: `IPolicyEngine` — default-deny, USING/WITH CHECK, tenant scope, field masks

Where the descriptor's five nullable rule strings become an enforceable decision. The
Postgres `CREATE POLICY` mapping is adopted verbatim: `update` reuses one expression for
both `USING` and `WITH CHECK`, which is what stops a caller from moving a row out of their
own scope.

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Rules/PolicyDecision.cs`
- Create: `src/MMLib.Alvo.Abstractions/Rules/IPolicyEngine.cs`
- Create: `src/MMLib.Alvo/Rules/PolicyCatalog.cs`
- Create: `src/MMLib.Alvo/Rules/Internal/PolicyCatalogBuilder.cs`
- Create: `src/MMLib.Alvo/Rules/Internal/PolicyEngine.cs`
- Create: `src/MMLib.Alvo/Rules/Setup.cs`
- Create: `src/MMLib.Alvo.Testing/Rules/PolicyEngineContractTests.cs`
- Test: `test/MMLib.Alvo.Tests/Rules/PolicyCatalogBuilderTests.cs`
- Test: `test/MMLib.Alvo.Tests/Rules/PolicyEngineTests.cs`

**Interfaces:**
- Consumes: `AlvoDescriptor`, `EntityDescriptor.Rules` (`AccessRules`), `FieldDescriptor.Hidden`
  / `ReadOnly` (`BoolOrCel`), `SchemaModel`, `ICelCompiler`, `AlvoContext`, `DataOperation`.
- Produces:
  - `PolicyDecision` — `sealed record`:
    `bool IsDenied`, `CompiledExpression? Using`, `CompiledExpression? WithCheck`,
    `CompiledExpression? TenantScope`, `IReadOnlySet<string> HiddenFields`,
    `IReadOnlySet<string> ReadOnlyFields`, `string? DenyReason`;
    `static PolicyDecision Deny(string reason)`.
  - `IPolicyEngine` — `PolicyDecision Resolve(string entity, DataOperation operation, AlvoContext context)`.
  - `PolicyCatalog` — built once from a descriptor + schema; holds, per
    `(entity, operation)`, the compiled `Using`/`WithCheck`, the synthesized tenant scope,
    and the compiled `hidden`/`readOnly` expressions.
    `static PolicyCatalog Build(AlvoDescriptor descriptor, SchemaModel schema, ICelCompiler compiler)`
    throwing `DescriptorValidationException` when any rule fails to compile;
    `static bool TryBuild(…, out PolicyCatalog?, out IReadOnlyList<DescriptorValidationError>)`
    for the validator path (Task 13).
  - `internal sealed class PolicyEngine : IPolicyEngine`.

Semantics (each one a test):
- No `rules` block at all, or a `null` for the requested operation → **deny**.
- `"true"` → allow with a constant-true predicate (not a `null` predicate — `null` must
  never be readable as "no filter").
- `list`/`get`/`delete` → `Using` only. `create` → `WithCheck` only. `update` → **both,
  from the same source**.
- A tenant-scoped entity (`EntitySchema.Tenancy == TenancyMode.Scoped`) gets a synthesized
  `TenantScope` = `CelBinary(Equal, CelFieldRef("tenant_id", Uuid, Current), CelContextRef(TenantId, Uuid))`,
  and a `context.Tenant is null` → **deny before any rule is consulted**, with
  `DenyReason` naming the missing tenant.
- A global entity ignores the tenant entirely (a tenantless context is fine).
- `hidden` / `readOnly`: `true` → always in the mask; `false`/absent → never; a CEL
  expression → compiled in the Rule profile, **rejected at build if it references a row
  field**, and evaluated per request against the context only.
- Unknown entity → deny (never throw — an unknown entity must not be distinguishable from
  an unauthorized one at this layer).

- [ ] **Step 1: Write the failing catalog-builder test**

`test/MMLib.Alvo.Tests/Rules/PolicyCatalogBuilderTests.cs` — cover: a rule referencing an
unknown column fails the build with a `DescriptorValidationError` whose path is
`/entities/orders/rules/list`; a row-dependent `hidden` fails with a fix suggestion naming
the deferral; `@user.role` fails with the plural fix; a valid descriptor builds and exposes
`update`'s `Using` and `WithCheck` as the *same* `Source`.

- [ ] **Step 2: Write the failing engine test**

`test/MMLib.Alvo.Tests/Rules/PolicyEngineTests.cs` — cover every semantic bullet above.
Two that must not be forgotten:

```csharp
[Fact]
public void A_scoped_entity_denies_a_context_with_no_tenant_before_any_rule_is_consulted()
{
    var decision = Engine("""{"list": "true"}""").Resolve("orders", DataOperation.List, CelFixtures.TenantlessAlice);

    decision.IsDenied.ShouldBeTrue();
    decision.DenyReason.ShouldContain("tenant");
}

[Fact]
public void Update_reuses_one_expression_for_both_using_and_with_check()
{
    var decision = Engine("""{"update": "owner_id == @user.id"}""")
        .Resolve("orders", DataOperation.Update, CelFixtures.Alice);

    decision.Using!.Source.ShouldBe("owner_id == @user.id");
    decision.WithCheck!.Source.ShouldBe(decision.Using.Source);
}
```

- [ ] **Step 3: Implement the catalog, builder and engine**

Keep `PolicyCatalogBuilder` as one method per concern (`CompileRules`, `CompileFieldFlags`,
`SynthesizeTenantScope`, `Error`). `PolicyEngine.Resolve` reads as: look up the entity →
tenant guard → operation lookup → assemble the decision.

- [ ] **Step 4: Add the abstract `IPolicyEngine` contract suite**

`src/MMLib.Alvo.Testing/Rules/PolicyEngineContractTests.cs` — an abstract class with an
abstract `IPolicyEngine CreateEngine(AlvoDescriptor descriptor, SchemaModel schema)` and the
operation-mapping / default-deny / tenant-guard facts, so any future engine implementation
(F7's dynamic-entity path) inherits the same judgment. Subclass it once in
`test/MMLib.Alvo.Tests/Rules/PolicyEngineContractTestsOverCatalog.cs`.

- [ ] **Step 5: Wire `Rules/Setup.cs`, run ring1, commit**

```bash
git add src/MMLib.Alvo.Abstractions/Rules src/MMLib.Alvo/Rules src/MMLib.Alvo.Testing/Rules test/MMLib.Alvo.Tests/Rules test/_shared
git commit -m "feat(rules): add the policy engine with default-deny and tenant scoping"
```

---

## Task 12: The data port and the adversarial suite

The suite is the milestone's security judgment, so it is written against the port before any
storage exists, in a shape PR2 can subclass unchanged. `InMemoryAlvoData` is a reference
implementation, not a shortcut: it applies the policy predicate through the in-memory
backend for every row, which is what "policy is enforced inside `IAlvoData`" means when the
store *is* memory.

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Data/AlvoQuery.cs`, `Data/AlvoFilter.cs`
- Create: `src/MMLib.Alvo.Abstractions/Data/IAlvoData.cs`
- Create: `src/MMLib.Alvo.Abstractions/Data/AlvoAuthorizationException.cs`
- Create: `src/MMLib.Alvo.Abstractions/Data/AlvoRecordNotFoundException.cs`
- Create: `src/MMLib.Alvo.Testing/Data/InMemoryAlvoData.cs`
- Create: `src/MMLib.Alvo.Testing/Data/AlvoDataAdversarialTests.cs`
- Test: `test/MMLib.Alvo.Tests/Data/InMemoryAlvoDataAdversarialTests.cs`

**Interfaces:**
- Produces:
  - `AlvoQuery` — `sealed record` `{ required string Entity; AlvoFilter? Filter; IReadOnlyList<AlvoSort> Sort = []; int? Limit; string? After; }`.
    XML doc records that projection, relation embedding, aggregates and bulk are modelled in
    PR3 and that adding them must stay additive (§2.1: a bad query language cannot be fixed
    without a breaking change).
  - `AlvoFilter` — abstract record with `AlvoComparison(string Field, AlvoFilterOperator Operator, object? Value)`,
    `AlvoAnd(IReadOnlyList<AlvoFilter>)`, `AlvoOr(IReadOnlyList<AlvoFilter>)`,
    `AlvoNot(AlvoFilter)`; `AlvoFilterOperator` = `Eq, Neq, Gt, Gte, Lt, Lte, Like, ILike, In, Is`
    (PostgREST names); `AlvoSort(string Field, bool Descending, AlvoNullPlacement Nulls)`.
  - `IAlvoData` — every member takes `AlvoContext context` as a required parameter:
    `Task<IReadOnlyList<AlvoRecord>> QueryAsync(AlvoQuery query, AlvoContext context, CancellationToken cancellationToken = default)`,
    `Task<AlvoRecord?> GetAsync(string entity, Guid id, AlvoContext context, CancellationToken cancellationToken = default)`,
    `Task<AlvoRecord> CreateAsync(string entity, IReadOnlyDictionary<string, object?> values, AlvoContext context, CancellationToken cancellationToken = default)`,
    `Task<AlvoRecord> UpdateAsync(string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext context, CancellationToken cancellationToken = default)`,
    `Task DeleteAsync(string entity, Guid id, AlvoContext context, CancellationToken cancellationToken = default)`.
    Documented failure contract, chosen so nothing leaks the existence of an invisible row:
    a row excluded by `USING` is **indistinguishable from absent** (`GetAsync` → `null`,
    `UpdateAsync`/`DeleteAsync` → `AlvoRecordNotFoundException`), while a denied operation or
    a failing `WITH CHECK` throws `AlvoAuthorizationException`.
  - `MMLib.Alvo.Testing.InMemoryAlvoData` — ctor
    `(IPolicyEngine policy, SchemaModel schema)`, `Seed(string entity, params AlvoRecord[] rows)`.
  - `MMLib.Alvo.Testing.AlvoDataAdversarialTests` — abstract, with
    `protected abstract Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed)`.

The suite's facts (all of them, written out — this is the list PR2 must satisfy on both
engines):

1. `List_returns_only_the_callers_own_rows` — Alice sees her two rows, not Bob's.
2. `Get_of_another_users_row_is_indistinguishable_from_absent` — `null`, not a 403.
3. `Update_of_another_users_row_reports_not_found` — `AlvoRecordNotFoundException`.
4. `Delete_of_another_users_row_reports_not_found_and_does_not_delete` — the row survives,
   verified as the owner afterwards.
5. `Create_that_would_place_the_row_outside_the_callers_scope_is_denied` —
   `owner_id == @user.id` with a payload naming Bob → `AlvoAuthorizationException`.
6. `Update_cannot_move_a_row_out_of_the_callers_scope` — the `WITH CHECK` half; Alice
   updating her own row to `owner_id = Bob` is denied and the stored row is unchanged.
7. `An_entity_with_no_rule_denies_every_operation` — all five operations throw.
8. `An_operation_with_no_rule_denies_while_its_siblings_work` — `list` allowed, `delete`
   denied on the same entity.
9. `A_tenant_scoped_entity_never_returns_another_tenants_rows` — Acme's caller cannot see
   Globex's rows even with a permissive `"true"` rule.
10. `A_query_with_no_tenant_context_fails_rather_than_returning_every_tenants_rows` — the
    §4 acceptance criterion, asserted as a throw plus "and returned no rows".
11. `A_tenantless_context_cannot_create_into_a_scoped_entity`.
12. `Cross_tenant_get_by_id_is_indistinguishable_from_absent` — no id-probing oracle.
13. `A_hidden_field_never_appears_in_a_returned_record` — for `hidden: true` and for a
    context-conditional `hidden`.
14. `A_write_to_a_read_only_field_is_rejected_rather_than_silently_dropped`.
15. `An_admin_rule_over_the_role_set_matches_a_multi_role_caller` — the regression that
    justifies `@user.roles`: a caller holding `{authenticated, admin}` satisfies both
    `'admin' in @user.roles` and `'authenticated' in @user.roles`.
16. `A_user_filter_cannot_widen_the_policy_predicate` — a caller-supplied
    `owner_id = <Bob>` filter returns nothing rather than Bob's rows.

- [ ] **Step 1: Write the ports**

Create the `Data/*` files above with full XML docs, then run
`dotnet build` — it must compile with no implementation.

- [ ] **Step 2: Write the adversarial suite (abstract) and the concrete subclass**

Write `AlvoDataAdversarialTests` with all 16 facts and
`test/MMLib.Alvo.Tests/Data/InMemoryAlvoDataAdversarialTests.cs` deriving from it and
constructing `InMemoryAlvoData`.

- [ ] **Step 3: Run and see it red for the right reason**

Run: `dotnet test --project test/MMLib.Alvo.Tests`
Expected: 16 failures, all `CS0246`/`NotImplementedException` from the missing
`InMemoryAlvoData` — **read the failure list and confirm each fact fails because the
implementation is absent, not because the test is wrong.** Record the count in the commit
message.

- [ ] **Step 4: Implement `InMemoryAlvoData`**

Per operation: `ScopeGate` is *not* consulted here (it belongs to the HTTP layer);
`IPolicyEngine.Resolve` first; deny → `AlvoAuthorizationException`; `Using` +
`TenantScope` + the query filter are all evaluated per row via `CelInterpreter` (filters
through a small `AlvoFilterEvaluator`); `WithCheck` is evaluated over the **post-image**
(existing values merged with the payload); `HiddenFields` are stripped from every returned
record; a payload touching a `ReadOnlyFields` member throws `AlvoAuthorizationException`
with a message naming the field.

- [ ] **Step 5: Run until green, then ring1**

Run: `dotnet test --project test/MMLib.Alvo.Tests` → 16 PASS. Then `scripts/test-ring1`,
accept the `MMLib.Alvo.Abstractions` + `MMLib.Alvo.Testing` baselines.

- [ ] **Step 6: Commit**

```bash
git add src/MMLib.Alvo.Abstractions/Data src/MMLib.Alvo.Testing/Data test/MMLib.Alvo.Tests/Data test/_shared
git commit -m "feat(data): add the IAlvoData port and the adversarial policy suite"
```

---

## Task 13: Apply-time integration — rules fail at save, and `@user.roles` in the schema

The last enforcement gap: a bad rule must be refused when the descriptor is applied, not
when a request arrives. This wires `PolicyCatalog.TryBuild` into the existing validator and
updates the two schema description strings that still say `@user.role`.

**Files:**
- Modify: `src/MMLib.Alvo/Descriptor/Internal/DescriptorValidator.cs`
- Modify: `schema/project.schema.json:145,689`
- Modify: `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs` (register `RoleCatalog` + `PolicyCatalog`)
- Test: `test/MMLib.Alvo.Tests/Descriptor/DescriptorValidatorTests.cs` (extend)
- Test: `test/MMLib.Alvo.Schema.Tests/*` (baselines may move)

**Interfaces:**
- Consumes: `PolicyCatalog.TryBuild`, `DescriptorToSchemaMapper` (to get the `SchemaModel`
  the rules type-check against).
- Produces: `DescriptorValidator` gains a rule-compilation pass whose findings are
  `DescriptorValidationError`s at `/entities/<entity>/rules/<operation>`,
  `/entities/<entity>/fields/<field>/hidden` and `…/readOnly`.

- [ ] **Step 1: Write the failing validator tests**

Extend `test/MMLib.Alvo.Tests/Descriptor/DescriptorValidatorTests.cs`:

```csharp
[Fact]
public void A_rule_referencing_an_unknown_column_fails_validation_not_the_request()
{
    var result = Validate(DescriptorWithRule("list", "ownr_id == @user.id"));

    result.IsValid.ShouldBeFalse();
    result.Errors.ShouldContain(error =>
        error.Path == "/entities/orders/rules/list" && error.Message.Contains("ownr_id"));
}

[Fact]
public void The_singular_user_role_is_refused_at_apply_with_the_plural_fix()
{
    var result = Validate(DescriptorWithRule("list", "@user.role == 'admin'"));

    result.Errors.ShouldContain(error => error.FixSuggestion!.Contains("in @user.roles"));
}

[Fact]
public void A_row_dependent_hidden_expression_is_refused_at_apply()
{
    var result = Validate(DescriptorWithHidden("owner_id != @user.id"));

    result.Errors.ShouldContain(error =>
        error.Path == "/entities/orders/fields/notes/hidden");
}

[Fact]
public void A_context_only_hidden_expression_is_accepted()
{
    Validate(DescriptorWithHidden("'compliance' in @user.roles")).IsValid.ShouldBeTrue();
}

[Fact]
public void A_valid_rule_set_still_validates()
{
    Validate(DescriptorWithRule("list", "owner_id == @user.id")).IsValid.ShouldBeTrue();
}
```

- [ ] **Step 2: Run, expect failure; implement the pass**

The pass runs only when the schema pass produced no errors (a malformed descriptor cannot be
mapped). Map the descriptor with `DescriptorToSchemaMapper`, call `PolicyCatalog.TryBuild`,
and append its errors. Catch nothing broad — a mapper `InvalidDataException` (e.g. today's
`computed` rejection) is already reported by the semantic pass, so guard on it explicitly.

- [ ] **Step 3: Update the two schema description strings**

`schema/project.schema.json:145` — `"… Referenced in CEL via @user.roles (a set; test membership with 'editor' in @user.roles)."`
`schema/project.schema.json:689` — the example becomes `"!('compliance' in @user.roles)"`.

Run: `dotnet test --project test/MMLib.Alvo.Schema.Tests`. If a Verify baseline moved
(canonical example / negative-error output), inspect the diff — only these two description
strings may change — accept it, and let `alvo-snapshot-judge` review.

- [ ] **Step 4: Register the catalogs**

In `AddAlvo`: `services.TryAddSingleton(RoleCatalog.BuiltInOnly)` stays as the fallback, and
add a `TryAddSingleton<PolicyCatalog>` factory that resolves the descriptor source +
mapper + compiler. If the descriptor is not available at registration time (the
`FromDescriptor` chicken/egg the existing `TODO(#19)` names), register a lazily-built
`PolicyCatalogProvider` instead and note it for PR3 — do **not** silently register an empty
catalog, which would read as "no rules" and therefore deny everything at runtime with a
confusing message. Whichever shape you pick, add a test asserting that resolving
`IPolicyEngine` from a fully configured container yields an engine over the real descriptor.

- [ ] **Step 5: ring2, commit**

```bash
git add src/MMLib.Alvo schema/project.schema.json test/MMLib.Alvo.Tests test/MMLib.Alvo.Schema.Tests test/_shared
git commit -m "feat(rules): compile rules at apply and switch the schema prose to @user.roles"
```

---

## Task 14: Wiring, hardening and the PR gate

**Files:**
- Modify: `stryker-config.json`
- Create: `docs/architecture/cel.md`
- Modify: `test/MMLib.Alvo.Abstractions.Tests/ArchitectureTests.cs`
- Test: `test/MMLib.Alvo.Tests/AlvoServiceCollectionExtensionsTests.cs` (extend or create)

- [ ] **Step 1: Add the architecture rules that keep the seam honest**

Two new facts in `test/MMLib.Alvo.Abstractions.Tests/ArchitectureTests.cs`:

```csharp
[Fact]
public void Abstractions_stays_free_of_asp_net_and_data_access()
{
    var referenced = typeof(AlvoContext).Assembly.GetReferencedAssemblies().Select(name => name.Name!);

    referenced.ShouldNotContain(name =>
        name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
        || name.StartsWith("Npgsql", StringComparison.Ordinal)
        || name.StartsWith("System.Data", StringComparison.Ordinal));
}

[Fact]
public void No_type_outside_the_renderer_composes_sql()
{
    var offenders = Types.InAssembly(typeof(AlvoContext).Assembly)
        .That().ResideInNamespace("MMLib.Alvo.Expressions")
        .Should().NotHaveDependencyOn("System.Text.StringBuilder")
        .GetResult();

    offenders.IsSuccessful.ShouldBeTrue();
}
```

The second fact is a weak proxy — replace it with a real one: a test in
`test/MMLib.Alvo.Tests` that greps `src/MMLib.Alvo/Expressions` and `src/MMLib.Alvo/Rules`
(via `RepositoryRoot`) for the tokens `SELECT `, `WHERE `, `ILIKE`, `COALESCE(` and asserts
the only file containing them is `Internal/SqlPredicateRenderer.cs`. That is the invariant
worth freezing: SQL text exists in exactly one core file.

- [ ] **Step 2: Update `stryker-config.json`**

Add the three wiring files to `mutate` exclusions:
`"!**/Expressions/Setup.cs"`, `"!**/Rules/Setup.cs"`, `"!**/Auth/Setup.cs"`.
Leave everything else in — `Expressions` and `Rules` must be mutated.

- [ ] **Step 3: Write `docs/architecture/cel.md`**

One page: the three profiles and their allow-lists (the table from Task 7), the
`USING`/`WITH CHECK` mapping table, the two-valued rendering rule with the
`!(owner_id == @user.id)` worked example, the `IFieldSqlRenderer` seam and why it exists
(F7 dynamic entities + dialect), and what a new storage driver must implement. Link it from
`docs/architecture/` siblings if they carry an index.

- [ ] **Step 4: Full local gate**

Run, in order, and fix anything that fires:

```bash
dotnet format --verify-no-changes
scripts/test-ring2
```

- [ ] **Step 5: Reviews before the PR**

1. `/code-review high` (large, security-relevant diff) — fix findings.
2. `/security-review` **with** the `alvo-security-core-review` checklist — this PR *is* the
   security core. Pay attention to: parameterization on every path, the two-valued collapse
   on every boolean node, default-deny on every early return, constant-time key comparison,
   and whether any error message leaks whether a row exists.
3. Dispatch `alvo-plan-guard` — drift from `docs/PLAN.md`, §0 principle violations,
   shortcuts in the security core.
4. Fix everything the three raise, re-run `scripts/test-ring2`.

- [ ] **Step 6: Open the PR**

```bash
git push -u origin f3/pr1-security-core
gh pr create --title "feat(f3): security core — caller context, CEL compiler, policy engine" --body-file <(cat <<'BODY'
Closes #74.

First of six F3 PRs. Delivers the unbypassable half of the vertical slice so the data port
in PR2 is born with policy inside it: strongly-typed caller context and role set, scoped
dev-auth keys, one CEL parser with three profiles compiling to both a two-valued SQL
predicate and an in-memory delegate, `IPolicyEngine` with default-deny and tenant scoping,
and the adversarial suite as the abstract judgment PR2 must satisfy on SQLite and PostgreSQL.

Design: `docs/superpowers/specs/2026-07-25-f3-crud-vertical-slice-design.md`
Plan: `docs/superpowers/plans/2026-07-25-f3-pr1-security-core.md`

Security core → reviewed with `/security-review` + `alvo-security-core-review`; run the
mutation workflow via `workflow_dispatch` before merging (mutation is post-merge on `main`).
BODY
)
```

- [ ] **Step 7: Trigger the mutation run and report**

Run: `gh workflow run mutation.yml --ref f3/pr1-security-core` (if the workflow supports a
ref input; otherwise note in the PR that it must be dispatched after merge). Report the
score in a PR comment.

---

## Self-review checklist (run before declaring the plan done)

- Spec coverage: caller context (T1–T3), dev auth + scopes (T4), tenant resolution (T4, T11),
  CEL parser/AST/checker (T5–T7), three profiles (T7), both Rule backends (T8, T9),
  `IFieldSqlRenderer` contract (T9), two-valued rendering (T9, T10), `IPolicyEngine` (T11),
  the two schema strings (T13), adversarial suite (T12). Every PR1 item in the spec's split
  table maps to a task.
- Deferred **on purpose**, with owners: ASP.NET binding of dev auth → PR3; per-engine
  renderers and golden per-engine SQL → PR2; `AlvoQuery` projection/embedding/aggregates →
  PR3; service-role bypass → #42; typed claims and `@user.teams` → #37.
- Type consistency: `CompiledExpression`, `SqlPredicate`, `PolicyDecision`, `AlvoRecord`,
  `AlvoContext`, `Role`, `ApiKeyScope`, `DataOperation` are declared once (T1–T3, T7, T9,
  T11, T12) and referenced by those exact names everywhere else.
