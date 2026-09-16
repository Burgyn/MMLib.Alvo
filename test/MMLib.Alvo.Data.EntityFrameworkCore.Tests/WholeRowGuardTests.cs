using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The two rules that make a create-or-replace a replacement: which field a body has to carry for the row
/// to be storable at all, and that a broken caller is told so by name rather than by whatever the engine or
/// the CLR raised next.
/// </summary>
public class WholeRowGuardTests
{
    /// <summary>
    /// Each argument is refused by name. The <see cref="ArgumentException.ParamName"/> is asserted rather
    /// than the type alone, because every one of these also fails a step or two later — on a dictionary
    /// copy, on the managed-column lookup — and blames a parameter the caller never passed.
    /// </summary>
    [Fact]
    public void Whole_row_requires_every_argument()
    {
        var decision = SnapshotFixture.UpdateDecision(readOnlyField: null);

        Should.Throw<ArgumentNullException>(() => WholeRowGuard.WholeRow(null!, Vehicle, decision))
            .ParamName.ShouldBe("values");
        Should.Throw<ArgumentNullException>(() => WholeRowGuard.WholeRow(Payload(), null!, decision))
            .ParamName.ShouldBe("schema");
        Should.Throw<ArgumentNullException>(() => WholeRowGuard.WholeRow(Payload(), Vehicle, null!))
            .ParamName.ShouldBe("decision");
    }

    /// <inheritdoc cref="Whole_row_requires_every_argument"/>
    [Fact]
    public void Ensure_whole_row_requires_every_argument()
    {
        Should.Throw<ArgumentNullException>(() => WholeRowGuard.EnsureWholeRow(null!, Vehicle))
            .ParamName.ShouldBe("values");
        Should.Throw<ArgumentNullException>(() => WholeRowGuard.EnsureWholeRow(Payload(), null!))
            .ParamName.ShouldBe("schema");
    }

    /// <summary>
    /// <c>required</c> is not the only way a column says no: a field the descriptor leaves neither
    /// <c>required</c> nor <c>nullable</c> maps to <c>NOT NULL</c> all the same, so a replacement omitting it
    /// has to be refused here — otherwise the engine refuses it, and the caller gets a 500 carrying the
    /// provider's wording instead of the name of the field to supply.
    /// </summary>
    /// <param name="required">Whether the field declares <c>required</c>.</param>
    /// <param name="nullable">Whether the field declares <c>nullable</c>.</param>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void A_field_the_store_refuses_null_for_must_be_supplied(bool required, bool nullable)
    {
        var refused = Should.Throw<ArgumentException>(
            () => WholeRowGuard.EnsureWholeRow(Payload(), Ledger(required, nullable)));

        refused.Message.ShouldContain("rank");
    }

    /// <summary>
    /// And a field that really is optional is not, so the refusal above is the column's answer rather than a
    /// guard that refuses every absence.
    /// </summary>
    [Fact]
    public void A_field_the_store_accepts_null_for_may_be_left_out()
        => WholeRowGuard.EnsureWholeRow(Payload(), Ledger(required: false, nullable: true));

    private static Dictionary<string, object?> Payload() =>
        new(StringComparer.Ordinal) { ["title"] = "Q3 close" };

    /// <summary>An entity whose one interesting field carries the facets under test.</summary>
    /// <param name="required">Whether <c>rank</c> declares <c>required</c>.</param>
    /// <param name="nullable">Whether <c>rank</c> declares <c>nullable</c>.</param>
    private static EntitySchema Ledger(bool required, bool nullable) => new()
    {
        Name = "ledger",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "title", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "rank", Type = FieldType.Integer, Required = required, Nullable = nullable },
        ],
    };

    private static EntitySchema Vehicle => AlvoDataFixtures.Vehicle;
}
