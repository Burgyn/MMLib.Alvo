using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Abstractions.Tests.Events;

/// <summary>
/// The one envelope every fact in this namespace starts from, with a fixed id and a fixed instant so a
/// snapshot of it is stable and a failure names the field that moved rather than the clock.
/// </summary>
internal static class SampleEvents
{
    internal static Guid FixedId { get; } = Guid.Parse("019fc77e-be7b-72e8-b7fd-ffd6f6306e3e");

    internal static Guid FixedRowId { get; } = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

    internal static DateTimeOffset FixedTime { get; } = new(2026, 8, 3, 9, 30, 0, TimeSpan.Zero);

    internal static AlvoEvent Sample() => new()
    {
        Id = FixedId,
        Source = AlvoEvent.DefaultSource,
        Type = "entity.vehicles.updated",
        Time = FixedTime,
        Subject = $"vehicles/{FixedRowId}",
        PartitionKey = $"vehicles:{FixedRowId}",
        AuthType = AlvoEventAuthType.ApiKey,
        AuthId = "key-42",
        CorrelationId = "4bf92f3577b34da6a3ce929d0e0e4736",
        CausationId = "00f067aa0ba902b7",
        Data = new AlvoEventData
        {
            Record = Record(("status", "approved"), ("make", "vw")),
            OldRecord = Record(("status", "draft"), ("make", "vw")),
            Changed = ["status"],
        },
    };

    internal static AlvoEvent SampleWith(AlvoRecord record) =>
        Sample() with { Data = new AlvoEventData { Record = record, Changed = [.. record.Values.Keys] } };

    internal static AlvoRecord Record(params (string Field, object? Value)[] values) =>
        new(values.ToDictionary(pair => pair.Field, pair => pair.Value, StringComparer.Ordinal));
}
