using MMLib.Alvo.Events;

namespace MMLib.Alvo.Abstractions.Tests.Events;

/// <summary>
/// The ordering key's own facts. Every number quoted here is spike Q1's
/// (<c>docs/superpowers/specs/evidence/2026-08-03-f3-pr5a-events/spike.txt</c>), measured on the same BCL:
/// <c>Guid.CreateVersion7()</c> inverted 49 839 of 100 000 adjacent pairs, and the monotonic wrapper
/// inverted none.
/// </summary>
/// <remarks>
/// <para>
/// The generator's state is <b>process-wide</b> — that is what makes the order total — so a fact that needs
/// a known starting point reads the last minted millisecond back out of a freshly minted id
/// (<see cref="LastMintedInstant"/>) instead of asking the clock. Without that, whichever test ran first
/// decided which branch the others took: a run that had already pushed the last millisecond a second into
/// the future made every later mint take the tail-increment path, and a fact meant to exercise the
/// repeated-millisecond path passed without ever reaching it. That was measured, not imagined — it is why
/// these facts do not read <c>DateTimeOffset.UtcNow</c>.
/// </para>
/// <para>
/// Parallelism is safe for the same reason the order is total: any subsequence of what one process mints is
/// increasing, so another class minting concurrently cannot make these ids go backwards.
/// </para>
/// </remarks>
public class AlvoEventIdTests
{
    private const int Samples = 100_000;

    /// <summary>
    /// The production shape: ids minted from the system clock, as the write path mints them.
    /// </summary>
    [Fact]
    public void A_hundred_thousand_ids_minted_from_the_system_clock_have_no_inversion()
    {
        var ids = Enumerable.Range(0, Samples).Select(_ => AlvoEventId.Create().ToString()).ToList();

        Inversions(ids).ShouldBe(
            0, "ORDER BY id is the outbox queue order, so an inversion is a delivery out of order");
    }

    /// <summary>
    /// The same count, forced entirely through the repeated-millisecond path — the only path the wrapper
    /// changes, and the one <c>Guid.CreateVersion7()</c> gets wrong 49.9 % of the time (spike Q1).
    /// </summary>
    /// <remarks>
    /// The second assertion is the non-vacuity control: all 100 000 ids must carry <b>one</b> embedded
    /// millisecond, which is only true if the repeat path ran 99 999 times. Without it, a run that happened
    /// to take the ordinary path would pass and prove nothing.
    /// </remarks>
    [Fact]
    public void A_hundred_thousand_ids_minted_inside_one_millisecond_have_no_inversion()
    {
        var oneMillisecond = LastMintedInstant();

        var ids = Enumerable.Range(0, Samples).Select(_ => AlvoEventId.Create(oneMillisecond)).ToList();

        Inversions([.. ids.Select(id => id.ToString())]).ShouldBe(
            0, $"Guid.CreateVersion7() alone measured 49 839 inversions over {Samples} (spike Q1)");
        ids.Select(MillisecondsOf).Distinct().Count().ShouldBe(
            1, "every id must share one millisecond, or this run never reached the repeat path");
    }

    /// <summary>
    /// The smallest form of the same fact. <c>Guid.CreateVersion7(fixed)</c> fails it about half the time
    /// (spike Q1: 515 inversions of 999 pairs).
    /// </summary>
    [Fact]
    public void Two_ids_minted_in_one_millisecond_sort_in_the_order_they_were_minted()
    {
        var oneMillisecond = LastMintedInstant();

        var first = AlvoEventId.Create(oneMillisecond).ToString();
        var second = AlvoEventId.Create(oneMillisecond).ToString();

        string.CompareOrdinal(first, second).ShouldBeLessThan(0);
    }

    /// <summary>
    /// Spike Q1 measured that a backwards clock step reorders the queue by the size of the step. It cannot
    /// do that within one process, because the last emitted millisecond never moves backwards.
    /// </summary>
    [Fact]
    public void A_backwards_clock_step_cannot_reorder_ids_within_one_process()
    {
        var now = LastMintedInstant();

        var before = AlvoEventId.Create(now).ToString();
        var afterTheClockWentBack = AlvoEventId.Create(now - TimeSpan.FromSeconds(5)).ToString();

        string.CompareOrdinal(before, afterTheClockWentBack).ShouldBeLessThan(0);
    }

    [Fact]
    public void An_id_is_still_a_uuid_version_7_with_the_rfc_variant()
    {
        var bytes = AlvoEventId.Create().ToByteArray(bigEndian: true);

        (bytes[6] & 0xF0).ShouldBe(0x70, "the version nibble must survive the tail increment");
        (bytes[8] & 0xC0).ShouldBe(0x80, "the variant bits must survive the tail increment");
    }

    /// <summary>
    /// Spike Q1: <c>Guid</c>'s <b>default</b> byte order is not time-sortable (5 050 inversions of 9 999),
    /// which is why the outbox stores the id as <c>TEXT</c> and never as a <c>BLOB</c> written from
    /// <c>ToByteArray()</c>.
    /// </summary>
    /// <remarks>
    /// Minted across advancing milliseconds on purpose. Ids that share a millisecond differ only in their
    /// tail, which the default byte order happens to leave in place — so a run inside one millisecond would
    /// sort under both orders and prove nothing about either.
    /// </remarks>
    [Fact]
    public void The_big_endian_bytes_sort_in_mint_order_and_the_default_ones_do_not()
    {
        var start = LastMintedInstant();
        var ids = Enumerable.Range(1, 1_000)
            .Select(millisecondsAhead => AlvoEventId.Create(start.AddMilliseconds(millisecondsAhead)))
            .ToList();

        Inversions([.. ids.Select(id => Convert.ToHexString(id.ToByteArray(bigEndian: true)))]).ShouldBe(0);
        Inversions([.. ids.Select(id => Convert.ToHexString(id.ToByteArray()))]).ShouldBeGreaterThan(
            0, "if the default order sorted too, the outbox could store the id as a BLOB — it cannot");
    }

    [Fact]
    public void An_id_minted_from_an_instant_before_the_unix_epoch_is_refused()
        => Should.Throw<ArgumentOutOfRangeException>(
            () => AlvoEventId.Create(new DateTimeOffset(1969, 1, 1, 0, 0, 0, TimeSpan.Zero)));

    /// <summary>
    /// The millisecond this process last minted in, read back out of a fresh id — the one starting point
    /// that is not a guess about what ran before.
    /// </summary>
    private static DateTimeOffset LastMintedInstant() =>
        DateTimeOffset.FromUnixTimeMilliseconds(MillisecondsOf(AlvoEventId.Create()));

    private static int Inversions(List<string> values)
    {
        var inversions = 0;
        for (var index = 1; index < values.Count; index++)
        {
            if (string.CompareOrdinal(values[index - 1], values[index]) >= 0)
            {
                inversions++;
            }
        }

        return inversions;
    }

    private static long MillisecondsOf(Guid id)
    {
        var bytes = id.ToByteArray(bigEndian: true);
        long milliseconds = 0;
        for (var index = 0; index < 6; index++)
        {
            milliseconds = (milliseconds << 8) | bytes[index];
        }

        return milliseconds;
    }
}
