using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Schema;

using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Data;

[AlvoEntity("orders")]
public sealed class Order
{
    public Guid Id { get; init; }

    public Guid? OwnerId { get; init; }

    public OrderStatus Status { get; init; }

    public OrderStatus? Previous { get; init; }

    public decimal Total { get; init; }

    public long Quantity { get; init; }

    public int? Rank { get; init; }

    public bool IsPaid { get; init; }

    public required string Note { get; init; }

    public string? Code { get; init; }

    public DateTimeOffset? DueAt { get; init; }

    public DateOnly? ShipOn { get; init; }

    [AlvoField("ref_no")]
    public string? Reference { get; init; }

    public string Summary => $"{Note} ({Status})";
}

public enum OrderStatus
{
    Draft,
    InReview,
    [AlvoEnumValue("ok")]
    Approved,
}

[AlvoEntity("orders")]
public sealed class OrderView(AlvoRecord record) : IAlvoEntity
{
    public AlvoRecord Record { get; } = record;

    [AlvoField("note")]
    public string? Note => (string?)Record["note"];
}

public sealed record OrderLine(Guid Id, string Note, decimal Total);

[AlvoEntity("tickets")]
public sealed class TypedTicket
{
    public Guid Id { get; init; }

    public required string Title { get; init; }

    public string? Code { get; init; }

    public TicketStatus Status { get; init; }
}

public enum TicketStatus
{
    Open,
    Resolved,
}

[AlvoEntity("tickets")]
public sealed class TicketWithVat
{
    public string? VatId { get; init; }
}

[AlvoEntity("tickets")]
public sealed class TicketWithNumericCode
{
    public long? Code { get; init; }
}

[AlvoEntity("tickets")]
public sealed class TicketWithSecret
{
    public string? Secret { get; init; }
}

[AlvoEntity("tickets")]
public sealed class TicketWithStrictCode
{
    public required string Code { get; init; }
}

[AlvoEntity("tickets")]
public sealed class TicketWithArchive
{
    public ArchivedTicketStatus Status { get; init; }
}

public enum ArchivedTicketStatus
{
    Open,
    Resolved,
    Archived,
}

[AlvoEntity("tickets")]
public sealed class TicketWithScore
{
    public double Score { get; init; }
}

[AlvoEntity("*")]
public sealed class EveryEntity
{
    public string? Title { get; init; }
}

public sealed class Unmarked
{
    public string? Title { get; init; }
}

public sealed class Uncallable(int seed)
{
    public string? Title { get; init; } = seed.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class BadFieldName
{
    [AlvoField("not a name")]
    public string? Title { get; init; }
}

public sealed class TwoNames
{
    public string? Title { get; init; }

