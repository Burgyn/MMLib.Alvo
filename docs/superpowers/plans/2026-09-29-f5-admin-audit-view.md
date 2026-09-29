# F5 admin: an entity's audit, seen in the dashboard (#290): implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** on an audited entity, the Data grid shows who changed each row last and when (a *Changed* column), can be
narrowed by change time and by person (§3.9 facets), and the record sheet shows *Created* and *Last changed*. The
Schema header says what `audit` does. People are named by address to Admins only, through the guarded membership
port.

**Architecture:** presentation lives in `MMLib.Alvo.Admin` as `internal` helpers under `Components/Data` (pure,
unit-tested), wired into `EntityData`, `RecordGrid` and `RecordForm`. Every row still comes from `IAlvoData` with the
operator's own context, and every facet is an `AlvoQuery` the Data API would answer the same way. One port member
is added: `IAlvoUserAdministration.FindAsync`, a batch lookup of people by id. It is implemented in
`MMLib.Alvo.Identity`, guarded in `MMLib.Alvo` (`ManageUsers`, Admin, at most 50 ids), and routed over HTTP. It is held
by the contract suite in `MMLib.Alvo.Testing`.

**Tech Stack:** .NET 10, Blazor Web App (server-interactive) on MudBlazor 9.10.0, EF Core (SQLite, PostgreSQL),
xUnit v3 on MTP, Shouldly, NSubstitute, Microsoft.Playwright.

**Spec:** `docs/superpowers/specs/2026-09-29-f5-admin-audit-view-design.md` (approved 2026-09-29). Its **§10
Rulings** and **Corrections C1–C9** bind: where a correction and the body of the design disagree, the correction
wins. The binding pattern language is `docs/superpowers/specs/2026-09-24-f5-admin-mudblazor-design.md` §3. D3 there
says a new pattern is written into §3 before it is built, so Task 1 comes first.

## Global Constraints

- **Rows only through `IAlvoData`** with the operator's own `AlvoContext` (`DataGateway`). No read of `alvo_outbox`,
  no `IAlvoUserStore` call from the dashboard (design §2.3, §3).
- **People only through the guarded `IAlvoUserAdministration`**, via `ManagementGateway`. Addresses are shown to
  Admin only. That rule is the core's (`ManageUsers`), never a check in the screen (ruling Q1, C3).
- **The new port member, exactly:**
  `Task<IReadOnlyList<AlvoUser>> FindAsync(IReadOnlyCollection<UserId> users, CancellationToken cancellationToken = default);`
  At most **50** distinct ids per call (more is a `ManagementRequestException`). Duplicates are answered once. An
  unknown id is absent. Order is not promised. A disabled person and the bootstrap administrator are returned like
  anyone.
- **Its route, exactly:** `GET {m}/projects/{project}/users/by-id?id=<uuid>&id=<uuid>`, gated at
  `ManagementOperation.ManageUsers`, excluded from the OpenAPI document like every management route (C1).
- **Public API:** `PublicApi.MMLib.Alvo.Abstractions.verified.txt` grows by exactly that one member (design §5:
  `internal` is impossible, because the implementer is in another package). `PublicApi.MMLib.Alvo.Testing.verified.txt`
  grows by the new contract facts and one paging fact (C2). Nothing else may appear in any baseline. Every new Admin
  type is `internal`. When the Stop hook's `turn-review-gate` fires on a grown baseline, justify each symbol against
  `alvo-architecture-rules` *"public is the contract"* and, where it asks, let `alvo-snapshot-judge` rule.
- **No change** to `IAlvoData`, `IAlvoManagement`, `IOutboxStore`, `IApiKeyStore`, `schema/project.schema.json`, either
  EF driver, or any index (ruling Q6).
- **Copy, verbatim** (all in `AuditWords`, Task 4):
  - column header `Changed`; record section `Record`, pairs `Created` and `Last changed`
  - record note `Alvo keeps who made the last change and when. Earlier versions of this record are not kept.`
  - actors `no identity` (title `Written by an anonymous caller, or before the column was filled`), `Alvo (system)`
    (title `Written by the framework: an automation, hook or rollup`), `you`
  - hint `Not a dashboard account: an API key's identity or an external caller.`
  - windows `Any time`, `Last 24 hours`, `Last 7 days`, `Last 30 days`; toggle `By me`; chip `Changed by {who}`,
    remove button `Remove the filter Changed by {who}`; group label `Narrow by change`
  - empty `Nothing changed in the last 24 hours` / `… in the last 7 days` / `… in the last 30 days` /
    `Nothing changed by you` / `Nothing changed by {who}` / `Nothing changed without an identity`, body
    `The search and the other filters still apply.`, actions `Show any time` / `Anyone`
  - people note `People cannot be looked up on this deployment, so changes show the caller's id.`
  - not audited `{entity} is not audited, so it keeps no record of who created or changed a row. Audit is chosen when an entity is created.`
  - badge title `Records who created each record and who changed it last, and when. Keeps no earlier versions.`
  - hidden `hidden from you`; person link label `Show only changes by {who}`; Schema button `Recently changed`
  - **A10:** no UI string says `audit log` or `history`, and `version` appears only in a sentence saying versions
    are not kept.
- **Time:** only the *Changed* column and the *Record* block use `OperatorTime.Clock` with `AdminInterop.UtcOffset`
  (ruling Q3). The facet window is taken from `TimeProvider` when the page is read, and truncated to whole seconds so
  the port filter and its `/api` query string are the same instant.
- **Default order stays `created_at desc`** (ruling Q7). `updated_by` is never a sort key.
- **No URL state for the facets** (ruling Q4). The one exception is the Schema button's one-shot
  `?order=updated_at.desc`, read on arrival and never written back (C7).
- **Pattern language:** facets follow the new §3.9 (Task 1). The window is a single-choice `ChipGroup` (a radio group
  with `aria-checked`). *By me* is a toggle with `aria-pressed` (C4). Loading uses `RefreshBar`; empty uses `EmptyState`
  with one action.
- **Code style** (`alvo-dotnet-conventions`): `.cs` files are **UTF-8 with BOM and CRLF**. `dotnet format` runs in
  pre-commit, so normalise any file written by a shell tool. No inline comments (name the thing instead). Methods
  stay within ~25 lines; extract by default. English only. `///` docs match the style of the file around them.
  Razor comments `@* *@` follow the file's own habit.
- **CI is Windows and Release.** The checkout is CRLF, so a test that reads a source file must not split on `\n` or
  compare line endings: use `Contains`/`Regex`. Tests build in Release, where CA analyzers are errors: pass an
  `IFormatProvider`/`StringComparison` everywhere, use `static readonly` for constant arrays (CA1861),
  `Regex.Count` over `Matches().Count` (CA1875), and `Count`/`Length` over `Count()` (CA1829).
- **E2E facts are order-free.** xUnit v3 runs a class's facts in no set order, one at a time, and each class gets its
  own world (`IClassFixture`). Every fact seeds its own rows under names no other fact uses, finds them by that name,
  and asserts lookup counts as a **delta** read before and after its own steps. A fact that needs "nobody changed X
  as the operator" gets a class, and so a world, of its own. The scenarios use `GetByTestId`/`GetByRole` only: no
  `.a-*` selector and no `:has-text(` (`EndToEndSelectorTests` holds both at zero).
- **Gates:** every task ends with `scripts/test-ring1`. Admin tasks also run `scripts/test-admin-e2e --filter …` for
  their scenarios. Task 10 runs `scripts/test-ring2`, `scripts/test-admin-e2e` whole,
  `dotnet build MMLib.Alvo.slnx -c Release -warnaserror` and `docker build -f src/MMLib.Alvo.Host/Dockerfile .`.
- **Commits:** Conventional Commits. Every message ends with the line
  `Claude-Session: https://claude.ai/code/session_01TrcGmun8WAuVxN5FYAtu6H`. Never push. Never switch branch. Work
  only in the worktree `/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-audit-view` (branch `f5/admin-audit-view`).
  Never bind ports 5080 or 5090.
- **Security core:** Task 2 changes an authorization guard (`GuardedUserAdministration`). Run the
  `alvo-security-core-review` checklist on it before its commit.

## Review Focus

The five inputs the spec implies but its scenarios do not reach, most likely first. Each has a test in the task
named.

1. **A stored actor that is not a person's uuid:** the all-zero uuid, a string uuid (a host-assembled schema), or a
   non-uuid string. Expected: `no identity` for all-zero and null, the short id for any other uuid, and never a crash
   (Task 4, `AuditStampTests`, `ActorLabelsTests`).
2. **The browser's offset is not known yet, and a change made just before midnight.** Expected: `14:05 UTC` until the
   circuit learns the zone, then the operator's time, and a date shown whenever the day differs in *their* zone (Task
   4, `AuditViewTests`).
3. **A reveal while a facet is on.** Expected: the created record is shown alone (a reveal replaces the search *and*
   the facets), and pressing a facet ends the reveal like typing does (Task 4 `GridQueryTests`, Task 7 scenario).
4. **The people lookup throws** (store down, a host refusing the member by name). Expected: the rows still load, the
   actors show ids, and the one note says people cannot be looked up. Never a red panel over a grid it could draw
   (Task 5, `ManagementGatewayPeopleLookupTests`, `ActorDirectoryTests`).
5. **A page with more distinct actors than one lookup takes** (25 rows × created + changed = 50), and a search over
   an entity with very many string fields plus two facets. Expected: one lookup of at most 50 ids, and a filter the
   port admits (Task 5 `ActorDirectoryTests`, Task 4 `GridQueryTests`, C9).

---

### Task 1: §3.9 in the pattern language (D3: the pattern is written before it is built)

**Files:**
- Modify: `docs/superpowers/specs/2026-09-24-f5-admin-mudblazor-design.md` (§3, after item 8 *Fields*, before
  `## 4. Screen map`)

**Interfaces:** none. Task 7 builds to this text.

- [ ] **Step 1: Add item 9 to §3**

Insert after the paragraph that ends `` `FieldConsistencyScenarios` measures the rule in a browser, both themes, sign-in included. ``:

```markdown
9. **Narrowing a list by a facet** (added 29 Sep, #290; `2026-09-29-f5-admin-audit-view-design.md` §4.1). A list
   narrowed by a facet shows its facets as **one row of chips under the list's search**, a `role="group"` named for
   what it narrows ("Narrow by change"): a single-choice `ChipGroup` per dimension (a radio group, `aria-checked`),
   whose **first option is the unnarrowed state** ("Any time"); a toggle chip for a one-value facet ("By me",
   `aria-pressed`); and a **dismissible chip for a value picked from a cell** ("Changed by …", its remove button named
   "Remove the filter Changed by …"), which replaces a toggle on the same dimension, since two actor filters would
   never both match. The value in the cell is a link-styled button that stops the press from reaching the row.
   Every change reads the list again **from its first page under `RefreshBar`**, and ends a reveal as typing does. An
   empty result is §3.6's empty state, titled by the facet that emptied it ("Nothing changed in the last 24 hours"),
   whose **one action removes that facet** ("Show any time", "Anyone") and gives focus back to the facet row. The
   facets join the quick search with `and`, and a reveal still replaces both (§3.5). **A facet is only ever a filter
   the Data API would answer for the same caller**: the grid never narrows rows in a way `/api` could not, and the
   facet state is not written to the URL (#290 ruling Q4). On a phone the row wraps.
```

- [ ] **Step 2: Commit**

```bash
git add docs/superpowers/specs/2026-09-24-f5-admin-mudblazor-design.md
git commit -m "docs(f5): add §3.9, narrowing a list by a facet, to the pattern language

Claude-Session: https://claude.ai/code/session_01TrcGmun8WAuVxN5FYAtu6H"
```

---

### Task 2: `IAlvoUserAdministration.FindAsync`: port, guard, Identity, route, contract

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Identity/IAlvoUserAdministration.cs` (add the member after `ListAsync`)
- Modify: `src/MMLib.Alvo/Management/Internal/GuardedUserAdministration.cs`
- Modify: `src/MMLib.Alvo.Identity/Internal/AlvoIdentityUserAdministration.cs`
- Modify: `src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs` (`MapUsers`, extract `MapPeopleReads`)
- Modify: `src/MMLib.Alvo.Testing/Management/UserAdministrationContractTests.cs`
- Modify: `test/MMLib.Alvo.Host.Tests/UserAdministrationRouteTests.cs`
- Create: `test/MMLib.Alvo.Host.Tests/UserLookupRouteTests.cs`
- Modify (every implementer, or the build breaks): `test/MMLib.Alvo.Admin.Tests/Internal/ManagementGatewayRoleChangeTests.cs`
  (`People`), `test/MMLib.Alvo.Admin.Tests.EndToEnd/PersonVanishesScenarios.cs` (`Hiding`),
  `test/MMLib.Alvo.Tests/Management/GuardedUserAdministrationScopeTests.cs` (`RecordingAdministration`). Before you
  start, confirm there are no others:
  `grep -rln "IAlvoUserAdministration" src test --include=*.cs | xargs grep -ln "Task<AlvoUserPage> ListAsync"`
- Modify: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`,
  `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt`
- Modify: `docs/architecture/management-api.md` (the second route table: "seven more" becomes "eight more", plus one row)

**Interfaces:**
- Produces: `IAlvoUserAdministration.FindAsync(IReadOnlyCollection<UserId> users, CancellationToken cancellationToken = default) : Task<IReadOnlyList<AlvoUser>>`;
  `GuardedUserAdministration.MaxLookup` (`internal const int`, 50).

- [ ] **Step 1: Write the failing contract facts**

In `UserAdministrationContractTests`, add to `A_caller_with_no_level_is_refused_every_member`, after the
`ClearLockoutAsync` line:

```csharp
        await Should.ThrowAsync<ManagementForbiddenException>(
            () => surface.FindAsync([Administrator]));
```

Then add these facts at the end of the class:

```csharp
    /// <summary>People are found by id; an id nobody holds is simply absent.</summary>
    /// <remarks>
    /// The member exists for a screen that names who wrote a row, where an id is often not a person at all (an API key's
    /// identity, an external caller): an absence is an answer, never a refusal.
    /// </remarks>
    [Fact]
    public async Task People_are_found_by_id_and_an_unknown_id_is_absent()
    {
        EnsureAvailable();
        var administration = AsAdministrator();
        var person = await administration.CreateAsync(
            new AlvoUserCreation($"found-{Guid.CreateVersion7():N}@alvo.test", []));

        var found = await administration.FindAsync([person.Id, UserId.New()]);

        found.ShouldHaveSingleItem().Email.ShouldBe(person.Email);
    }

    /// <summary>An id asked for twice is answered once.</summary>
    [Fact]
    public async Task A_repeated_id_is_answered_once()
    {
        EnsureAvailable();
        var administration = AsAdministrator();
        var person = await administration.CreateAsync(
            new AlvoUserCreation($"twice-{Guid.CreateVersion7():N}@alvo.test", []));

        var found = await administration.FindAsync([person.Id, person.Id]);

        found.ShouldHaveSingleItem().Id.ShouldBe(person.Id);
    }

    /// <summary>Fifty ids is one lookup; fifty-one is refused, so a caller pages rather than scanning the directory.</summary>
    [Fact]
    public async Task A_lookup_names_at_most_fifty_people()
    {
        EnsureAvailable();
        var administration = AsAdministrator();
        var fifty = Enumerable.Range(0, 50).Select(_ => UserId.New()).ToList();

        (await administration.FindAsync(fifty)).ShouldBeEmpty();
        await Should.ThrowAsync<ManagementRequestException>(
            () => administration.FindAsync([.. fifty, UserId.New()]));
    }

    /// <summary>A disabled person is still found: an author who was disabled later is still the author.</summary>
    [Fact]
    public async Task A_disabled_person_is_still_found_by_id()
    {
        EnsureAvailable();
        var administration = AsAdministrator();
        var person = await administration.CreateAsync(
            new AlvoUserCreation($"gone-{Guid.CreateVersion7():N}@alvo.test", []));
        await administration.SetDisabledAsync(person.Id, disabled: true);

        (await administration.FindAsync([person.Id])).ShouldHaveSingleItem().IsDisabled.ShouldBeTrue();
    }

    /// <summary>The bootstrap administrator is found like anyone: a lookup is a read, and no guard is about reads.</summary>
    [Fact]
    public async Task The_bootstrap_administrator_is_found_like_anyone()
    {
        EnsureAvailable();

        (await AsAdministrator().FindAsync([BootstrapAdministrator])).ShouldHaveSingleItem()
            .Id.ShouldBe(BootstrapAdministrator);
    }
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet build test/MMLib.Alvo.Host.Tests/MMLib.Alvo.Host.Tests.csproj`
Expected: FAIL, `'IAlvoUserAdministration' does not contain a definition for 'FindAsync'`.

- [ ] **Step 3: Add the port member**

In `IAlvoUserAdministration`, after `ListAsync`:

```csharp
    /// <summary>The people with these ids.</summary>
    /// <remarks>
    /// <para>
    /// <b>For a screen that names who wrote a row</b>: an audited row stores its writers' ids, and paging through
    /// <see cref="ListAsync"/> to find twenty-five of them is the shape its remarks call wrong at the thousands a real
    /// deployment has. So this is a batch, bounded like a page: at most <b>50</b> ids, and a caller asking for more is
    /// refused with <see cref="Management.ManagementRequestException"/>, never answered in part.
    /// </para>
    /// <para>
    /// An id nobody holds is absent from the answer rather than refused: most such ids are an API key's identity or an
    /// external caller, which is what the screen then says. An id asked for twice is answered once, the order is not
    /// promised, and a disabled person and the bootstrap administrator are returned like anyone, because an author who
    /// was disabled later is still the author. It is guarded like <see cref="ListAsync"/>: whoever may read every
    /// address may read these.
    /// </para>
    /// </remarks>
    /// <param name="users">Whose records to return; at most 50 distinct ids.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The people found, in no promised order.</returns>
    Task<IReadOnlyList<AlvoUser>> FindAsync(
        IReadOnlyCollection<UserId> users, CancellationToken cancellationToken = default);
```

- [ ] **Step 4: Guard it**

In `GuardedUserAdministration`, after `ListAsync`:

```csharp
    /// <summary>The most distinct ids one <see cref="FindAsync"/> may name.</summary>
    internal const int MaxLookup = 50;

    /// <inheritdoc/>
    /// <remarks>
    /// <b>The bound is here, not in the implementation</b>, for the reason every other guard is: it is the one place
    /// every transport passes through. Duplicates are dropped before the bound is counted, so a caller is refused for
    /// the people it named, not for how often it named them.
    /// </remarks>
    public Task<IReadOnlyList<AlvoUser>> FindAsync(
        IReadOnlyCollection<UserId> users, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(users);

        EnsureMayManage();
        var distinct = users.Distinct().ToList();
        EnsureWithinLookupBound(distinct.Count);
        return distinct.Count == 0
            ? Task.FromResult<IReadOnlyList<AlvoUser>>([])
            : InScopeAsync(inner => inner.FindAsync(distinct, cancellationToken));
    }
```

and, beside `EnsureTenantIsReal`:

```csharp
    private static void EnsureWithinLookupBound(int count)
    {
        if (count > MaxLookup)
        {
            throw new ManagementRequestException(
                $"A lookup names at most {MaxLookup} people and this one named {count}. Ask in batches of "
                + $"{MaxLookup}, or page through the people list instead.");
        }
    }
```

- [ ] **Step 5: Implement it over ASP.NET Core Identity**

In `AlvoIdentityUserAdministration`, after `After(...)`:

```csharp
    /// <inheritdoc/>
    /// <remarks>
    /// One query over the primary key, then the roles per person, as <see cref="ListAsync"/> projects a page; the guard
    /// has already bounded it to 50.
    /// </remarks>
    public async Task<IReadOnlyList<AlvoUser>> FindAsync(
        IReadOnlyCollection<UserId> users, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(users);

        var ids = users.Select(user => user.Value).Distinct().ToList();
        var rows = await store.Users.AsNoTracking().Where(row => ids.Contains(row.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var found = new List<AlvoUser>(rows.Count);
        foreach (var row in rows)
        {
            found.Add(await ProjectAsync(row).ConfigureAwait(false));
        }

        return found;
    }
```

- [ ] **Step 6: Give it a route**

In `ManagementEndpoints.MapUsers`, replace the `ListAsync` `Gate(...)` block with a call `MapPeopleReads(group);`
and add:

