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
    public async Task A_write_inside_a_unit_runs_its_hooks_in_that_unit_and_none_when_it_suppresses_them()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Create));
        await using var world = await StartAsync(hooks);

        await world.Transactions.TransactAsync(world.Caller, (unit, ct) => unit.CreateAsync(Note, WritePathFixture.CreatePayload("in a unit"), ct), cancellationToken: Ct);

        hooks.Stages.ShouldBe(["before", "after"]);
        hooks.Calls.First().Invocation.Transaction!.Context.Provenance.Depth.ShouldBe(1);
    }

    private static async Task<WritePathWorld> StartAsync(RecordingWriteExtensions hooks, Descriptor.AlvoDescriptor? descriptor = null)
    {
        var world = await WritePathWorld.StartAsync(descriptor ?? WritePathFixture.Descriptor(), WritePathFixture.Schema(), extensions: hooks);
        hooks.Autocommit = () => world.InAutocommit;

        return world;
    }
}