    [AlvoField("title")]
    public string? Heading { get; init; }
}

[Flags]
public enum Flagged
{
    None = 0,
    One = 1,
}

public sealed class WithFlags
{
    public Flagged Flags { get; init; }
}

[AlvoEvent("desk.escalated")]
public sealed record TicketEscalated(string Reason, long Level);

public sealed class TypedTicketResolved : IAlvoEventHandler<AlvoUpdated<TypedTicket>>
{
    public Task HandleAsync(AlvoUpdated<TypedTicket> message, AlvoEventContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class EscalationHandler : IAlvoEventHandler<TicketEscalated>
{
    public Task HandleAsync(TicketEscalated message, AlvoEventContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

public abstract class NotedBase
{
    [AlvoField("note")]
    public virtual string? Text => null;
}

public sealed class NotedOverride : NotedBase
{
    public override string? Text => "noted";
}

public enum SharedValue
{
    [AlvoEnumValue("open")]
    Opened,
    Open,
}

public sealed class WithSharedValue
{
    public SharedValue Status { get; init; }
}

public sealed class WrapperWithoutRecord : IAlvoEntity
{
    public AlvoRecord Record { get; } = new(new Dictionary<string, object?>(StringComparer.Ordinal));
}

public sealed class LooseNote
{
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public string Note { get; init; } = string.Empty;
}

public sealed class FixedNote(string note, string? code)
{
    [AlvoField("note")]
    public string Note { get; } = note;

    [AlvoField("code")]
    public string? Code { get; } = code;
}

public sealed class PrivatelySetNote
{
    [AlvoField("note")]
    public string Note { get; private set; } = string.Empty;
}

public sealed class UncoveredNote
{
    public UncoveredNote(string note) => Note = note;

    [AlvoField("note")]
    public string Note { get; }

    [AlvoField("code")]
    public string? Code { get; }
}

public sealed class MistypedParameter(string level)
{
    public long Level { get; init; } = level.Length;
}

public abstract class AbstractNote
{
    public string? Note { get; init; }
}

public sealed class RefusingNote
{
    private readonly string? _note;

    public string? Note
    {
        get => _note;
        init
        {
            _note = value;
            throw new ArgumentException("The host refuses this note.");
        }
    }
}

public sealed class RefusingView : IAlvoEntity
{
    public RefusingView(AlvoRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        throw new ArgumentException("The host refuses this record.");
    }

    public AlvoRecord Record => AlvoRecord.Empty;
}

/// <summary>Host types, the <c>orders</c> entity they map, and records shaped as event images carry them.</summary>
public static class TypedFixtures
{
    /// <summary>Removes a field from <see cref="OrderRecord"/> when given as its value.</summary>
    public static readonly object Absent = new();

    public static EntitySchema OrdersSchema { get; } = new()
    {
        Name = "orders",
        Tenancy = TenancyMode.Global,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid },
            new FieldSchema { Name = "owner_id", Type = FieldType.Uuid, Nullable = true },
            new FieldSchema { Name = "status", Type = FieldType.Enum, EnumValues = ["draft", "in_review", "ok"] },
            new FieldSchema { Name = "previous", Type = FieldType.Enum, EnumValues = ["draft", "in_review", "ok"], Nullable = true },
            new FieldSchema { Name = "total", Type = FieldType.Decimal, Precision = 18, Scale = 2 },
            new FieldSchema { Name = "quantity", Type = FieldType.Integer },
            new FieldSchema { Name = "rank", Type = FieldType.Integer, Nullable = true },
            new FieldSchema { Name = "is_paid", Type = FieldType.Boolean },
            new FieldSchema { Name = "note", Type = FieldType.String },
            new FieldSchema { Name = "code", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "due_at", Type = FieldType.DateTime, Nullable = true },
            new FieldSchema { Name = "ship_on", Type = FieldType.Date, Nullable = true },
            new FieldSchema { Name = "ref_no", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "legacy_code", Type = FieldType.String, Nullable = true },
        ],
    };

    /// <summary>An <c>orders</c> record shaped as an event image carries it: ids, days and instants as text.</summary>
    /// <param name="changes">Fields to replace; <see cref="Absent"/> as a value removes the field.</param>
    public static AlvoRecord OrderRecord(params (string Field, object? Value)[] changes)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "0b6f6a7e-4a8e-4bdb-9f3e-0f2b9b6e1c11",
            ["owner_id"] = null,
            ["status"] = "ok",
            ["previous"] = "in_review",
            ["total"] = 12.5m,
            ["quantity"] = 3L,
            ["rank"] = 7L,
            ["is_paid"] = true,
            ["note"] = "fragile",
            ["code"] = null,
            ["due_at"] = "2026-11-02T08:30:00Z",
            ["ship_on"] = "2026-11-03",
            ["ref_no"] = "R-1",
        };
        foreach (var (field, value) in changes)
        {
            Apply(values, field, value);
        }

        return new AlvoRecord(values);
    }

    private static void Apply(Dictionary<string, object?> values, string field, object? value)
    {
        if (ReferenceEquals(value, Absent))
        {
            values.Remove(field);
            return;
        }

        values[field] = value;
    }
}