```csharp
    /// <summary>The two reads of people: a page of them, and a batch by id.</summary>
    /// <param name="group">The group to map into.</param>
    private static void MapPeopleReads(RouteGroupBuilder group)
    {
        Gate(
            group.MapGet(
                "/projects/{project}/users",
                (string project, string? search, int? limit, string? after,
                    IAlvoUserAdministration users, CancellationToken ct) =>
                        Answer(() => users.ListAsync(
                            new AlvoUserQuery(search, limit ?? 50, after), ct))),
            new ManagementRoute(nameof(IAlvoUserAdministration.ListAsync), ManagementOperation.ManageUsers));

        Gate(
            group.MapGet(
                "/projects/{project}/users/by-id",
                (string project, [FromQuery(Name = "id")] Guid[] id,
                    IAlvoUserAdministration users, CancellationToken ct) =>
                        Answer(() => users.FindAsync([.. id.Select(value => new UserId(value))], ct))),
            new ManagementRoute(nameof(IAlvoUserAdministration.FindAsync), ManagementOperation.ManageUsers));
    }
```

(`using Microsoft.AspNetCore.Mvc;` for `FromQuery`, if the file does not have it.) Update `MapUsers`' summary from
"seven" to "eight" routes.

- [ ] **Step 7: Route facts**

In `UserAdministrationRouteTests.Every_user_route_refuses_a_caller_the_project_names_nowhere`, add to `calls`:

```csharp
            (HttpMethod.Get, $"/management/projects/{project}/users/by-id?id={Guid.Empty}", null),
```

Create `test/MMLib.Alvo.Host.Tests/UserLookupRouteTests.cs`:

```csharp
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// <c>GET {m}/projects/{p}/users/by-id</c> over the wire, as an administrator: the batch lookup a dashboard names
/// writers with (#290), reachable over HTTP because every member of the port is.
/// </summary>
/// <remarks>
/// The world's dev key is given the <c>operator</c> role, which <c>host-user-admin</c>'s access block resolves to
/// <c>admin</c>, as <see cref="UserLockoutRouteTests"/> does, so these facts measure the member's answers.
/// </remarks>
public sealed class UserLookupRouteTests : IAsyncLifetime
{
    private AlvoHostWorld? _world;

    private AlvoHostWorld World => _world!;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
        => _world = await AlvoHostWorld.StartAsync(
            "host-user-admin.alvo.json",
            overrides: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Alvo:Auth:DevKeys:0:Roles:0"] = "operator",
            });

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_world is not null)
        {
            await _world.DisposeAsync();
        }
    }

    [Fact]
    public async Task Repeated_id_parameters_answer_the_people_found()
    {
        var id = await CreateAsync("looked-up@alvo.test");

        using var response = await World.GetAsync($"{ByIdPath}?id={id}&id={Guid.NewGuid()}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        var people = JsonNode.Parse(await response.ReadTextAsync())!.AsArray();
        people.ShouldHaveSingleItem()!["email"]!.GetValue<string>().ShouldBe("looked-up@alvo.test");
    }

    [Fact]
    public async Task More_than_fifty_ids_are_refused_422()
    {
        var query = string.Join('&', Enumerable.Range(0, 51).Select(_ => $"id={Guid.NewGuid()}"));

        using var response = await World.GetAsync($"{ByIdPath}?{query}");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.ReadTextAsync());
        (await response.ReadTextAsync()).ShouldContain("at most 50 people");
    }

    private const string ByIdPath = "/management/projects/host/users/by-id";

    private async Task<Guid> CreateAsync(string email)
    {
        using var response = await World.SendAsync(
            HttpMethod.Post, "/management/projects/host/users",
            new JsonObject { ["email"] = email, ["roleNames"] = new JsonArray() });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        return JsonNode.Parse(await response.ReadTextAsync())!["id"]!.GetValue<Guid>();
    }
}
```

If `ManagementRequestException` maps to another status than 422 on this surface (check `Answer<T>` in
`ManagementEndpoints`), assert the status it maps to. `UserLockoutRouteTests` shows 422 for the same exception type.

- [ ] **Step 8: Every other implementer**

`ManagementGatewayRoleChangeTests.People`:

```csharp
        public Task<IReadOnlyList<AlvoUser>> FindAsync(
            IReadOnlyCollection<UserId> users, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
```

`PersonVanishesScenarios.Hiding` (a hidden person must vanish from this read too):

```csharp
        public async Task<IReadOnlyList<AlvoUser>> FindAsync(
            IReadOnlyCollection<UserId> users, CancellationToken cancellationToken = default)
            => [.. (await inner.FindAsync(users, cancellationToken).ConfigureAwait(false))
                .Where(person => !hidden.ContainsKey(person.Email))];
```

`GuardedUserAdministrationScopeTests.RecordingAdministration`:

```csharp
        public Task<IReadOnlyList<AlvoUser>> FindAsync(
            IReadOnlyCollection<UserId> users, CancellationToken cancellationToken = default)
            => Record<IReadOnlyList<AlvoUser>>([]);
```

If that suite has a theory listing every member (search it for `ClearLockoutAsync`), add a `FindAsync` row with one
non-empty id list, because an empty list never reaches the implementation.

- [ ] **Step 9: Accept the baselines**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests/MMLib.Alvo.Abstractions.Tests.csproj` and
`dotnet test --project test/MMLib.Alvo.Tests/MMLib.Alvo.Tests.csproj`. Both public-API facts fail with a
`*.received.txt`. Check that the Abstractions diff is **exactly** one line inside `IAlvoUserAdministration`:

```
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<MMLib.Alvo.AlvoUser>> FindAsync(System.Collections.Generic.IReadOnlyCollection<MMLib.Alvo.UserId> users, System.Threading.CancellationToken cancellationToken = default);
```

and that the Testing diff is exactly the five new `public System.Threading.Tasks.Task …() { }` facts of
`UserAdministrationContractTests`. Move each received file over its verified file. Justify the growth when the Stop
hook asks: the port member for design §5, and the facts for C2.

- [ ] **Step 10: The route table**

In `docs/architecture/management-api.md`, change "And seven more, from a second contract" to "And eight more, from a
second contract", and add after the `ListAsync` row:

```markdown
| `GET {m}/projects/{project}/users/by-id?id=…` | `FindAsync` | `admin` |
```

- [ ] **Step 11: Security-core pass, then ring1**

Run the `alvo-security-core-review` checklist over `GuardedUserAdministration.FindAsync`: the gate runs before the
bound and before any scope is created, and an empty list still passes the gate. Then:
Run: `scripts/test-ring1`
Expected: PASS (the contract suite runs over Identity in `UserAdministrationContractOverIdentityTests`).

- [ ] **Step 12: Commit**

```bash
git add -A src/MMLib.Alvo.Abstractions src/MMLib.Alvo src/MMLib.Alvo.Identity src/MMLib.Alvo.Testing test docs/architecture/management-api.md
git commit -m "feat(identity): look people up by id, in batches of at most fifty, at the admin level

Claude-Session: https://claude.ai/code/session_01TrcGmun8WAuVxN5FYAtu6H"
```

---

### Task 3: keyset paging by the audit stamp, on every engine (A5)

**Files:**
- Modify: `src/MMLib.Alvo.Testing/Data/AlvoDataPagingTests.cs`
- Modify: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt`

**Interfaces:** none new. The fact runs over in-memory (`InMemoryAlvoDataPagingTests`), SQLite
(`SqliteAlvoDataPagingTests`) and PostgreSQL (`PostgreSqlAlvoDataPagingTests`, ring2).

- [ ] **Step 1: Write the fact**

Add to `AlvoDataPagingTests`, after `The_null_keyed_rows_sit_where_the_placement_puts_them`:

```csharp
    /// <summary>
    /// A page sorted by <c>updated_at</c> keeps keyset paging: walked a page at a time it is exactly the unpaged order,
    /// and a walk back to page one returns page one (#290, A5).
    /// </summary>
    /// <remarks>
    /// Three rows share one stamp, because a batch write stamps every row it writes at one instant: that run is decided
    /// by the row-id tie-breaker alone, which is the case a boundary that compared the stamp and nothing else would
    /// get wrong.
    /// </remarks>
    /// <param name="descending">Whether the stamp is sorted newest first.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Paging_by_the_audit_stamp_walks_out_exactly_the_unpaged_order(bool descending)
    {
        var world = await StampedWorldAsync();
        var sort = new[] { new AlvoSort(AlvoManagedColumns.UpdatedAt, descending) };
        var unpaged = await world.Data.QueryAsync(new AlvoQuery { Entity = "notes", Sort = sort }, world.Alice);

        var walked = await WalkAsync(world, sort, pageSize: 2);
        var first = await world.Data.QueryAsync(new AlvoQuery { Entity = "notes", Sort = sort, Limit = 2 }, world.Alice);
        var again = await world.Data.QueryAsync(new AlvoQuery { Entity = "notes", Sort = sort, Limit = 2 }, world.Alice);

        walked.ShouldBe([.. unpaged.Items.Select(row => row["id"])]);
        again.Items.Select(row => row["id"]).ShouldBe(first.Items.Select(row => row["id"]));
    }

    /// <summary>
    /// A global, audited <c>notes</c> entity of <see cref="NullKeyedRowCount"/> rows, whose stamps hold one run of
    /// three equal instants, seeded in an order that matches neither sorted order.
    /// </summary>
    private async Task<SeededWorld> StampedWorldAsync()
    {
        var descriptor = new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "stamp-paging-fixture",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
            {
                ["notes"] = new EntityDescriptor
                {
                    Tenancy = EntityTenancy.Global,
                    Audit = true,
                    Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                    {
                        ["title"] = new() { Type = DescField.String, Required = true },
                    },
                    Rules = new AccessRules { List = "true", Get = "true" },
                },
            },
        };

        var data = await CreateAsync(StampedSchema, descriptor, new Dictionary<string, IReadOnlyList<AlvoRecord>>(
            StringComparer.Ordinal) { ["notes"] = StampedRows() });

        return new SeededWorld(data, Caller);
    }

    private static SchemaModel StampedSchema => new([
        new EntitySchema
        {
            Name = "notes",
            Tenancy = TenancyMode.Global,
            Audit = true,
            Fields =
            [
                new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "title", Type = SchemaField.String, Required = true, MaxLength = 32 },
                new FieldSchema { Name = AlvoManagedColumns.CreatedAt, Type = SchemaField.DateTime, Required = true },
                new FieldSchema { Name = AlvoManagedColumns.CreatedBy, Type = SchemaField.Uuid, Nullable = true },
                new FieldSchema { Name = AlvoManagedColumns.UpdatedAt, Type = SchemaField.DateTime, Required = true },
                new FieldSchema { Name = AlvoManagedColumns.UpdatedBy, Type = SchemaField.Uuid, Nullable = true },
            ],
        },
    ]);

    private static List<AlvoRecord> StampedRows()
    {
        var start = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        int[] minutes = [30, 0, 10, 10, 50, 10, 20];
        return [.. minutes.Select((minute, index) => new AlvoRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Guid.NewGuid(),
            ["title"] = $"row-{index:D4}",
            [AlvoManagedColumns.CreatedAt] = start,
            [AlvoManagedColumns.CreatedBy] = null,
            [AlvoManagedColumns.UpdatedAt] = start.AddMinutes(minute),
            [AlvoManagedColumns.UpdatedBy] = null,
        }))];
    }
```

`WalkAsync` is bounded by `NullKeyedRowCount` (7), which is also the number of rows here. Keep the two equal; if you
change one, rename the constant to `WalkedRowCount`.

- [ ] **Step 2: Run it**

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests/MMLib.Alvo.Data.Sqlite.Tests.csproj -- --filter-method "*audit_stamp*"`
and the same for `test/MMLib.Alvo.Tests/MMLib.Alvo.Tests.csproj`.
Expected: PASS on both. This pins behaviour that already exists. If it fails, the driver's keyset over a managed
column is the defect: stop and report it rather than change the fact.

- [ ] **Step 3: Accept the Testing baseline** (one new public theory method), then `scripts/test-ring1`.

- [ ] **Step 4: Commit**

```bash
git add src/MMLib.Alvo.Testing/Data/AlvoDataPagingTests.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt
git commit -m "test(data): a page sorted by the audit stamp keeps keyset paging on every engine

Claude-Session: https://claude.ai/code/session_01TrcGmun8WAuVxN5FYAtu6H"
```

---

### Task 4: the audit helpers: stamp, actor labels, facets, words, and the grid query

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Data/AuditStamp.cs` (`AuditMark`, `AuditStamp`)
- Create: `src/MMLib.Alvo.Admin/Components/Data/ActorLabels.cs` (`ActorKind`, `ActorLabel`, `ActorLabels`)
- Create: `src/MMLib.Alvo.Admin/Components/Data/AuditFacets.cs` (`ChangeWindow`, `AuditFacet`, `ActorFacet`, `AuditFacets`)
- Create: `src/MMLib.Alvo.Admin/Components/Data/AuditView.cs` (`AuditView`, `RecordAudit`)
- Create: `src/MMLib.Alvo.Admin/Components/Data/AuditWords.cs`
- Modify: `src/MMLib.Alvo.Admin/Components/Data/GridQuery.cs` (`Searchable`, `Filter`, `Both`, `Arrival`)
- Test: `test/MMLib.Alvo.Admin.Tests/Data/AuditStampTests.cs`, `ActorLabelsTests.cs`, `AuditFacetsTests.cs`,
  `AuditViewTests.cs`, `AuditWordsTests.cs`; modify `GridQueryTests.cs`

**Interfaces:**
- Produces (all `internal`, namespace `MMLib.Alvo.Admin.Components.Data`):
  - `sealed record AuditMark(bool Shown, DateTimeOffset? At, Guid? By)`; `sealed record AuditStamp(AuditMark Created, AuditMark Updated)` with `static AuditStamp Of(AlvoRecord row)`
  - `enum ActorKind { NoIdentity, System, You, Person, Unresolved }`
  - `sealed record ActorLabel(ActorKind Kind, string Text, string Title, Guid? Id, string? Hint = null)` with `string Short`
  - `sealed class ActorLabels(UserId? self, string? selfAddress, IReadOnlyDictionary<Guid, AlvoUser> people, bool lookedUp)` with `ActorLabel Of(Guid? by)`, `static ActorLabels None`, `static Guid SystemId`, `static string ShortId(Guid id)`
  - `enum ChangeWindow { AnyTime, Last24Hours, Last7Days, Last30Days }`; `enum AuditFacet { Window, Actor }`
  - `sealed record ActorFacet(Guid? Id, bool Mine, string Label)` with `static ActorFacet Me(UserId self)` and `static ActorFacet For(ActorLabel actor)`
  - `sealed record AuditFacets(ChangeWindow Window, ActorFacet? Actor)` with `static AuditFacets None`, `const int MaxTerms = 3`, `static IReadOnlyList<ChangeWindow> Windows`, `bool Narrows`, `AlvoFilter? Filter(DateTimeOffset now)`, `string ApiQuery(DateTimeOffset now)`, `AuditFacets Without(AuditFacet facet)`, `static string Words(ChangeWindow window)`
  - `sealed record AuditView(FieldSchema UpdatedAt, ActorLabels Actors, DateTimeOffset Now, TimeSpan? Offset, Func<ActorLabel, Task> Narrow)` with `string Clock(AuditMark mark)`, `string Line(AuditMark mark)`, `static string Iso(DateTimeOffset? at)`
  - `sealed record RecordAudit(AuditStamp Stamp, AuditView View)`
  - `static class AuditWords` (every string in Global Constraints *Copy*)
  - `GridQuery.Filter(IReadOnlyList<string> fields, string? term, Guid? revealing, AlvoFilter? facets = null)`, `GridQuery.Both(AlvoFilter?, AlvoFilter?)`, `GridQuery.Arrival(EntitySchema entity, string? order) : GridSort?`

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Admin.Tests/Data/AuditStampTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Data;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Tests.Data;

/// <summary>The four audit values of one row, read as the grid and the record sheet draw them.</summary>
public sealed class AuditStampTests
{
    private static readonly DateTimeOffset _at = new(2026, 9, 28, 12, 5, 0, TimeSpan.Zero);
    private static readonly Guid _ada = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

    [Fact]
    public void A_row_carries_who_created_it_and_who_changed_it_last()
    {
        var stamp = AuditStamp.Of(Row(created: _at.AddDays(-1), createdBy: null, updated: _at, updatedBy: _ada));

        stamp.Created.ShouldBe(new AuditMark(Shown: true, _at.AddDays(-1), By: null));
        stamp.Updated.ShouldBe(new AuditMark(Shown: true, _at, _ada));
    }

    [Fact]
    public void A_stamp_the_row_does_not_carry_is_hidden_not_empty()
        => AuditStamp.Of(new AlvoRecord(new Dictionary<string, object?> { ["id"] = Guid.NewGuid() }))
            .Updated.Shown.ShouldBeFalse("a missing *_at is 'hidden from you', never 'no identity'");

    [Theory]
    [InlineData("7c9e6679-7425-40de-944b-e07fc1f90ae7", true)]
    [InlineData("00000000-0000-0000-0000-000000000000", false)]
    [InlineData("not-a-uuid", false)]
    public void A_writer_is_a_uuid_and_the_all_zero_one_is_nobody(string stored, bool isSomebody)
        => (AuditStamp.Of(Row(_at, null, _at, stored)).Updated.By is not null).ShouldBe(isSomebody);

    [Fact]
    public void An_instant_stored_as_text_or_as_a_utc_datetime_reads_as_the_same_instant()
    {
        AuditStamp.Of(Row(_at, null, "2026-09-28T12:05:00Z", null)).Updated.At.ShouldBe(_at);
        AuditStamp.Of(Row(_at, null, _at.UtcDateTime, null)).Updated.At.ShouldBe(_at);
    }

    private static AlvoRecord Row(object created, object? createdBy, object updated, object? updatedBy)
        => new(new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid(),
            ["created_at"] = created,
            ["created_by"] = createdBy,
            ["updated_at"] = updated,
            ["updated_by"] = updatedBy,
        });
}
```

`test/MMLib.Alvo.Admin.Tests/Data/ActorLabelsTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Data;

namespace MMLib.Alvo.Admin.Tests.Data;

/// <summary>Who a stored writer id reads as, in the order design §4.4 rules them.</summary>
public sealed class ActorLabelsTests
{
    private static readonly UserId _me = new(Guid.Parse("11111111-2222-4333-8444-555555555555"));
    private static readonly Guid _katarina = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
    private static readonly Guid _key = Guid.Parse("5eed0000-0000-4000-8000-000000000001");

    private static readonly Dictionary<Guid, AlvoUser> _people = new()
    {
        [_katarina] = new AlvoUser
        {
            Id = new UserId(_katarina), Email = "katarina.novotna@velo-dielna.example", RoleNames = [],
        },
    };

    [Fact]
    public void Nobody_reads_as_no_identity_and_says_why()
    {
        var label = Admin().Of(null);

        label.Kind.ShouldBe(ActorKind.NoIdentity);
        label.Text.ShouldBe("no identity");
        label.Title.ShouldBe("Written by an anonymous caller, or before the column was filled");
    }

    [Fact]
    public void The_framework_reads_as_Alvo_system()
        => Admin().Of(ActorLabels.SystemId).Text.ShouldBe("Alvo (system)");

    [Fact]
    public void The_operator_reads_as_you_with_their_own_address_as_the_title()
    {
        var label = Admin().Of(_me.Value);

        label.Text.ShouldBe("you");
        label.Title.ShouldBe("me@velo-dielna.example");
    }

    [Fact]
    public void A_person_the_lookup_found_reads_as_their_address_and_the_cell_as_its_local_part()
    {
        var label = Admin().Of(_katarina);

        label.Kind.ShouldBe(ActorKind.Person);
        label.Text.ShouldBe("katarina.novotna@velo-dielna.example");
        label.Short.ShouldBe("katarina.novotna");
    }

    [Fact]
    public void An_id_an_admin_lookup_did_not_find_is_not_a_dashboard_account()
    {
        var label = Admin().Of(_key);

        label.Kind.ShouldBe(ActorKind.Unresolved);
        label.Text.ShouldBe("5eed0000…0001");
        label.Title.ShouldBe(_key.ToString());
        label.Hint.ShouldBe("Not a dashboard account: an API key's identity or an external caller.");
    }

