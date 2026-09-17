namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// What each write tells <c>WritePayloadGuard</c> and <c>AlvoPrecondition</c> about itself, judged by the
/// answers a caller gets rather than by the call.
/// </summary>
/// <remarks>
/// <para>
/// The two flags look like plumbing and are not. <c>isUpdate</c> decides whether <c>tenant_id</c> is a column
/// this caller may write, so an update told it is a create accepts a payload that moves a row to another
/// tenant — the one managed column whose rule differs between the two verbs. And a create-or-replace tells
/// the guard <c>isUpdate: true</c> on <b>both</b> branches on purpose, so the refusal a caller reads cannot
/// report whether the row they named already exists.
/// </para>
/// <para>
/// The precondition check belongs to the same order: it is refused for an entity that keeps no version at
/// all, before any row is read, because the alternative — letting it through to the comparison — answers
/// "your version is stale" for a row whose version never existed.
/// </para>
/// </remarks>
public class EfAlvoDataWritePayloadGuardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// An update carrying <c>tenant_id</c> is refused. Told <c>isUpdate: false</c>, the guard treats the
    /// column as caller-writable — which it is, but only on a create — and the update moves the row into
    /// whichever tenant the payload named.
    /// </summary>
    [Fact]
    public async Task An_update_may_not_carry_the_tenant_column()
    {
        await using var world = await StartAsync();
        var id = await SeededRowAsync(world);
        var payload = WritePathFixture.Payload("moved");
        payload["tenant_id"] = Guid.NewGuid();

        var refusal = await Should.ThrowAsync<AlvoAuthorizationException>(
            () => world.Data.UpdateAsync(WritePathFixture.Entity, id, payload, world.Caller, cancellationToken: Ct));

        refusal.Message.ShouldContain("tenant_id");
    }

    /// <summary>
    /// A create-or-replace naming <c>id</c> is refused as a rewrite of a key, not as a supplied one — the
    /// wording of the <em>update</em> branch, on a call whose branch has not been chosen yet.
    /// </summary>
    /// <remarks>
    /// The two branches' guards are asked the same question, so only the first one's answer is ever read. A
    /// create-shaped answer here is the existence oracle the unconditional <c>isUpdate: true</c> exists to
    /// prevent: it differs from the update-shaped one by exactly what the caller is trying to learn.
    /// </remarks>
    [Fact]
    public async Task A_replace_refuses_the_row_key_the_way_an_update_does()
    {
        await using var world = await StartAsync();
        var payload = WritePathFixture.Payload("keyed");
        payload["id"] = Guid.NewGuid();

        var refusal = await Should.ThrowAsync<AlvoAuthorizationException>(() => world.Data.ReplaceAsync(
            WritePathFixture.Entity, Guid.NewGuid(), payload, world.Caller, cancellationToken: Ct));

        refusal.Message.ShouldContain("'id'");
        refusal.Message.ShouldContain("can never be rewritten");
    }

    /// <summary>
    /// A precondition against an entity that keeps no version is refused for <em>that</em> reason, before any
    /// row is read. Unchecked, it reaches the comparison, where a null stored version fails every
    /// precondition — the same status, with a sentence that tells the caller to fix their request instead of
    /// their entity.
    /// </summary>
    [Fact]
    public async Task A_precondition_against_an_unversioned_entity_names_the_missing_column()
    {
        await using var world = await WritePathWorld.StartAsync(
            WritePathFixture.Descriptor(), WritePathFixture.Schema(audit: false));

        var refusal = await Should.ThrowAsync<AlvoPreconditionFailedException>(() => world.Data.ReplaceAsync(
            WritePathFixture.Entity, Guid.NewGuid(), WritePathFixture.Payload("versioned"), world.Caller,
            new AlvoPrecondition(DateTimeOffset.UnixEpoch), cancellationToken: Ct));

        refusal.Message.ShouldContain("updated_at");
    }

    private static Task<WritePathWorld> StartAsync() =>
        WritePathWorld.StartAsync(WritePathFixture.Descriptor(), WritePathFixture.Schema());

    private static async Task<Guid> SeededRowAsync(WritePathWorld world)
    {
        var seeded = await world.Data.CreateAsync(
            WritePathFixture.Entity, WritePathFixture.CreatePayload("seed"), world.Caller, cancellationToken: Ct);

        return (Guid)seeded["id"]!;
    }
}
