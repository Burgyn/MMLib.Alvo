using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Rules;

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

    private const string ReadOnlyTitle = "Field 'title' is read-only and cannot be written.";

    private static Dictionary<string, object?> UntitledPayload()
    {
        var payload = WritePathFixture.CreatePayload("unused");
        payload.Remove("title");

        return payload;
    }

    private static async Task<long> NotesAsync(WritePathWorld world) => (long)(await world.ScalarAsync("SELECT COUNT(*) FROM \"note\""))!;

    private static async Task<long> OutboxAsync(WritePathWorld world) => (long)(await world.ScalarAsync("SELECT COUNT(*) FROM alvo_outbox"))!;

    private static async Task<WritePathWorld> StartAsync(RecordingWriteExtensions hooks, Descriptor.AlvoDescriptor? descriptor = null)
    {
        var world = await WritePathWorld.StartAsync(descriptor ?? WritePathFixture.Descriptor(), WritePathFixture.Schema(), extensions: hooks);
        hooks.Autocommit = () => world.InAutocommit;

        return world;
    }
}