    [Fact]
    public void Below_admin_an_id_is_its_short_form_and_never_claims_what_it_is()
    {
        var label = new ActorLabels(_me, "me@velo-dielna.example", new Dictionary<Guid, AlvoUser>(), lookedUp: false)
            .Of(_katarina);

        label.Text.ShouldBe("7c9e6679…0ae7");
        label.Hint.ShouldBeNull();
    }

    private static ActorLabels Admin() => new(_me, "me@velo-dielna.example", _people, lookedUp: true);
}
```

`test/MMLib.Alvo.Admin.Tests/Data/AuditFacetsTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Data;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Tests.Data;

/// <summary>The facets of an audited grid, as the filter the port is sent and the query string /api would take.</summary>
public sealed class AuditFacetsTests
{
    private static readonly DateTimeOffset _now = new(2026, 9, 29, 12, 5, 0, 700, TimeSpan.Zero);
    private static readonly Guid _ada = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

    [Fact]
    public void No_facet_narrows_nothing()
    {
        AuditFacets.None.Narrows.ShouldBeFalse();
        AuditFacets.None.Filter(_now).ShouldBeNull();
        AuditFacets.None.ApiQuery(_now).ShouldBe(string.Empty);
    }

    [Fact]
    public void A_window_is_a_gte_on_updated_at_cut_to_the_whole_second()
    {
        var facets = AuditFacets.None with { Window = ChangeWindow.Last24Hours };

        facets.Filter(_now).ShouldBe(new AlvoComparison(
            "updated_at", AlvoFilterOperator.Gte, new DateTimeOffset(2026, 9, 28, 12, 5, 0, TimeSpan.Zero)));
        facets.ApiQuery(_now).ShouldBe("updated_at=gte.2026-09-28T12:05:00Z");
    }

    [Fact]
    public void A_person_is_an_eq_and_no_identity_is_an_is_null()
    {
        (AuditFacets.None with { Actor = new ActorFacet(_ada, Mine: false, "ada") }).ApiQuery(_now)
            .ShouldBe($"updated_by=eq.{_ada}");
        var nobody = AuditFacets.None with { Actor = new ActorFacet(null, Mine: false, "no identity") };
        nobody.Filter(_now).ShouldBe(new AlvoComparison("updated_by", AlvoFilterOperator.Is, null));
        nobody.ApiQuery(_now).ShouldBe("updated_by=is.null");
    }

    [Fact]
    public void Two_facets_join_with_and()
    {
        var facets = new AuditFacets(ChangeWindow.Last7Days, ActorFacet.Me(new UserId(_ada)));

        facets.Filter(_now).ShouldBeOfType<AlvoAnd>().Filters.Count.ShouldBe(2);
        facets.ApiQuery(_now).ShouldBe($"updated_at=gte.2026-09-22T12:05:00Z&updated_by=eq.{_ada}");
        AlvoFilter.EnsureWithinLimits(facets.Filter(_now)!);
    }

    [Fact]
    public void Removing_a_facet_leaves_the_other()
    {
        var facets = new AuditFacets(ChangeWindow.Last30Days, ActorFacet.Me(new UserId(_ada)));

        facets.Without(AuditFacet.Window).ShouldBe(facets with { Window = ChangeWindow.AnyTime });
        facets.Without(AuditFacet.Actor).ShouldBe(facets with { Actor = null });
    }

    [Fact]
    public void The_window_chips_start_with_the_unnarrowed_state()
        => AuditFacets.Windows.Select(AuditFacets.Words)
            .ShouldBe(["Any time", "Last 24 hours", "Last 7 days", "Last 30 days"]);
}
```

`test/MMLib.Alvo.Admin.Tests/Data/AuditViewTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Data;

/// <summary>How a change reads: the operator's clock, then who.</summary>
public sealed class AuditViewTests
{
    private static readonly DateTimeOffset _now = new(2026, 9, 29, 12, 30, 0, TimeSpan.Zero);
    private static readonly UserId _me = UserId.New();

    [Fact]
    public void Today_reads_as_the_time_and_who()
        => View(offset: TimeSpan.FromHours(2)).Line(new AuditMark(true, _now.AddMinutes(-25), _me.Value))
            .ShouldBe("14:05 · you");

    [Fact]
    public void Until_the_zone_is_known_the_time_says_UTC()
        => View(offset: null).Clock(new AuditMark(true, _now.AddMinutes(-25), null)).ShouldBe("12:05 UTC");

    [Fact]
    public void A_change_on_another_day_in_the_operators_zone_shows_its_date()
        => View(offset: TimeSpan.FromHours(-11)).Clock(new AuditMark(true, _now.AddHours(-2), null))
            .ShouldBe("2026-09-28 23:30");

    [Fact]
    public void A_hidden_stamp_says_so_rather_than_nobody()
        => View(offset: null).Line(new AuditMark(false, null, null)).ShouldBe("hidden from you");

    [Fact]
    public void The_title_is_the_full_iso_instant_in_utc()
        => AuditView.Iso(new DateTimeOffset(2026, 9, 29, 14, 5, 9, TimeSpan.FromHours(2)))
            .ShouldBe("2026-09-29T12:05:09Z");

    private static AuditView View(TimeSpan? offset)
        => new(new FieldSchema { Name = "updated_at", Type = FieldType.DateTime },
            new ActorLabels(_me, "me@example.test", new Dictionary<Guid, AlvoUser>(), lookedUp: false),
            _now, offset, _ => Task.CompletedTask);
}
```

`test/MMLib.Alvo.Admin.Tests/Data/AuditWordsTests.cs` (A10):

```csharp
using MMLib.Alvo.Admin.Components.Data;
using System.Reflection;

namespace MMLib.Alvo.Admin.Tests.Data;

/// <summary>What the audit view says, and what it never claims to be (design A10).</summary>
public sealed class AuditWordsTests
{
    private static readonly string[] _neverSaid = ["audit log", "history"];

    public static TheoryData<string> Words()
    {
        var data = new TheoryData<string>();
        foreach (var field in typeof(AuditWords).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
                     .Where(field => field.FieldType == typeof(string)))
        {
            data.Add((string)field.GetValue(null)!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Words))]
    public void No_sentence_claims_a_log_or_a_history(string words)
        => _neverSaid.ShouldAllBe(term => !words.Contains(term, StringComparison.OrdinalIgnoreCase));

    [Theory]
    [MemberData(nameof(Words))]
    public void Versions_are_named_only_to_say_they_are_not_kept(string words)
        => (!words.Contains("version", StringComparison.OrdinalIgnoreCase)
            || words.Contains("not kept", StringComparison.Ordinal)
            || words.Contains("no earlier versions", StringComparison.Ordinal)).ShouldBeTrue(words);
}
```

Add to `GridQueryTests`:

```csharp
    [Fact]
    public void The_facets_join_the_search_with_and()
    {
        var facet = new AlvoComparison("updated_at", AlvoFilterOperator.Gte, DateTimeOffset.UnixEpoch);

        GridQuery.Filter(["reference"], "0002", revealing: null, facet).ShouldBeOfType<AlvoAnd>().Filters
            .ShouldBe([GridQuery.Search(["reference"], "0002")!, facet]);
        GridQuery.Filter(["reference"], null, revealing: null, facet).ShouldBe(facet);
    }

    [Fact]
    public void A_reveal_replaces_the_search_and_the_facets()
    {
        var id = Guid.NewGuid();
        var facet = new AlvoComparison("updated_at", AlvoFilterOperator.Gte, DateTimeOffset.UnixEpoch);

        GridQuery.Filter(["reference"], "0002", id, facet).ShouldBe(GridQuery.Only(id));
    }

    [Fact]
    public void A_search_leaves_room_for_the_facets_under_the_ports_term_limit()
    {
        var entity = new EntitySchema
        {
            Name = "wide",
            Fields = [.. Enumerable.Range(0, 300).Select(index => new FieldSchema { Name = $"s{index}", Type = FieldType.String })],
        };
        var fields = GridQuery.Searchable(entity, FieldMasks.None);
        var facets = new AuditFacets(ChangeWindow.Last24Hours, new ActorFacet(Guid.NewGuid(), false, "ada"));

        Should.NotThrow(() => AlvoFilter.EnsureWithinLimits(
            GridQuery.Filter(fields, "x", revealing: null, facets.Filter(DateTimeOffset.UnixEpoch.AddDays(40)))!));
    }

    [Theory]
    [InlineData(true, "updated_at.desc", true)]
    [InlineData(false, "updated_at.desc", false)]
    [InlineData(true, "updated_at.asc", false)]
    [InlineData(true, null, false)]
    public void Arriving_from_Recently_changed_sorts_an_audited_entity_by_its_last_change(
        bool audited, string? order, bool sorted)
        => (GridQuery.Arrival(_orders with { Audit = audited }, order) == new GridSort("updated_at", Descending: true))
            .ShouldBe(sorted);
```

(`EntitySchema` is a record; if it is a class without `with`, build the second entity with an object initializer.)

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build test/MMLib.Alvo.Admin.Tests/MMLib.Alvo.Admin.Tests.csproj`
Expected: FAIL, the types `AuditStamp`, `ActorLabels`, `AuditFacets`, `AuditView`, `AuditWords` do not exist.

- [ ] **Step 3: `AuditWords`**

```csharp
namespace MMLib.Alvo.Admin.Components.Data;

/// <summary>
/// Every sentence the audit view draws, in one place so none of them claims more than the trait does: who and when of
/// the last change, never a log or a history (design A10; <c>AuditWordsTests</c> reads every field here).
/// </summary>
internal static class AuditWords
{
    public const string Changed = "Changed";
    public const string Record = "Record";
    public const string Created = "Created";
    public const string LastChanged = "Last changed";
    public const string NoVersions = "Alvo keeps who made the last change and when. Earlier versions of this record are not kept.";
    public const string NoIdentity = "no identity";
    public const string NoIdentityWhy = "Written by an anonymous caller, or before the column was filled";
    public const string System = "Alvo (system)";
    public const string SystemWhy = "Written by the framework: an automation, hook or rollup";
    public const string You = "you";
    public const string NotADashboardAccount = "Not a dashboard account: an API key's identity or an external caller.";
    public const string ByMe = "By me";
    public const string NarrowByChange = "Narrow by change";
    public const string StillApply = "The search and the other filters still apply.";
    public const string ShowAnyTime = "Show any time";
    public const string Anyone = "Anyone";
    public const string PeopleUnresolvable = "People cannot be looked up on this deployment, so changes show the caller's id.";
    public const string AuditedTitle = "Records who created each record and who changed it last, and when. Keeps no earlier versions.";
    public const string Hidden = "hidden from you";
    public const string RecentlyChanged = "Recently changed";

    public static string NotAudited(string entity)
        => $"{entity} is not audited, so it keeps no record of who created or changed a row. Audit is chosen when an entity is created.";

    public static string ChangedBy(string who) => $"Changed by {who}";

    public static string RemoveChangedBy(string who) => $"Remove the filter Changed by {who}";

    public static string ShowOnly(string who) => $"Show only changes by {who}";
}
```

The `NotAudited`, `ChangedBy`, `RemoveChangedBy` and `ShowOnly` templates are methods, so the reflection test does
not read them. Their fixed parts contain none of the banned words (check this by reading them).

- [ ] **Step 4: `AuditStamp`**

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;
using System.Globalization;

namespace MMLib.Alvo.Admin.Components.Data;

/// <summary>One half of a row's audit: whether the caller is shown it, when, and by whom.</summary>
/// <param name="Shown">Whether the row carries the instant at all; a row that does not is hidden from this caller.</param>
/// <param name="At">The instant, when there is one.</param>
/// <param name="By">The writer's id; <see langword="null"/> for nobody, the all-zero id included.</param>
internal sealed record AuditMark(bool Shown, DateTimeOffset? At, Guid? By);

/// <summary>
/// Who created a row and who changed it last, and when, read from the row the grid already has, never read again
/// (design A2).
/// </summary>
/// <param name="Created">The row's creation.</param>
/// <param name="Updated">Its last change.</param>
internal sealed record AuditStamp(AuditMark Created, AuditMark Updated)
{
    /// <summary>The stamp <paramref name="row"/> carries.</summary>
    public static AuditStamp Of(AlvoRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new AuditStamp(
            Mark(row, AlvoManagedColumns.CreatedAt, AlvoManagedColumns.CreatedBy),
            Mark(row, AlvoManagedColumns.UpdatedAt, AlvoManagedColumns.UpdatedBy));
    }

    private static AuditMark Mark(AlvoRecord row, string at, string by)
        => new(row.TryGetValue(at, out var instant), Instant(instant), Writer(row[by]));

    private static DateTimeOffset? Instant(object? value) => value switch
    {
        DateTimeOffset instant => instant,
        DateTime moment => new DateTimeOffset(DateTime.SpecifyKind(moment, DateTimeKind.Utc)),
        string text when DateTimeOffset.TryParse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) => parsed,
        _ => null,
    };

    private static Guid? Writer(object? value)
        => RefLabels.IdOf(value) is { } id && id != Guid.Empty ? id : null;
}
```

- [ ] **Step 5: `ActorLabels`**

```csharp
namespace MMLib.Alvo.Admin.Components.Data;

/// <summary>What kind of writer a stored id turned out to be.</summary>
internal enum ActorKind
{
    /// <summary>Nobody: an anonymous caller, or a row older than the column.</summary>
    NoIdentity,

    /// <summary>The framework itself.</summary>
    System,

    /// <summary>The operator reading the screen.</summary>
    You,

    /// <summary>A person the membership store knows.</summary>
    Person,

    /// <summary>An id nobody resolved: a key's identity, an external caller, or a caller not allowed to look.</summary>
    Unresolved,
}

/// <summary>A writer, as text: what the cell shows, its title, and a hint when one is honest.</summary>
/// <param name="Kind">What kind of writer.</param>
/// <param name="Text">The whole label: the address for a person.</param>
/// <param name="Title">What the tooltip says.</param>
/// <param name="Id">The stored id, when there is one.</param>
/// <param name="Hint">Why an id stays an id, shown to an Admin only.</param>
internal sealed record ActorLabel(ActorKind Kind, string Text, string Title, Guid? Id, string? Hint = null)
{
    /// <summary>What a grid cell shows: a person's address before the <c>@</c>, anything else whole.</summary>
    public string Short => Kind == ActorKind.Person && Text.IndexOf('@', StringComparison.Ordinal) is > 0 and var at
        ? Text[..at]
        : Text;
}

/// <summary>
/// Turns a stored writer id into a label by design §4.4's rules, in order: nobody, the framework, you, a person the
/// lookup found, and any other id.
/// </summary>
/// <remarks>
/// <b>Addresses come only from <paramref name="people"/></b>, which only an Admin's lookup fills: below Admin it is
/// empty and every other writer stays the id the Data API already shows that caller (the identity rule).
/// </remarks>
/// <param name="self">The operator's own id, when they have one.</param>
/// <param name="selfAddress">The operator's own address, for "you"'s title.</param>
/// <param name="people">The people the lookup found, by id.</param>
/// <param name="lookedUp">Whether an Admin's lookup ran, so an id it did not find is honestly not a dashboard account.</param>
internal sealed class ActorLabels(
    UserId? self, string? selfAddress, IReadOnlyDictionary<Guid, AlvoUser> people, bool lookedUp)
{
    private const int ShortTail = 4;

    /// <summary>Labels with nobody known: every writer but nobody and the framework stays an id.</summary>
    public static ActorLabels None { get; } = new(null, null, new Dictionary<Guid, AlvoUser>(), lookedUp: false);

    /// <summary>The framework's one reserved writer id.</summary>
    public static Guid SystemId { get; } = AlvoContext.System(tenant: null).User.Value;

    /// <summary>The label for the writer stored as <paramref name="by"/>.</summary>
    public ActorLabel Of(Guid? by) => by switch
    {
        null => new ActorLabel(ActorKind.NoIdentity, AuditWords.NoIdentity, AuditWords.NoIdentityWhy, null),
        { } id when id == SystemId => new ActorLabel(ActorKind.System, AuditWords.System, AuditWords.SystemWhy, id),
        { } id when self is { } me && me.Value == id
            => new ActorLabel(ActorKind.You, AuditWords.You, selfAddress ?? id.ToString(), id),
        { } id when people.TryGetValue(id, out var person)
            => new ActorLabel(ActorKind.Person, person.Email, person.Email, id),
        { } id => new ActorLabel(ActorKind.Unresolved, ShortId(id), id.ToString(), id,
            lookedUp ? AuditWords.NotADashboardAccount : null),
    };

    /// <summary>An id as its first eight characters and its last four: <c>5eed0000…0001</c>.</summary>
    public static string ShortId(Guid id)
    {
        var text = id.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
        return $"{text[..RefLabels.ShortIdLength]}…{text[^ShortTail..]}";
    }
}
```

`RefLabels.ShortIdLength` is `public const int` on an internal class already (8).

- [ ] **Step 6: `AuditFacets`**

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;
using System.Globalization;

namespace MMLib.Alvo.Admin.Components.Data;

/// <summary>How far back a grid is narrowed to rows changed since.</summary>
internal enum ChangeWindow
{
    /// <summary>Not narrowed.</summary>
    AnyTime,

    /// <summary>Changed in the last day.</summary>
    Last24Hours,

    /// <summary>Changed in the last week.</summary>
    Last7Days,

    /// <summary>Changed in the last thirty days.</summary>
    Last30Days,
}

/// <summary>One of the two things the facet row narrows by.</summary>
internal enum AuditFacet
{
    /// <summary>When a row was changed.</summary>
    Window,

    /// <summary>Who changed it.</summary>
    Actor,
}

/// <summary>A writer the grid is narrowed to.</summary>
/// <param name="Id">Their id; <see langword="null"/> for no identity.</param>
/// <param name="Mine">Whether it is the operator, pressed as "By me".</param>
/// <param name="Label">What the chip calls them.</param>
internal sealed record ActorFacet(Guid? Id, bool Mine, string Label)
{
    /// <summary>The operator, as "By me" narrows to them.</summary>
    public static ActorFacet Me(UserId self) => new(self.Value, Mine: true, AuditWords.You);

    /// <summary>The writer a cell's link named.</summary>
    public static ActorFacet For(ActorLabel actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new ActorFacet(actor.Kind == ActorKind.NoIdentity ? null : actor.Id, actor.Kind == ActorKind.You, actor.Short);
    }
}

/// <summary>
/// The audited grid's facets (pattern language §3.9) as the <see cref="AlvoFilter"/> the port is sent, and as the
/// query string <c>/api</c> would take for the same rows (design A4).
/// </summary>
/// <remarks>
/// <b>The window's edge is cut to the whole second</b>, so the port's filter and the query string name the same
/// instant: the string carries seconds, and an edge a few hundred milliseconds apart would admit a different row.
/// </remarks>
/// <param name="Window">How far back.</param>
/// <param name="Actor">Who, when narrowed to one writer.</param>
internal sealed record AuditFacets(ChangeWindow Window, ActorFacet? Actor)
{
    /// <summary>The most filter terms the facets add to a search: the <c>and</c> node and two comparisons.</summary>
    public const int MaxTerms = 3;

    private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>Nothing narrowed.</summary>
    public static AuditFacets None { get; } = new(ChangeWindow.AnyTime, null);

    /// <summary>The window chips, the unnarrowed state first.</summary>
    public static IReadOnlyList<ChangeWindow> Windows { get; } =
        [ChangeWindow.AnyTime, ChangeWindow.Last24Hours, ChangeWindow.Last7Days, ChangeWindow.Last30Days];

    /// <summary>Whether any facet narrows the grid.</summary>
    public bool Narrows => Window != ChangeWindow.AnyTime || Actor is not null;

    /// <summary>The filter the facets become at <paramref name="now"/>, or nothing when none narrows.</summary>
    public AlvoFilter? Filter(DateTimeOffset now)
    {
        List<AlvoFilter> terms = [.. Since(now), .. By()];
        return terms.Count switch
        {
            0 => null,
            1 => terms[0],
            _ => new AlvoAnd(terms),
        };
    }

    /// <summary>The same narrowing as <c>/api</c>'s query string, without the leading <c>?</c>.</summary>
    public string ApiQuery(DateTimeOffset now)
    {
        List<string> parts = [];
        if (Cutoff(now) is { } cutoff)
        {
            parts.Add($"{AlvoManagedColumns.UpdatedAt}=gte.{cutoff.ToString(InstantFormat, CultureInfo.InvariantCulture)}");
        }

        if (Actor is { } actor)
        {
            parts.Add(actor.Id is { } id ? $"{AlvoManagedColumns.UpdatedBy}=eq.{id}" : $"{AlvoManagedColumns.UpdatedBy}=is.null");
        }

        return string.Join('&', parts);
    }

    /// <summary>These facets less <paramref name="facet"/>.</summary>
    public AuditFacets Without(AuditFacet facet)
        => facet == AuditFacet.Window ? this with { Window = ChangeWindow.AnyTime } : this with { Actor = null };

    /// <summary>What a window's chip says.</summary>
    public static string Words(ChangeWindow window) => window switch
    {
        ChangeWindow.Last24Hours => "Last 24 hours",
        ChangeWindow.Last7Days => "Last 7 days",
        ChangeWindow.Last30Days => "Last 30 days",
        _ => "Any time",
    };

    /// <summary>The empty state's title, for the facet that emptied the page.</summary>
    public string EmptyTitle(AuditFacet emptied) => emptied == AuditFacet.Window || Actor is null
        ? $"Nothing changed in the {Words(Window).ToLowerInvariant()}"
        : Actor switch
        {
            { Mine: true } => "Nothing changed by you",
            { Id: null } => "Nothing changed without an identity",
            { } actor => $"Nothing changed by {actor.Label}",
        };

    /// <summary>The empty state's one action, which removes the facet that emptied the page.</summary>
    public static string EmptyAction(AuditFacet emptied)
        => emptied == AuditFacet.Window ? AuditWords.ShowAnyTime : AuditWords.Anyone;

    private IEnumerable<AlvoFilter> Since(DateTimeOffset now)
        => Cutoff(now) is { } cutoff ? [new AlvoComparison(AlvoManagedColumns.UpdatedAt, AlvoFilterOperator.Gte, cutoff)] : [];

    private IEnumerable<AlvoFilter> By() => Actor switch
    {
        null => [],
        { Id: { } id } => [new AlvoComparison(AlvoManagedColumns.UpdatedBy, AlvoFilterOperator.Eq, id)],
        _ => [new AlvoComparison(AlvoManagedColumns.UpdatedBy, AlvoFilterOperator.Is, null)],
    };

    private DateTimeOffset? Cutoff(DateTimeOffset now) => Span(Window) is { } span ? WholeSecond(now.ToUniversalTime() - span) : null;

    private static TimeSpan? Span(ChangeWindow window) => window switch
    {
        ChangeWindow.Last24Hours => TimeSpan.FromHours(24),
        ChangeWindow.Last7Days => TimeSpan.FromDays(7),
        ChangeWindow.Last30Days => TimeSpan.FromDays(30),
        _ => null,
    };

    private static DateTimeOffset WholeSecond(DateTimeOffset instant)
        => new(instant.Ticks - (instant.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
}
```

