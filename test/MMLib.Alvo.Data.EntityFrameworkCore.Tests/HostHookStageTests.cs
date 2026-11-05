using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Rules;

using System.Collections.Concurrent;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// Where the driver runs host write hooks (spec 2026-10-14 §4.1 stages 4, 5, 7, 7b): in a unit of the write's own only when a
/// hook is registered (Ruling 1), with the candidate, the pre-image and the stored row, one hop deeper, and the patch judged
/// by the rules again. The core's runner is a recording fake here; the end-to-end facts are <c>WriteHookFacts</c>.
/// </summary>
public sealed class HostHookStageTests
{
    private const string Note = WritePathFixture.Entity;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_write_no_hook_runs_on_opens_no_unit_and_takes_no_savepoint()
    {
        var hooks = new RecordingWriteExtensions();
        await using var world = await StartAsync(hooks);

        await world.Data.CreateAsync(Note, WritePathFixture.CreatePayload("plain"), world.Caller, cancellationToken: Ct);

        hooks.Calls.ShouldBeEmpty();
        world.Statements.Seen.ShouldNotContain(statement => statement.Contains("SAVEPOINT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_create_runs_its_hooks_in_a_unit_of_its_own_with_the_candidate_and_the_stored_row_one_hop_deeper()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Create));
        await using var world = await StartAsync(hooks);

        var created = await world.Data.CreateAsync(Note, WritePathFixture.CreatePayload("hooked"), world.Caller, cancellationToken: Ct);

        hooks.Stages.ShouldBe(["before", "after"]);
        hooks.Calls.ShouldAllBe(call => call.UnitOnFlow);
        var (_, before, _, _) = hooks.Calls.First();
        before.New!["title"].ShouldBe("hooked");
        before.Old.ShouldBeNull();
        before.Transaction!.Context.Provenance.Depth.ShouldBe(1);
        hooks.Calls.Last().Invocation.Saved!["id"].ShouldBe(created["id"]);
    }

    [Fact]
    public async Task A_patch_is_stored_and_judged_by_the_create_rule_again()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Create));
        await using var world = await StartAsync(hooks, WritePathFixture.Descriptor(create: "owner_id == @user.id"));

        hooks.Patch["title"] = "set by the hook";
        var created = await world.Data.CreateAsync(Note, WritePathFixture.CreatePayload("asked"), world.Caller, cancellationToken: Ct);
        created["title"].ShouldBe("set by the hook");

