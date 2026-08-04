namespace MMLib.Alvo.Events;

/// <summary>
/// Mints an <see cref="AlvoEvent.Id"/>: a UUIDv7 that is <b>monotonic within this process</b>, so that
/// ordering events by their id orders them by the order they were minted in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists, measured.</b> <c>Guid.CreateVersion7()</c> is time-ordered only above its 48-bit
/// millisecond: everything below it is fresh random data, with no counter. Over 100 000 successive mints,
/// <b>49 839</b> adjacent pairs sorted backwards — 49.9 % of the pairs that shared a millisecond
/// (<c>docs/superpowers/specs/evidence/2026-08-03-f3-pr5a-events/spike.txt</c>, Q1). The outbox claims in
/// <c>ORDER BY id</c>, so each of those is a delivery out of order. Reusing the last emitted millisecond and
/// incrementing the random tail instead measured <b>0 inversions over 100 000</b>, and changes no DDL.
/// </para>
/// <para>
/// <b>What it does not fix.</b> The order is total only within one process. Two hosts minting inside one
/// millisecond still interleave, so Alvo's guarantee is per-entity-key ordering with <em>one dispatcher</em>
/// and <em>no two events for one key inside the same millisecond</em>; the cross-process half is tracked in
/// issue #150. What it does close, for free, is a backwards clock step: the last emitted millisecond never
/// moves backwards, so a clock that jumps back cannot reorder ids this process already handed out.
/// </para>
/// <para>
/// <b>Why it lives in the ports.</b> The id is minted on the write path, which is a driver package that
/// sees only this assembly's public surface — an <see langword="internal"/> generator, or one in the core,
/// would be unreachable from there. And the ordering contract belongs to the envelope rather than to one
/// driver: a driver-local generator would have to be re-derived by the next store implementation, and
/// forgetting is invisible, because a plain <c>Guid.CreateVersion7()</c> also produces a valid v7 id.
/// </para>
/// </remarks>
public static class AlvoEventId
{
    private const int GuidByteCount = 16;
    private const int TimestampByteCount = 6;
    private const int VersionByteIndex = 6;
    private const int RandomAByteIndex = 7;
    private const int VariantByteIndex = 8;
    private const int RandomBByteIndex = 9;
    private const byte Version7 = 0x70;
    private const byte RfcVariant = 0x80;
    private const byte LowNibbleMask = 0x0F;
    private const byte LowSixBitsMask = 0x3F;
    private const int TailBitCount = 74;

    private static readonly UInt128 _tailCeiling = (UInt128.One << TailBitCount) - UInt128.One;
    private static readonly Lock _gate = new();

    private static long _lastMilliseconds = long.MinValue;
    private static UInt128 _lastTail;

    /// <summary>Mints an id for an event happening now.</summary>
    public static Guid Create() => Create(DateTimeOffset.UtcNow);

    /// <summary>Mints an id for an event happening at <paramref name="timestamp"/>.</summary>
    /// <param name="timestamp">The instant the change committed, normally the write's own audit instant.</param>
    /// <remarks>
    /// Passing the write's instant is what makes the envelope's <c>time</c>, the outbox row's
    /// <c>created_at</c> and the id's embedded millisecond one instant rather than three clock reads. The
    /// returned id's millisecond is the <b>later</b> of <paramref name="timestamp"/> and the last one this
    /// process minted: a total order outranks an exact stamp, because the order is what the dispatcher
    /// claims by.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="timestamp"/> is before the Unix epoch.
    /// </exception>
    public static Guid Create(DateTimeOffset timestamp)
    {
        Span<byte> candidate = stackalloc byte[GuidByteCount];
        Guid.CreateVersion7(timestamp).TryWriteBytes(candidate, bigEndian: true, out _);

        lock (_gate)
        {
            return NextInOrder(candidate);
        }
    }

    private static Guid NextInOrder(ReadOnlySpan<byte> candidate)
    {
        var milliseconds = MillisecondsOf(candidate);

        if (milliseconds > _lastMilliseconds)
        {
            return Remember(milliseconds, TailOf(candidate));
        }

        return _lastTail < _tailCeiling
            ? Remember(_lastMilliseconds, _lastTail + UInt128.One)
            : Remember(_lastMilliseconds + 1, TailOf(candidate));
    }

    private static Guid Remember(long milliseconds, UInt128 tail)
    {
        _lastMilliseconds = milliseconds;
        _lastTail = tail;

        return Compose(milliseconds, tail);
    }

    private static Guid Compose(long milliseconds, UInt128 tail)
    {
        Span<byte> bytes = stackalloc byte[GuidByteCount];
        WriteTimestamp(bytes, milliseconds);
        WriteTail(bytes, tail);

        return new Guid(bytes, bigEndian: true);
    }

    private static void WriteTimestamp(Span<byte> bytes, long milliseconds)
    {
        for (var index = TimestampByteCount - 1; index >= 0; index--)
        {
            bytes[index] = (byte)milliseconds;
            milliseconds >>= 8;
        }
    }

    private static void WriteTail(Span<byte> bytes, UInt128 tail)
    {
        for (var index = GuidByteCount - 1; index >= RandomBByteIndex; index--)
        {
            bytes[index] = (byte)tail;
            tail >>= 8;
        }

        bytes[VariantByteIndex] = (byte)(RfcVariant | ((byte)tail & LowSixBitsMask));
        tail >>= 6;
        bytes[RandomAByteIndex] = (byte)tail;
        tail >>= 8;
        bytes[VersionByteIndex] = (byte)(Version7 | ((byte)tail & LowNibbleMask));
    }

    private static long MillisecondsOf(ReadOnlySpan<byte> bytes)
    {
        long milliseconds = 0;
        for (var index = 0; index < TimestampByteCount; index++)
        {
            milliseconds = (milliseconds << 8) | bytes[index];
        }

        return milliseconds;
    }

    private static UInt128 TailOf(ReadOnlySpan<byte> bytes)
    {
        var tail = (UInt128)(bytes[VersionByteIndex] & LowNibbleMask);
        tail = (tail << 8) | bytes[RandomAByteIndex];
        tail = (tail << 6) | (UInt128)(bytes[VariantByteIndex] & LowSixBitsMask);

        for (var index = RandomBByteIndex; index < GuidByteCount; index++)
        {
            tail = (tail << 8) | bytes[index];
        }

        return tail;
    }
}