If `EmptyTitle`'s "the any time" can be reached (it is not: `Window` is the emptier only when it narrows), leave it.
Add a fact for `EmptyTitle`/`EmptyAction` to `AuditFacetsTests`:

```csharp
    [Theory]
    [InlineData(AuditFacet.Window, "Nothing changed in the last 24 hours", "Show any time")]
    [InlineData(AuditFacet.Actor, "Nothing changed by you", "Anyone")]
    public void The_empty_state_names_the_facet_that_emptied_the_page(AuditFacet emptied, string title, string action)
    {
        var facets = new AuditFacets(ChangeWindow.Last24Hours, ActorFacet.Me(new UserId(_ada)));

        facets.EmptyTitle(emptied).ShouldBe(title);
        AuditFacets.EmptyAction(emptied).ShouldBe(action);
    }
```

- [ ] **Step 7: `AuditView`**

```csharp
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Schema;
using System.Globalization;

namespace MMLib.Alvo.Admin.Components.Data;

/// <summary>What the grid needs to draw the Changed column, cascaded in <see cref="RecordGridScope"/>.</summary>
/// <param name="UpdatedAt">The <c>updated_at</c> field, which the Changed header sorts by.</param>
/// <param name="Actors">Who each writer reads as.</param>
/// <param name="Now">When the page was read, which decides "today".</param>
/// <param name="Offset">The operator's UTC offset, or <see langword="null"/> while it is not known.</param>
/// <param name="Narrow">Narrows the grid to the writer a cell's link named.</param>
internal sealed record AuditView(
    FieldSchema UpdatedAt, ActorLabels Actors, DateTimeOffset Now, TimeSpan? Offset, Func<ActorLabel, Task> Narrow)
{
    private const string Dash = "—";

    /// <summary>When, on the operator's clock (<see cref="OperatorTime.Clock"/>).</summary>
    public string Clock(AuditMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        return !mark.Shown ? AuditWords.Hidden
            : mark.At is { } at ? OperatorTime.Clock(at, Now, Offset) : Dash;
    }

    /// <summary>When and who on one line, <c>14:05 · you</c>: a phone card's pair and the record sheet's.</summary>
    public string Line(AuditMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        return mark.Shown ? $"{Clock(mark)} · {Actors.Of(mark.By).Text}" : AuditWords.Hidden;
    }

    /// <summary>The whole instant in UTC, for a <c>&lt;time datetime&gt;</c> and its title.</summary>
    public static string Iso(DateTimeOffset? at)
        => at?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>One record's audit for the record sheet: its stamp and how to draw it.</summary>
/// <param name="Stamp">The record's stamp, from the row the grid has or the record the write returned.</param>
/// <param name="View">How a change is drawn.</param>
internal sealed record RecordAudit(AuditStamp Stamp, AuditView View);
```

- [ ] **Step 8: `GridQuery`**

Change `Searchable`'s `.Take(AlvoFilter.MaxTerms - 1)` to `.Take(AlvoFilter.MaxTerms - 1 - AuditFacets.MaxTerms)` and
extend its summary with "less the room the audit facets take (C9)". Replace `Filter`:

```csharp
    /// <summary>
    /// The filter a page is read with: only the record just created while it is being revealed, else the search and
    /// the facets together.
    /// </summary>
    /// <remarks>
    /// A reveal replaces the search and the facets rather than joining them, the way Access's does (spec §3.5, amended
    /// 27 Sep; §3.9): the record was created to be seen, and one a facet would exclude is the case the reveal exists for.
    /// </remarks>
    /// <param name="fields">The fields a search looks in.</param>
    /// <param name="term">What was typed into the search.</param>
    /// <param name="revealing">The record being revealed, or <see langword="null"/>.</param>
    /// <param name="facets">What the facet row narrows to, or <see langword="null"/>.</param>
    public static AlvoFilter? Filter(
        IReadOnlyList<string> fields, string? term, Guid? revealing, AlvoFilter? facets = null)
        => revealing is { } id ? Only(id) : Both(Search(fields, term), facets);

    /// <summary>Two filters that must both hold; either alone when the other is absent.</summary>
    public static AlvoFilter? Both(AlvoFilter? first, AlvoFilter? second) => (first, second) switch
    {
        (null, _) => second,
        (_, null) => first,
        _ => new AlvoAnd([first, second]),
    };

    /// <summary>
    /// The sort a screen arriving with <c>?order=</c> asks for: the Schema header's <em>Recently changed</em>, read
    /// once on arrival and only on an audited entity (#290, C7). Anything else is the entity's own order.
    /// </summary>
    /// <param name="entity">The entity arrived at.</param>
    /// <param name="order">The query's <c>order</c>, in the Data API's own syntax.</param>
    public static GridSort? Arrival(EntitySchema entity, string? order)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return entity.Audit && string.Equals(order, RecentlyChangedOrder, StringComparison.Ordinal)
            ? new GridSort(AlvoManagedColumns.UpdatedAt, Descending: true)
            : null;
    }

    /// <summary>The one <c>order</c> a screen may arrive with.</summary>
    public const string RecentlyChangedOrder = "updated_at.desc";
```

- [ ] **Step 9: Run the tests**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests/MMLib.Alvo.Admin.Tests.csproj`
Expected: PASS, including the existing `GridQueryTests` (the new parameter defaults to `null`).

- [ ] **Step 10: ring1 and commit**

Run: `scripts/test-ring1`. Expected: PASS, `PublicApi.MMLib.Alvo.Admin.verified.txt` unchanged (all new types are internal).

```bash
git add src/MMLib.Alvo.Admin/Components/Data test/MMLib.Alvo.Admin.Tests/Data
git commit -m "feat(admin): the audit stamp, who a writer reads as, and the change facets as a data query

Claude-Session: https://claude.ai/code/session_01TrcGmun8WAuVxN5FYAtu6H"
```

---

### Task 5: naming writers: the gateway's lookup and the page's directory

**Files:**
- Create: `src/MMLib.Alvo.Admin/Internal/PeopleLookup.cs`
- Modify: `src/MMLib.Alvo.Admin/Internal/ManagementGateway.cs` (`PeopleByIdAsync`, the refusal memo, `Invalidate`)
- Create: `src/MMLib.Alvo.Admin/Components/Data/ActorDirectory.cs`
- Test: `test/MMLib.Alvo.Admin.Tests/Internal/ManagementGatewayPeopleLookupTests.cs`,
  `test/MMLib.Alvo.Admin.Tests/Data/ActorDirectoryTests.cs`

**Interfaces:**
- Consumes: `IAlvoUserAdministration.FindAsync` (Task 2); `AuditStamp`, `ActorLabels` (Task 4).
- Produces:
  - `enum PeopleLookupState { Found, NotAllowed, Unavailable }`; `sealed record PeopleLookup(PeopleLookupState State, IReadOnlyList<AlvoUser> People)` with `static PeopleLookup NotAllowed`, `static PeopleLookup Unavailable`, `static PeopleLookup Found(IReadOnlyList<AlvoUser> people)` (namespace `MMLib.Alvo.Admin.Internal`)
  - `ManagementGateway.PeopleByIdAsync(IReadOnlyCollection<UserId> ids, CancellationToken ct) : Task<PeopleLookup>`
  - `sealed class ActorDirectory` with `const int MaxPerLookup = 50`, `PeopleLookupState State`, `IReadOnlyList<UserId> Needed(IEnumerable<AuditStamp> stamps, UserId? self)`, `void Learn(IReadOnlyCollection<UserId> asked, PeopleLookup lookup)`, `ActorLabels Labels(UserId? self, string? selfAddress)`

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Admin.Tests/Internal/ManagementGatewayPeopleLookupTests.cs`:

```csharp
using Microsoft.AspNetCore.Components.Authorization;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;
using NSubstitute;
using System.Security.Claims;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// Naming writers goes through the guarded port, and its refusal is an answer: below Admin the screen shows ids, and it
/// asks again only after the next navigation (#290, C3).
/// </summary>
public sealed class ManagementGatewayPeopleLookupTests
{
    private static readonly UserId _ada = UserId.New();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_admin_is_answered_the_people_found()
    {
        var people = Substitute.For<IAlvoUserAdministration>();
        people.FindAsync(Arg.Any<IReadOnlyCollection<UserId>>(), Arg.Any<CancellationToken>())
            .Returns([new AlvoUser { Id = _ada, Email = "ada@example.test", RoleNames = [] }]);

        var lookup = await Gateway(people).PeopleByIdAsync([_ada], Ct);

        lookup.State.ShouldBe(PeopleLookupState.Found);
        lookup.People.ShouldHaveSingleItem().Email.ShouldBe("ada@example.test");
    }

    [Fact]
    public async Task A_refusal_is_remembered_until_the_next_navigation()
    {
        var people = Substitute.For<IAlvoUserAdministration>();
        people.FindAsync(Arg.Any<IReadOnlyCollection<UserId>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<AlvoUser>>(new ManagementForbiddenException()));
        var gateway = Gateway(people);

        (await gateway.PeopleByIdAsync([_ada], Ct)).State.ShouldBe(PeopleLookupState.NotAllowed);
        (await gateway.PeopleByIdAsync([_ada], Ct)).State.ShouldBe(PeopleLookupState.NotAllowed);
        await people.Received(1).FindAsync(Arg.Any<IReadOnlyCollection<UserId>>(), Arg.Any<CancellationToken>());

        gateway.Invalidate();
        await gateway.PeopleByIdAsync([_ada], Ct);
        await people.Received(2).FindAsync(Arg.Any<IReadOnlyCollection<UserId>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_host_that_refuses_the_member_by_name_cannot_look_people_up()
    {
        var people = Substitute.For<IAlvoUserAdministration>();
        people.FindAsync(Arg.Any<IReadOnlyCollection<UserId>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<AlvoUser>>(new NotSupportedException("a directory")));

        (await Gateway(people).PeopleByIdAsync([_ada], Ct)).State.ShouldBe(PeopleLookupState.Unavailable);
    }

    [Fact]
    public async Task A_deployment_with_no_membership_store_asks_nobody()
        => (await Gateway(people: null).PeopleByIdAsync([_ada], Ct)).State.ShouldBe(PeopleLookupState.Unavailable);

    [Fact]
    public async Task No_ids_ask_nobody()
    {
        var people = Substitute.For<IAlvoUserAdministration>();

        (await Gateway(people).PeopleByIdAsync([], Ct)).State.ShouldBe(PeopleLookupState.Found);
        await people.DidNotReceive().FindAsync(Arg.Any<IReadOnlyCollection<UserId>>(), Arg.Any<CancellationToken>());
    }

    private static ManagementGateway Gateway(IAlvoUserAdministration? people)
    {
        var callers = Substitute.For<IAlvoAdminCallerResolver>();
        callers.ResolveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns((AlvoPrincipal?)null);
        var authentication = Substitute.For<AuthenticationStateProvider>();
        authentication.GetAuthenticationStateAsync()
            .Returns(Task.FromResult(new AuthenticationState(new ClaimsPrincipal())));

        return new ManagementGateway(
            Substitute.For<IAlvoManagement>(), people, callers, authentication, Substitute.For<IAlvoContextAccessor>());
    }
}
```

`test/MMLib.Alvo.Admin.Tests/Data/ActorDirectoryTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Data;
using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Tests.Data;

/// <summary>Which writers a page still needs named, and what the page knows once it asked (design A3).</summary>
public sealed class ActorDirectoryTests
{
    private static readonly UserId _me = UserId.New();

    [Fact]
    public void Nobody_the_framework_and_the_operator_are_never_looked_up()
    {
        var stamps = new[] { Stamp(null, ActorLabels.SystemId), Stamp(_me.Value, null) };

        new ActorDirectory().Needed(stamps, _me).ShouldBeEmpty();
    }

    [Fact]
    public void A_writer_is_asked_for_once_across_pages()
    {
        var directory = new ActorDirectory();
        var ada = Guid.NewGuid();
        var needed = directory.Needed([Stamp(ada, ada)], _me);

        directory.Learn(needed, PeopleLookup.Found([]));

        needed.ShouldBe([new UserId(ada)]);
        directory.Needed([Stamp(ada, null)], _me).ShouldBeEmpty("an id the lookup did not find is known to be nobody's");
    }

    [Fact]
    public void A_full_page_of_distinct_writers_is_one_lookup_of_at_most_fifty()
    {
        var stamps = Enumerable.Range(0, 25).Select(_ => Stamp(Guid.NewGuid(), Guid.NewGuid())).ToList();

        new ActorDirectory().Needed(stamps, _me).Count.ShouldBe(ActorDirectory.MaxPerLookup);
    }

    [Fact]
    public void A_lookup_that_could_not_run_leaves_ids_without_a_claim_and_asks_again_later()
    {
        var directory = new ActorDirectory();
        var ada = Guid.NewGuid();
        directory.Learn(directory.Needed([Stamp(ada, ada)], _me), PeopleLookup.Unavailable);

        directory.State.ShouldBe(PeopleLookupState.Unavailable);
        directory.Labels(_me, null).Of(ada).Hint.ShouldBeNull();
        directory.Needed([Stamp(ada, ada)], _me).ShouldNotBeEmpty();
    }

    [Fact]
    public void Found_people_read_as_their_address()
    {
        var directory = new ActorDirectory();
        var ada = new AlvoUser { Id = UserId.New(), Email = "ada@example.test", RoleNames = [] };
        directory.Learn(directory.Needed([Stamp(ada.Id.Value, null)], _me), PeopleLookup.Found([ada]));

        directory.Labels(_me, null).Of(ada.Id.Value).Text.ShouldBe("ada@example.test");
    }

    private static AuditStamp Stamp(Guid? createdBy, Guid? updatedBy)
        => new(new AuditMark(true, DateTimeOffset.UnixEpoch, createdBy), new AuditMark(true, DateTimeOffset.UnixEpoch, updatedBy));
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet build test/MMLib.Alvo.Admin.Tests/MMLib.Alvo.Admin.Tests.csproj`
Expected: FAIL, `PeopleLookup`, `PeopleByIdAsync` and `ActorDirectory` are missing.

- [ ] **Step 3: `PeopleLookup`**

```csharp
namespace MMLib.Alvo.Admin.Internal;

/// <summary>What asking for people by id came to.</summary>
internal enum PeopleLookupState
{
    /// <summary>The lookup ran; the people it found are the answer, and an id it did not find is nobody's.</summary>
    Found,

    /// <summary>The operator may not read addresses: below Admin (the identity rule).</summary>
    NotAllowed,

    /// <summary>This deployment cannot look people up: no membership store, a host refusing the member, a fault.</summary>
    Unavailable,
}

/// <summary>The answer to one lookup of people by id.</summary>
/// <param name="State">What the lookup came to.</param>
/// <param name="People">The people found; empty unless <see cref="PeopleLookupState.Found"/>.</param>
internal sealed record PeopleLookup(PeopleLookupState State, IReadOnlyList<AlvoUser> People)
{
    /// <summary>Refused below Admin.</summary>
    public static PeopleLookup NotAllowed { get; } = new(PeopleLookupState.NotAllowed, []);

    /// <summary>Not possible on this deployment.</summary>
    public static PeopleLookup Unavailable { get; } = new(PeopleLookupState.Unavailable, []);

    /// <summary>The people found.</summary>
    public static PeopleLookup Found(IReadOnlyList<AlvoUser> people) => new(PeopleLookupState.Found, people);
}
```

- [ ] **Step 4: `ManagementGateway.PeopleByIdAsync`**

Add a field `private bool _peopleRefused;` beside `_generation`. In `Invalidate()`, add `_peopleRefused = false;`.
After `PeopleAsync`:

```csharp
    /// <summary>The people who wrote a page's rows, by id: one guarded lookup, whose refusal is an answer.</summary>
    /// <remarks>
    /// <para>
    /// <b>Below Admin the guard refuses, and that is the identity rule working</b> (#290 §4.4): the screen shows ids,
    /// which the Data API already shows that caller. The refusal is remembered until the next navigation (see
    /// <see cref="Invalidate"/>), so a screen asks at most once per visit, and the membership store is never reached
    /// (C3). Nothing here decides who may: the dashboard learns it from the core, as Access does.
    /// </para>
    /// <para>
    /// A deployment with no membership store, or one whose store refuses the member by name, cannot look anybody up;
    /// the screen then says so once, under the grid. Any other fault is the caller's to log: a failed lookup never
    /// blocks the rows.
    /// </para>
    /// </remarks>
    /// <param name="ids">Whose people to find; the page's directory keeps this within the port's bound of 50.</param>
    /// <param name="ct">Cancels the read.</param>
    public async Task<PeopleLookup> PeopleByIdAsync(IReadOnlyCollection<UserId> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (people is null)
        {
            return PeopleLookup.Unavailable;
        }

        if (_peopleRefused)
        {
            return PeopleLookup.NotAllowed;
        }

        return ids.Count == 0 ? PeopleLookup.Found([]) : await FindPeopleAsync(ids, ct).ConfigureAwait(false);
    }

    private async Task<PeopleLookup> FindPeopleAsync(IReadOnlyCollection<UserId> ids, CancellationToken ct)
    {
        try
        {
            return PeopleLookup.Found(await AsOperatorAsync(() => Administration.FindAsync(ids, ct), ct).ConfigureAwait(false));
        }
        catch (ManagementForbiddenException)
        {
            _peopleRefused = true;
            return PeopleLookup.NotAllowed;
        }
        catch (NotSupportedException)
        {
            return PeopleLookup.Unavailable;
        }
    }
```