        hooks.Patch["owner_id"] = Guid.NewGuid();
        await Should.ThrowAsync<AlvoAuthorizationException>(
            () => world.Data.CreateAsync(Note, WritePathFixture.CreatePayload("stolen"), world.Caller, cancellationToken: Ct));
        (await world.ScalarAsync("""SELECT COUNT(*) FROM "note" """)).ShouldBe(1L);
    }

    [Fact]
    public async Task An_update_hands_the_hook_the_pre_image_and_the_merged_candidate()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Update));
        await using var world = await StartAsync(hooks);
        var id = await world.SeedAsync("before");

        await world.Data.UpdateAsync(Note, id, new Dictionary<string, object?> { ["title"] = "after" }, world.Caller, cancellationToken: Ct);

        var (_, before, _, _) = hooks.Calls.First();
        (before.Operation, before.Old!["title"], before.New!["title"]).ShouldBe((AlvoWriteOperation.Update, "before", "after"));
    }

    [Fact]
    public async Task A_replace_reports_replace_and_a_delete_hands_the_row_being_deleted()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Replace));
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Delete));
        await using var world = await StartAsync(hooks);
        var id = await world.SeedAsync("kept");

        await world.Data.ReplaceAsync(Note, id, WritePathFixture.Payload("replaced"), world.Caller, cancellationToken: Ct);
        await world.Data.DeleteAsync(Note, id, world.Caller, cancellationToken: Ct);

        hooks.Calls.Select(call => (call.Stage, call.Invocation.Operation)).ShouldBe(
            [("before", AlvoWriteOperation.Replace), ("after", AlvoWriteOperation.Replace), ("before", AlvoWriteOperation.Delete), ("after", AlvoWriteOperation.Delete)]);
        hooks.Calls.Last().Invocation.Old!["title"].ShouldBe("replaced");
    }

    [Fact]
    public async Task A_rejected_batch_row_is_that_row_s_refusal_and_nothing_is_written()
    {
        var hooks = new RecordingWriteExtensions { RejectBefore = new RowScopedRefusal() };
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Create));
        await using var world = await StartAsync(hooks);

        var result = await world.Data.CreateManyAsync(Note, [WritePathFixture.CreatePayload("a"), WritePathFixture.CreatePayload("b")], world.Caller, cancellationToken: Ct);

        result.Succeeded.ShouldBeFalse();
        result.Refusals.Select(refusal => (refusal.Index, refusal.Code)).ShouldBe([(0, "refused-by-hook"), (1, "refused-by-hook")]);
        (await world.ScalarAsync("""SELECT COUNT(*) FROM "note" """)).ShouldBe(0L);
    }

    [Fact]
    public async Task An_after_write_that_wrote_makes_the_driver_read_the_row_again()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Create));
        hooks.After = (_, _) => Task.FromResult<string?>("writer");
        await using var world = await StartAsync(hooks);
        world.Statements.Clear();

        await world.Data.CreateAsync(Note, WritePathFixture.CreatePayload("re-read"), world.Caller, cancellationToken: Ct);

        world.Statements.Seen.Count(statement => statement.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && statement.Contains("\"note\"", StringComparison.Ordinal))
            .ShouldBeGreaterThanOrEqualTo(3, "the insert's read-back, the post-image re-read, and the echo");
    }

    [Fact]
    public async Task A_row_an_after_write_made_vanish_fails_the_write_as_row_vanished_and_logs_it_at_error()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Create));
        hooks.After = async (write, ct) =>
        {
            await write.Transaction!.DeleteAsync(Note, (Guid)write.Saved!["id"]!, cancellationToken: ct);
            return "eraser";
        };
        var logs = new HookLogRecorder();
        await using var world = await WritePathWorld.StartAsync(
            WritePathFixture.Descriptor(), WritePathFixture.Schema(), services => services.AddLogging(logging => logging.AddProvider(logs)), extensions: hooks);

        var failure = await Should.ThrowAsync<HookFailedException>(() => world.Data.CreateAsync(Note, WritePathFixture.CreatePayload("gone"), world.Caller, cancellationToken: Ct));

        (failure.Extension, failure.Reason).ShouldBe(("eraser", HookFailedException.RowVanished));
        logs.Errors.ShouldHaveSingleItem().ShouldBe(
            "Write hook eraser failed (row-vanished): after its writes, the write could no longer read back the row it ran on; the write was rolled back.");
        (await NotesAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task A_write_inside_a_unit_runs_its_hooks_in_that_unit_one_hop_deeper()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Create));
        await using var world = await StartAsync(hooks);

        await world.Transactions.TransactAsync(world.Caller, (unit, ct) => unit.CreateAsync(Note, WritePathFixture.CreatePayload("in a unit"), ct), cancellationToken: Ct);

        hooks.Stages.ShouldBe(["before", "after"]);
        hooks.Calls.First().Invocation.Transaction!.Context.Provenance.Depth.ShouldBe(1);
    }

    [Fact]
    public async Task An_update_patch_moving_the_row_to_another_owner_is_refused_by_the_update_rule_and_changes_nothing()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Update));
        await using var world = await StartAsync(hooks, WritePathFixture.Descriptor(update: "owner_id == @user.id"));
        var id = await world.SeedAsync("kept");
        hooks.Patch["owner_id"] = Guid.NewGuid();

        await Should.ThrowAsync<AlvoAuthorizationException>(
            () => world.Data.UpdateAsync(Note, id, new Dictionary<string, object?> { ["title"] = "moved" }, world.Caller, cancellationToken: Ct));

        (await world.ScalarAsync("SELECT title FROM \"note\"")).ShouldBe("kept");
        (await OutboxAsync(world)).ShouldBe(0L);
    }

    [Theory]
    [InlineData("owner_id")]
    [InlineData("tenant_id")]
    public async Task A_batch_create_patch_placing_rows_under_another_owner_or_tenant_refuses_each_row_and_writes_none(string field)
    {
        var hooks = new RecordingWriteExtensions();
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Create));
        await using var world = await StartAsync(hooks, WritePathFixture.Descriptor(create: "owner_id == @user.id"));
        hooks.Patch[field] = Guid.NewGuid();

        var result = await world.Data.CreateManyAsync(Note, [WritePathFixture.CreatePayload("a"), WritePathFixture.CreatePayload("b")], world.Caller, cancellationToken: Ct);

        result.Refusals.Select(refusal => (refusal.Index, refusal.Code)).ShouldBe([(0, "forbidden"), (1, "forbidden")]);
        (await NotesAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task A_patch_of_a_read_only_field_is_refused_as_the_caller_s_own_payload_would_be_on_a_create_and_an_update()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Create));
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Update));
        await using var world = await StartAsync(hooks, WritePathFixture.Descriptor(readOnlyField: "title"));
        var id = await world.SeedAsync("kept");
        hooks.Patch["title"] = "set by the hook";

        var created = await Should.ThrowAsync<AlvoAuthorizationException>(
            () => world.Data.CreateAsync(Note, UntitledPayload(), world.Caller, cancellationToken: Ct));
        var updated = await Should.ThrowAsync<AlvoAuthorizationException>(
            () => world.Data.UpdateAsync(Note, id, new Dictionary<string, object?> { ["owner_id"] = world.Caller.User.Value }, world.Caller, cancellationToken: Ct));

        (created.Message, updated.Message).ShouldBe((ReadOnlyTitle, ReadOnlyTitle));
        (await NotesAsync(world)).ShouldBe(1L);
        (await world.ScalarAsync("SELECT title FROM \"note\"")).ShouldBe("kept");
    }

    [Fact]
    public async Task A_patch_of_a_read_only_field_is_that_batch_row_s_refusal_on_a_create_and_an_update()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Create));
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Update));
        await using var world = await StartAsync(hooks, WritePathFixture.Descriptor(readOnlyField: "title"));
        var id = await world.SeedAsync("kept");
        hooks.Patch["title"] = "set by the hook";

        var created = await world.Data.CreateManyAsync(Note, [UntitledPayload()], world.Caller, cancellationToken: Ct);
        var updated = await world.Data.UpdateManyAsync(
            Note, [new AlvoRowPatch(id, new Dictionary<string, object?> { ["owner_id"] = world.Caller.User.Value })], world.Caller, cancellationToken: Ct);

        created.Refusals.Single().Message.ShouldBe(ReadOnlyTitle);
        updated.Refusals.Single().Message.ShouldBe(ReadOnlyTitle);
        (await world.ScalarAsync("SELECT title FROM \"note\"")).ShouldBe("kept");
    }

    [Fact]
    public async Task A_patch_of_a_field_hidden_and_read_only_for_the_caller_is_refused_without_naming_it()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Create));
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Update));
        await using var world = await StartAsync(hooks, HiddenReadOnlyTitle());
        var id = await world.SeedAsync("kept");
        hooks.Patch["title"] = "set by the hook";

        var created = await Should.ThrowAsync<AlvoAuthorizationException>(
            () => world.Data.CreateAsync(Note, UntitledPayload(), world.Caller, cancellationToken: Ct));
        var updated = await Should.ThrowAsync<AlvoAuthorizationException>(
            () => world.Data.UpdateAsync(Note, id, new Dictionary<string, object?> { ["owner_id"] = world.Caller.User.Value }, world.Caller, cancellationToken: Ct));
        var batch = await world.Data.CreateManyAsync(Note, [UntitledPayload()], world.Caller, cancellationToken: Ct);

        (created.Message, updated.Message, batch.Refusals.Single().Message).ShouldBe((HiddenRefusal, HiddenRefusal, HiddenRefusal));
        (await world.ScalarAsync("SELECT title FROM \"note\"")).ShouldBe("kept");
    }

    [Fact]
    public async Task An_after_write_rejection_of_a_create_or_an_update_leaves_no_row_change_and_no_event()
    {
        var hooks = new RecordingWriteExtensions { After = (_, _) => throw new RowScopedRefusal() };
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Create));
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Update));
        await using var world = await StartAsync(hooks);
        var id = await world.SeedAsync("kept");

        await Should.ThrowAsync<RowScopedRefusal>(() => world.Data.CreateAsync(Note, WritePathFixture.CreatePayload("refused"), world.Caller, cancellationToken: Ct));
        await Should.ThrowAsync<RowScopedRefusal>(
            () => world.Data.UpdateAsync(Note, id, new Dictionary<string, object?> { ["title"] = "refused" }, world.Caller, cancellationToken: Ct));

        (await NotesAsync(world)).ShouldBe(1L);
        (await world.ScalarAsync("SELECT title FROM \"note\"")).ShouldBe("kept");
        (await OutboxAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task An_after_write_rejection_in_a_batch_is_that_row_s_refusal_and_rolls_every_row_back()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Create));
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Update));
        hooks.After = (write, _) => Equals(write.Saved!["title"], "b") ? throw new RowScopedRefusal() : Task.FromResult<string?>(null);
        await using var world = await StartAsync(hooks);
        var id = await world.SeedAsync("kept");

        var created = await world.Data.CreateManyAsync(Note, [WritePathFixture.CreatePayload("a"), WritePathFixture.CreatePayload("b")], world.Caller, cancellationToken: Ct);
        var updated = await world.Data.UpdateManyAsync(
            Note, [new AlvoRowPatch(id, new Dictionary<string, object?> { ["title"] = "b" })], world.Caller, cancellationToken: Ct);

        created.Refusals.Select(refusal => (refusal.Index, refusal.Code)).ShouldBe([(1, "refused-by-hook")]);
        updated.Refusals.Select(refusal => (refusal.Index, refusal.Code)).ShouldBe([(0, "refused-by-hook")]);
        (await NotesAsync(world)).ShouldBe(1L);
        (await world.ScalarAsync("SELECT title FROM \"note\"")).ShouldBe("kept");
        (await OutboxAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task An_after_delete_rejection_in_a_batch_is_that_row_s_refusal_and_keeps_every_row()
    {
        var hooks = new RecordingWriteExtensions { After = (_, _) => throw new RowScopedRefusal() };
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Delete));
        await using var world = await StartAsync(hooks);
        Guid[] ids = [await world.SeedAsync("a"), await world.SeedAsync("b")];

        var result = await world.Data.DeleteManyAsync(Note, ids, world.Caller, cancellationToken: Ct);

        result.Succeeded.ShouldBeFalse();
        result.Refusals.Single().Code.ShouldBe("refused-by-hook");
        (await NotesAsync(world)).ShouldBe(2L);
        (await OutboxAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task Validate_runs_before_any_transaction_with_the_authorized_candidate_and_no_id()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.Validating.Add((Note, AlvoWriteOperation.Create));
        await using var world = await StartAsync(hooks);
        world.Statements.Clear();

        await world.Data.CreateAsync(Note, WritePathFixture.CreatePayload("checked"), world.Caller, cancellationToken: Ct);

        var (stage, invocation, unitOnFlow, autocommit) = hooks.Calls.ShouldHaveSingleItem();
        (stage, unitOnFlow, autocommit, invocation.Transaction).ShouldBe(("validate", false, (bool?)true, (IAlvoTransaction?)null));
        invocation.New!["title"].ShouldBe("checked");
        invocation.New.Values.ContainsKey("id").ShouldBeFalse("the row id is minted per attempt");
    }

    [Fact]
    public async Task Validate_of_an_update_sees_the_row_read_before_the_transaction_and_a_row_the_rule_does_not_admit_is_404_before_any_hook()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.Validating.Add((Note, AlvoWriteOperation.Update));
        await using var world = await StartAsync(hooks, WritePathFixture.Descriptor(update: "title != 'locked'"));
        var open = await world.SeedAsync("open");
        var locked = await world.SeedAsync("locked");

        await world.Data.UpdateAsync(Note, open, new Dictionary<string, object?> { ["title"] = "moved" }, world.Caller, cancellationToken: Ct);
        await Should.ThrowAsync<AlvoRecordNotFoundException>(() => world.Data.UpdateAsync(Note, locked, new Dictionary<string, object?> { ["title"] = "x" }, world.Caller, cancellationToken: Ct));

        var validated = hooks.Calls.ShouldHaveSingleItem().Invocation;
        (validated.Old!["title"], validated.New!["title"]).ShouldBe(("open", "moved"));
    }

    [Fact]
    public async Task A_validate_hook_never_sees_an_update_the_payload_guard_refuses()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.Validating.Add((Note, AlvoWriteOperation.Update));
        await using var world = await StartAsync(hooks);
        var id = await world.SeedAsync("kept");

        await Should.ThrowAsync<AlvoAuthorizationException>(() => world.Data.UpdateAsync(
            Note, id, new Dictionary<string, object?> { ["created_by"] = "someone else" }, world.Caller, cancellationToken: Ct));

        hooks.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_batch_reports_every_bad_row_whichever_check_refused_it()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.Validating.Add((Note, AlvoWriteOperation.Update));
        await using var world = await StartAsync(hooks);
        var kept = await world.SeedAsync("kept");
        var other = await world.SeedAsync("other");

        var result = await world.Data.UpdateManyAsync(
            Note,
            [
                new AlvoRowPatch(kept, new Dictionary<string, object?> { ["title"] = "fine" }),
                new AlvoRowPatch(other, new Dictionary<string, object?> { ["created_by"] = "x" }),
                new AlvoRowPatch(Guid.NewGuid(), new Dictionary<string, object?> { ["title"] = "gone" }),
                new AlvoRowPatch(kept, new Dictionary<string, object?> { ["title"] = "again" }),
            ],
            world.Caller,
            cancellationToken: Ct);

        result.Refusals.Select(refusal => (refusal.Index, refusal.Code)).ShouldBe([(1, "forbidden"), (2, "forbidden"), (3, "duplicate-row")]);
        hooks.Calls.ShouldHaveSingleItem("only the admitted row reached the validate hook").Invocation.New!["title"].ShouldBe("fine");
    }

    [Fact]
    public async Task Validate_of_a_replace_sees_the_stored_row_and_validates_a_missing_row_as_the_create_it_will_be()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.Validating.Add((Note, AlvoWriteOperation.Replace));
        await using var world = await StartAsync(hooks);
        var stored = await world.SeedAsync("stored");
        var fresh = Guid.NewGuid();

        await world.Data.ReplaceAsync(Note, stored, WritePathFixture.Payload("replaced"), world.Caller, cancellationToken: Ct);
        await world.Data.ReplaceAsync(Note, fresh, WritePathFixture.Payload("created"), world.Caller, cancellationToken: Ct);

        var calls = hooks.Calls.Select(call => call.Invocation).ToArray();
        calls.Select(call => call.Operation).ShouldBe([AlvoWriteOperation.Replace, AlvoWriteOperation.Replace]);
        (calls[0].Old!["title"], calls[0].New!["title"], calls[0].New!["id"]).ShouldBe(("stored", "replaced", stored));
        (calls[1].Old, calls[1].New!["title"], calls[1].New!["id"]).ShouldBe(((AlvoRecord?)null, "created", fresh));
    }

    [Fact]
    public async Task A_validate_hook_never_sees_a_replace_the_whole_row_guard_refuses()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.Validating.Add((Note, AlvoWriteOperation.Replace));
        await using var world = await WritePathWorld.StartAsync(WritePathFixture.Descriptor(), RequiredTitleSchema(), extensions: hooks);
        var stored = await world.SeedAsync("stored");
        var untitled = new Dictionary<string, object?> { ["owner_id"] = world.Caller.User.Value };

        await Should.ThrowAsync<ArgumentException>(() => world.Data.ReplaceAsync(Note, stored, untitled, world.Caller, cancellationToken: Ct));
        await Should.ThrowAsync<ArgumentException>(() => world.Data.ReplaceAsync(Note, Guid.NewGuid(), untitled, world.Caller, cancellationToken: Ct));

        hooks.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_replace_that_creates_is_judged_by_the_create_rule_before_validate()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.Validating.Add((Note, AlvoWriteOperation.Replace));
        await using var world = await StartAsync(hooks, WritePathFixture.Descriptor(create: "title != 'refused'"));
        var stored = await world.SeedAsync("stored");

        await Should.ThrowAsync<AlvoAuthorizationException>(
            () => world.Data.ReplaceAsync(Note, Guid.NewGuid(), WritePathFixture.Payload("refused"), world.Caller, cancellationToken: Ct));
        hooks.Calls.ShouldBeEmpty("the create rule refuses before any outbound call (Ruling 60)");

        await world.Data.ReplaceAsync(Note, stored, WritePathFixture.Payload("refused"), world.Caller, cancellationToken: Ct);
        hooks.Calls.ShouldHaveSingleItem("a replace of a stored row is judged by the update rule").Invocation.Old.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_replace_that_creates_with_a_precondition_answers_412_before_the_create_rule_whether_or_not_it_validates(bool validated)
    {
        var hooks = new RecordingWriteExtensions();
        if (validated)
        {
            hooks.Validating.Add((Note, AlvoWriteOperation.Replace));
        }

        await using var world = await StartAsync(hooks, WritePathFixture.Descriptor(create: "title != 'refused'"));

        await Should.ThrowAsync<AlvoPreconditionFailedException>(() => world.Data.ReplaceAsync(
            Note, Guid.NewGuid(), WritePathFixture.Payload("refused"), world.Caller, new AlvoPrecondition(DateTimeOffset.UnixEpoch), cancellationToken: Ct));
        hooks.Calls.ShouldBeEmpty("a version names a row that is not there: 412, and no outbound call (Ruling 61)");
    }

    [Fact]
    public async Task A_replayed_key_skips_validate()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.Validating.Add((Note, AlvoWriteOperation.Create));
        await using var world = await StartAsync(hooks);
        var key = new AlvoIdempotency("create-1", "fingerprint-1");

        await world.Data.CreateAsync(Note, WritePathFixture.CreatePayload("once"), world.Caller, key, Ct);
        await world.Data.CreateAsync(Note, WritePathFixture.CreatePayload("once"), world.Caller, key, Ct);

        hooks.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_batch_validates_every_row_before_its_transaction_and_a_refused_row_refuses_the_batch()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.Validating.Add((Note, AlvoWriteOperation.Create));
        await using var world = await StartAsync(hooks);
        world.Statements.Clear();

        var result = await world.Data.CreateManyAsync(Note, [WritePathFixture.CreatePayload("a"), WritePathFixture.CreatePayload("b")], world.Caller, cancellationToken: Ct);

        result.Succeeded.ShouldBeTrue();
        hooks.Calls.Select(call => (call.Stage, call.UnitOnFlow, call.Autocommit)).ShouldBe([("validate", false, (bool?)true), ("validate", false, (bool?)true)]);
    }

    [Theory]
    [InlineData(AlvoWriteOperation.Update)]
    [InlineData(AlvoWriteOperation.Delete)]
    public async Task Every_row_of_a_hooked_batch_is_held_while_its_hooks_run(AlvoWriteOperation operation)
    {
        var hooks = new RecordingWriteExtensions();
        hooks.InTransaction.Add((Note, operation));
        await using var world = await StartAsync(hooks);
        Guid[] ids = [await world.SeedAsync("a"), await world.SeedAsync("b")];
        var held = new ConcurrentQueue<bool>();
        hooks.OnBefore = _ => held.Enqueue(ids.All(id => HeldRows.Holds(Note, id)));

        var result = operation == AlvoWriteOperation.Update
            ? await world.Data.UpdateManyAsync(
                Note, [.. ids.Select(id => new AlvoRowPatch(id, new Dictionary<string, object?> { ["title"] = "x" }))], world.Caller, cancellationToken: Ct)
            : await world.Data.DeleteManyAsync(Note, ids, world.Caller, cancellationToken: Ct);

        result.Succeeded.ShouldBeTrue();
        held.ToArray().ShouldBe([true, true], "a hook writing a sibling row of its batch is a self-write (Ruling 43)");
        ids.ShouldAllBe(id => !HeldRows.Holds(Note, id));
    }

    private const string ReadOnlyTitle = "Field 'title' is read-only and cannot be written.";

    private const string HiddenRefusal = "A write hook set a field this caller may not write.";

    private static Descriptor.AlvoDescriptor HiddenReadOnlyTitle()
    {
        var descriptor = WritePathFixture.Descriptor(readOnlyField: "title");
        var note = descriptor.Entities[Note];
        var fields = new Dictionary<string, Descriptor.FieldDescriptor>(note.Fields, StringComparer.Ordinal)
        {
            ["title"] = note.Fields["title"] with { Hidden = Descriptor.BoolOrCel.FromBoolean(true) },
        };

        return descriptor with
        {
            Entities = new Dictionary<string, Descriptor.EntityDescriptor>(StringComparer.Ordinal) { [Note] = note with { Fields = fields } },
        };
    }

    private static Dictionary<string, object?> UntitledPayload()
    {
        var payload = WritePathFixture.CreatePayload("unused");
        payload.Remove("title");

        return payload;
    }

    private static Schema.SchemaModel RequiredTitleSchema()
    {
        var note = WritePathFixture.Note();

        return new([note with { Fields = [.. note.Fields.Select(field => field.Name == "title" ? field with { Nullable = false } : field)] }]);
    }

    private static async Task<long> NotesAsync(WritePathWorld world) => (long)(await world.ScalarAsync("SELECT COUNT(*) FROM \"note\""))!;

    private static async Task<long> OutboxAsync(WritePathWorld world) => (long)(await world.ScalarAsync("SELECT COUNT(*) FROM alvo_outbox"))!;

    private static async Task<WritePathWorld> StartAsync(RecordingWriteExtensions hooks, Descriptor.AlvoDescriptor? descriptor = null)
    {
        var world = await WritePathWorld.StartAsync(descriptor ?? WritePathFixture.Descriptor(), WritePathFixture.Schema(), extensions: hooks);
        hooks.Autocommit = () => world.InAutocommit;

        return world;
    }

    /// <summary>Records the Error lines the port writes, by their rendered text.</summary>
    private sealed class HookLogRecorder : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _errors = new();

        internal IReadOnlyList<string> Errors => [.. _errors];

        public ILogger CreateLogger(string categoryName) => new Recording(this);

        public void Dispose()
        {
        }

        private sealed class Recording(HookLogRecorder owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Error;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    owner._errors.Enqueue(formatter(state, exception));
                }
            }
        }
    }
}
