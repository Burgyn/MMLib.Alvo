using MMLib.Alvo.Data;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Testing.Data;

/// <summary>The write path's half of the exclusion suite: an overlapping row is refused as an exclusion at the range's start.</summary>
public abstract partial class AlvoDataExclusionTests
{
    private const int Contenders = 20;

    /// <summary>
    /// Acceptance (3): twenty creates of one room, pairwise overlapping and released together, store exactly one; every other
    /// is an <see cref="AlvoConstraintKind.Exclusion"/> at <c>starts_at</c>, and no other failure (no deadlock) occurs.
    /// </summary>
    [Fact]
    public async Task Concurrent_overlapping_bookings_admit_one_and_refuse_the_rest_with_the_exclusion_kind()
    {
        var data = await CreateAsync(Schema(), Descriptor());
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var creates = Enumerable.Range(0, Contenders).Select(async minute =>
        {
            await start.Task;
            return await Outcome(() => BookAsync(data, _acme, "r1", (_nine.AddMinutes(minute), _nine.AddMinutes(60 + minute))));
        }).ToList();
        start.SetResult();
        var outcomes = await Task.WhenAll(creates);

        outcomes.Count(outcome => outcome is null).ShouldBe(1);
        outcomes.OfType<Exception>().ShouldAllBe(failure => IsExclusionAtTheStart(failure));
    }

    /// <summary>A range ends where the next starts: <c>[9, 10)</c> and <c>[10, 11)</c> share no instant.</summary>
    [Fact]
    public async Task Back_to_back_bookings_do_not_overlap()
    {
        var data = await CreateAsync(Schema(), Descriptor());

        await BookAsync(data, _acme, "r1", Hours(9, 10));
        await BookAsync(data, _acme, "r1", Hours(10, 11));
    }

    /// <summary>Only rows with equal <c>fields</c> collide — another room, or another tenant's same room, does not.</summary>
    [Fact]
    public async Task Other_rooms_and_other_tenants_do_not_collide()
    {
        var data = await CreateAsync(Schema(), Descriptor());

        await BookAsync(data, _acme, "r1", Hours(9, 11));
        await BookAsync(data, _acme, "r2", Hours(9, 11));
        await BookAsync(data, _globex, "r1", Hours(9, 11));
    }

    /// <summary>A row outside the exclusion's <c>where</c> takes part in no overlap.</summary>
    [Fact]
    public async Task A_cancelled_booking_outside_the_where_does_not_block()
    {
        var data = await CreateAsync(Schema(where: NotCancelled), Descriptor());

        await BookAsync(data, _acme, "r1", Hours(9, 11), status: "cancelled");
        await BookAsync(data, _acme, "r1", Hours(10, 12));

        IsExclusionAtTheStart(await Outcome(() => BookAsync(data, _acme, "r1", Hours(11, 12)))).ShouldBeTrue();
    }

    /// <summary>An update is judged like a create: moving a booking onto another's range is refused.</summary>
    [Fact]
    public async Task A_patch_moving_a_booking_onto_another_is_refused()
    {
        var data = await CreateAsync(Schema(), Descriptor());
        await BookAsync(data, _acme, "r1", Hours(9, 10));
        var later = await BookAsync(data, _acme, "r1", Hours(10, 11));

        var refused = await Outcome(() => MoveAsync(data, later, _nine.AddMinutes(30)));

        IsExclusionAtTheStart(refused).ShouldBeTrue();
    }

    /// <summary>Review Focus 2: <c>[t, t)</c> is empty and overlaps nothing — PostgreSQL's <c>'[)'</c>, which SQLite agrees with.</summary>
    [Fact]
    public async Task An_empty_range_overlaps_nothing_on_either_engine()
    {
        var data = await CreateAsync(Schema(), Descriptor());

        await BookAsync(data, _acme, "r1", Hours(10, 10));
        await BookAsync(data, _acme, "r1", Hours(9, 11));
        await BookAsync(data, _acme, "r1", Hours(10, 10));
    }

    /// <summary>
    /// Review Focus 2: an inverted range is the end field's own validation (D4) — a <see cref="AlvoConstraintKind.Check"/> at
    /// <c>ends_at</c>, never an engine's range error.
    /// </summary>
    [Fact]
    public async Task An_inverted_range_is_refused_at_its_end_field()
    {
        var data = await CreateAsync(Schema(), Descriptor());

        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(() => BookAsync(data, _acme, "r1", Hours(11, 10)));

        refused.Kind.ShouldBe(AlvoConstraintKind.Check);
        refused.Fields.ShouldBe([EndsAt]);
    }

    /// <summary>Two patches racing two bookings onto one free slot admit one: the update path takes the same locks (D3).</summary>
    [Fact]
    public async Task Concurrent_patches_moving_two_bookings_onto_one_slot_admit_one()
    {
        var data = await CreateAsync(Schema(), Descriptor());
        var first = await BookAsync(data, _acme, "r1", Hours(13, 14));
        var second = await BookAsync(data, _acme, "r1", Hours(15, 16));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var moves = new[] { first, second }.Select(async booking =>
        {
            await start.Task;
            return await Outcome(() => MoveAsync(data, booking, _nine));
        }).ToList();
        start.SetResult();
        var outcomes = await Task.WhenAll(moves);

        outcomes.Count(outcome => outcome is null).ShouldBe(1);
        outcomes.OfType<Exception>().ShouldAllBe(failure => IsExclusionAtTheStart(failure));
    }

    private static Task<AlvoRecord> MoveAsync(IAlvoData data, AlvoRecord booking, DateTimeOffset from) =>
        data.UpdateAsync(
            Bookings,
            (Guid)booking["id"]!,
            new Dictionary<string, object?>(StringComparer.Ordinal) { [StartsAt] = from, [EndsAt] = from.AddHours(1) },
            _acme,
            cancellationToken: Ct);

    private static async Task<Exception?> Outcome(Func<Task> write)
    {
        try
        {
            await write();
            return null;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return failure;
        }
    }

    private static bool IsExclusionAtTheStart(Exception? failure) =>
        failure is AlvoConstraintViolationException { Kind: AlvoConstraintKind.Exclusion } violation
        && violation.Fields.SequenceEqual([StartsAt]);
}