- [ ] **Step 5: `ActorDirectory`**

```csharp
using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Components.Data;

/// <summary>
/// The writers the Data screen has asked about, so each is looked up once and a page costs at most one lookup
/// (design A3).
/// </summary>
/// <remarks>
/// An id the lookup did not find is remembered as nobody's, and is not asked about again. An id asked about while the
/// lookup could not run is not remembered, so a later page asks again once the store is back.
/// </remarks>
internal sealed class ActorDirectory
{
    /// <summary>The most ids one lookup names: the port's bound.</summary>
    public const int MaxPerLookup = 50;

    private readonly Dictionary<Guid, AlvoUser> _people = [];
    private readonly HashSet<Guid> _asked = [];

    /// <summary>What the last lookup came to; <see cref="PeopleLookupState.Found"/> before any.</summary>
    public PeopleLookupState State { get; private set; } = PeopleLookupState.Found;

    /// <summary>The writers of <paramref name="stamps"/> not yet asked about, at most <see cref="MaxPerLookup"/>.</summary>
    public IReadOnlyList<UserId> Needed(IEnumerable<AuditStamp> stamps, UserId? self)
    {
        ArgumentNullException.ThrowIfNull(stamps);

        return [.. stamps
            .SelectMany(stamp => new[] { stamp.Created.By, stamp.Updated.By })
            .OfType<Guid>()
            .Where(id => id != ActorLabels.SystemId && id != self?.Value && !_asked.Contains(id))
            .Distinct()
            .Take(MaxPerLookup)
            .Select(id => new UserId(id))];
    }

    /// <summary>Takes in what a lookup of <paramref name="asked"/> answered.</summary>
    public void Learn(IReadOnlyCollection<UserId> asked, PeopleLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(lookup);

        State = lookup.State;
        if (lookup.State != PeopleLookupState.Found)
        {
            return;
        }

        _asked.UnionWith(asked.Select(id => id.Value));
        foreach (var person in lookup.People)
        {
            _people[person.Id.Value] = person;
        }
    }

    /// <summary>Who each writer reads as, with what is known now.</summary>
    public ActorLabels Labels(UserId? self, string? selfAddress)
        => new(self, selfAddress, _people, lookedUp: State == PeopleLookupState.Found && _asked.Count > 0);
}
```

- [ ] **Step 6: Run the tests, ring1, commit**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests/MMLib.Alvo.Admin.Tests.csproj`, then `scripts/test-ring1`.
Expected: PASS.

```bash
git add src/MMLib.Alvo.Admin test/MMLib.Alvo.Admin.Tests
git commit -m "feat(admin): name a page's writers in one guarded lookup, and remember a refusal

Claude-Session: https://claude.ai/code/session_01TrcGmun8WAuVxN5FYAtu6H"
```

---

### Task 6: the *Changed* column, its sort, the phone card, and the not-audited note

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Data/EntityData.Audit.cs` (partial: audit state and methods)
- Modify: `src/MMLib.Alvo.Admin/Components/Data/EntityData.razor`, `EntityData.razor.cs`
- Modify: `src/MMLib.Alvo.Admin/Components/Data/RecordGrid.razor`, `RecordGridScope.cs`
- Modify: `src/MMLib.Alvo.Admin/AlvoAdminServiceCollectionExtensions.cs` (`TryAddSingleton(TimeProvider.System)`)
- Modify: `src/MMLib.Alvo.Admin/wwwroot/alvo.css` (`.a-cell--changed`, `.a-changed`, `.a-changed__by`, `.a-row-card__pair--wide`)
- Create: `test/MMLib.Alvo.Host.Tests/AuditColumnExamplesTests.cs` (A1, over every example)
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/AuditWorlds.cs` (`AuditWorld`, `NoPeopleWorld`, `CountingFieldServiceWorld`, `AuditSeed`)
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/AuditGridScenarios.cs`, `AuditIdentityScenarios.cs`,
  `AuditNoPeopleScenarios.cs`, `AuditScopedScenarios.cs`

**Interfaces:**
- Consumes: Task 4 and Task 5 types; `ManagementGateway.PeopleByIdAsync`, `ManagementGateway.AuthorAsync`.
- Produces: `RecordGridScope.Audit` (a new last positional parameter, `AuditView? Audit`); in `EntityData`:
  `_facets` (`AuditFacets`), `_actors` (`ActorLabels`), `_now`, `_self`, `_selfAddress`, `_directory`,
  `NarrowToAsync(ActorLabel)` (for Task 7, which writes its body), `RecordAuditOf(AlvoRecord)` (Task 8), and the
  test ids `changed-sort`, `changed-cell`, `changed-by`, `changed-card`, `not-audited-note`, `people-unresolvable`.

- [ ] **Step 1: Write the failing A1 fact over every example**

`test/MMLib.Alvo.Host.Tests/AuditColumnExamplesTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Data;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// The Changed column is added beside an audited entity's content columns and never among them: the content columns
/// are exactly those the same entity would show without audit (#290, A1).
/// </summary>
public sealed class AuditColumnExamplesTests
{
    public static TheoryData<string, string> AuditedEntities()
    {
        var data = new TheoryData<string, string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepositoryRoot.Find(), "examples"), "*.alvo.json",
                     SearchOption.AllDirectories).Where(file => !file.Contains("_negative", StringComparison.Ordinal)))
        {
            foreach (var entity in DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(File.ReadAllText(file))).Entities
                         .Where(entity => entity.Audit))
            {
                data.Add(file, entity.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AuditedEntities))]
    public void Audit_does_not_change_which_content_columns_an_entity_shows(string file, string name)
    {
        var json = File.ReadAllText(file);
        var entity = DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(json)).Entities.Single(e => e.Name == name);
        var masks = DescriptorLens.Masks(json, name);
        var unaudited = entity with
        {
            Audit = false,
            Fields = [.. entity.Fields.Where(field => !AlvoManagedColumns.Audit.Contains(field.Name))],
        };

        GridColumns.Choose(entity, masks).Select(field => field.Name)
            .ShouldBe(GridColumns.Choose(unaudited, masks).Select(field => field.Name));
    }
}
```

Check the parse method's name (`AlvoDescriptor.Parse`, as `DescriptorToSchemaMapperTests` uses it) and whether
`EntitySchema` supports `with`; adapt if either differs. This fact pins existing behaviour, and it guards the new code
against letting *Changed* count towards `GridColumns.Cap` (X6).

- [ ] **Step 2: Write the failing grid scenarios**

`test/MMLib.Alvo.Admin.Tests.EndToEnd/AuditWorlds.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Identity;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The bike-workshop example, whose membership store counts every lookup by id that reaches it: the counting fake
/// design A3 and A6 are measured by (C3: at the store, behind the guard).
/// </summary>
public class AuditWorld : AdminWorld
{
    private int _lookups;

    /// <summary>How many <c>FindAsync</c> calls reached the membership store so far.</summary>
    public int Lookups => Volatile.Read(ref _lookups);

    /// <inheritdoc/>
    protected override string Descriptor => Descriptors.BikeWorkshop;

    /// <inheritdoc/>
    protected override void Configure(IServiceCollection services)
        => CountingPeople.Wrap(services, () => Interlocked.Increment(ref _lookups));
}

/// <summary>The bike-workshop example on a host with no membership administration (design §4.4, scenario 9).</summary>
public sealed class NoPeopleWorld : AdminWorld
{
    /// <inheritdoc/>
    protected override string Descriptor => Descriptors.BikeWorkshop;

    /// <inheritdoc/>
    protected override void Configure(IServiceCollection services)
    {
        foreach (var shipped in services
                     .Where(entry => entry.ServiceType == typeof(IAlvoUserAdministration) && !entry.IsKeyedService)
                     .ToList())
        {
            services.Remove(shipped);
        }
    }
}

/// <summary>The field-service example with its lookups counted, for its audited, tenant-scoped <c>work_orders</c> (C5).</summary>
public sealed class CountingFieldServiceWorld : AdminWorld
{
    private int _lookups;

    /// <summary>How many <c>FindAsync</c> calls reached the membership store so far.</summary>
    public int Lookups => Volatile.Read(ref _lookups);

    /// <inheritdoc/>
    protected override void Configure(IServiceCollection services)
        => CountingPeople.Wrap(services, () => Interlocked.Increment(ref _lookups));
}

/// <summary>Wraps the unguarded membership implementation, the one the guard resolves, so its lookups are counted.</summary>
internal static class CountingPeople
{
    public static void Wrap(IServiceCollection services, Action counted)
    {
        var shipped = services.Last(entry => entry.ServiceType == typeof(IAlvoUserAdministration)
            && entry.IsKeyedService && Equals(entry.ServiceKey, AlvoUserAdministration.UnguardedKey));
        services.Remove(shipped);
        services.Add(new ServiceDescriptor(
            typeof(IAlvoUserAdministration), AlvoUserAdministration.UnguardedKey,
            (provider, key) => new Counting(Build(shipped, provider, key), counted), shipped.Lifetime));
    }

    private static IAlvoUserAdministration Build(ServiceDescriptor shipped, IServiceProvider provider, object? key)
        => (IAlvoUserAdministration)(shipped.KeyedImplementationFactory?.Invoke(provider, key)
            ?? shipped.KeyedImplementationInstance
            ?? ActivatorUtilities.CreateInstance(provider, shipped.KeyedImplementationType!));

    private sealed class Counting(IAlvoUserAdministration inner, Action counted) : IAlvoUserAdministration
    {
        public Task<IReadOnlyList<AlvoUser>> FindAsync(
            IReadOnlyCollection<UserId> users, CancellationToken cancellationToken = default)
        {
            counted();
            return inner.FindAsync(users, cancellationToken);
        }

        public Task<AlvoUserPage> ListAsync(AlvoUserQuery query, CancellationToken cancellationToken = default)
            => inner.ListAsync(query, cancellationToken);

        public Task<AlvoUser> CreateAsync(AlvoUserCreation creation, CancellationToken cancellationToken = default)
            => inner.CreateAsync(creation, cancellationToken);

        public Task<AlvoUser> SetRolesAsync(
            UserId user, IReadOnlyList<string> roleNames, CancellationToken cancellationToken = default)
            => inner.SetRolesAsync(user, roleNames, cancellationToken);

        public Task<AlvoUser> SetTenantAsync(UserId user, TenantId? tenant, CancellationToken cancellationToken = default)
            => inner.SetTenantAsync(user, tenant, cancellationToken);

        public Task<AlvoUser> SetDisabledAsync(UserId user, bool disabled, CancellationToken cancellationToken = default)
            => inner.SetDisabledAsync(user, disabled, cancellationToken);

        public Task<AlvoUser> ClearLockoutAsync(UserId user, CancellationToken cancellationToken = default)
            => inner.ClearLockoutAsync(user, cancellationToken);

        public Task<AlvoCredentialToken> IssueCredentialTokenAsync(UserId user, CancellationToken cancellationToken = default)
            => inner.IssueCredentialTokenAsync(user, cancellationToken);
    }
}

/// <summary>Rows and people of the bike-workshop example, written the way a real deployment holds them.</summary>
internal static class AuditSeed
{
    /// <summary>The identity the demo's dev key authenticates as: not a dashboard account (design §2.3).</summary>
    public static UserId KeyIdentity { get; } = new(Guid.Parse("5eed0000-0000-4000-8000-000000000001"));

    /// <summary>A writer the technicians' rules admit, as <paramref name="user"/>.</summary>
    public static AlvoContext Writer(UserId user) => new()
    {
        User = user,
        Roles = new HashSet<Role> { Role.Admin, Role.Authenticated },
    };

    /// <summary>A technician named <paramref name="name"/>, written as <paramref name="writer"/>.</summary>
    public static async Task<AlvoRecord> TechnicianAsync(IServiceProvider services, AlvoContext writer, string name)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAlvoData>().CreateAsync(
            "technicians",
            new Dictionary<string, object?>
            {
                ["full_name"] = name,
                ["email"] = $"{name.Replace(' ', '.').ToLowerInvariant()}@velo-dielna.example",
                ["specialization"] = "general",
                ["hourly_rate"] = 30m,
            },
            writer);
    }

    /// <summary>A person the membership store holds, with no password.</summary>
    public static async Task<AlvoUser> PersonAsync(IServiceProvider services, string email, params string[] roles)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredKeyedService<IAlvoUserAdministration>(AlvoUserAdministration.UnguardedKey)
            .CreateAsync(new AlvoUserCreation(email, roles));
    }

    /// <summary>A person who can sign in with <paramref name="password"/>: created, given a token, and the token redeemed.</summary>
    public static async Task<AlvoUser> SignablePersonAsync(
        IServiceProvider services, string email, string password, params string[] roles)
    {
        var person = await PersonAsync(services, email, roles);
        using var scope = services.CreateScope();
        var token = await scope.ServiceProvider
            .GetRequiredKeyedService<IAlvoUserAdministration>(AlvoUserAdministration.UnguardedKey)
            .IssueCredentialTokenAsync(person.Id);
        (await scope.ServiceProvider.GetRequiredService<AlvoSignIn>().SetPasswordAsync(email, token.Token, password))
            .ShouldBe(AlvoPasswordSetOutcome.Set);
        return person;
    }

    /// <summary>A record's stored <c>updated_at</c>, as the operator's browser draws it (<c>HH:mm</c> in its zone).</summary>
    public static async Task<string> ClockAsync(AdminSession session, IServiceProvider services, string entity, Guid id)
    {
        using var scope = services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<IAlvoData>()
            .GetAsync(entity, id, Writer(UserId.New()));
        var at = (DateTimeOffset)row.ShouldNotBeNull()["updated_at"]!;
        var east = await session.Page.EvaluateAsync<int>("() => -new Date().getTimezoneOffset()");
        return at.ToOffset(TimeSpan.FromMinutes(east)).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }
}
```

If `row["updated_at"]` comes back as `DateTime` or text on SQLite, read it through the same rule as
`AuditStamp.Instant` (Task 4); keep the helper's contract, which is `HH:mm` in the browser's zone.

`test/MMLib.Alvo.Admin.Tests.EndToEnd/AuditGridScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The Changed column on an audited entity: who changed a row last and when, a sort, a phone card, and nothing on an
/// entity that is not audited (#290 §4.1, §4.5; scenarios 1, 3, 10, 12; A1, A7).
/// </summary>
/// <remarks>
/// Its own world. Every fact writes technicians under names only it uses and finds them by those names, so no fact
/// depends on another's rows or on the order xUnit runs them in.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class AuditGridScenarios(AuditWorld world) : IClassFixture<AuditWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_record_created_now_reads_its_time_over_you_in_Changed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/technicians");
        await session.Page.GetByTestId("record-new").ClickAsync();
        var sheet = session.Dialog("record-sheet");
        await sheet.Locator("#rf-full_name").FillAsync("Grid Created Now");
        await sheet.Locator("#rf-email").FillAsync("grid.created@velo-dielna.example");
        await sheet.Locator("#rf-hourly_rate").FillAsync("31");
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "general", Exact = true }).ClickAsync();
        await sheet.GetByTestId("record-save").ClickAsync();

        var row = session.Page.GetByTestId("grid-row").Filter(new() { HasText = "Grid Created Now" });
        await row.WaitForAsync();
        var cell = row.GetByTestId("changed-cell");
        (await cell.GetByTestId("changed-by").InnerTextAsync()).Trim().ShouldBe("you");
        var id = await IdOfAsync("Grid Created Now");
        var clock = await AuditSeed.ClockAsync(session, world.Services, "technicians", id);
        await cell.GetByText(clock, new() { Exact = true }).WaitForAsync();
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Changed_sorts_ascending_then_descending_then_off_in_the_ports_order()
    {
        foreach (var name in new[] { "Sort One", "Sort Two", "Sort Three" })
        {
            await AuditSeed.TechnicianAsync(world.Services, AuditSeed.Writer(UserId.New()), name);
        }

        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/technicians");
        var header = session.Page.GetByRole(AriaRole.Columnheader, new() { Name = "Changed" });

        await session.Page.GetByTestId("changed-sort").ClickAsync();
        await Expect(header, "ascending");
        await session.Page.GetByTestId("changed-sort").ClickAsync();
        await Expect(header, "descending");
        (await GridNamesAsync(session)).ShouldBe(await PortNamesAsync(), "the grid reads /api?order=updated_at.desc");
        await session.Page.GetByTestId("changed-sort").ClickAsync();
        (await header.GetAttributeAsync("aria-sort")).ShouldBeNull();
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_entity_that_is_not_audited_has_no_Changed_column_and_says_why()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/order_lines");

        (await session.Page.GetByTestId("not-audited-note").InnerTextAsync()).ShouldBe(
            "order_lines is not audited, so it keeps no record of who created or changed a row. Audit is chosen when an entity is created.");
        (await session.Page.GetByRole(AriaRole.Columnheader, new() { Name = "Changed" }).CountAsync()).ShouldBe(0);
        (await session.Page.GetByTestId("audit-facets").CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task At_1280_with_the_drawer_open_service_orders_shows_Changed_without_scrolling_the_grid()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width: 1280);
        await session.GoAsync("/data/service_orders");
        await session.Page.GetByRole(AriaRole.Columnheader, new() { Name = "Changed" }).WaitForAsync();

        var overflow = await session.Page.GetByTestId("record-grid")
            .EvaluateAsync<int>("grid => grid.parentElement.scrollWidth - grid.parentElement.clientWidth");
        overflow.ShouldBeLessThanOrEqualTo(1, "Changed is the column a scroll inside the grid would cut off");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task On_a_phone_a_card_reads_Changed_across_its_width_and_nothing_scrolls_sideways()
    {
        var writer = new UserId(Guid.Parse("7a000000-0000-4000-8000-000000000007"));
        await AuditSeed.TechnicianAsync(world.Services, AuditSeed.Writer(writer), "Phone Card");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width: 375);
        await session.GoAsync("/data/technicians");

        var card = session.Page.GetByTestId("row-card").Filter(new() { HasText = "Phone Card" });
        (await card.GetByTestId("changed-card").InnerTextAsync()).ShouldEndWith(" · 7a000000…0007", Case.Sensitive,
            "a writer the lookup did not find reads as a short id after the time");
        await session.AssertNoHorizontalScrollAsync();
        session.AssertConsoleClean();
    }

    private static Task Expect(ILocator header, string sort)
        => header.Page.WaitForFunctionAsync(
            "([name, sort]) => [...document.querySelectorAll('th')].some(th => th.textContent.includes(name) && th.getAttribute('aria-sort') === sort)",
            new object[] { "Changed", sort });

    private static async Task<IReadOnlyList<string>> GridNamesAsync(AdminSession session)
        => await session.Page.GetByTestId("grid-row")
            .EvaluateAllAsync<string[]>("rows => rows.map(row => row.cells[0].innerText.trim())");

    private async Task<IReadOnlyList<string>> PortNamesAsync()
    {
        using var scope = world.Services.CreateScope();
        var page = await scope.ServiceProvider.GetRequiredService<IAlvoData>().QueryAsync(
            new AlvoQuery { Entity = "technicians", Sort = [new AlvoSort("updated_at", Descending: true)], Limit = 25 },
            AuditSeed.Writer(UserId.New()));
        return [.. page.Items.Select(row => (string)row["full_name"]!)];
    }

    private async Task<Guid> IdOfAsync(string name)
    {
        using var scope = world.Services.CreateScope();
        var page = await scope.ServiceProvider.GetRequiredService<IAlvoData>().QueryAsync(
            new AlvoQuery
            {
                Entity = "technicians",
                Filter = new AlvoComparison("full_name", AlvoFilterOperator.Eq, name),
            },
            AuditSeed.Writer(UserId.New()));
        return FieldServiceSeed.IdOf(page.Items.ShouldHaveSingleItem());
    }
}
```

Add `using Microsoft.Extensions.DependencyInjection;` and `using MMLib.Alvo.Data;` at the top of the file.

