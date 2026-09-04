# PR-H — idempotent writes on every verb, and a transactional batch

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `Idempotency-Key` work on `PATCH` and `DELETE` (#102), and add a transactional batch create/update/delete whose policy is evaluated per row (#106).

**Architecture:** One idempotency record now describes a write that is not a create and a write that touched many rows, which is why the two issues are one PR. The fingerprint gains the row id and the precondition; the record's `row_id` column keeps its name and widens to hold a JSON array. The batch is **two passes inside one transaction** — judge every row collecting refusals, then write every row — because the single-row helpers throw on the first failure and PostgreSQL aborts a transaction after any statement error.

**Tech Stack:** .NET 10 (`net10.0`), EF Core over SQLite + PostgreSQL, `System.Text.Json`, xUnit v3 + Shouldly + Verify on Microsoft.Testing.Platform.

**Spec:** `docs/superpowers/specs/2026-09-04-f4-pr-h-idempotent-writes-and-batch-design.md` — read it before Task 1. §12 records five claims the design's own verification pass killed; do not re-introduce them.

## Global Constraints

- **Branch:** `f4/pr-h-idempotent-writes-and-batch`, off the merged PR-G. Never commit to `main`.
- **C# files are CRLF + UTF-8 BOM.** The pre-commit `dotnet format` gate fails on a file written LF-without-BOM through a shell heredoc. Prefer `Write`/`Edit`; if a shell writes a `.cs`, normalise before staging.
- **Zero inline comments.** Rationale goes in `/// <remarks>`; a `//` is a signal to rename or extract. XML docs are the house style on internal members too, and **required** on public ones.
- **Methods stay short** — a ~25-line ceiling; extract by default.
- **Three `PublicApi.*.verified.txt` baselines will grow.** `.claude/hooks/turn-review-gate` blocks the turn until the `alvo-architecture-rules` pass has justified each added symbol against *"public is the contract"*. Design §9 is that justification; keep it accurate as the surface lands.
- **No message may echo caller-supplied text.** Server-owned values only; the `Pointer` carries a location the caller authored.
- **Assertions are Shouldly.** Never FluentAssertions.
- **Run `scripts/test-ring0` after every task**, `scripts/test-ring2` before the PR. **`ring2` does not run the TeaPie e2e suites** — grep `test/teapie*` for pinned path/field sets before pushing (this cost PR-G three CI cycles).
- **Conventional Commits.** Every message ends with `Claude-Session: https://claude.ai/code/session_01Uh7NkobnQZy5fDftEZbVLp`.

---

### Task 1: The fingerprint covers the row, the precondition, and no body

Design §1 and §1.1. Nothing else changes yet — the new parameters are supplied by the one existing caller as `null`, so every digest stays byte-identical and the whole suite must stay green.

**Files:**
- Modify: `src/MMLib.Alvo/Api/Internal/IdempotencyFingerprint.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs` (the one call site, in `Idempotency`)
- Modify: `src/MMLib.Alvo.Abstractions/Data/AlvoIdempotency.cs` (the stale prose on `Fingerprint`)
- Test: `test/MMLib.Alvo.Api.Tests/IdempotencyTests.cs`

**Interfaces:**
- Produces: `internal static string IdempotencyFingerprint.Of(string method, string entity, Guid? id, AlvoPrecondition? precondition, JsonObject? body)`

- [ ] **Step 1: Write the failing tests**

Add to `test/MMLib.Alvo.Api.Tests/IdempotencyTests.cs`:

```csharp
    /// <summary>
    /// A create's digest is byte-identical to the one this API has always produced, so no request in
    /// flight across the deploy that widens the fingerprint becomes a conflict.
    /// </summary>
    /// <remarks>
    /// The obvious widening — always joining an id segment — digests a create as
    /// <c>POST\nvehicles\n\n{…}</c>, which is a different digest. The segment is appended only when there
    /// is an id, and this is what holds that rather than a remark claiming it.
    /// </remarks>
    [Fact]
    public void A_creates_fingerprint_is_unchanged_by_the_parameters_a_create_does_not_carry()
    {
        var body = new JsonObject { ["name"] = "Acme" };

        var widened = IdempotencyFingerprint.Of("POST", "owners", id: null, precondition: null, body);
        var asItWas = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("POST\nowners\n{\"name\":\"Acme\"}")));

        widened.ShouldBe(asItWas, "widening the digest must not move a create's");
    }

    /// <summary>
    /// The same key and the same body against two different rows is two different requests. Without the
    /// id in the digest the second is answered as a replay of the first — the row is never written and
    /// the caller is told it was, which is the silent-wrong-answer this digest exists to prevent.
    /// </summary>
    [Fact]
    public void Two_updates_of_different_rows_with_one_body_have_different_fingerprints()
    {
        var body = new JsonObject { ["name"] = "Renamed" };

        var first = IdempotencyFingerprint.Of("PATCH", "owners", Guid.NewGuid(), precondition: null, body);
        var second = IdempotencyFingerprint.Of("PATCH", "owners", Guid.NewGuid(), precondition: null, body);

        first.ShouldNotBe(second);
    }

    /// <summary>A delete carries no body at all, so the digest has to accept its absence.</summary>
    [Fact]
    public void A_delete_has_a_fingerprint_and_it_is_not_a_patch_of_an_empty_body()
    {
        var id = Guid.NewGuid();

        var deleted = IdempotencyFingerprint.Of("DELETE", "owners", id, precondition: null, body: null);
        var patched = IdempotencyFingerprint.Of("PATCH", "owners", id, precondition: null, new JsonObject());

        deleted.ShouldNotBeNullOrWhiteSpace();
        deleted.ShouldNotBe(patched, "the method alone must keep these apart");
    }

    /// <summary>
    /// "Write only if the row is at v1" and "write unconditionally" are two requests, so one key cannot
    /// stand for both — a caller who retried with a corrected precondition would otherwise be answered
    /// with the result of a write that never checked one.
    /// </summary>
    [Fact]
    public void A_precondition_is_part_of_the_request_the_key_stands_for()
    {
        var id = Guid.NewGuid();
        var body = new JsonObject { ["name"] = "Renamed" };
        var version = new AlvoPrecondition(DateTimeOffset.UtcNow);

        var conditional = IdempotencyFingerprint.Of("PATCH", "owners", id, version, body);
        var unconditional = IdempotencyFingerprint.Of("PATCH", "owners", id, precondition: null, body);

        conditional.ShouldNotBe(unconditional);
    }

    /// <summary>
    /// The precondition is digested as an instant, not as a formatted timestamp: the port compares
    /// instants, so two offsets of one instant are one precondition and must not be two digests.
    /// </summary>
    [Fact]
    public void One_instant_at_two_offsets_is_one_precondition_to_the_digest()
    {
        var id = Guid.NewGuid();
        var utc = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var shifted = utc.ToOffset(TimeSpan.FromHours(2));

        var asUtc = IdempotencyFingerprint.Of("PATCH", "owners", id, new AlvoPrecondition(utc), body: null);
        var asShifted = IdempotencyFingerprint.Of(
            "PATCH", "owners", id, new AlvoPrecondition(shifted), body: null);

        asShifted.ShouldBe(asUtc);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build MMLib.Alvo.slnx -c Debug`
Expected: FAIL — `Of` takes three parameters.

- [ ] **Step 3: Widen `Of`**

Replace the method and extend its `<remarks>`. Keep every existing paragraph; add the unambiguity argument for the new segments, because the file's own remark rests on it.

```csharp
    /// <summary>The fingerprint of one write: its method, its entity, its row, its precondition and its body.</summary>
    /// <param name="method">The request method, e.g. <c>POST</c>.</param>
    /// <param name="entity">The entity being written, as the applied schema names it.</param>
    /// <param name="id">The row the write addresses, or <see langword="null"/> for a create.</param>
    /// <param name="precondition">The version the write is conditional on, or <see langword="null"/>.</param>
    /// <param name="body">The request body as the payload reader parsed it, or <see langword="null"/> for a delete.</param>
    /// <returns>A lower-case hex SHA-256 digest.</returns>
    /// <remarks>
    /// <para>
    /// <b>The row id is in the digest and it has to be.</b> Without it, <c>PATCH /vehicles/A</c> and
    /// <c>PATCH /vehicles/B</c> carrying one key and one body share a fingerprint — so the second is
    /// answered as a replay of the first, row B is never written, and the caller is told it was. That is
    /// the "silently wrong" direction this type's own bullet list describes, one level up.
    /// </para>
    /// <para>
    /// <b>Appended only when present, which is what keeps a create's digest where it was.</b> Always
    /// joining an empty segment would digest a create as <c>POST\nowners\n\n{…}</c> — a different value,
    /// so every create in flight across the deploy would become a 409.
    /// <c>A_creates_fingerprint_is_unchanged_by_the_parameters_a_create_does_not_carry</c> holds it.
    /// </para>
    /// <para>
    /// <b>The precondition is digested as an instant</b> (<see cref="DateTimeOffset.UtcTicks"/>), because
    /// <see cref="AlvoPrecondition.EnsureMatches"/> compares instants: two offsets of one instant are one
    /// precondition to the port, and a formatted timestamp would make them two digests and two records.
    /// <c>RowVersionETag</c> already encodes the ticks, so this is the existing spelling.
    /// </para>
    /// <para>
    /// <b>The unambiguity argument extends, and here is why.</b> The parts are newline-joined and no part
    /// can hold a separator: the method is one token of HTTP's closed set, the entity is
    /// <c>^[a-z][a-z0-9_]{0,62}$</c> by the descriptor's own schema, a <see cref="Guid"/>'s <c>D</c> form is
    /// hex and hyphens, the precondition is <c>v</c> followed by digits, and the body is last and carries no
    /// raw newline because a JSON writer escapes every control character. The <c>v</c> prefix is what keeps
    /// a precondition from ever being read as an id, which matters only if a future write carries one
    /// without the other.
    /// </para>
    /// </remarks>
    internal static string Of(
        string method, string entity, Guid? id, AlvoPrecondition? precondition, JsonObject? body)
    {
        var input = new StringBuilder(method).Append('\n').Append(entity);
        if (id is { } row)
        {
            input.Append('\n').Append(row);
        }

        if (precondition is { } version)
        {
            input.Append("\nv").Append(version.Version.UtcTicks);
        }

        input.Append('\n').Append(body is null ? string.Empty : Canonical(body));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString())));
    }
```

Add `using MMLib.Alvo.Data;` if the file lacks it (for `AlvoPrecondition`).

- [ ] **Step 4: Update the one call site**

`DataApiEndpoints.Idempotency` currently builds `new AlvoIdempotency(key, IdempotencyFingerprint.Of(method, entity.Name, body))`. Give it the two new arguments as `null` for now — Task 5 supplies them for real:

```csharp
        return new AlvoIdempotency(
            key, IdempotencyFingerprint.Of(method, entity.Name, id: null, precondition: null, body));
```

- [ ] **Step 5: Correct the port's stale prose**

`AlvoIdempotency.Fingerprint`'s documentation says the layer computing it "hashes the whole request — for HTTP, the method, the path and the body, and the path names the entity". The route template was deliberately removed from the digest (it embedded `RoutePrefix`), so that sentence has been wrong since. Replace it with what is now true: *the method, the entity, the row the write addresses, the precondition it carries and the body* — and keep the paragraph's point, which is that a matched fingerprint proves the replay is for the same entity.

- [ ] **Step 6: Run the tests**

Run: `scripts/test-ring0`
Expected: `[ring0] OK`, with the five new facts passing and **every existing idempotency fact still green** — the create digest did not move.

- [ ] **Step 7: Commit**

```bash
git add src/MMLib.Alvo/Api/Internal/IdempotencyFingerprint.cs \
        src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs \
        src/MMLib.Alvo.Abstractions/Data/AlvoIdempotency.cs \
        test/MMLib.Alvo.Api.Tests/IdempotencyTests.cs
git commit -m "feat(api): the fingerprint covers the row, the precondition and an absent body

Without the row id, one key and one body against two different rows share a
digest — so the second write is answered as a replay of the first, the row
is never written, and the caller is told it was. The id segment is appended
only when there is one, so a create's digest is byte-identical to the one
this API has always produced.

Also corrects the port's own prose, which has said the digest covers 'the
path' since the route template was deliberately removed from it.

Refs #102

Claude-Session: https://claude.ai/code/session_01Uh7NkobnQZy5fDftEZbVLp"
```

---

### Task 2: The record holds a list of rows

Design §3. The column keeps its name and widens what its text means, so there is **no DDL change** — which is the point, because both creators use `CREATE TABLE IF NOT EXISTS` and would silently skip a redefinition.

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/IdempotencyTable.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs` (`IdempotencyScope`, `ReplayedAsync`, `RecordedCreateAsync`)
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/IdempotencyTableTests.cs` (create)

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces:
  - `internal readonly record struct IdempotencyTable.IdempotencyRecord(string Fingerprint, IReadOnlyList<Guid> RowIds)`
  - `IdempotencyTable.InsertAsync(..., IReadOnlyList<Guid> rowIds, ...)`
  - `internal static string IdempotencyTable.Encode(IReadOnlyList<Guid> rowIds)` and `internal static IReadOnlyList<Guid> Decode(string stored)`

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/IdempotencyTableTests.cs`:

```csharp
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// What the idempotency record's row column holds. The column is <c>TEXT</c> and keeps its name, so
/// widening it to a list is a change to what the text means and to nothing else — which is the whole
/// reason it is safe against a database the framework created with <c>CREATE TABLE IF NOT EXISTS</c>.
/// </summary>
public sealed class IdempotencyTableTests
{
    /// <summary>One write's row list round-trips.</summary>
    [Fact]
    public void One_row_round_trips()
    {
        var id = Guid.NewGuid();

        IdempotencyTable.Decode(IdempotencyTable.Encode([id])).ShouldBe([id]);
    }

    /// <summary>A batch's does too, in the order the batch wrote them.</summary>
    [Fact]
    public void Many_rows_round_trip_in_order()
    {
        IReadOnlyList<Guid> ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

        IdempotencyTable.Decode(IdempotencyTable.Encode(ids)).ShouldBe(ids);
    }

    /// <summary>
    /// A record written before this widening holds a bare GUID, and it is still readable.
    /// </summary>
    /// <remarks>
    /// Nothing is released, so this is a courtesy to a developer's existing local database rather than a
    /// compatibility obligation — but without it the first replay against such a database throws inside
    /// the write transaction, which the contended-write retry then retries ten times before surfacing it
    /// as an unattributable 500.
    /// </remarks>
    [Fact]
    public void A_record_written_before_the_widening_is_still_one_row()
    {
        var id = Guid.NewGuid();

        IdempotencyTable.Decode(id.ToString()).ShouldBe([id]);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build MMLib.Alvo.slnx -c Debug`
Expected: FAIL — `Encode`/`Decode` do not exist.

- [ ] **Step 3: Widen the record and add the codec**

In `IdempotencyTable.cs`, change the record and add the two members. Keep the DDL **exactly as it is**.

```csharp
    /// <summary>
    /// The record stored for one key in one scope, or <see langword="null"/> when the key is unused there.
    /// </summary>
    /// <param name="Fingerprint">The fingerprint of the request the key was first used for.</param>
    /// <param name="RowIds">
    /// The rows that request wrote, in the order it wrote them — one for every write this API had before
    /// the batch, and more only for a batch.
    /// </param>
    internal readonly record struct IdempotencyRecord(string Fingerprint, IReadOnlyList<Guid> RowIds);

    /// <summary>The rows a record covers, as the column's text.</summary>
    /// <remarks>
    /// <b>A JSON array, in a column that has always been <c>TEXT</c> — so this is not a schema change.</b>
    /// It could not be one: <see cref="EnsureAsync"/> and <c>SystemSchemaInitializer</c> both create the
    /// table with <c>CREATE TABLE IF NOT EXISTS</c>, so a redefinition against an existing database is
    /// silently skipped and every statement naming a new column would fail inside the write transaction —
    /// where the contended-write retry would retry it ten times and surface it as a 500.
    /// </remarks>
    /// <param name="rowIds">The rows the write covered.</param>
    internal static string Encode(IReadOnlyList<Guid> rowIds)
    {
        ArgumentNullException.ThrowIfNull(rowIds);
        return JsonSerializer.Serialize(rowIds.Select(id => id.ToString()));
    }

    /// <summary>The rows a stored value names.</summary>
    /// <remarks>
    /// A value that does not begin with <c>[</c> is one row, which is the shape every record written
    /// before the widening holds. Two lines, and they are what keeps a developer's existing local
    /// database working across this commit.
    /// </remarks>
    /// <param name="stored">The column's text.</param>
    internal static IReadOnlyList<Guid> Decode(string stored)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stored);
        return stored.StartsWith('[')
            ? [.. JsonSerializer.Deserialize<string[]>(stored)!.Select(Guid.Parse)]
            : [Guid.Parse(stored)];
    }
```

Add `using System.Text.Json;` to the file.

- [ ] **Step 4: Read and write through the codec**

`FindAsync`'s projection becomes `new IdempotencyRecord(reader.GetString(0), Decode(reader.GetString(1)))`.

`InsertAsync`'s parameter `Guid rowId` becomes `IReadOnlyList<Guid> rowIds`, its `<param>` doc updated, and its binding becomes `RelationalSqlBatch.AddParameter(command, "@row_id", Encode(rowIds));`.

- [ ] **Step 5: Update the two `EfAlvoData` call sites**

`IdempotencyScope.InsertAsync(Guid rowId, …)` becomes `InsertAsync(IReadOnlyList<Guid> rowIds, …)` and forwards the list. `RecordedCreateAsync` calls it with `[(Guid)candidate[AlvoDataContext.IdColumn]]`. `ReplayedAsync` reads `record.RowIds[0]` — and asserts the list is non-empty, because an empty one is a broken invariant of this file rather than a caller error:

```csharp
        var rowId = record.RowIds.Count > 0
            ? record.RowIds[0]
            : throw new InvalidOperationException(
                "An idempotency record names no row. Every write records at least one, so an empty list "
                + "means the record was written by something other than this port's write paths.");
```

- [ ] **Step 6: Run the tests**

Run: `scripts/test-ring0`
Expected: `[ring0] OK` — the three new facts pass and every existing idempotency fact is unchanged, because a one-element array is what a create now stores and the reader accepts both shapes.

- [ ] **Step 7: Commit**

```bash
git add src/MMLib.Alvo.Data.EntityFrameworkCore/ test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/
git commit -m "refactor(data): an idempotency record names the rows its write covered

The column keeps its name and widens what its text means, because both
creators use CREATE TABLE IF NOT EXISTS and would silently skip a
redefinition — a renamed column would fail inside the write transaction,
where the contended-write retry would retry it ten times and surface it as
an unattributable 500.

Refs #102, refs #106

Claude-Session: https://claude.ai/code/session_01Uh7NkobnQZy5fDftEZbVLp"
```

---

### Task 3: The port carries a token on an update and a delete

Design §2 and §9. Signature-only in the port; the two shipped implementations honour it in Task 4. This task is where three approval baselines first move and where the positional callers break.

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Data/IAlvoData.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs` (signatures only)
- Modify: `src/MMLib.Alvo.Testing/Data/InMemoryAlvoData.cs` (signatures only)
- Modify: `test/_shared/api/FaultingAlvoData.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs` (the two positional call sites)
- Test: the three `PublicApi.*.verified.txt` baselines move

**Interfaces:**
- Produces:
  - `Task<AlvoRecord> UpdateAsync(string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext context, AlvoPrecondition? precondition = null, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)`
  - `Task DeleteAsync(string entity, Guid id, AlvoContext context, AlvoPrecondition? precondition = null, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)`

- [ ] **Step 1: Widen the two port members**

`idempotency` goes **after** `precondition` and **before** `cancellationToken`. That order is deliberate and must be stated in the `<param>` doc: it groups the two optional write-channel parameters together and keeps `cancellationToken` last, which is the .NET convention every other member here follows.

Each `<param name="idempotency">` reads:

```
/// The caller's idempotency token, or <see langword="null"/> for an ordinary write. With a token, the
/// first write is recorded against it and a replay carrying the same
/// <see cref="AlvoIdempotency.Fingerprint"/> is answered without writing again — an update by re-reading
/// the recorded row under a freshly resolved <c>get</c> decision, a delete by answering that the row is
/// gone without reading anything, because there is nothing left to read. The record is scoped to the
/// caller's tenant and user, and a token from an anonymous caller is refused.
```

Extend the type's own remarks where they describe the two concurrency channels: the sentence *"an `AlvoIdempotency` token is their claim that a create may already have happened"* becomes *"…that this write may already have happened"*, and the paragraph on what a record stores gains the delete's answer.

Add to the `<exception cref="AlvoIdempotencyConflictException">` list on both members: *"`idempotency`'s key was already used for a request with a different fingerprint."*

- [ ] **Step 2: Widen the three implementors**

`EfAlvoData`, `InMemoryAlvoData` and `test/_shared/api/FaultingAlvoData.cs`. For now each accepts the parameter and **ignores it** — Task 4 implements the two real ones. `FaultingAlvoData` keeps throwing the fifth failure family, which is its whole contract.

Add `AlvoIdempotency.EnsureUsableToken(idempotency, context);` to the head of both real implementations, beside the guards already there. It is the port's own rule and applies whichever verb carries the token — a blank, over-long or anonymous key is refused before anything is resolved.

- [ ] **Step 3: Fix the positional callers**

`DataApiEndpoints` passes `(…, context, precondition, ct)` positionally at both sites. Inserting a parameter makes `ct` bind to `idempotency` — a **compile error**, not a silent bind, because the types differ. Change both to named arguments:

```csharp
                    var record = await data
                        .UpdateAsync(entity.Name, id, body.Values, context, precondition, cancellationToken: ct)
                        .ConfigureAwait(false);
```

```csharp
                    await data.DeleteAsync(entity.Name, id, context, precondition, cancellationToken: ct)
                        .ConfigureAwait(false);
```

Then grep the whole repo for other call sites: `rg 'UpdateAsync\(|DeleteAsync\(' --type cs` and fix any that pass the token positionally. Named-argument sites (`AlvoDataWorlds.cs`, `AlvoDataStatementTests.cs`) survive untouched.

- [ ] **Step 4: Build and accept the baselines**

Run: `dotnet build MMLib.Alvo.slnx -c Debug`
Expected: PASS.

Run: `scripts/test-ring0`
Expected: FAIL on `PublicApiApprovalTests` for `MMLib.Alvo.Abstractions` and `MMLib.Alvo.Testing`.

Read each `.received.txt` diff before accepting. It must contain **only** the two changed signatures (Abstractions) and the two changed `InMemoryAlvoData` signatures (Testing). Then move each `.received.txt` over its `.verified.txt`.

The Stop hook will require `alvo-snapshot-judge` for the moved baselines **and** the `alvo-architecture-rules` pass because a public surface grew. Design §9 is the justification; dispatch both and act on their verdicts.

- [ ] **Step 5: Run ring0 and commit**

```bash
scripts/test-ring0
git add src/ test/
git commit -m "feat(data): an update and a delete may carry an idempotency token

Signature only — the two shipped implementations honour it next. The
parameter sits after precondition and before cancellationToken, which groups
the two write-channel options and keeps the token last as every other member
here does; every positional caller of cancellationToken becomes named, which
is a compile error rather than a silent rebind because the types differ.

Refs #102

Claude-Session: https://claude.ai/code/session_01Uh7NkobnQZy5fDftEZbVLp"
```

---

### Task 4: The two implementations honour the token

Design §2. An update replays by re-reading; a delete replays by answering that the row is gone.

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs`
- Modify: `src/MMLib.Alvo.Testing/Data/InMemoryAlvoData.cs`
- Test: `src/MMLib.Alvo.Testing/Data/AlvoDataConcurrencyTests.cs`

**Interfaces:**
- Consumes: `IdempotencyTable.IdempotencyRecord` with `RowIds` (Task 2), the widened signatures (Task 3).
- Produces: nothing new; behaviour only.

- [ ] **Step 1: Write the failing contract facts**

Add to `src/MMLib.Alvo.Testing/Data/AlvoDataConcurrencyTests.cs` — the shared suite, so each fact runs on SQLite, PostgreSQL and in-memory. Follow the file's existing world-construction idiom.

```csharp
    /// <summary>
    /// A retried update is answered with the row rather than refused, which is the whole of #102: without
    /// a key the retry is a 412 the caller cannot tell from someone else having changed the row.
    /// </summary>
    /// <remarks>
    /// The version is what makes this non-vacuous. A second write would advance it again, so asserting
    /// that it did **not** move is what proves the replay wrote nothing — a status alone could not.
    /// </remarks>
    [Fact]
    public async Task A_retried_update_answers_the_row_and_writes_nothing()
    {
        await using var world = await WorldAsync();
        var id = await world.SeedAsync("notes", new() { ["title"] = "first" });
        var token = new AlvoIdempotency("k-update", "fingerprint-update");

        var first = await world.Data.UpdateAsync(
            "notes", id, new Dictionary<string, object?> { ["title"] = "second" }, world.Caller,
            idempotency: token);
        var replay = await world.Data.UpdateAsync(
            "notes", id, new Dictionary<string, object?> { ["title"] = "second" }, world.Caller,
            idempotency: token);

        replay["title"].ShouldBe("second");
        replay[AlvoManagedColumns.UpdatedAt].ShouldBe(
            first[AlvoManagedColumns.UpdatedAt],
            "a replay must not write again — a second write would advance the version");
    }

    /// <summary>
    /// A retried delete is answered as done rather than as absent. Without a key the retry is a 404 that
    /// cannot be told from somebody else's delete, which is precisely the question the key answers.
    /// </summary>
    [Fact]
    public async Task A_retried_delete_answers_as_done_rather_than_as_absent()
    {
        await using var world = await WorldAsync();
        var id = await world.SeedAsync("notes", new() { ["title"] = "doomed" });
        var token = new AlvoIdempotency("k-delete", "fingerprint-delete");

        await world.Data.DeleteAsync("notes", id, world.Caller, idempotency: token);
        await Should.NotThrowAsync(
            () => world.Data.DeleteAsync("notes", id, world.Caller, idempotency: token));

        (await world.Data.GetAsync("notes", id, world.Caller)).ShouldBeNull();
    }

    /// <summary>The same key for a different request is a conflict on these verbs too, not a replay.</summary>
    [Fact]
    public async Task One_key_reused_for_a_different_update_is_a_conflict()
    {
        await using var world = await WorldAsync();
        var id = await world.SeedAsync("notes", new() { ["title"] = "first" });

        await world.Data.UpdateAsync(
            "notes", id, new Dictionary<string, object?> { ["title"] = "second" }, world.Caller,
            idempotency: new AlvoIdempotency("k-shared", "fingerprint-a"));

        await Should.ThrowAsync<AlvoIdempotencyConflictException>(
            () => world.Data.UpdateAsync(
                "notes", id, new Dictionary<string, object?> { ["title"] = "third" }, world.Caller,
                idempotency: new AlvoIdempotency("k-shared", "fingerprint-b")));
    }

    /// <summary>
    /// A retried update whose precondition held the first time is not re-evaluated against the row it
    /// already advanced — which is the 412 #102 exists to remove.
    /// </summary>
    [Fact]
    public async Task A_retried_conditional_update_is_not_refused_by_its_own_first_write()
    {
        await using var world = await WorldAsync();
        var id = await world.SeedAsync("notes", new() { ["title"] = "first" });
        var stored = await world.Data.GetAsync("notes", id, world.Caller);
        var version = new AlvoPrecondition((DateTimeOffset)stored![AlvoManagedColumns.UpdatedAt]!);
        var token = new AlvoIdempotency("k-conditional", "fingerprint-conditional");

        await world.Data.UpdateAsync(
            "notes", id, new Dictionary<string, object?> { ["title"] = "second" }, world.Caller,
            version, token);

        await Should.NotThrowAsync(
            () => world.Data.UpdateAsync(
                "notes", id, new Dictionary<string, object?> { ["title"] = "second" }, world.Caller,
                version, token));
    }
```

> **Executor note:** `AlvoDataConcurrencyTests` builds its world through the abstract members the file already declares. Read the top of that file and use whatever `SeedAsync`/`WorldAsync`/`Caller` equivalents it actually exposes rather than the shapes sketched above — the *claims* are what this plan fixes, not the helper names. If the file has no seed helper, add one there so all three implementations share it.

- [ ] **Step 2: Run to verify they fail**

Run: `scripts/test-ring0`
Expected: FAIL — the token is accepted and ignored, so the replay writes a second time and the version moves.

- [ ] **Step 3: Implement in `EfAlvoData`**

`UpdateAsync` and `DeleteAsync` gain the same shape `CreateAsync` already has: without a token, the path is exactly what it is today; with one, the whole attempt is wrapped in the contended-write retry and the transaction body becomes find-record-then-branch.

```csharp
        return idempotency is { } token
            ? await ReplayableWriteAsync(
                () => RecordedUpdateAsync(entity, id, values, decision, context, precondition, token, cancellationToken),
                cancellationToken)
            : await UpdatedAsync(entity, id, values, decision, context, precondition, cancellationToken);
```

`ReplayableWriteAsync` is `ReplayableCreateAsync`'s loop, generalised over a delegate — extract it rather than copying it, because two copies of a retry policy is two places for the backoff to drift.

Inside the transaction, before any write:

```csharp
        var recorded = await records.FindAsync(cancellationToken);
        if (recorded is { } record)
        {
            return await ReplayedUpdateAsync(db, schema, context, record, token, cancellationToken);
        }
```

`ReplayedUpdateAsync` mirrors `ReplayedAsync`: it compares the fingerprint (mismatch → `AlvoIdempotencyConflictException`) and re-reads `record.RowIds[0]` under a freshly resolved **`get`** decision — never the `update` decision the call arrived with, for the reason `CreateAsync`'s contract gives: an update decision's `USING` filters the rows the caller may *write*, which is not the same set.

The delete's replay is simpler and must not read at all:

```csharp
        var recorded = await records.FindAsync(cancellationToken);
        if (recorded is { } record)
        {
            EnsureSameRequest(record, token);
            return;
        }
```

`EnsureSameRequest` throws `AlvoIdempotencyConflictException` when `!token.Matches(record.Fingerprint)`, and is shared by all three replay sites.

Record the row after the write, exactly as the create does: `await records.InsertAsync([id], now, cancellationToken);`.

- [ ] **Step 4: Implement in `InMemoryAlvoData`**

The same shape under `_gate`: look the record up, branch, and record after the write. The existing `Replay`, `RecordIdempotencyLocked` and `IdempotencyKey` helpers already do most of it; `Replay` gains an overload that answers "this key is spent" without producing a row, which is what a delete's replay needs.

- [ ] **Step 5: Run the tests**

Run: `scripts/test-ring0`
Expected: `[ring0] OK`. Then `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration` to run the PostgreSQL leg, which ring0 skips.

- [ ] **Step 6: Commit**

```bash
git add src/
git commit -m "feat(data): an update replays by re-reading, a delete by answering it is done

An update's replay re-reads the recorded row under a freshly resolved get
decision, never the update decision the call arrived with — an update's
USING filters the rows the caller may write, which is not the same set. A
delete's replay reads nothing, because there is nothing left to read, and
that is the whole value: without it the retry is a 404 the caller cannot
tell from somebody else's delete.

The contended-write retry is extracted rather than copied — two copies of a
retry policy is two places for the backoff to drift.

Refs #102

Claude-Session: https://claude.ai/code/session_01Uh7NkobnQZy5fDftEZbVLp"
```

---

### Task 5: The HTTP layer honours the header on both verbs

Design §1, §1.1 and §2. This is where `data-api.md`'s *"`Idempotency-Key` is **ignored** on `PATCH` and `DELETE`"* stops being true.

**Files:**
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs` (`MapUpdate`, `MapDelete`, `Idempotency`)
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiDocumentation.cs` (the update and delete prose)
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiParameters.cs` (`HeaderNames`)
- Test: `test/MMLib.Alvo.Api.Tests/IdempotencyTests.cs`, `test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.cs`

**Interfaces:**
- Consumes: Task 1's `Of`, Task 3's signatures, Task 4's behaviour.

- [ ] **Step 1: Write the failing facts**

Add to `test/MMLib.Alvo.Api.Tests/IdempotencyTests.cs`, over `AlvoApiWorld`:

```csharp
    /// <summary>
    /// The scenario #102 was filed for: the 200 is lost, the caller retries the identical request, and
    /// the answer is the row rather than the 412 they cannot attribute.
    /// </summary>
    [Fact]
    public async Task A_retried_conditional_patch_is_answered_with_the_row_not_a_precondition_failure()
    {
        var world = await SeededAsync();
        await using var _ = world;
        var (id, etag) = await CreateOwnerAsync(world, "Acme Ltd");
        var headers = new[]
        {
            new KeyValuePair<string, string>("If-Match", etag),
            new KeyValuePair<string, string>(DataApiEndpoints.IdempotencyKeyHeader, "retry-1"),
        };

        using var first = await world.SendAsync(
            HttpMethod.Patch, $"/api/owners/{id}", _admin, body: Owner("Renamed"), headers: headers);
        using var retry = await world.SendAsync(
            HttpMethod.Patch, $"/api/owners/{id}", _admin, body: Owner("Renamed"), headers: headers);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        retry.StatusCode.ShouldBe(
            HttpStatusCode.OK, "the retry is the caller's own write, not somebody else's change");
        (await retry.ReadFieldAsync("name")).ShouldBe(await first.ReadFieldAsync("name"));
    }

    /// <summary>A retried delete is 204, not the 404 that reads as somebody else's delete.</summary>
    [Fact]
    public async Task A_retried_delete_is_answered_as_done()
    {
        var world = await SeededAsync();
        await using var _ = world;
        var (id, _) = await CreateOwnerAsync(world, "Doomed Ltd");
        var headers = new[]
        {
            new KeyValuePair<string, string>(DataApiEndpoints.IdempotencyKeyHeader, "retry-delete"),
        };

        using var first = await world.SendAsync(HttpMethod.Delete, $"/api/owners/{id}", _admin, headers: headers);
        using var retry = await world.SendAsync(HttpMethod.Delete, $"/api/owners/{id}", _admin, headers: headers);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        retry.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// One key against two different rows is a conflict, not a replay — the defect the fingerprint's row
    /// id exists to prevent, asserted over HTTP where a caller would actually hit it.
    /// </summary>
    [Fact]
    public async Task One_key_against_two_rows_is_a_conflict_over_http()
    {
        var world = await SeededAsync();
        await using var _ = world;
        var (first, _) = await CreateOwnerAsync(world, "First Ltd");
        var (second, _) = await CreateOwnerAsync(world, "Second Ltd");
        var headers = new[]
        {
            new KeyValuePair<string, string>(DataApiEndpoints.IdempotencyKeyHeader, "one-key"),
        };

        using var wrote = await world.SendAsync(
            HttpMethod.Patch, $"/api/owners/{first}", _admin, body: Owner("Renamed"), headers: headers);
        using var other = await world.SendAsync(
            HttpMethod.Patch, $"/api/owners/{second}", _admin, body: Owner("Renamed"), headers: headers);

        wrote.StatusCode.ShouldBe(HttpStatusCode.OK);
        other.StatusCode.ShouldBe(
            HttpStatusCode.Conflict, "the same key against another row is another request");
        (await world.ReadOwnerNameAsync(second)).ShouldBe(
            "Second Ltd", "the refused write must not have landed");
    }

    /// <summary>An anonymous caller still cannot hold a key, on these verbs as on the create.</summary>
    [Fact]
    public async Task An_anonymous_callers_key_is_refused_on_an_update_too()
    {
        var world = await SeededAsync();
        await using var _ = world;
        var (id, _) = await CreateOwnerAsync(world, "Acme Ltd");

        using var response = await world.SendAsync(
            HttpMethod.Patch,
            $"/api/owners/{id}",
            key: null,
            body: Owner("Renamed"),
            headers: [new KeyValuePair<string, string>(DataApiEndpoints.IdempotencyKeyHeader, "anon")]);

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.UnprocessableEntity, HttpStatusCode.Forbidden);
    }
```

> **Executor note:** `IdempotencyTests` already has its own world, key and body helpers. Use them; the helper names above are illustrative and the *claims* are what this plan fixes. `An_anonymous_callers_key_is_refused_on_an_update_too` accepts either status because policy may refuse the anonymous caller before the key is read — which is correct precedence, and the fact's point is only that the key never buys them anything.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --test-modules "test/MMLib.Alvo.Api.Tests/bin/Debug/*/MMLib.Alvo.Api.Tests.dll" --filter-class "*IdempotencyTests" --root-directory .`
Expected: FAIL — the header is still ignored on both verbs.

- [ ] **Step 3: Read the key on both verbs**

In `MapUpdate`, after the decision and **before** the body is read — the position `IdempotencyKey`'s own remarks fix, because a request carrying a key this API cannot serve must not be answered with advice about a field:

```csharp
                    var context = Caller(caller);
                    var decision = EnsureOperationIsAllowed(
                        policies, entity.Name, DataApiEndpointKind.Update.ToDataOperation(), context);
                    var precondition = Precondition(http.Request);
                    var key = IdempotencyKey(http.Request, context, options);
```

and after validation:

```csharp
                    var token = Idempotency(key, http.Request.Method, entity, id, precondition, body.Document);
                    var record = await data
                        .UpdateAsync(entity.Name, id, body.Values, context, precondition, token, ct)
                        .ConfigureAwait(false);
```

`MapDelete` reads the key in the same position and builds its token with no body:

```csharp
                    var token = Idempotency(key, http.Request.Method, entity, id, precondition, document: null);
                    await data.DeleteAsync(entity.Name, id, context, precondition, token, ct).ConfigureAwait(false);
```

- [ ] **Step 4: Widen `Idempotency`**

```csharp
    /// <summary>
    /// The token this write is performed under: the caller's key plus the fingerprint of the request it
    /// belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built <b>after</b> validation, because the fingerprint covers the body and a body that was refused
    /// never reaches the port at all — so a fingerprint over it would digest a request that was never
    /// performed and reserve the key against it.
    /// </para>
    /// <para>
    /// <b>A create's <paramref name="document"/> is non-null by construction and a delete's is null by
    /// contract</b>, so the invariant this used to assert — "a create reached the port with no parsed
    /// body" — now only holds for a create, and is asserted only there.
    /// </para>
    /// </remarks>
    /// <param name="key">The caller's key, or <see langword="null"/> when they sent none.</param>
    /// <param name="method">The request method, for the digest.</param>
    /// <param name="entity">The entity being written.</param>
    /// <param name="id">The row the write addresses, or <see langword="null"/> for a create.</param>
    /// <param name="precondition">The version the write is conditional on, or <see langword="null"/>.</param>
    /// <param name="document">The body as it was parsed, or <see langword="null"/> for a delete.</param>
    private static AlvoIdempotency? Idempotency(
        string? key,
        string method,
        EntitySchema entity,
        Guid? id,
        AlvoPrecondition? precondition,
        JsonObject? document)
    {
        if (key is null)
        {
            return null;
        }

        EnsureACreateParsedItsBody(id, document);

        return new AlvoIdempotency(
            key, IdempotencyFingerprint.Of(method, entity.Name, id, precondition, document));
    }

    /// <summary>
    /// Asserts the invariant that a create with no violations bound as an object — family 5, rendered 500,
    /// the same reasoning as <see cref="AssignedId"/>.
    /// </summary>
    /// <remarks>
    /// Scoped to a create, because it is only a create's invariant: a delete legitimately has no body, and
    /// an update's is checked by the same violation path a create's is.
    /// </remarks>
    /// <param name="id">The row the write addresses, or <see langword="null"/> for a create.</param>
    /// <param name="document">The body as it was parsed.</param>
    private static void EnsureACreateParsedItsBody(Guid? id, JsonObject? document)
    {
        if (id is null && document is null)
        {
            throw new InvalidOperationException(
                "A create reached the port with no parsed body. JsonPayloadReader reports a body that is "
                + "not an object as a violation, and a violation is answered before this point.");
        }
    }
```

The create's call site passes `id: null, precondition: null`.

- [ ] **Step 5: Publish the header on both operations**

`DataApiParameters.HeaderNames` currently gives `IdempotencyKeyId` to `Create` alone, and `IfMatchId` to `Update`/`Delete` on a versioned entity. Both write verbs now honour the key, so:

```csharp
            DataApiEndpointKind.Create => [IdempotencyKeyId],
            DataApiEndpointKind.Update or DataApiEndpointKind.Delete
                when AlvoManagedColumns.VersionColumn(entity) is not null => [IfMatchId, IdempotencyKeyId],
            DataApiEndpointKind.Update or DataApiEndpointKind.Delete => [IdempotencyKeyId],
```

The parameter's description says "Makes this create retry-safe" — reword it to cover all three verbs, and add the sentence a caller cannot infer: **a key covers the row and the precondition too, so the same key against another row, or with another `If-Match`, is a 409 rather than a replay.**

- [ ] **Step 6: Rewrite the prose that says the header is ignored**

`DataApiDocumentation.Update` and `Delete` each carry a paragraph beginning *"**`Idempotency-Key` is accepted and ignored here — a known limitation…**"*, and `UpdateRetry(entity)` exists only to describe what that costs. Replace them:

- The update's paragraph says the key makes the retry answer the row, that the fingerprint covers the row and the precondition, and that a key reused for another row or another precondition is 409.
- The delete's says a retried delete is 204 rather than a 404 the caller cannot attribute.
- `UpdateRetry(entity)`'s two arms shrink to one sentence each: with a key, a retried write is answered; without one, the version-less advice it already gives still stands.

- [ ] **Step 7: Move the OpenAPI counts and snapshot**

`OpenApiDocumentTests` pins `documented.Count` and `refusals.Count`. Adding the 409 to update and delete (the key can now conflict there) moves both — **compute the new numbers from the failure, do not guess**, and update the reason strings to say what changed. `ProbesAsync` needs an `update`/`delete` 409 probe driven by a reused key.

Then read the `.received.txt` diff before accepting: it must contain the two new header parameters, the reworded prose, and the two 409 responses. Dispatch `alvo-snapshot-judge` for the moved baseline.

- [ ] **Step 8: Run ring0 and commit**

```bash
scripts/test-ring0
git add src/ test/
git commit -m "feat(api): Idempotency-Key is honoured on PATCH and DELETE

The scenario #102 was filed for: the 200 is lost, the caller retries the
identical request, and the answer is the row rather than a 412 they cannot
attribute. A retried delete is 204 rather than a 404 that reads as somebody
else's delete.

The key covers the row and the precondition, so one key against another row
— or the same row with another If-Match — is a 409 rather than a replay of
the first. data-api.md's 'ignored on PATCH and DELETE' section is now the
history of a limitation rather than a description of one.

Closes #102

Claude-Session: https://claude.ai/code/session_01Uh7NkobnQZy5fDftEZbVLp"
```

---

### Task 6: One rule, one evaluation — the write verdict becomes collectable

Design §5. This is the enabling refactor for the batch, and it is behaviour-neutral: the single-row paths answer exactly what they answer today.

**The reason it comes first, and why it is shaped this way.** A batch must report *every* bad row, so it needs a verdict it can collect rather than one that throws. Writing a second, collecting variant beside the throwing one would be two expressions of one authorization rule — which is precisely how the two come to differ, and this rule is the `WITH CHECK` predicate. So the collecting form becomes the **only** implementation, and the throwing form becomes a caller that throws on its result.

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs` (`EnsureWriteAllowed`)
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/WritePayloadGuard.cs`
- Modify: `src/MMLib.Alvo.Testing/Data/InMemoryAlvoData.cs` (its own `EnsureWriteAllowed`)
- Test: the existing adversarial suite must stay green **untouched**

**Interfaces:**
- Produces:
  - `private string? EfAlvoData.WriteRefusal(PolicyDecision decision, AlvoRecord postImage, AlvoRecord? previous, AlvoContext context)` — the deny reason, or `null` when the write is allowed.
  - `internal static string? WritePayloadGuard.PayloadRefusal(IReadOnlyDictionary<string, object?> values, EntitySchema schema, PolicyDecision decision, bool isUpdate)`

- [ ] **Step 1: Invert the two guards**

`EnsureWriteAllowed` currently evaluates and throws. Split it: `WriteRefusal` returns the reason or `null`, and `EnsureWriteAllowed` becomes

```csharp
    /// <summary>
    /// Throws when the candidate row fails its <c>WITH CHECK</c> predicate or the tenant scope.
    /// </summary>
    /// <remarks>
    /// <b>A caller of <see cref="WriteRefusal"/> rather than a second evaluation of the rule.</b> A batch
    /// needs the same verdict without a throw, so that it can report every bad row rather than the first;
    /// two expressions of one authorization rule is how the two come to differ, and this rule is the
    /// <c>WITH CHECK</c> predicate. One evaluation, and the throw is the single-row caller's choice.
    /// </remarks>
    private void EnsureWriteAllowed(
        PolicyDecision decision, AlvoRecord postImage, AlvoRecord? previous, AlvoContext context)
    {
        if (WriteRefusal(decision, postImage, previous, context) is { } reason)
        {
            throw new AlvoAuthorizationException(reason);
        }
    }
```

Do the same to `WritePayloadGuard.EnsureWritable` → `PayloadRefusal` + a throwing wrapper, and to `InMemoryAlvoData`'s own `EnsureWriteAllowed`.

- [ ] **Step 2: Run the suite unchanged**

Run: `scripts/test-ring0`
Expected: `[ring0] OK` with **no test edited**. That is the whole check: an inversion that changed a refusal would fail the adversarial suite, which already drives every arm of both guards.

- [ ] **Step 3: Commit**

```bash
git add src/
git commit -m "refactor(data): the write verdict is computed once and thrown by its caller

A batch has to report every bad row, which needs a verdict it can collect.
Writing a second collecting variant beside the throwing one would be two
expressions of one authorization rule — the WITH CHECK predicate — and that
is how two expressions of one rule come to differ. The collecting form is
now the only implementation; the throw is a caller's choice. No test moved,
which is the check.

Refs #106

Claude-Session: https://claude.ai/code/session_01Uh7NkobnQZy5fDftEZbVLp"
```

---

### Task 7: The port's batch vocabulary

Design §4, §6 and §9. Types only — no implementation, no route.

**Files:**
- Move: `src/MMLib.Alvo/Api/AlvoViolation.cs` → `src/MMLib.Alvo.Abstractions/Data/AlvoViolation.cs`
- Create: `src/MMLib.Alvo.Abstractions/Data/AlvoRowPatch.cs`
- Create: `src/MMLib.Alvo.Abstractions/Data/AlvoBatchResult.cs`
- Modify: `src/MMLib.Alvo.Abstractions/Data/IAlvoData.cs`
- Modify: every file with `using MMLib.Alvo.Api;` that resolved `AlvoViolation`
- Test: three `PublicApi.*.verified.txt` baselines move

**Interfaces:**
- Produces:
  - `public sealed record AlvoRowPatch(Guid Id, IReadOnlyDictionary<string, object?> Values)`
  - `public sealed record AlvoBatchResult(IReadOnlyList<AlvoRecord> Rows, IReadOnlyList<AlvoViolation> Violations)`
  - `Task<AlvoBatchResult> CreateManyAsync(string entity, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)`
  - `Task<AlvoBatchResult> UpdateManyAsync(string entity, IReadOnlyList<AlvoRowPatch> rows, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)`
  - `Task<AlvoBatchResult> DeleteManyAsync(string entity, IReadOnlyList<Guid> ids, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)`

- [ ] **Step 1: Move `AlvoViolation` to the ports**

Namespace becomes `MMLib.Alvo.Data`. Its documentation gains one paragraph explaining why it lives here now: **the port reports a batch's per-row refusals**, and a refusal that named no row would make a 500-row import unfixable. The `pointer` documentation — including PR-G's rule that tells an RFC 6901 body pointer from a query-parameter role — travels with it unchanged.

Then fix every consumer. `MMLib.Alvo` already references `Abstractions`, so most files need only their `using` adjusted; `grep -rn "AlvoViolation" src/ test/` finds them.

- [ ] **Step 2: Add the two records**

```csharp
namespace MMLib.Alvo.Data;

/// <summary>One row of a batch update: which row, and the fields to change on it.</summary>
/// <remarks>
/// <b>A named type rather than a tuple</b>, because it appears in an implementor's signature and a tuple
/// element cannot carry documentation — and the thing that most needs documenting is that
/// <paramref name="Values"/> is <em>partial</em>, exactly as <see cref="IAlvoData.UpdateAsync"/>'s is: a
/// field this dictionary does not mention keeps its stored value.
/// </remarks>
/// <param name="Id">The row to change.</param>
/// <param name="Values">The fields to change; a field this dictionary does not mention keeps its stored value.</param>
public sealed record AlvoRowPatch(Guid Id, IReadOnlyDictionary<string, object?> Values);
```

```csharp
namespace MMLib.Alvo.Data;

/// <summary>What one batch produced: the rows it wrote, or every reason it wrote none.</summary>
/// <remarks>
/// <para>
/// <b>Exactly one of the two is non-empty.</b> A batch is one transaction, so it either wrote every row
/// or wrote none — there is no partial outcome for this type to express, and a result carrying both would
/// be describing one that cannot happen.
/// </para>
/// <para>
/// <b><see cref="Violations"/> is how a refusal names a row.</b> A batch of five hundred that reports
/// only "refused" is a batch nobody can fix, so each violation's pointer carries the offending row's
/// index — <c>/rows/3/quoted_price</c>. That is the reason this port can report a policy refusal at all
/// rather than raising <see cref="AlvoAuthorizationException"/>, which carries a message and nothing else.
/// </para>
/// <para>
/// <b><see cref="Rows"/> is empty for a delete</b>, which removes rows rather than producing them. The
/// count of what it removed is <see cref="IReadOnlyList{T}.Count"/> on what the caller passed in, so this
/// type does not repeat it.
/// </para>
/// </remarks>
/// <param name="Rows">The rows the batch wrote, in request order; empty for a delete and for a refusal.</param>
/// <param name="Violations">Every reason the batch wrote nothing; empty when it wrote.</param>
public sealed record AlvoBatchResult(
    IReadOnlyList<AlvoRecord> Rows, IReadOnlyList<AlvoViolation> Violations);
```

- [ ] **Step 3: Declare the three port members**

Each gets full documentation covering: the transaction ("either every row or none"), per-row policy ("every row is judged against `WITH CHECK` and the tenant scope individually"), the idempotency token ("one key for the batch — a batch is one request, so a partial retry is not expressible"), and its exceptions. `DeleteManyAsync` also documents that it takes no precondition, and why (design §4).

- [ ] **Step 4: Stub the three implementors**

`EfAlvoData`, `InMemoryAlvoData` and `FaultingAlvoData`. The first two throw `NotImplementedException` for now — Tasks 8 and 9 fill them; `FaultingAlvoData` throws its family-5 failure, which is its contract.

- [ ] **Step 5: Move the baselines**

Run: `scripts/test-ring0`
Expected: FAIL on all three `PublicApiApprovalTests`.

Read each `.received.txt`. Abstractions gains `AlvoRowPatch`, `AlvoBatchResult`, `AlvoViolation` and three interface members; `MMLib.Alvo` **loses** `AlvoViolation`; Testing gains three `InMemoryAlvoData` members. Anything else in those diffs is a mistake. Accept, then run `alvo-snapshot-judge` and the `alvo-architecture-rules` pass — design §9 justifies each symbol.

- [ ] **Step 6: Commit**

```bash
git add src/ test/
git commit -m "feat(data): the port's batch vocabulary, and AlvoViolation moves to it

A batch's refusals travel on its result, because a refusal that named no row
makes a five-hundred-row import unfixable — and AlvoAuthorizationException
carries a message and nothing else. That is what puts AlvoViolation in the
ports: the port has to be able to name a row's refusal without the HTTP
layer inventing one.

Types only; the three members throw until the next two tasks.

Refs #106

Claude-Session: https://claude.ai/code/session_01Uh7NkobnQZy5fDftEZbVLp"
```

---

### Task 8: The contract facts, and the in-memory implementation

Design §5 and §10. The facts come before the shipped implementation deliberately: they are the contract, and the in-memory reference is where the atomicity claim can be implemented wrong and still stay green — so it is written against them first.

**Files:**
- Create: `src/MMLib.Alvo.Testing/Data/AlvoDataBatchTests.cs`
- Create: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataBatchTests.cs`
- Create: `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoDataBatchTests.cs`
- Create: `test/MMLib.Alvo.Tests/Data/InMemoryAlvoDataBatchTests.cs`
- Modify: `src/MMLib.Alvo.Testing/Data/InMemoryAlvoData.cs`

**Interfaces:**
- Consumes: Task 7's port members, Task 6's collectable verdict.

- [ ] **Step 1: Write the shared facts**

`AlvoDataBatchTests` is an abstract class in the pattern `AlvoDataConcurrencyTests` already uses — read that file's world-construction members and mirror them exactly. The facts, each with the control that makes it non-vacuous:

```csharp
    /// <summary>
    /// The headline claim: a batch is one transaction. A batch whose LAST row fails leaves no row
    /// written — asserted as a count over the whole entity, because a partial commit is exactly what a
    /// per-row assertion would miss.
    /// </summary>
    [Fact]
    public async Task A_batch_whose_last_row_fails_writes_nothing()
    {
        await using var world = await WorldAsync();
        var before = await CountAsync(world, "notes");

        var result = await world.Data.CreateManyAsync(
            "notes",
            [Row("first"), Row("second"), RowThatFailsTheCheck()],
            world.Caller);

        result.Rows.ShouldBeEmpty();
        result.Violations.ShouldNotBeEmpty();
        (await CountAsync(world, "notes")).ShouldBe(before, "a batch commits every row or none");
    }

    /// <summary>
    /// Every offending row is reported, not the first — a five-hundred-row import that reports row 3 and
    /// stops will be run five hundred times.
    /// </summary>
    [Fact]
    public async Task Every_offending_row_is_reported_with_its_index()
    {
        await using var world = await WorldAsync();

        var result = await world.Data.CreateManyAsync(
            "notes",
            [Row("ok"), RowThatFailsTheCheck(), Row("ok"), Row("ok"), RowThatFailsTheCheck()],
            world.Caller);

        result.Violations.Select(violation => violation.Pointer).ShouldBe(
            ["/rows/1", "/rows/4"],
            ignoreOrder: false,
            customMessage: "every bad row, in the order the batch carried them");
    }

    /// <summary>
    /// Policy is judged per row over that row's own post-image, so a batch cannot smuggle a value past a
    /// check by pairing it with a row that passes.
    /// </summary>
    [Fact]
    public async Task A_row_the_check_refuses_is_refused_even_beside_rows_it_admits()
    {
        await using var world = await WorldAsync();

        var alone = await world.Data.CreateManyAsync("notes", [RowThatFailsTheCheck()], world.Caller);
        var beside = await world.Data.CreateManyAsync(
            "notes", [Row("ok"), RowThatFailsTheCheck()], world.Caller);

        alone.Violations.ShouldNotBeEmpty();
        beside.Violations.ShouldNotBeEmpty("a passing neighbour must not admit a failing row");
    }

    /// <summary>
    /// Cross-tenant isolation, as a test rather than an argument: a batch naming one row of another
    /// tenant writes nothing, and the same batch from the owning tenant succeeds.
    /// </summary>
    [Fact]
    public async Task A_batch_naming_another_tenants_row_writes_nothing()
    {
        await using var world = await TenantWorldAsync();
        var theirs = await world.SeedForAsync(world.OtherTenant, "notes", Row("theirs"));
        var mine = await world.SeedForAsync(world.Caller, "notes", Row("mine"));

        var refused = await world.Data.UpdateManyAsync(
            "notes", [Patch(mine, "renamed"), Patch(theirs, "renamed")], world.Caller);
        var allowed = await world.Data.UpdateManyAsync(
            "notes", [Patch(mine, "renamed")], world.Caller);

        refused.Rows.ShouldBeEmpty();
        refused.Violations.ShouldNotBeEmpty();
        allowed.Rows.Count.ShouldBe(1, "the same batch without the other tenant's row must succeed");
    }

    /// <summary>A batch is one request under one key, so replaying it writes no second set of rows.</summary>
    [Fact]
    public async Task A_replayed_batch_writes_no_second_set_of_rows()
    {
        await using var world = await WorldAsync();
        var token = new AlvoIdempotency("k-batch", "fingerprint-batch");

        var first = await world.Data.CreateManyAsync(
            "notes", [Row("a"), Row("b")], world.Caller, token);
        var replay = await world.Data.CreateManyAsync(
            "notes", [Row("a"), Row("b")], world.Caller, token);

        replay.Rows.Select(row => row[AlvoManagedColumns.Id]).ShouldBe(
            first.Rows.Select(row => row[AlvoManagedColumns.Id]),
            "a replay answers the rows the first batch wrote");
        (await CountAsync(world, "notes")).ShouldBe(2, "two rows, not four");
    }

    /// <summary>A delete removes every named row, or none of them.</summary>
    [Fact]
    public async Task A_batch_delete_removes_every_named_row_or_none()
    {
        await using var world = await WorldAsync();
        var first = await world.SeedAsync("notes", Row("first"));
        var second = await world.SeedAsync("notes", Row("second"));

        var refused = await world.Data.DeleteManyAsync(
            "notes", [first, second, Guid.NewGuid()], world.Caller);

        refused.Violations.ShouldNotBeEmpty("an absent row refuses the batch");
        (await CountAsync(world, "notes")).ShouldBe(2, "and leaves the two real rows in place");

        var removed = await world.Data.DeleteManyAsync("notes", [first, second], world.Caller);

        removed.Violations.ShouldBeEmpty();
        (await CountAsync(world, "notes")).ShouldBe(0);
    }

    /// <summary>An empty batch is refused rather than treated as a write of nothing.</summary>
    /// <remarks>
    /// It is the only reading that survives the transport: a <c>DELETE</c> carrying a body is undefined by
    /// RFC 9110 §9.3.5 and an intermediary may strip it, which would arrive as an empty batch. Answering
    /// "nothing to do, 200" would make a stripped body a silent success.
    /// </remarks>
    [Fact]
    public async Task An_empty_batch_is_refused()
    {
        await using var world = await WorldAsync();

        var result = await world.Data.CreateManyAsync("notes", [], world.Caller);

        result.Violations.ShouldNotBeEmpty();
    }
```

- [ ] **Step 2: Wire the three legs**

Nothing does this automatically. Create the three subclasses, each a thin `: AlvoDataBatchTests` with the world factory its sibling suites already use. Read `SqliteAlvoDataConcurrencyTests`, `PostgreSqlAlvoDataConcurrencyTests` and `InMemoryAlvoDataConcurrencyTests` and copy their shape exactly.

- [ ] **Step 3: Run to verify they fail**

Run: `scripts/test-ring0`
Expected: FAIL on every batch fact — the port members throw `NotImplementedException`.

- [ ] **Step 4: Implement in `InMemoryAlvoData`, with a real rollback**

The in-memory store mutates a `List<AlvoRecord>` under `_gate` and **has no transaction**, so "every row or none" has to be built rather than inherited:

```csharp
        lock (_gate)
        {
            var live = RowsForLocked(entity);
            var staged = new List<AlvoRecord>(live);

            foreach (var row in rows)
            {
                Judge(row, violations);
                Stage(row, staged);
            }

            if (violations.Count > 0)
            {
                return Task.FromResult(new AlvoBatchResult([], violations));
            }

            live.Clear();
            live.AddRange(staged);
        }
```

Staging into a copy and swapping under the same lock is the reference's stand-in for a transaction — the same role `_gate` already plays for the create's find-then-insert.

- [ ] **Step 5: Run the in-memory leg**

Run: `dotnet test --test-modules "test/MMLib.Alvo.Tests/bin/Debug/*/MMLib.Alvo.Tests.dll" --filter-class "*InMemoryAlvoDataBatchTests" --root-directory .`
Expected: PASS. The two engine legs still fail — Task 9 owns them.

- [ ] **Step 6: Commit**

```bash
git add src/MMLib.Alvo.Testing/ test/
git commit -m "test(data): the batch contract, and the in-memory reference that has to build it

The facts come before the shipped implementation because they ARE the
contract, and because the in-memory reference is the one place 'every row or
none' can be implemented wrong and still stay green — it has no transaction
to inherit the property from, so it stages into a copy and swaps under the
same lock the create's find-then-insert already uses.

Refs #106

Claude-Session: https://claude.ai/code/session_01Uh7NkobnQZy5fDftEZbVLp"
```

---

### Task 9: The EF batch — two passes, one transaction

Design §5. The security core of this PR. Every structural choice here is one the design's verification pass forced.

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/BatchWrite.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs`
- Test: the two engine legs from Task 8 turn green

**Interfaces:**
- Consumes: `WriteRefusal`/`PayloadRefusal` (Task 6), the port members (Task 7), the facts (Task 8).

- [ ] **Step 1: Implement `CreateManyAsync`**

The shape, and the five things about it that are not obvious:

```csharp
    public async Task<AlvoBatchResult> CreateManyAsync(
        string entity, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, AlvoContext context,
        AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(context);
        AlvoIdempotency.EnsureUsableToken(idempotency, context);

        var decision = Resolve(entity, DataOperation.Create, context);

        return await BatchAsync(
            entity, decision, context, idempotency,
            (db, schema, now) => CreateEveryRowAsync(db, schema, decision, context, rows, now, cancellationToken),
            cancellationToken);
    }
```

1. **`BatchAsync` owns the transaction, the record lookup and the retry**, exactly as `CreatedOrReplayedAsync` does for one row — so the three batch members differ only in what they do inside it.
2. **The judging pass runs first and writes nothing.** For a create that is `PayloadRefusal` then `WriteRefusal` over the candidate; a violation is collected with its index, not thrown.
3. **Nothing is written until the judging pass has consumed every row.** If any violation exists, the transaction rolls back and the result carries the violations.
4. **`RecomputeRollupsAsync` is called once**, after every row is written, with the whole list of stored images — it already takes a list and groups by parent precisely so N children of one parent cost one recompute rather than N.
5. **One instant covers the batch.** `WriteInstantNow()` is read once by `BatchAsync` and threaded into every row's audit stamp, every event's `time` and the record's `created_at` — so all N rows share one `updated_at` and one `ETag`, which is right: they were written together.

- [ ] **Step 2: Implement `UpdateManyAsync`**

Two things it must do that the create does not:

**Sort the rows by id before either pass.** Two concurrent batches whose id sets overlap take their row locks in whatever order the callers wrote them, which deadlocks on PostgreSQL. A fixed order costs one sort and is the standard fix. Document it as such:

```csharp
    /// <remarks>
    /// <b>Sorted by id before anything is locked.</b> Each row's verdict is reached over its row-locked
    /// pre-image, so two concurrent batches whose id sets overlap would take the same locks in the order
    /// their callers happened to write them — a deadlock on PostgreSQL. A fixed order removes it for one
    /// sort, and the request order is preserved separately for the result and for the violation indices,
    /// because a caller's row 3 must be reported as row 3.
    /// </remarks>
```

**Take all N pre-images before writing any row**, because the `WITH CHECK` verdict is reached over *that row's* locked pre-image merged with *that row's* patch. That is what makes the judging pass possible at all.

The violation index is the **request** index, not the sorted one. Carry the original position alongside each row.

- [ ] **Step 3: Implement `DeleteManyAsync`**

`EnsureNotSoftDeleted(schema)` once, before the transaction — it throws `InvalidOperationException` (family 5, a 500), and an entity that cannot be deleted at all is not a per-row refusal.

Otherwise the same shape: sorted ids, all pre-images read, every absent-or-invisible row collected as a violation, then the deletes.

- [ ] **Step 4: Narrow the retry**

`ReplayableWriteAsync` (Task 4) retries the whole attempt on any storage write failure. Around a batch that is N inserts, N hooks and N outbox rows, ten times — so an unrecognised failure on row 400 costs ten full batches before it surfaces.

The retry exists for exactly one thing: a rival committing the same idempotency key first, which fails **the record insert**. Narrow it to that:

```csharp
    /// <summary>
    /// The record insert, wrapped so a rival that committed the same key first becomes a replay rather
    /// than a failure.
    /// </summary>
    /// <remarks>
    /// <b>Only this insert is retried, and around a batch that matters.</b> The retry exists because the
    /// record's primary key is the concurrency control — a rival can make this one statement fail and
    /// nothing else. Retrying the whole attempt would re-run N inserts, N hooks and N outbox rows ten
    /// times for a failure no retry can fix, which for a five-hundred-row batch is five thousand wasted
    /// writes before the caller is told anything.
    /// </remarks>
```

The single-row create keeps the behaviour it has; what changes is that the batch does not inherit the wide form.

- [ ] **Step 5: Run both engine legs**

Run: `scripts/test-ring0` (SQLite + in-memory)
Then: `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration -c Debug`
Expected: every batch fact green on all three implementations.

- [ ] **Step 6: Check the SQL-composing allow-list**

`ChangeTrackerReachTests.Only_allow_listed_files_compose_sql_or_build_a_command` guards which files may compose SQL. If `BatchWrite.cs` composes any, add it to the allow-list **with a reason**; if it only calls existing helpers, it must not appear there — and that is the better outcome.

- [ ] **Step 7: Commit**

```bash
git add src/ test/
git commit -m "feat(data): a transactional batch whose policy is judged per row

Two passes inside one transaction, and the shape is forced rather than
chosen: the single-row helpers throw on the first failure, and PostgreSQL
aborts a transaction after any statement error — so 'catch per row and keep
going' is not available, and every row is judged before any row is written.

The rows are sorted by id before anything is locked, because two concurrent
batches whose id sets overlap would otherwise take the same locks in the
order their callers wrote them. The request order is kept separately, since
a caller's row 3 must be reported as row 3.

Rollups are recomputed once with every stored image rather than once per
row, one instant covers the batch, and the contended-write retry is narrowed
to the record insert — the only statement a rival can make fail.

Refs #106

Claude-Session: https://claude.ai/code/session_01Uh7NkobnQZy5fDftEZbVLp"
```

---

### Task 10: Measure the row bound, then write it down

Design §7. #106 asks for a bound *measured* the way `AlvoFilter.MaxTerms` was, not chosen. This task produces the measurement first and the constant second; if the measurement finds no clean ceiling, the number is recorded as **chosen** with its reason, which is what PR-G did for `MaxPatternLength` rather than pretending to a measurement it did not have.

**Files:**
- Create: `docs/superpowers/specs/evidence/2026-09-04-batch-row-bound.md`
- Modify: `src/MMLib.Alvo/Api/AlvoApiOptions.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/AlvoApiOptionsValidator.cs`

**Interfaces:**
- Produces: `public int AlvoApiOptions.MaxBatchRows { get; set; }`

- [ ] **Step 1: Measure**

A throwaway harness — **labelled throwaway and not committed** — driving `CreateManyAsync`, `UpdateManyAsync` and `DeleteManyAsync` at N = 10, 50, 100, 250, 500, 1000, 2500, 5000 against **both** SQLite and a real PostgreSQL container, recording per N: wall time, peak managed allocation (`GC.GetTotalAllocatedBytes`), and the transaction's open duration.

What the measurement is looking for is the same shape `MaxTerms`' own remark records — the N at which something *breaks* or degrades sharply, named. Candidates: the round-trip cost of N single-row inserts plus N outbox inserts; SQLite's write-lock duration; PostgreSQL's lock table.

- [ ] **Step 2: Write the evidence file**

Follow the format of the existing files under `docs/superpowers/specs/evidence/`. It must state the engine versions, the hardware, the raw numbers, and — the part that matters — **what fails and at what N**, or that nothing does within the range measured.

- [ ] **Step 3: Add the option**

```csharp
    /// <summary>The most rows one batch request may carry. Default <b>measured</b>; see the evidence file.</summary>
    /// <remarks>
    /// <para>
    /// <b>Not <see cref="MaxPayloadKeys"/>, and that is the whole reason this exists.</b> The key bound
    /// counts property names at every depth, so a batch of N rows with K fields spends <c>1 + N·K</c> of
    /// it — about a hundred rows for a five-field entity. A batch refused by that bound is told it sent
    /// too many *fields*, which is advice about the wrong thing; the batch reader counts rows against this
    /// and applies <see cref="MaxPayloadKeys"/> per row, which is what that number has always meant on a
    /// single write.
    /// </para>
    /// <para>
    /// The number comes from <c>docs/superpowers/specs/evidence/2026-09-04-batch-row-bound.md</c>, which
    /// records what degrades and at what N on both shipped engines.
    /// </para>
    /// </remarks>
    public int MaxBatchRows { get; set; } = 500;
```

> **The literal `500` above is a placeholder for the plan's sake and must not survive Step 1.** Replace it
> with whatever the measurement supports, and make the remark name the failure mode the way
> `AlvoFilter.MaxTerms`' does ("900 filter terms answered in 14 ms and 1000 threw a raw
> `SqliteException`"). If the measurement finds no ceiling inside a range worth bounding, keep a number and
> **say it is chosen**, with the reason — which is what `MaxPatternLength` did in PR-G rather than claiming
> a measurement it did not have.

Add a validator rule beside the existing ones: at least 1, and a message naming the option and the fix.

- [ ] **Step 4: Run ring0 and commit**

```bash
scripts/test-ring0
git add src/ docs/superpowers/specs/evidence/
git commit -m "feat(api): a measured bound on a batch's row count

#106 asks for a bound measured the way MaxTerms was rather than chosen, and
the evidence file records what degrades and at what N on both engines.

It is a row bound and not the field bound, because MaxPayloadKeys counts
property names at every depth — a batch of N rows with K fields spends
1 + N*K of it, so a five-field entity is capped near a hundred rows and told
it sent too many FIELDS. That is advice about the wrong thing.

Refs #106

Claude-Session: https://claude.ai/code/session_01Uh7NkobnQZy5fDftEZbVLp"
```

---

### Task 11: The batch route

Design §4.1. One path per entity, three verbs, three endpoint kinds.

**Files:**
- Create: `src/MMLib.Alvo/Api/Internal/BatchBodyReader.cs`
- Create: `src/MMLib.Alvo/Api/Internal/BatchViolations.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiEndpointKind.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs`
- Test: `test/MMLib.Alvo.Api.Tests/DataApiBatchTests.cs` (create), `DataApiRoutingTests.cs`

**Interfaces:**
- Produces:
  - `DataApiEndpointKind.BatchCreate`, `BatchUpdate`, `BatchDelete` with wire names `batchCreate`, `batchUpdate`, `batchDelete`
  - `internal static Task<BatchBodyReader.Result> BatchBodyReader.ReadAsync(HttpRequest request, EntitySchema entity, AlvoApiOptions options, DataApiEndpointKind kind, PolicyDecision decision, FormatCatalog formats, IAlvoData data, AlvoContext context, CancellationToken cancellationToken)`

- [ ] **Step 1: Write the failing routing facts**

`DataApiRoutingTests` pins the route table by name and by count. Six routes per entity becomes **nine**:

```csharp
            routes.ShouldContain($"POST /api/{entity}/batch");
            routes.ShouldContain($"PATCH /api/{entity}/batch");
            routes.ShouldContain($"DELETE /api/{entity}/batch");
```

and `routes.Count.ShouldBe(_entities.Length * 9, …)`, and the same for `endpoints.Count`. `ExpectedOperation` gains the `/batch` suffix as a discriminator so `POST …/batch` resolves to `Create`, `PATCH …/batch` to `Update` and `DELETE …/batch` to `Delete`.

`LazyRouteMaterialisationTests.PathsPerEntity` goes 3 → **4**, with the fourth path asserted by name.

- [ ] **Step 2: Add the three kinds**

```csharp
    /// <summary>The batch create, taking many rows in one transaction.</summary>
    BatchCreate,

    /// <summary>The batch update.</summary>
    BatchUpdate,

    /// <summary>The batch delete.</summary>
    BatchDelete,
```

`ToDataOperation` maps them to `Create`, `Update` and `Delete` — so each is gated by the filter that already gates its single-row sibling, with no new policy vocabulary. `ToWireName` gives them `batchCreate`, `batchUpdate`, `batchDelete`; the existing five keep the spellings they publish.

Extend the enum's `<remarks>`: the batches are the second reason this type exists — three routes gated as three operations that a caller may hold independently, which is why one route per verb rather than one route with a mode in its body.

- [ ] **Step 3: Read the batch body**

`BatchBodyReader` reads `{"rows": [ … ]}` and:

- refuses a body that is not an object, or that carries no `rows` array, with the read-flavoured shape refusals `BoundedJsonBody` already produces;
- **counts rows against `MaxBatchRows` while reading**, refusing at the first row past it — the bound must be spent while reading, exactly as `QueryBodyReader`'s value bound is;
- **applies `MaxPayloadKeys` per row** rather than across the body, which is what that number has always meant on a single write;
- binds and validates each row through the existing `JsonPayloadReader` + `RecordValidator` path, so a batch refuses exactly what a single write refuses;
- prefixes every violation's pointer with `/rows/{index}`.

`BatchViolations` composes the batch's own refusals: `EmptyBatch` (an empty `rows` array — never "nothing to do"), `TooManyRows(max)`, and `NotABatch` (no `rows` member). Each with a fix suggestion naming the bound.

- [ ] **Step 4: Map the three routes**

`MapBatchCreate`, `MapBatchUpdate`, `MapBatchDelete` on `{collection}/batch`, each `.Protect(entity, DataApiEndpointKind.Batch*, filters, conventions)`. Each resolves its decision before reading a byte of the body, for the reason every other write does.

`DELETE` carries a body, which RFC 9110 §9.3.5 leaves undefined — so the empty-batch refusal is what turns a stripped body into a 422 rather than a silent success. Document it on `MapBatchDelete`.

The response: `200` with `{ "items": [ … ] }` when the batch wrote, using the same `DataApiPage`-style envelope shape minus the cursor; `422` with the violations when it did not; `403` for a policy refusal that named no row. A batch delete answers `200` with an empty `items`, not `204`, because it is reporting on many rows.

- [ ] **Step 5: Write the end-to-end facts**

`DataApiBatchTests`, over `AlvoApiWorld`: a batch of three creates 3 rows; a batch whose last row is invalid creates none and reports `/rows/2`; a batch past `MaxBatchRows` is refused **naming the row bound**; an empty batch is 422; a batch on an entity the caller may not write is 403 before the store is touched; one key replayed writes no second set.

- [ ] **Step 6: Run ring0 and commit**

```bash
scripts/test-ring0
git add src/ test/
git commit -m "feat(api): the batch route — one path, three verbs, three endpoint kinds

Each verb is gated as the operation it already means, so a caller who may
create but not delete is refused by the filter that already refuses them on
the single-row routes. The row bound is spent while reading, and
MaxPayloadKeys applies per row rather than across the body — a batch told it
sent too many FIELDS is advice about the wrong thing.

A DELETE carrying a body is undefined by RFC 9110 9.3.5 and an intermediary
may strip it, so an empty batch is refused rather than read as 'no rows to
delete': the hazard becomes a 422 instead of a silent success.

Refs #106

Claude-Session: https://claude.ai/code/session_01Uh7NkobnQZy5fDftEZbVLp"
```

---

### Task 12: Publish it, and record what it costs

**Files:**
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiDocumentation.cs`, `DataApiParameters.cs`, `SchemaComponentBuilder.cs`, `AlvoDocumentTransformer.cs`
- Modify: `docs/architecture/data-api.md`, `docs/architecture/data-path.md`, `docs/architecture/events.md`
- Modify: `test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.cs`, `OpenApiDocumentCostTests.cs`, and the snapshot
- Modify: `test/teapie-field-service/090-Docs/001-openapi-document-test.csx`
- Create: `test/teapie-field-service/025-Batch/001-batch-req.http` + `-test.csx`

- [ ] **Step 1: Document the three operations**

`SummaryOf`, `DescriptionOf` and `ResponsesFor` gain the three kinds. The prose states: one transaction; per-row policy; the violation pointer's `/rows/{index}` shape; one key for the whole batch and why a partial retry is not expressible; the row bound; that a `DELETE` carries a body and an empty one is refused; and — the cost — **one event per row**, so a 500-row import fans out to 500 deliveries.

`DataApiParameters` gives each batch kind the `Idempotency-Key` header and a `{entity}Batch*` request-body component built from the same field set the single-row create and patch schemas use.

- [ ] **Step 2: Move the counts and the snapshot**

`RoutesPerEntity` 6 → 9; `Operations(document)` `* 6` → `* 9`; `documented.Count` and `refusals.Count` move — **compute them from the failure**. `ProbesAsync` gains 200/401/403/422 probes for each of the three, and a 409 for a replayed key.

Read the `.received.txt` before accepting; dispatch `alvo-snapshot-judge`.

- [ ] **Step 3: Fix the e2e pins and add e2e coverage**

`test/teapie-field-service/090-Docs/001-openapi-document-test.csx` pins the path set by **equality** — add `/api/{entity}/batch` for all three entities. This is the pin that cost PR-G a CI cycle; check it before pushing.

Add `025-Batch`: a batch create of three work orders against real PostgreSQL, a batch whose last row is refused leaving the count unchanged, and a batch naming another tenant's row writing nothing.

- [ ] **Step 4: Record it in the architecture docs**

- `data-api.md`: the route table gains three lines; the budget table gains `MaxBatchRows`; the `Idempotency-Key` section stops saying the header is ignored on `PATCH`/`DELETE` and describes what it now does on all five write routes; the status catalogue gains the batch's new codes; *Alternatives rejected* gains the mixed batch, the array-on-collection body and the filter-based bulk update.
- `data-path.md`: what "one write is one instant" means for a batch, and the sorted-id locking rule.
- `events.md`: one event per row, the source's own coalescing requirement, and the follow-up issue.

- [ ] **Step 5: File the coalescing follow-up**

`gh issue create` for batch event delivery: the source requirement (`baas-analyza` §3: *"import 10k riadkov nesmie znamenať 10k webhookov"*, acceptance criterion *"Bulk insert 10k riadkov s batch pravidlom = 1 batch event"*), why it is a descriptor feature rather than a write-path one (a rule declares `batch` delivery — a schema change, a compiler change and a new event shape), and what it costs until then.

- [ ] **Step 6: Run ring2 and commit**

```bash
scripts/test-ring2
git add src/ test/ docs/
git commit -m "docs(api): publish the batch, and record that it emits one event per row

The source asks for batch delivery — 'import 10k riadkov nesmie znamenat 10k
webhookov' — and this PR does not do it, because coalescing is a descriptor
feature: a rule has to declare batch delivery, which is a schema change, a
compiler change and a new event shape. Building it inside a data-path PR
would make the descriptor change invisible. Filed, and named here so a
500-row import fanning out to 500 deliveries is a known cost.

Refs #106

Claude-Session: https://claude.ai/code/session_01Uh7NkobnQZy5fDftEZbVLp"
```

---

## Before the PR

- [ ] `scripts/test-ring1` — green.
- [ ] `scripts/test-ring2` — green, including the PostgreSQL integration leg.
- [ ] **Grep `test/teapie*` for pinned path and field sets.** ring2 does not run them; this cost PR-G three CI cycles.
- [ ] Reviewer subagents on a **frozen tree**: `csharp-reviewer`, and a security reviewer against `alvo-security-core-review` — the per-row `WITH CHECK` is the thing to review hardest in this PR, and the maintainer has said so.
- [ ] `alvo-plan-guard`.
- [ ] `alvo-pr-report`.
- [ ] `gh pr create` with **`Closes #102`** and **`Closes #106`** — the keyword repeated per issue, because one keyword closes only the first. Verify both closed after merge.
- [ ] Label `needs-deep-review`.
