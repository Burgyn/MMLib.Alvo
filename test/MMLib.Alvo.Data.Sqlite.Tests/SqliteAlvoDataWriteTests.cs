using MMLib.Alvo.Data.EntityFrameworkCore;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The most dangerous half of the port. A tracked <c>SaveChanges</c> emits <c>UPDATE … WHERE id = @p</c>
/// with no policy predicate at all, so every write here goes through <c>ExecuteUpdate</c>/
/// <c>ExecuteDelete</c> composed over the <c>FromSql</c> root that carries <c>USING</c> — which makes
/// <c>rows affected == 0</c> the not-found signal, indistinguishable from a row that never existed.
/// </summary>
public sealed class SqliteAlvoDataWriteTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    [Fact]
    public async Task An_update_of_an_unrelated_field_succeeds_because_the_post_image_still_satisfies_the_rule()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        var updated = await world.UpdateAsync(
            "notes", world.AliceRowId, new Dictionary<string, object?> { ["title"] = "renamed" }, world.Alice);

        updated["title"].ShouldBe("renamed");
        updated["owner_id"].ShouldBe(world.Alice.User.Value);
    }

    /// <summary>
    /// The <c>USING</c> predicate lives inside the update statement, so another caller's row is not
    /// updated and the outcome is indistinguishable from a row that never existed.
    /// </summary>
    [Fact]
    public async Task An_update_of_another_callers_row_reports_not_found_and_changes_nothing()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        await Should.ThrowAsync<AlvoRecordNotFoundException>(() => world.UpdateAsync(
            "notes", world.BobRowId, new Dictionary<string, object?> { ["title"] = "hacked" }, world.Alice));

        var bobsRow = await world.GetAsync("notes", world.BobRowId, world.Bob);
        bobsRow!["title"].ShouldNotBe("hacked");
    }

    [Fact]
    public async Task An_absent_row_and_an_invisible_row_report_the_same_failure()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        var invisible = await Should.ThrowAsync<AlvoRecordNotFoundException>(
            () => world.DeleteAsync("notes", world.BobRowId, world.Alice));
        var absent = await Should.ThrowAsync<AlvoRecordNotFoundException>(
            () => world.DeleteAsync("notes", Guid.NewGuid(), world.Alice));

        invisible.Message.ShouldBe(absent.Message);
    }

    /// <summary>
    /// The same indistinguishability on the update path, where the pre-image read is the check that decides
    /// it — an invisible row and an absent one must reach the same refusal by the same route.
    /// </summary>
    [Fact]
    public async Task An_absent_row_and_an_invisible_row_report_the_same_update_failure()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);
        var patch = new Dictionary<string, object?> { ["title"] = "x" };

        var invisible = await Should.ThrowAsync<AlvoRecordNotFoundException>(
            () => world.UpdateAsync("notes", world.BobRowId, patch, world.Alice));
        var absent = await Should.ThrowAsync<AlvoRecordNotFoundException>(
            () => world.UpdateAsync("notes", Guid.NewGuid(), patch, world.Alice));

        invisible.Message.ShouldBe(absent.Message);
    }

    [Fact]
    public async Task An_update_that_would_move_the_row_out_of_the_callers_scope_is_denied_and_the_row_is_unchanged()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        await Should.ThrowAsync<AlvoAuthorizationException>(() => world.UpdateAsync(
            "notes",
            world.AliceRowId,
            new Dictionary<string, object?> { ["owner_id"] = world.Bob.User.Value },
            world.Alice));

        var stillHers = await world.GetAsync("notes", world.AliceRowId, world.Alice);
        stillHers!["owner_id"].ShouldBe(world.Alice.User.Value);
    }

    /// <summary>
    /// The distinguishing case between post-image and payload-only evaluation, at the statement level: the
    /// pre-image is read inside the transaction and merged under the patch, so a payload naming only
    /// <c>title</c> is judged against the stored <c>owner_id</c> rather than against an absent one.
    /// </summary>
    [Fact]
    public async Task The_update_reads_its_pre_image_before_it_writes()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        await world.UpdateAsync(
            "notes", world.AliceRowId, new Dictionary<string, object?> { ["title"] = "renamed" }, world.Alice);

        world.Statements[0].ShouldStartWith("SELECT");
        world.Statements[0].ShouldContain("\"owner_id\" = @alvo_u0");
        world.Statements.ShouldContain(statement => statement.StartsWith("UPDATE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_multi_field_patch_of_several_clr_types_lands_in_one_statement()
    {
        var world = await AlvoDataWorlds.VehicleAsync(_fixture);
        var patch = new Dictionary<string, object?>
        {
            ["status"] = "closed",
            ["mileage"] = 4242L,
            ["price"] = 1234.56m,
            ["is_public"] = false,
            ["created_at"] = DateTimeOffset.UnixEpoch,
        };

        var updated = await world.UpdateAsync("vehicle", world.RowId, patch, world.Alice);

        updated["mileage"].ShouldBe(4242L);
        updated["price"].ShouldBe(1234.56m);
        updated["is_public"].ShouldBe(false);
        updated["status"].ShouldBe("closed");
        world.Statements.Count(statement => statement.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
            .ShouldBe(1);
    }

    /// <summary>
    /// The policy predicate is inside the <c>UPDATE</c> itself, as a subquery over the same <c>FromSql</c>
    /// root the read path uses — not applied by a preceding <c>SELECT</c> whose verdict a concurrent writer
    /// could invalidate.
    /// </summary>
    [Fact]
    public async Task The_update_statement_itself_carries_the_policy_predicate()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        await world.UpdateAsync(
            "notes", world.AliceRowId, new Dictionary<string, object?> { ["title"] = "renamed" }, world.Alice);

        var update = world.Statements.First(statement => statement.StartsWith("UPDATE", StringComparison.Ordinal));
        update.ShouldContain("\"owner_id\" = @alvo_u0");
    }

    [Fact]
    public async Task A_patch_setting_a_nullable_field_to_null_really_clears_it()
    {
        var world = await AlvoDataWorlds.VehicleAsync(_fixture);

        var updated = await world.UpdateAsync(
            "vehicle", world.RowId, new Dictionary<string, object?> { ["status"] = null }, world.Alice);

        updated["status"].ShouldBeNull();
    }

    /// <summary>
    /// A <c>WITH CHECK</c> verdict is reached over the complete stored row, so the pre-image read is
    /// unmasked: a rule referencing a <c>hidden</c> field must see its real value, not the projected
    /// <c>NULL</c> a masked read would return — otherwise masking a field silently changes what the rule
    /// decides, and this update would be denied.
    /// </summary>
    [Fact]
    public async Task The_pre_image_a_check_is_evaluated_over_carries_a_hidden_fields_real_value()
    {
        var world = await AlvoDataWorlds.GuardedSecretAsync(_fixture);

        var updated = await world.UpdateAsync(
            "vaults", world.RowId, new Dictionary<string, object?> { ["title"] = "renamed" }, world.Member);

        updated["title"].ShouldBe("renamed");
        updated.Values.ContainsKey("secret").ShouldBeFalse();
    }

    /// <summary>
    /// The pre-image the verdict is based on is read <b>locked</b>, in the update mode, so a concurrent writer
    /// cannot change the row between the check and the write. SQLite has no locking clause and serializes
    /// write transactions instead, so its own answer is empty — the request is what this pins, because on
    /// PostgreSQL that request is the whole mechanism.
    /// </summary>
    [Fact]
    public async Task The_pre_image_read_asks_for_the_updates_own_row_lock_mode()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        await world.UpdateAsync(
            "notes", world.AliceRowId, new Dictionary<string, object?> { ["title"] = "renamed" }, world.Alice);

        world.RequestedLocks.ShouldBe([PreImageMutation.Update]);
    }

    /// <summary>
    /// A read takes no lock at all: a list or a get is not a pre-image for anything, and locking rows a
    /// caller is only reading would serialize unrelated writers against them.
    /// </summary>
    [Fact]
    public async Task A_read_asks_for_no_row_lock()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        await world.QueryAsync(new AlvoQuery { Entity = "notes" }, world.Alice);
        await world.GetAsync("notes", world.AliceRowId, world.Alice);

        world.RequestedLocks.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_delete_of_the_callers_own_row_removes_it()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        await world.DeleteAsync("notes", world.AliceRowId, world.Alice);

        (await world.GetAsync("notes", world.AliceRowId, world.Alice)).ShouldBeNull();
    }

    [Fact]
    public async Task A_delete_of_another_callers_row_leaves_it_in_place()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        await Should.ThrowAsync<AlvoRecordNotFoundException>(
            () => world.DeleteAsync("notes", world.BobRowId, world.Alice));

        (await world.GetAsync("notes", world.BobRowId, world.Bob)).ShouldNotBeNull();
    }

    [Fact]
    public async Task The_delete_statement_itself_carries_the_policy_predicate()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);

        await world.DeleteAsync("notes", world.AliceRowId, world.Alice);

        world.Statements.Count.ShouldBe(1);
        world.LastStatement.ShouldStartWith("DELETE FROM \"notes\"");
        world.LastStatement.ShouldContain("\"owner_id\" = @alvo_u0");
    }

    [Fact]
    public async Task A_write_with_no_context_throws_rather_than_defaulting_to_anyone()
    {
        var world = await AlvoDataWorlds.NotesAsync(_fixture);
        var patch = new Dictionary<string, object?> { ["title"] = "x" };

        await Should.ThrowAsync<ArgumentNullException>(() => world.Data.UpdateAsync(
            "notes", world.AliceRowId, patch, null!, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentNullException>(() => world.Data.DeleteAsync(
            "notes", world.AliceRowId, null!, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentNullException>(() => world.Data.CreateAsync(
            "notes", patch, null!, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A read-only field is refused before the row is looked up, so the refusal cannot be used to probe for a
    /// row's existence — and the stored value is unchanged.
    /// </summary>
    [Fact]
    public async Task A_write_to_a_read_only_field_is_refused_before_any_row_is_read()
    {
        var world = await AlvoDataWorlds.AccountsAsync(_fixture);

        var refused = await Should.ThrowAsync<AlvoAuthorizationException>(() => world.UpdateAsync(
            "accounts", world.RowId, new Dictionary<string, object?> { ["status"] = "closed" }, world.Member));

        refused.Message.ShouldContain("status");
        world.Statements.ShouldBeEmpty();
        var unchanged = await world.GetAsync("accounts", world.RowId, world.Member);
        unchanged!["status"].ShouldBe("active");
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