`test/MMLib.Alvo.Admin.Tests.EndToEnd/AuditIdentityScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Who a writer reads as, by the identity rule: an Admin sees a person's address and an unknown id called what it is;
/// a viewer sees ids only, and no lookup reaches the membership store (#290 §4.4; scenarios 7, 8; A3, A6).
/// </summary>
/// <remarks>Its own world. Lookup counts are read as deltas around each fact's own steps.</remarks>
/// <param name="world">The running host and browser, whose membership store counts lookups.</param>
public sealed class AuditIdentityScenarios(AuditWorld world) : IClassFixture<AuditWorld>
{
    private const string ViewerPassword = "Alvo-e2e-Viewer-Pass-1";

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_admin_sees_a_second_persons_address_and_a_key_identity_as_not_a_dashboard_account()
    {
        var katarina = await AuditSeed.PersonAsync(world.Services, "katarina.novotna@velo-dielna.example", "manager");
        await AuditSeed.TechnicianAsync(world.Services, AuditSeed.Writer(katarina.Id), "Identity By Katarina");
        await AuditSeed.TechnicianAsync(world.Services, AuditSeed.Writer(AuditSeed.KeyIdentity), "Identity By Key");
        await using var admin = await world.SignInAsync(TestContext.Current.CancellationToken);
        var before = world.Lookups;

        await admin.GoAsync("/data/technicians");
        var byKatarina = Row(admin, "Identity By Katarina").GetByTestId("changed-by");
        (await byKatarina.InnerTextAsync()).Trim().ShouldBe("katarina.novotna");
        (await byKatarina.GetAttributeAsync("title")).ShouldBe("katarina.novotna@velo-dielna.example");
        var byKey = Row(admin, "Identity By Key").GetByTestId("changed-by");
        (await byKey.InnerTextAsync()).Trim().ShouldBe("5eed0000…0001");
        (world.Lookups - before).ShouldBe(1, "one lookup names the whole page (A3)");
        admin.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_viewer_sees_ids_and_no_address_and_no_lookup_reaches_the_store()
    {
        var katarina = await AuditSeed.PersonAsync(world.Services, "katarina.viewer-case@velo-dielna.example", "manager");
        await AuditSeed.TechnicianAsync(world.Services, AuditSeed.Writer(katarina.Id), "Viewer Sees Id");
        await AuditSeed.SignablePersonAsync(world.Services, "reception@velo-dielna.example", ViewerPassword, "reception");
        var before = world.Lookups;

        await using var viewer = await world.SignInAsAsync(
            "reception@velo-dielna.example", ViewerPassword, TestContext.Current.CancellationToken);
        await viewer.GoAsync("/data/technicians");
        var cell = Row(viewer, "Viewer Sees Id").GetByTestId("changed-by");
        (await cell.InnerTextAsync()).Trim().ShouldBe(RefShort(katarina.Id));

        (await viewer.Content.InnerHTMLAsync()).ShouldNotContain(katarina.Email, Case.Insensitive);
        (world.Lookups - before).ShouldBe(0, "below Admin the guard refuses before the store (C3, A6)");
        viewer.AssertConsoleClean();
    }

    private static ILocator Row(AdminSession session, string name)
        => session.Page.GetByTestId("grid-row").Filter(new() { HasText = name });

    private static string RefShort(UserId id)
    {
        var text = id.Value.ToString();
        return $"{text[..8]}…{text[^4..]}";
    }
}
```

The record sheet's side of scenario 8 (the hint and the copy action) is Task 8's `AuditRecordScenarios`.

`test/MMLib.Alvo.Admin.Tests.EndToEnd/AuditNoPeopleScenarios.cs`:

```csharp
namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>A deployment that cannot look people up shows ids and says so once; the rows still load (scenario 9).</summary>
/// <param name="world">A host with no membership administration.</param>
public sealed class AuditNoPeopleScenarios(NoPeopleWorld world) : IClassFixture<NoPeopleWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Without_a_membership_store_changes_show_ids_and_one_note()
    {
        var writer = new UserId(Guid.Parse("9b000000-0000-4000-8000-000000000009"));
        await AuditSeed.TechnicianAsync(world.Services, AuditSeed.Writer(writer), "No People Row");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/technicians");

        var row = session.Page.GetByTestId("grid-row").Filter(new() { HasText = "No People Row" });
        (await row.GetByTestId("changed-by").InnerTextAsync()).Trim().ShouldBe("9b000000…0009");
        (await session.Page.GetByTestId("people-unresolvable").InnerTextAsync())
            .ShouldBe("People cannot be looked up on this deployment, so changes show the caller's id.");
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }
}
```

`test/MMLib.Alvo.Admin.Tests.EndToEnd/AuditScopedScenarios.cs`:

```csharp
namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// An audited, tenant-scoped entity read with no tenant is the unchanged out-of-scope state, with no read and no lookup
/// (scenario 13; C5: field-service <c>work_orders</c>).
/// </summary>
/// <param name="world">The field-service host, whose lookups are counted.</param>
public sealed class AuditScopedScenarios(CountingFieldServiceWorld world) : IClassFixture<CountingFieldServiceWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_scoped_audited_entity_without_a_tenant_is_out_of_scope_and_looks_nobody_up()
    {
        var before = world.Lookups;
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/work_orders");

        await session.Content.GetByText("You hold no tenant, and this entity is scoped to one").WaitForAsync();
        (await session.Page.GetByTestId("audit-facets").CountAsync()).ShouldBe(0);
        (world.Lookups - before).ShouldBe(0);
        session.AssertConsoleClean();
    }
}
```

- [ ] **Step 3: Run the new scenarios to see them fail**

Run: `scripts/test-admin-e2e --filter "AuditGridScenarios|AuditIdentityScenarios|AuditNoPeopleScenarios|AuditScopedScenarios"`
Expected: FAIL, no *Changed* column header and no `changed-cell`/`not-audited-note` test ids. The scoped fact may
already pass; that is fine, because it pins that the new code keeps it so.

- [ ] **Step 4: Register the clock**

In `AddAlvoAdmin` (`AlvoAdminServiceCollectionExtensions`), before the `ManagementGateway` registration:

```csharp
        /* The Data screen reads "now" for the change facets and the Changed column's "today". The core registers the
           same TryAdd; this keeps a host that composes the dashboard over its own services working. */
        services.TryAddSingleton(TimeProvider.System);
```

- [ ] **Step 5: The grid scope**

Add to `RecordGridScope` a last positional parameter `AuditView? Audit` with the doc
`/// <param name="Audit">How the Changed column is drawn; <see langword="null"/> on an entity that is not audited.</param>`.

- [ ] **Step 6: The `EntityData` audit partial**

`src/MMLib.Alvo.Admin/Components/Data/EntityData.Audit.cs`:

```csharp
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Components.Data;

/// <summary>The audit half of the Data screen: who changed each row and when, and the facets that narrow by it (#290).</summary>
public partial class EntityData
{
    private readonly ActorDirectory _directory = new();
    private AuditFacets _facets = AuditFacets.None;
    private AuditFacet _lastNarrowed = AuditFacet.Window;
    private ActorLabels _actors = ActorLabels.None;
    private DateTimeOffset _now;
    private UserId? _self;
    private string? _selfAddress;

    /// <summary>Whether the page on screen is of an audited entity.</summary>
    private bool Audited => _entity is { Audit: true };

    /// <summary>What the grid draws in the Changed column; nothing on an entity that is not audited.</summary>
    private AuditView? AuditViewOf()
        => Audited && _entity!.Fields.FirstOrDefault(field => field.Name == AlvoManagedColumns.UpdatedAt) is { } updatedAt
            ? new AuditView(updatedAt, _actors, _now, Interop.UtcOffset, NarrowToAsync)
            : null;

    /// <summary>The operator as a writer: their id, unless they resolve to none, and their address for "you"'s title.</summary>
    private async Task LearnSelfAsync()
    {
        _self = _context?.User is { } user && user != default ? user : null;
        _selfAddress = await Gateway.AuthorAsync();
    }

    /// <summary>
    /// Who wrote <paramref name="rows"/>, with at most one lookup (design A3); nothing asked on an entity that is not
    /// audited. A lookup that fails is logged and leaves ids, never a refusal over rows the screen can draw.
    /// </summary>
    private async Task<ActorLabels> ActorsAsync(IReadOnlyList<AlvoRecord> rows)
    {
        if (!Audited)
        {
            return ActorLabels.None;
        }

        var needed = _directory.Needed(rows.Select(AuditStamp.Of), _self);
        if (needed.Count > 0)
        {
            _directory.Learn(needed, await LookUpAsync(needed));
        }

        return _directory.Labels(_self, _selfAddress);
    }

    private async Task<PeopleLookup> LookUpAsync(IReadOnlyList<UserId> ids)
    {
        try
        {
            return await Gateway.PeopleByIdAsync(ids, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AdminProblem.Absorb(exception, Logger, Site);
            return PeopleLookup.Unavailable;
        }
    }

    /// <summary>Whether the screen says, once, that people cannot be looked up here.</summary>
    private bool PeopleUnresolvable => Audited && _directory.State == PeopleLookupState.Unavailable;

    /// <summary>Narrows the grid to the writer a Changed cell's link named (§3.9); Task 7 draws the chip.</summary>
    private Task NarrowToAsync(ActorLabel actor) => Task.CompletedTask;
}
```

(Task 7 replaces `NarrowToAsync`'s body. If an analyzer refuses a method that ignores its argument, add
`_ = actor;` or a discard parameter name.)

- [ ] **Step 7: Wire it into `EntityData.razor.cs`**

1. `@inject TimeProvider Time` in `EntityData.razor` (with the other injects).
2. In `OpenEntityAsync`, after `_who = await WhoAsync();`, add `await LearnSelfAsync();`.
3. In `ReadPageAsync`, read the clock once and resolve the writers with the labels. Replace the body's `try` block with:

```csharp
        try
        {
            var now = Time.GetUtcNow();
            var query = GridQuery.Page(
                _entity!, GridQuery.Filter(_searchable, _search, _revealing, _facets.Filter(now)), _sort, PageSize, _cursor);
            var context = await Records.ContextAsync(CancellationToken.None);
            var page = await Records.PageAsync(query, context, CancellationToken.None);
            var labels = await LabelsAsync(page, context);
            var actors = await ActorsAsync(page.Items);
            if (version != _loadVersion)
            {
                return;
            }

            Show(page, labels, actors, now);
        }
```

and add below it:

```csharp
    /// <summary>Puts a page read on screen, with its labels, its writers and the instant it was read at.</summary>
    private void Show(
        AlvoPage page, IReadOnlyDictionary<string, IReadOnlyDictionary<Guid, string>> labels, ActorLabels actors,
        DateTimeOffset now)
    {
        _page = page;
        _labels = labels;
        _actors = actors;
        _now = now;
        _problem.Clear();
        _refreshing = false;
    }
```

4. `GridScope` passes `Audit: AuditViewOf()` as the new last argument.

- [ ] **Step 8: Draw it in `EntityData.razor`**

1. Under the `<PageTitle>`, add `@OperatorClock.On(StateHasChanged)`. Until the circuit learns the zone, times say UTC.
2. Right after the "Reading as" `<p class="a-note">`, add:

```razor
        @if (!_entity.Audit)
        {
            <p class="a-note" data-testid="not-audited-note">@AuditWords.NotAudited(EntityName)</p>
        }
```

3. After the grid's `else { <CascadingValue …> … }` block, still inside the `!OutOfScope` branch, add:

```razor
            @if (PeopleUnresolvable)
            {
                @* A state of this deployment, said once, not an error (design §4.4). *@
                <p class="a-note" data-testid="people-unresolvable">@AuditWords.PeopleUnresolvable</p>
            }
```

- [ ] **Step 9: Draw the column in `RecordGrid.razor`**

In `<thead>`, after the content columns' `@foreach` and before `<th></th>`:

```razor
                    @if (Scope.Audit is { } audit)
                    {
                        <th scope="col" class="a-cell--changed" title="@audit.UpdatedAt.Name" aria-sort="@AriaSort(audit.UpdatedAt)">
                            <button type="button" class="a-grid__sort" data-testid="changed-sort"
                                    @onclick="() => OnSort.InvokeAsync(audit.UpdatedAt)">
                                @AuditWords.Changed<span class="a-grid__arrow" aria-hidden="true">@Arrow(audit.UpdatedAt)</span>
                            </button>
                        </th>
                    }
```

In each row, after the content cells' `@foreach` and before the actions `<td>`:

```razor
                        @if (Scope.Audit is { } audit)
                        {
                            <td class="a-cell--changed">@Changed(audit, row)</td>
                        }
```

In each phone card's `<dl class="a-row-card__meta">`, after the pairs:

```razor
                @if (Scope.Audit is { } audit)
                {
                    <div class="a-row-card__pair a-row-card__pair--wide">
                        <dt title="@audit.UpdatedAt.Name">@AuditWords.Changed</dt>
                        <dd data-testid="changed-card">@audit.Line(AuditStamp.Of(row).Updated)</dd>
                    </div>
                }
```

In `@code`:

```csharp
    /// <summary>
    /// A Changed cell: the time on the operator's clock, the whole instant as its title, and under it who, as a link
    /// that narrows the grid to them (§3.9) and does not open the row.
    /// </summary>
    private static RenderFragment Changed(AuditView audit, AlvoRecord row)
    {
        var mark = AuditStamp.Of(row).Updated;
        if (!mark.Shown)
        {
            return @<span class="a-note">@AuditWords.Hidden</span>;
        }

        var actor = audit.Actors.Of(mark.By);
        return @<span class="a-changed" data-testid="changed-cell">
            <time datetime="@AuditView.Iso(mark.At)" title="@AuditView.Iso(mark.At)">@audit.Clock(mark)</time>
            <button type="button" class="a-link a-changed__by" data-testid="changed-by" title="@actor.Title"
                    aria-label="@AuditWords.ShowOnly(actor.Text)" @onclick="() => audit.Narrow(actor)"
                    @onclick:stopPropagation="true">@actor.Short</button>
        </span>;
    }
```

`AriaSort`/`Arrow` take a `FieldSchema` already, so they work for `audit.UpdatedAt`.

- [ ] **Step 10: Styles**

In `alvo.css`, in the `@layer components` block that holds `.a-row-card__pair` (keep to existing tokens only):

```css
  /* The Changed column: time over who, never wrapped, sized by its content (#290 §4.1). */
  .a-cell--changed {
    white-space: nowrap;
  }

  .a-changed {
    display: flex;
    flex-direction: column;
    gap: 1px;
    font-variant-numeric: tabular-nums;
  }

  .a-changed__by {
    align-self: flex-start;
    padding: 0;
    border: 0;
    background: none;
    font-size: var(--text-xs);
    cursor: pointer;
  }

  /* A card pair that takes the card's whole width: the one line "14:05 · you". */
  .a-row-card__pair--wide {
    grid-column: 1 / -1;
  }
```

`StylesheetHygieneTests` requires every class the product names to be defined, and every defined class to be named.
All four are named in Step 9.

- [ ] **Step 11: Run and fix**

Run: `dotnet test --project test/MMLib.Alvo.Host.Tests/MMLib.Alvo.Host.Tests.csproj -- --filter-class "*AuditColumnExamplesTests"`, then
`scripts/test-admin-e2e --filter "AuditGridScenarios|AuditIdentityScenarios|AuditNoPeopleScenarios|AuditScopedScenarios|DataGridScenarios|RecordRevealScenarios"`.
Expected: PASS. `DataGridScenarios` and `RecordRevealScenarios` guard the grid's existing behaviour on field-service.

- [ ] **Step 12: ring1 and commit**

Run: `scripts/test-ring1` (`PatternLanguageTests`, `StylesheetHygieneTests`, `EndToEndSelectorTests` included).

```bash
git add src/MMLib.Alvo.Admin test/MMLib.Alvo.Host.Tests/AuditColumnExamplesTests.cs test/MMLib.Alvo.Admin.Tests.EndToEnd
git commit -m "feat(admin): the Data grid shows who changed each audited row last, and when

Claude-Session: https://claude.ai/code/session_01TrcGmun8WAuVxN5FYAtu6H"
```

---

### Task 7: the facet row (§3.9): window, By me, Changed by, and its empty state; API equivalence (A4)

**Files:**
- Modify: `src/MMLib.Alvo.Admin/Components/Data/EntityData.Audit.cs`, `EntityData.razor`, `EntityData.razor.cs`
  (`ResetForEntity` resets the facets)
- Modify: `src/MMLib.Alvo.Admin/wwwroot/alvo.css` (`.a-facets`, `.a-facet`, `.a-facet__remove`)
- Modify: `test/MMLib.Alvo.Admin.Tests/DesignSystem/PatternLanguageTests.cs` (one fact for §3.9)
- Create: `test/MMLib.Alvo.Host.Tests/descriptors/host-audited.alvo.json`
- Create: `test/MMLib.Alvo.Host.Tests/AuditFacetApiEquivalenceTests.cs`
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/AuditFacetScenarios.cs`, `AuditEmptyFacetScenarios.cs`

**Interfaces:**
- Consumes: `AuditFacets`, `ActorFacet`, `ChangeWindow`, `AuditFacet` (Task 4); `_facets`, `_lastNarrowed`, `_self`,
  `NarrowToAsync` (Task 6).
- Produces: the test ids `audit-facets`, `facet-by-me`, `facet-actor`, `facet-empty-clear`.

- [ ] **Step 1: Write the failing A4 fact**

`test/MMLib.Alvo.Host.Tests/descriptors/host-audited.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "host-audited",
  "description": "One audited entity, for the dashboard's change facets measured against the Data API.",
  "auth": {
    "providers": ["local"],
    "roles": ["admin"]
  },
  "entities": {
    "tickets": {
      "description": "A support ticket.",
      "audit": true,
      "fields": {
        "title": { "type": "string", "required": true, "maxLength": 60 }
      },
      "rules": {
        "list": "'authenticated' in @user.roles",
        "get": "'authenticated' in @user.roles",
        "create": "'authenticated' in @user.roles",
        "update": "'authenticated' in @user.roles"
      }
    }
  }
}
```

`test/MMLib.Alvo.Host.Tests/AuditFacetApiEquivalenceTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Admin.Components.Data;
using MMLib.Alvo.Data;
using MMLib.Alvo.Management;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// Every change facet is a query the Data API answers identically for the same caller: the grid never narrows rows in
/// a way <c>/api</c> could not (#290, A4; pattern language §3.9).
/// </summary>
/// <remarks>
/// The host's clock is moved between two batches of writes, so a window really separates them. Both sides read as the
/// world's dev key: over HTTP with its header, and through the port with the context the key resolves to.
/// </remarks>
public sealed class AuditFacetApiEquivalenceTests
{
    private static readonly DateTimeOffset _monday = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
    private static readonly UserId _ada = new(Guid.Parse("a0000000-0000-4000-8000-00000000000a"));
    private static readonly UserId _grace = new(Guid.Parse("b0000000-0000-4000-8000-00000000000b"));

