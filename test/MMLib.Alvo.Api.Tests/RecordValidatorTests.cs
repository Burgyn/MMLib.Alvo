using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Api.Tests;

public sealed class RecordValidatorTests
{
    private static readonly EntitySchema _orders = new()
    {
        Name = "orders",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "branch", Type = FieldType.String, Required = true },
            new FieldSchema { Name = "number", Type = FieldType.Integer, Required = true, Sequence = new SequenceSchema() },
        ],
    };

    [Fact]
    public async Task A_required_sequence_field_absent_from_a_create_is_not_reported_missing()
    {
        var request = new RecordValidationRequest(
            _orders,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["branch"] = "north" },
            IsCreate: true,
            ReadOnlyFields: new HashSet<string>(StringComparer.Ordinal),
            AlreadyReported: new HashSet<string>(StringComparer.Ordinal),
            FormatCatalog.Build([_orders]),
            Data: null!,
            AlvoContext.Anonymous);

        (await RecordValidator.ValidateAsync(request, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }
}
