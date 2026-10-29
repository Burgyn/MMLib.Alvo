using System.Text.Json;

namespace MMLib.Alvo.Management;

/// <summary>
/// A delivery to reproduce outside the instance it ran on: the handler, the attempt, and the event's envelope with every
/// field value that is not a key replaced by <c>&lt;redacted:FIELD&gt;</c>.
/// </summary>
/// <remarks>
/// Kept: the envelope's attributes (id, type, time, tenant, caller, chain depth; a data event's subject and partition key),
/// the record's <c>id</c> and <c>tenant_id</c>, fields of type <c>ref</c> and <c>enum</c>, the changed-field names, and null
/// values. Everything else is redacted — an application event keeps only <c>id</c> in its payload and the namespace part of
/// its partition key, and <c>tracestate</c> is dropped. Fill in what a reproduction needs before delivering it: a typed handler
/// cannot read <c>&lt;redacted:FIELD&gt;</c> as a number, a date or a flag.
/// </remarks>
public sealed record DeliveryFixture
{
    /// <summary>Gets the handler the delivery is for.</summary>
    public required string Subscriber { get; init; }

    /// <summary>Gets the delivery it was taken from.</summary>
    public required Guid Delivery { get; init; }

    /// <summary>Gets the attempt the handler is told it is on, from 1.</summary>
    public required int Attempt { get; init; }

    /// <summary>Gets the redacted CloudEvents envelope.</summary>
    public required JsonElement Envelope { get; init; }

    /// <summary>Gets the JSON pointers, into <see cref="Envelope"/>, of every value that was redacted.</summary>
    public IReadOnlyList<string> Redacted { get; init; } = [];
}