    private static AlvoContext KeyCaller { get; } = new()
    {
        User = new UserId(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff")),
        Roles = new HashSet<Role> { Role.Admin, Role.Authenticated },
    };

    public static TheoryData<AuditFacets, int> Facets() => new()
    {
        { AuditFacets.None, 6 },
        { AuditFacets.None with { Window = ChangeWindow.Last24Hours }, 3 },
        { AuditFacets.None with { Window = ChangeWindow.Last7Days }, 6 },
        { AuditFacets.None with { Actor = new ActorFacet(_ada.Value, false, "ada") }, 3 },
        { new AuditFacets(ChangeWindow.Last24Hours, new ActorFacet(_ada.Value, false, "ada")), 1 },
        { AuditFacets.None with { Actor = new ActorFacet(null, false, "no identity") }, 1 },
    };

    [Theory]
    [MemberData(nameof(Facets))]
    public async Task A_facet_answers_the_rows_the_data_api_answers(AuditFacets facets, int expected)
    {
        var clock = new MovableClock(_monday);
        await using var world = await AlvoHostWorld.StartAsync(
            "host-audited.alvo.json", configure: builder => builder.Services.AddSingleton<TimeProvider>(clock));
        await SeedAsync(world, clock);
        var now = clock.GetUtcNow().AddHours(1);

        var port = await PortIdsAsync(world, facets, now);
        var api = await ApiIdsAsync(world, facets, now);

        port.Count.ShouldBe(expected, "the fixture must discriminate the facet, or the comparison proves nothing");
        api.ShouldBe(port, ignoreOrder: true);
    }

    private static async Task SeedAsync(AlvoHostWorld world, MovableClock clock)
    {
        await WriteAsync(world, Writer(_ada), "ada-old-1");
        await WriteAsync(world, Writer(_ada), "ada-old-2");
        await WriteAsync(world, Writer(default), "nobody-old");
        clock.Now = _monday.AddDays(2);
        await WriteAsync(world, Writer(_grace), "grace-new-1");
        await WriteAsync(world, Writer(_grace), "grace-new-2");
        await WriteAsync(world, Writer(_ada), "ada-new");
    }

    private static async Task WriteAsync(AlvoHostWorld world, AlvoContext writer, string title)
    {
        using var scope = world.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAlvoData>().CreateAsync(
            "tickets", new Dictionary<string, object?> { ["title"] = title }, writer, TestContext.Current.CancellationToken);
    }

    private static async Task<List<Guid>> PortIdsAsync(AlvoHostWorld world, AuditFacets facets, DateTimeOffset now)
    {
        using var scope = world.Services.CreateScope();
        var schema = await scope.ServiceProvider.GetRequiredService<IAlvoManagement>()
            .GetSchemaAsync("host-audited", TestContext.Current.CancellationToken);
        var entity = schema.Entities.Single(candidate => candidate.Name == "tickets");
        var query = GridQuery.Page(entity, GridQuery.Filter([], null, null, facets.Filter(now)), null, 100, null);
        var page = await scope.ServiceProvider.GetRequiredService<IAlvoData>()
            .QueryAsync(query, KeyCaller, TestContext.Current.CancellationToken);
        return [.. page.Items.Select(row => (Guid)row["id"]!)];
    }

    private static async Task<List<Guid>> ApiIdsAsync(AlvoHostWorld world, AuditFacets facets, DateTimeOffset now)
    {
        var narrowing = facets.ApiQuery(now);
        using var response = await world.GetAsync($"/api/tickets?limit=100{(narrowing.Length > 0 ? "&" : "")}{narrowing}");
        response.EnsureSuccessStatusCode();
        return [.. JsonNode.Parse(await response.ReadTextAsync())!["items"]!.AsArray()
            .Select(item => item!["id"]!.GetValue<Guid>())];
    }

    private static AlvoContext Writer(UserId user) => new()
    {
        User = user,
        Roles = new HashSet<Role> { Role.Authenticated },
    };

    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
```

Check against the code and adapt, keeping the fact's meaning:
- the project name `GetSchemaAsync` takes (the descriptor's `name`; `AlvoHostWorld`'s management facts use `"host"`
  for their descriptor, so it may be the host's own project name). If the schema read is awkward, build the
  `EntitySchema` from `DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(json))` instead;
- whether `IAlvoData.QueryAsync`/`CreateAsync` take a `CancellationToken` in that position;
- whether an anonymous writer (`User = default`) may create under `'authenticated'`. If not, give the "nobody" row
  another rule (e.g. `create: "true"`), because the `is.null` facet needs a null writer.

- [ ] **Step 2: Write the failing facet scenarios**

`test/MMLib.Alvo.Admin.Tests.EndToEnd/AuditFacetScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The facet row narrows an audited grid by when and by whom, joins the search, and a person in a Changed cell narrows
/// to them without opening the row (§3.9; scenarios 4 and 6).
/// </summary>
/// <remarks>Its own world. Every fact writes technicians under its own names and searches for them.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class AuditFacetScenarios(AuditWorld world) : IClassFixture<AuditWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Last_24_hours_narrows_the_grid_and_joins_the_search()
    {
        await AuditSeed.TechnicianAsync(world.Services, AuditSeed.Writer(UserId.New()), "Window Fresh Alpha");
        await AuditSeed.TechnicianAsync(world.Services, AuditSeed.Writer(UserId.New()), "Window Fresh Beta");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/technicians");
        var facets = session.Page.GetByTestId("audit-facets");
        (await facets.GetAttributeAsync("aria-label")).ShouldBe("Narrow by change");

        var last24 = facets.GetByRole(AriaRole.Radio, new() { Name = "Last 24 hours", Exact = true });
        await last24.ClickAsync();
        await facets.GetByRole(AriaRole.Radio, new() { Name = "Last 24 hours", Checked = true }).WaitForAsync();

        await session.Page.FillAsync("[data-testid='grid-search']", "Window Fresh Alpha");
        var rows = session.Page.GetByTestId("grid-row");
        await rows.Filter(new() { HasText = "Window Fresh Beta" }).WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await rows.CountAsync()).ShouldBe(1, "the search and the facet both apply");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_person_in_a_Changed_cell_narrows_the_grid_without_opening_the_row_and_the_chip_restores_it()
    {
        var writer = await AuditSeed.PersonAsync(world.Services, "facet.link@velo-dielna.example", "manager");
        await AuditSeed.TechnicianAsync(world.Services, AuditSeed.Writer(writer.Id), "Linked Writer Row");
        await AuditSeed.TechnicianAsync(world.Services, AuditSeed.Writer(UserId.New()), "Other Writer Row");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/technicians");

        var link = session.Page.GetByTestId("grid-row").Filter(new() { HasText = "Linked Writer Row" }).GetByTestId("changed-by");
        (await link.GetAttributeAsync("aria-label")).ShouldBe("Show only changes by facet.link@velo-dielna.example");
        await link.ClickAsync();

        var chip = session.Page.GetByTestId("facet-actor");
        await chip.WaitForAsync();
        (await chip.InnerTextAsync()).ShouldContain("Changed by facet.link");
        (await session.Page.GetByTestId("record-sheet").CountAsync()).ShouldBe(0, "the link does not open the row");
        (await session.Page.GetByTestId("facet-by-me").CountAsync()).ShouldBe(0, "the chip replaces By me");
        await session.Page.GetByTestId("grid-row").Filter(new() { HasText = "Other Writer Row" })
            .WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await chip.GetByRole(AriaRole.Button, new() { Name = "Remove the filter Changed by facet.link" }).ClickAsync();
        await session.Page.GetByTestId("grid-row").Filter(new() { HasText = "Other Writer Row" }).WaitForAsync();
        session.AssertConsoleClean();
    }
}
```

`test/MMLib.Alvo.Admin.Tests.EndToEnd/AuditEmptyFacetScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>A facet that empties the page is the empty state, whose one action removes it (§3.9; scenario 5).</summary>
/// <remarks>
/// <b>Its own world, and it must stay so</b>: "By me" is empty only if nobody writes <c>parts</c> as the operator, and
/// a fact in another class could. This class writes parts as another person only.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class AuditEmptyFacetScenarios(AuditWorld world) : IClassFixture<AuditWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task By_me_on_an_entity_nobody_changed_as_the_operator_is_empty_and_Anyone_restores_it()
    {
        await PartAsync("EMPTY-FACET-1");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/parts");

        await session.Page.GetByTestId("facet-by-me").ClickAsync();
        await session.Page.GetByRole(AriaRole.Button, new() { Name = "By me", Pressed = true }).WaitForAsync();
        await session.Content.GetByText("Nothing changed by you", new() { Exact = true }).WaitForAsync();
        await session.Content.GetByText("The search and the other filters still apply.").WaitForAsync();

        await session.Page.GetByTestId("facet-empty-clear").ClickAsync();
        await session.Page.GetByTestId("grid-row").First.WaitForAsync();
        await session.WaitForFocusInsideAsync("audit-facets");
        session.AssertConsoleClean();
    }

    private async Task PartAsync(string sku)
    {
        using var scope = world.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAlvoData>().CreateAsync(
            "parts",
            new Dictionary<string, object?>
            {
                ["sku"] = sku, ["name"] = sku, ["brand"] = "Shimano", ["category"] = "brakes",
                ["unit_price"] = 10m, ["purchase_price"] = 6m, ["stock_quantity"] = 3,
            },
            AuditSeed.Writer(UserId.New()));
    }
}
```

Add to `PatternLanguageTests`:

```csharp
    /// <summary>
    /// A list narrowed by a facet draws its facets as one named group under its search (spec §3.9): the Data grid's
    /// change facets are the one instance.
    /// </summary>
    [Fact]
    public void The_change_facets_are_one_named_group()
    {
        var source = Components().Single(file => file.Name == "Data/EntityData.razor").Source;

        source.ShouldContain("role=\"group\" aria-label=\"@AuditWords.NarrowByChange\"", Case.Sensitive);
        source.ShouldContain("<ChipGroup TValue=\"ChangeWindow\"", Case.Sensitive);
    }
```

- [ ] **Step 3: Run to see them fail**

Run: `dotnet test --project test/MMLib.Alvo.Host.Tests/MMLib.Alvo.Host.Tests.csproj -- --filter-class "*AuditFacetApiEquivalenceTests"`
(this may already pass, because it exercises Task 4's code over the real host; that is fine) and
`scripts/test-admin-e2e --filter "AuditFacetScenarios|AuditEmptyFacetScenarios"`.
Expected: the e2e scenarios FAIL, because `audit-facets` is missing.

- [ ] **Step 4: The facet handlers**

In `EntityData.Audit.cs`, replace `NarrowToAsync` and add:

```csharp
    /// <summary>Narrows the grid to the writer a Changed cell's link named; the chip replaces By me (§3.9).</summary>
    /// <remarks>Reached through the grid's scope, not an event callback, so the screen draws itself again here.</remarks>
    private async Task NarrowToAsync(ActorLabel actor)
    {
        await NarrowAsync(_facets with { Actor = ActorFacet.For(actor) }, AuditFacet.Actor);
        StateHasChanged();
    }

    private Task WindowChosenAsync(ChangeWindow window)
        => NarrowAsync(_facets with { Window = window }, AuditFacet.Window);

    private Task ToggleByMeAsync()
        => _self is { } self
            ? NarrowAsync(_facets with { Actor = _facets.Actor is { Mine: true } ? null : ActorFacet.Me(self) }, AuditFacet.Actor)
            : Task.CompletedTask;

    private Task ClearActorAsync() => NarrowAsync(_facets.Without(AuditFacet.Actor), AuditFacet.Actor);

    /// <summary>The empty state's one action: removes the facet that emptied the page, and focus goes back to the row.</summary>
    private async Task ClearEmptyingFacetAsync()
    {
        await NarrowAsync(_facets.Without(_lastNarrowed), _lastNarrowed);
        _focusAfterRender = _facetRow;
        _focusMoves++;
    }

    /// <summary>
    /// Applies new facets: a reveal ends, as typing ends one, and the list is read again from its first page under the
    /// refresh bar (§3.9, §3.6).
    /// </summary>
    private async Task NarrowAsync(AuditFacets facets, AuditFacet changed)
    {
        _facets = facets;
        _lastNarrowed = changed;
        _revealing = null;
        _beforeReveal = null;
        ResetPaging();
        await LoadAsync();
    }

    /// <summary>Where focus goes after the empty state's action: the chosen window chip, else the row's first chip.</summary>
    private static readonly string[] _facetRow =
        ["[data-testid='audit-facets'] [aria-checked='true']", "[data-testid='audit-facets'] button"];

    /// <summary>Whether the facets, and not the search, are what emptied the page.</summary>
    private bool FacetsEmptied => Audited && _facets.Narrows && _page is { Items.Count: 0 };
```

In `ResetForEntity` (`EntityData.razor.cs`) add `_facets = AuditFacets.None;`. `ResetPaging` already clears
`_focusAfterRender`, and `ClearEmptyingFacetAsync` sets it after the load, as `ClearRevealAsync` does.

- [ ] **Step 5: Draw the row and the empty state in `EntityData.razor`**

After the search `@if (_searchable.Count > 0) { … }` block, and outside every branch below it, so a facet change never
unmounts the row that has focus:

```razor
            @if (_entity.Audit)
            {
                <div class="a-facets" role="group" aria-label="@AuditWords.NarrowByChange" data-testid="audit-facets">
                    <ChipGroup TValue="ChangeWindow" Items="AuditFacets.Windows" Selected="@([_facets.Window])"
                               Label="AuditFacets.Words" aria-label="@AuditWords.Changed"
                               SelectedChanged="windows => WindowChosenAsync(windows[0])" />
                    @if (_facets.Actor is { Mine: false } actor)
                    {
                        <span class="a-chip a-chip--on a-facet" data-testid="facet-actor">
                            @AuditWords.ChangedBy(actor.Label)
                            <button type="button" class="a-facet__remove" aria-label="@AuditWords.RemoveChangedBy(actor.Label)"
                                    @onclick="ClearActorAsync">×</button>
                        </span>
                    }
                    else if (_self is not null)
                    {
                        <button type="button" class="@(_facets.Actor is not null ? "a-chip a-chip--on" : "a-chip")"
                                aria-pressed="@(_facets.Actor is not null ? "true" : "false")" data-testid="facet-by-me"
                                @onclick="ToggleByMeAsync">@AuditWords.ByMe</button>
                    }
                </div>
            }
```

Replace the empty branch `else if (_page.Items.Count == 0) { … }` with two branches:

```razor
            else if (FacetsEmptied)
            {
                <EmptyState Title="@_facets.EmptyTitle(_lastNarrowed)" Body="@AuditWords.StillApply">
                    <AlvoButton Tone="AlvoButton.ButtonTone.Primary" Small="true" data-testid="facet-empty-clear"
                                OnClick="_ => ClearEmptyingFacetAsync()">@AuditFacets.EmptyAction(_lastNarrowed)</AlvoButton>
                </EmptyState>
                @if (_focusAfterRender is { } focus)
                {
                    @FocusFirstOnRender.On(focus, _focusMoves, () => _focusAfterRender = null)
                }
            }
            else if (_page.Items.Count == 0)
            {
                @* the existing search/empty EmptyState, unchanged *@
            }
```

The focus after **Anyone/Show any time** lands on a page that has rows, so the grid branch's existing
`FocusFirstOnRender` carries it. The one in the empty branch covers a page that is still empty after removing the
facet (the other facet still narrows it).

- [ ] **Step 6: Styles**

```css
  /* The facet row under a list's search (§3.9): chips on one line, wrapping on a phone. */
  .a-facets {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: var(--space-2);
  }

  .a-facet {
    display: inline-flex;
    align-items: center;
    gap: var(--space-1);
  }

  .a-facet__remove {
    padding: 0 2px;
    border: 0;
    background: none;
    color: inherit;
    font: inherit;
    cursor: pointer;
  }
```

If `ChipGroup`'s `.a-choice` already draws the window chips on a flex row, the nested group sits inline in `.a-facets`.
Check it at 375 px in Step 7.

- [ ] **Step 7: Run and fix**

Run: `scripts/test-admin-e2e --filter "AuditFacetScenarios|AuditEmptyFacetScenarios|AuditGridScenarios|DataGridScenarios|RecordRevealScenarios"`
and the Host fact. Expected: PASS. Then add a phone check to `AuditGridScenarios.On_a_phone_…`: after the card assertion,
`(await session.Page.GetByTestId("audit-facets").BoundingBoxAsync())!.Width.ShouldBeLessThanOrEqualTo(375)` before
`AssertNoHorizontalScrollAsync` (A7: the facet row wraps).

- [ ] **Step 8: ring1 and commit**

```bash
git add src/MMLib.Alvo.Admin test/MMLib.Alvo.Admin.Tests test/MMLib.Alvo.Host.Tests test/MMLib.Alvo.Admin.Tests.EndToEnd
git commit -m "feat(admin): narrow an audited grid by when and by whom, as the Data API would

Claude-Session: https://claude.ai/code/session_01TrcGmun8WAuVxN5FYAtu6H"
```

---

### Task 8: the *Record* block in the record sheet

**Files:**
- Modify: `src/MMLib.Alvo.Admin/Components/Data/RecordFormScope.cs` (a last parameter `RecordAudit? Audit = null`)
- Modify: `src/MMLib.Alvo.Admin/Components/Data/EntityData.razor.cs` (`Open`, `NewRecord`, `OpenLinkedRecordAsync`),
  `EntityData.Audit.cs` (`RecordAuditOf`, `LearnActorsAsync`)
- Modify: `src/MMLib.Alvo.Admin/Components/Data/RecordForm.razor`, `RecordForm.razor.cs`
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/AuditRecordScenarios.cs`

**Interfaces:**
- Consumes: `AuditStamp`, `AuditView`, `RecordAudit` (Task 4); `AuditViewOf`, `_directory`, `_actors` (Task 6).
- Produces: the test ids `record-audit`, `record-created`, `record-changed`, `record-copy-id`.

- [ ] **Step 1: Write the failing scenarios**

`test/MMLib.Alvo.Admin.Tests.EndToEnd/AuditRecordScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The record sheet says who created a record and who changed it last, and that earlier versions are not kept; a save
/// moves only Last changed (#290 §4.2; scenarios 1, 2, 8, 10).
/// </summary>
/// <remarks>Its own world. Every fact writes and opens technicians under its own names.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class AuditRecordScenarios(AuditWorld world) : IClassFixture<AuditWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Saving_moves_Last_changed_to_you_and_leaves_Created_alone()
    {
        var katarina = await AuditSeed.PersonAsync(world.Services, "katarina.record@velo-dielna.example", "manager");
        await AuditSeed.TechnicianAsync(world.Services, AuditSeed.Writer(katarina.Id), "Record Saved Once");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/technicians");
        var row = session.Page.GetByTestId("grid-row").Filter(new() { HasText = "Record Saved Once" });

        await row.ClickAsync();
        var sheet = session.Dialog("record-sheet");
        var created = (await sheet.GetByTestId("record-created").InnerTextAsync()).Trim();
        created.ShouldEndWith("· katarina.record@velo-dielna.example");
        (await sheet.GetByTestId("record-audit").InnerTextAsync()).ShouldContain(
            "Alvo keeps who made the last change and when. Earlier versions of this record are not kept.");
        await sheet.Locator("#rf-phone").FillAsync("+421 900 000 001");
        await sheet.GetByTestId("record-save").ClickAsync();
        await sheet.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await row.ClickAsync();
        var again = session.Dialog("record-sheet");
        (await again.GetByTestId("record-created").InnerTextAsync()).Trim().ShouldBe(created);
        (await again.GetByTestId("record-changed").InnerTextAsync()).Trim().ShouldEndWith("· you");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_key_identity_in_the_record_is_named_for_what_it_is_with_its_id_to_copy()
    {
        await AuditSeed.TechnicianAsync(world.Services, AuditSeed.Writer(AuditSeed.KeyIdentity), "Record By Key");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/technicians");

        await session.Page.GetByTestId("grid-row").Filter(new() { HasText = "Record By Key" }).ClickAsync();
        var audit = session.Dialog("record-sheet").GetByTestId("record-audit");
        (await audit.InnerTextAsync()).ShouldContain("Not a dashboard account: an API key's identity or an external caller.");
        await audit.GetByTestId("record-copy-id").First.WaitForAsync();
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_new_record_and_an_entity_that_is_not_audited_have_no_Record_block()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/technicians");
        await session.Page.GetByTestId("record-new").ClickAsync();
        (await session.Dialog("record-sheet").GetByTestId("record-audit").CountAsync()).ShouldBe(0);
        await session.Page.Keyboard.PressAsync("Escape");

        await session.GoAsync("/data/order_lines");
        (await session.Page.GetByTestId("not-audited-note").CountAsync()).ShouldBe(1);
        session.AssertConsoleClean();
    }
}
```

(Opening an existing `order_lines` record to see the last-write-wins caveat needs a seeded order line, which needs an
order and a bike and a customer. The caveat is already pinned by `RecordEditorScenarios`/`RecordVersionTests`, so this
fact checks only that the not-audited screen has no *Record* block path.)

- [ ] **Step 2: Run to see them fail**

Run: `scripts/test-admin-e2e --filter AuditRecordScenarios`
Expected: FAIL, `record-created` not found.

- [ ] **Step 3: Carry the stamp to the sheet**

`RecordFormScope`: add the last positional parameter `RecordAudit? Audit = null`, documented
`/// <param name="Audit">The open record's audit, on an audited entity when a record is edited; otherwise nothing.</param>`.

`EntityData.Audit.cs`:

```csharp
    /// <summary>The audit the record sheet shows for <paramref name="record"/>: from the row already read, never read again.</summary>
    private RecordAudit? RecordAuditOf(AlvoRecord record)
        => AuditViewOf() is { } view ? new RecordAudit(AuditStamp.Of(record), view) : null;

    /// <summary>Names the writers of a record opened by a link, which the page on screen may not hold.</summary>
    private async Task LearnActorsAsync(AlvoRecord record) => _actors = await ActorsAsync([record]);
```

`EntityData.razor.cs`:

- `Open(AlvoRecord record)`: after building `_form`, add `_scope = _scope! with { Audit = RecordAuditOf(record) };`.
- `NewRecord()`: add `_scope = _scope is null ? null : _scope with { Audit = null };`.
- `OpenLinkedRecordAsync`: before `Open(record);`, add `await LearnActorsAsync(record);`.

- [ ] **Step 4: Draw the block in `RecordForm.razor`**

Add `@inject AdminInterop Interop` at the top. After the `_calculated` section and before
`<details class="a-disclosure">`:

```razor
        @*
            Who created the record and who changed it last, and that nothing older is kept (#290 §4.2): read-only, so it
            is the readout's text and not a box (§3.8).
        *@
        @if (!Creating && Entity.Audit && Scope?.Audit is { } audit)
        {
            <section class="a-readout" aria-labelledby="rf-record" data-testid="record-audit">
                <span class="a-section__title" id="rf-record">@AuditWords.Record</span>
                <dl class="a-readout__list">
                    @Stamped(AuditWords.Created, audit.Stamp.Created, audit.View, "record-created")
                    @Stamped(AuditWords.LastChanged, audit.Stamp.Updated, audit.View, "record-changed")
                </dl>
                <p class="a-note">@AuditWords.NoVersions</p>
            </section>
        }
```

In the `@code` block of `RecordForm.razor`:

```csharp
    /// <summary>
    /// One audit pair: when and who on one line, and for an id nobody resolved the hint an Admin gets and a way to copy
    /// the whole id.
    /// </summary>
    private RenderFragment Stamped(string name, AuditMark mark, AuditView view, string testId)
    {
        var actor = view.Actors.Of(mark.By);
        return @<div class="a-readout__pair">
            <dt>@name</dt>
            <dd>
                <span data-testid="@testId" title="@AuditView.Iso(mark.At)">@view.Line(mark)</span>
                @if (actor is { Kind: ActorKind.Unresolved, Id: { } id })
                {
                    @if (actor.Hint is { } hint)
                    {
                        <span class="a-hint">@hint</span>
                    }
                    <AlvoButton Tone="AlvoButton.ButtonTone.Ghost" Small="true" data-testid="record-copy-id"
                                OnClick="_ => CopyIdAsync(id)">Copy id</AlvoButton>
                }
            </dd>
        </div>;
    }
```

In `RecordForm.razor.cs`:

```csharp
    /// <summary>Copies a writer's whole id, which the sheet shows short, and says so.</summary>
    private async Task CopyIdAsync(Guid id)
    {
        await Interop.CopyAsync(id.ToString());
        Scope?.Report("Id copied");
    }
```

`Report` shows a snackbar (`EntityData.Report`). The sheet stays open, and the snackbar sits bottom-left, away from
the sheet's footer (§3.3).

- [ ] **Step 5: Restore the grid scenario's record step**

`AuditIdentityScenarios`' Admin fact stays grid-only (Task 6). The key-identity hint in the sheet is
`AuditRecordScenarios.A_key_identity_in_the_record_…`. Also add to `AuditGridScenarios.A_record_created_now_…`, after the
cell assertions: open the row and assert
`(await session.Dialog("record-sheet").GetByTestId("record-created").InnerTextAsync()).Trim().ShouldEndWith("· you")`
(scenario 1's second half).

- [ ] **Step 6: Run and fix**

Run: `scripts/test-admin-e2e --filter "AuditRecordScenarios|AuditGridScenarios|RecordEditorScenarios|RecordFormScenarios|RecordConflictScenarios|EditorScenarios"`
Expected: PASS. The existing editor scenarios guard the sheet's focus, dirty guard and conflict flows.

- [ ] **Step 7: ring1 and commit**

```bash
git add src/MMLib.Alvo.Admin test/MMLib.Alvo.Admin.Tests.EndToEnd
git commit -m "feat(admin): the record sheet says who created a record and who changed it last

Claude-Session: https://claude.ai/code/session_01TrcGmun8WAuVxN5FYAtu6H"
```

---

### Task 9: the Schema header: the badge's tone and title, *Recently changed*, and contrast (A8)

**Files:**
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/Entity.razor` (badge, button)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/SchemaList.razor` (title only)
- Modify: `src/MMLib.Alvo.Admin/Internal/AdminPaths.cs` (`RecentlyChanged`)
- Modify: `src/MMLib.Alvo.Admin/Components/Data/EntityData.razor.cs` (`Order` query parameter, arrival sort)
- Modify: `test/MMLib.Alvo.Admin.Tests/Internal/AdminPathsTests.cs`
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/AuditSchemaScenarios.cs`

**Interfaces:**
- Consumes: `GridQuery.Arrival`, `GridQuery.RecentlyChangedOrder`, `AuditWords.AuditedTitle`, `AuditWords.RecentlyChanged` (Task 4).
- Produces: `AdminPaths.RecentlyChanged(string entity) : string`; the test ids `entity-audited`, `recently-changed`.

- [ ] **Step 1: Write the failing tests**

Add to `AdminPathsTests`:

```csharp
    [Fact]
    public void Recently_changed_opens_the_entitys_records_sorted_by_their_last_change()
        => AdminPaths.RecentlyChanged("service_orders").ShouldBe($"{AdminPaths.Data}/service_orders?order=updated_at.desc");
```

`test/MMLib.Alvo.Admin.Tests.EndToEnd/AuditSchemaScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The Schema header says what audit does, opens the entity's recently changed records, and the new tones read at AA
/// in both themes (#290 §4.3, §4.6; scenario 11; A8, measured by ControlContrastScenarios' probe, C6).
/// </summary>
/// <remarks>Its own world. Every fact writes technicians under its own names.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class AuditSchemaScenarios(AuditWorld world) : IClassFixture<AuditWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_audited_entity_says_so_in_the_accent_tone_with_what_it_keeps()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/service_orders");
        var badge = session.Page.GetByTestId("entity-audited");

        (await badge.InnerTextAsync()).Trim().ShouldBe("audited");
        (await badge.GetAttributeAsync("class"))!.ShouldContain("a-badge--accent");
        (await badge.GetAttributeAsync("title")).ShouldBe(
            "Records who created each record and who changed it last, and when. Keeps no earlier versions.");

        await session.GoAsync("/schema/order_lines");
        (await session.Page.GetByTestId("entity-audited").InnerTextAsync()).Trim().ShouldBe("not audited");
        (await session.Page.GetByTestId("recently-changed").CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Recently_changed_opens_the_records_newest_change_first()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/technicians");

        await session.Page.GetByTestId("recently-changed").ClickAsync();
        await session.Page.WaitForURLAsync("**/data/technicians?order=updated_at.desc");
        await session.Page.WaitForFunctionAsync(
            "() => [...document.querySelectorAll('th')].some(th => th.textContent.includes('Changed') && th.getAttribute('aria-sort') === 'descending')");
        session.AssertConsoleClean();
    }

    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData(ColorScheme.Light)]
    [InlineData(ColorScheme.Dark)]
    public async Task The_badge_the_facet_chips_and_the_writer_links_read_at_AA(ColorScheme scheme)
    {
        await AuditSeed.TechnicianAsync(world.Services, AuditSeed.Writer(UserId.New()), $"Contrast {scheme}");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, colorScheme: scheme);

        await session.GoAsync("/schema/technicians");
        var failures = (await ContrastProbe.ReadAsync(session.Page.GetByTestId("entity-audited")))
            .Where(reading => reading.Ratio < ContrastProbe.AA).Select(reading => $"the badge: {reading}").ToList();

        await session.GoAsync("/data/technicians");
        var controls = session.Page.GetByTestId("audit-facets").GetByRole(AriaRole.Radio)
            .Or(session.Page.GetByTestId("facet-by-me"))
            .Or(session.Page.GetByTestId("changed-by"));
        failures.AddRange(await BelowAAAsync(controls, "the facets, By me off"));

        await session.Page.GetByTestId("facet-by-me").ClickAsync();
        await session.Page.GetByRole(AriaRole.Button, new() { Name = "By me", Pressed = true }).WaitForAsync();
        failures.AddRange(await BelowAAAsync(session.Page.GetByTestId("facet-by-me"), "By me, on"));

        failures.ShouldBeEmpty();
    }

    private static async Task<IEnumerable<string>> BelowAAAsync(ILocator controls, string what)
        => (await ContrastProbe.ReadAsync(controls))
            .Where(reading => reading.Ratio < ContrastProbe.AA).Select(reading => $"{what}: {reading}");
}
```

*By me* pressed on the seeded writer's rows leaves the page empty, which is fine: the chip is still drawn, and it is
the chip that is read.

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests/MMLib.Alvo.Admin.Tests.csproj -- --filter-method "*Recently_changed*"`
and `scripts/test-admin-e2e --filter AuditSchemaScenarios`. Expected: FAIL.

- [ ] **Step 3: `AdminPaths.RecentlyChanged`**

```csharp
    /// <summary>An audited entity's records, newest change first: the Schema header's <em>Recently changed</em> (#290, C7).</summary>
    /// <param name="entity">The entity.</param>
    public static string RecentlyChanged(string entity) => $"{Records(entity)}?order={GridQuery.RecentlyChangedOrder}";
```

(`using MMLib.Alvo.Admin.Components.Data;`. If `Internal` referring to a feature's constant is refused by a layering
test, `ComponentLayerTests` or `BoundaryArchitectureTests`, restate the literal `"updated_at.desc"` here and pin that
the two agree in `AdminPathsTests`.)

- [ ] **Step 4: Read it on arrival**

In `EntityData.razor.cs`, beside `Record`:

```csharp
    /// <summary>
    /// The order to open with, when a screen links here sorted: only <see cref="GridQuery.RecentlyChangedOrder"/> on an
    /// audited entity (#290, C7). Read once when the entity opens, and never written back: the grid keeps no URL state
    /// (ruling Q4).
    /// </summary>
    [SupplyParameterFromQuery(Name = "order")]
    private string? Order { get; set; }
```

In `OpenEntityAsync`, after `await DescribeAsync();`, add `_sort = _entity is null ? null : GridQuery.Arrival(_entity, Order);`.

- [ ] **Step 5: The header**

In `Entity.razor`, replace `<span class="a-badge">@(_entity.Audit ? "audited" : "not audited")</span>` with:

```razor
            @if (_entity.Audit)
            {
                @* A feature the operator switched on, so the accent, and what it keeps and does not (#290 §4.3). *@
                <span class="a-badge a-badge--accent" data-testid="entity-audited" title="@AuditWords.AuditedTitle">audited</span>
            }
            else
            {
                <span class="a-badge" data-testid="entity-audited">not audited</span>
            }
```

In `<Secondary>`, after *Browse records*:

```razor
            @if (_entity is { Audit: true } && !_pending)
            {
                <AlvoButton Small="true" data-testid="recently-changed" Href="@AdminPaths.RecentlyChanged(EntityName)">@AuditWords.RecentlyChanged</AlvoButton>
            }
```

(Check that `_pending` is the field meaning "declared in the working copy, not applied yet", as the badge block uses
it. An entity that is not applied has no rows to open.) In `SchemaList.razor`, give the `audited` badge
`title="@AuditWords.AuditedTitle"`. Add `@using MMLib.Alvo.Admin.Components.Data` where it is needed.

- [ ] **Step 6: Run and fix**

Run: `scripts/test-admin-e2e --filter "AuditSchemaScenarios|SchemaScenarios|SchemaListScenarios|ControlContrastScenarios|CreateActionScenarios"`
Expected: PASS. `CreateActionScenarios` scans the header's buttons. *Recently changed* is not a create, so it
must not trip that scan; if it does, read the scan's rule before you change anything.

- [ ] **Step 7: ring1 and commit**

```bash
git add src/MMLib.Alvo.Admin test/MMLib.Alvo.Admin.Tests test/MMLib.Alvo.Admin.Tests.EndToEnd
git commit -m "feat(admin): the Schema header says what audit keeps and opens recently changed records

Claude-Session: https://claude.ai/code/session_01TrcGmun8WAuVxN5FYAtu6H"
```

---

### Task 10: measure A9, record it, and the final gates

**Files:**
- Create: `test/MMLib.Alvo.Data.Sqlite.Tests/AuditOrderTimingProbe.cs` (an explicit fact, run on demand)
- Modify: `docs/superpowers/specs/2026-09-29-f5-admin-audit-view-design.md` (§10, Q6 row: the measured numbers)
- Modify: `docs/todo-admin.md` (the follow-ups: Q3 all datetime columns, Q4 facets in the URL, Q2 user-to-key lookup,
  soft-deleted records in Data, #290 text Q8), only if that file lists admin follow-ups. Read it first and match its
  format.

**Interfaces:** none.

- [ ] **Step 1: The probe**

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Testing;
using System.Diagnostics;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// #290 criterion A9, measured once and kept so it can be measured again: a page of 10 000 service orders sorted or
/// narrowed by <c>updated_at</c>, against today's default <c>created_at desc</c> page, both unindexed (ruling Q6).
/// </summary>
/// <remarks>
/// Explicit, so no ring runs it: it is a measurement, not a check. Run it with
/// <c>dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class "*AuditOrderTimingProbe" --explicit only</c>.
/// </remarks>
public sealed class AuditOrderTimingProbe : IAsyncDisposable
{
    private const int Orders = 10_000;
    private const int Reads = 21;
    private readonly SqliteAlvoDataFixture _fixture = new();

    [Fact(Explicit = true)]
    public async Task A_page_by_the_last_change_is_within_twice_the_default_page()
    {
        var data = await SeededAsync();
        var byCreated = await MedianAsync(data, Page(new AlvoSort("created_at", Descending: true), filter: null));
        var byUpdated = await MedianAsync(data, Page(new AlvoSort("updated_at", Descending: true), filter: null));
        var narrowed = await MedianAsync(data, Page(new AlvoSort("created_at", Descending: true),
            new AlvoComparison("updated_at", AlvoFilterOperator.Gte, Start.AddDays(Orders / 2 / 24.0))));

        TestContext.Current.SendDiagnosticMessage(
            $"A9 over {Orders} rows, median of {Reads}: created_at desc {byCreated:F1} ms, "
            + $"updated_at desc {byUpdated:F1} ms, updated_at gte {narrowed:F1} ms");
        byUpdated.ShouldBeLessThanOrEqualTo(byCreated * 2);
        narrowed.ShouldBeLessThanOrEqualTo(byCreated * 2);
    }

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static AlvoQuery Page(AlvoSort sort, AlvoFilter? filter) => new()
    {
        Entity = "service_orders", Sort = [sort], Filter = filter, Limit = 25, IncludeTotalCount = true,
    };

    private static async Task<double> MedianAsync(IAlvoData data, AlvoQuery query)
    {
        var times = new List<double>(Reads);
        for (var read = 0; read < Reads; read++)
        {
            var clock = Stopwatch.StartNew();
            await data.QueryAsync(query, Reader, TestContext.Current.CancellationToken);
            times.Add(clock.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        return times[Reads / 2];
    }

    private static AlvoContext Reader { get; } = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Admin, Role.Authenticated },
    };

    private async Task<IAlvoData> SeededAsync()
    {
        var json = await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot.Find(), "examples", "bike-workshop", "bike-workshop.alvo.json"),
            TestContext.Current.CancellationToken);
        var descriptor = AlvoDescriptor.Parse(json);
        var host = await _fixture.StartAsync(MMLib.Alvo.Descriptor.DescriptorToSchemaMapper.Map(descriptor), descriptor);
        await AlvoDataSeed.SeedAsync(
            host.Services.GetRequiredService<AlvoDataContextFactory>(), Seed(), TestContext.Current.CancellationToken);
        return host.Data;
    }

    private static Dictionary<string, IReadOnlyList<AlvoRecord>> Seed()
    {
        var customer = Guid.NewGuid();
        var bike = Guid.NewGuid();
        return new(StringComparer.Ordinal)
        {
            ["customers"] = [Row(customer, Start, new() { ["first_name"] = "Jana", ["last_name"] = "Nováková", ["phone"] = "+421", ["loyalty_tier"] = "none" })],
            ["bikes"] = [Row(bike, Start, new() { ["customer_id"] = customer, ["brand"] = "Kellys", ["model"] = "Soot", ["category"] = "road", ["frame_number"] = "F-1" })],
            ["service_orders"] = [.. Enumerable.Range(0, Orders).Select(index => Row(Guid.NewGuid(), Start.AddHours(index), new()
            {
                ["order_number"] = $"SO-{index:D5}", ["bike_id"] = bike, ["status"] = "received", ["priority"] = "normal",
                ["service_type"] = "basic_service", ["received_at"] = Start.AddHours(index), ["problem_description"] = "noise",
            }))],
        };
    }

    private static AlvoRecord Row(Guid id, DateTimeOffset at, Dictionary<string, object?> values)
    {
        values["id"] = id;
        values["created_at"] = at;
        values["created_by"] = null;
        values["updated_at"] = at.AddMinutes(id.GetHashCode() % 997);
        values["updated_by"] = null;
        return new AlvoRecord(values);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
```

Adapt to what the fixture and the seed accept: a generated or rollup column the seed refuses must be left out; a
required field the seed needs must be added. `SqliteAlvoDataPagingTests` shows the fixture's shape. If the core mapper
is not reachable from this project, check `InternalsVisibleTo` in `src/MMLib.Alvo/Properties/AssemblyInfo.cs`
(`MMLib.Alvo.Data.Sqlite.Tests` is listed). The row that counts is service orders: 10 000 of them, created
`created_at desc` versus `updated_at desc` versus an `updated_at gte` window.

- [ ] **Step 2: Measure once, and record it**

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests/MMLib.Alvo.Data.Sqlite.Tests.csproj -c Release -- --filter-class "*AuditOrderTimingProbe" --explicit only`
Copy the diagnostic line into the design's §10 Q6 row, after "Measure A9 once and record the number here.", as
`Measured 2026-09-29, SQLite, 10 000 service_orders, median of 21 reads: created_at desc X ms, updated_at desc Y ms,
updated_at gte Z ms.` If Y or Z is more than 2× X, stop: Q6 is then a blocker for the maintainer, so report it and do
not add an index.

- [ ] **Step 3: The follow-ups**

Record the follow-ups the rulings name (Q2, Q3, Q4, soft-deleted records in Data, Q8) where the repository keeps admin
follow-ups (`docs/todo-admin.md`, if it has such a list). Do not open GitHub issues from the plan: the maintainer does.

- [ ] **Step 4: The gates**

Run, in order, and fix anything red before the next:

1. `scripts/test-ring2`: ring1, the affected integration suites (PostgreSQL's leg of Task 3's paging fact), the API
   invariants and the Vacuum lint. Read a PostgreSQL connect timeout's trace before calling it a regression (the
   known `PagingPerformanceTests` flake).
2. `scripts/test-admin-e2e`: the whole suite, not filtered.
3. `dotnet build MMLib.Alvo.slnx -c Release -warnaserror`: the CA analyzers CI enforces in Release.
4. `docker build -f src/MMLib.Alvo.Host/Dockerfile .`: the only local build that reproduces CI's image build.
5. `git diff origin/f5/admin-dashboard --stat -- '*PublicApi*'`: exactly
   `PublicApi.MMLib.Alvo.Abstractions.verified.txt` (+1 line) and `PublicApi.MMLib.Alvo.Testing.verified.txt` (the 5
   contract facts and 1 paging theory). Nothing else.
6. `git ls-files --eol -- 'src/**/*.cs' 'test/**/*.cs' | grep -v 'w/crlf'`: every `.cs` file the branch added or
   touched is CRLF in the working tree. Check the BOM with
   `for f in $(git diff --name-only origin/f5/admin-dashboard -- '*.cs'); do head -c3 "$f" | xxd -p | grep -q efbbbf || echo "no BOM: $f"; done`.

- [ ] **Step 5: Commit**

```bash
git add test/MMLib.Alvo.Data.Sqlite.Tests/AuditOrderTimingProbe.cs docs
git commit -m "test(data): measure a page by the last change against the default page, and record it

Claude-Session: https://claude.ai/code/session_01TrcGmun8WAuVxN5FYAtu6H"
```

Then, per `CLAUDE.md`, before a PR: `alvo-plan-guard`, `/code-review medium` and `/security-review` (Task 2 touches
the guard, so pair them with `alvo-security-core-review`), then `alvo-pr-report`. Those are the controller's steps,
not a task of this plan.
