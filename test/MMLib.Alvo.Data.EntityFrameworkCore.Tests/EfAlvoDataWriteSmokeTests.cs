namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// That <see cref="WritePathWorld"/> is a working port and not a mock: the three single-row writes against a
/// real engine, so every sharper fact this assembly asserts about them is asserted about a write that landed.
/// </summary>
public class EfAlvoDataWriteSmokeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A create, a partial update and a replacement of the same row all commit and read back.</summary>
    [Fact]
    public async Task Create_then_update_then_replace_round_trips()
    {
        await using var world = await WritePathWorld.StartAsync(
            WritePathFixture.Descriptor(), WritePathFixture.Schema());

        var created = await world.Data.CreateAsync(
            WritePathFixture.Entity, WritePathFixture.CreatePayload("first"), world.Caller, cancellationToken: Ct);
        var id = (Guid)created["id"]!;

        var updated = await world.Data.UpdateAsync(
            WritePathFixture.Entity, id,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["title"] = "second" },
            world.Caller, cancellationToken: Ct);

        updated["title"].ShouldBe("second");

        var replaced = await world.Data.ReplaceAsync(
            WritePathFixture.Entity, id, WritePathFixture.Payload("third"), world.Caller, cancellationToken: Ct);

        replaced.Created.ShouldBeFalse();
        replaced.Row["title"].ShouldBe("third");
    }

    /// <summary>A replacement of an id no row holds takes the create branch and keeps the caller's id.</summary>
    [Fact]
    public async Task A_replace_of_an_unused_id_creates_the_row_at_that_id()
    {
        await using var world = await WritePathWorld.StartAsync(
            WritePathFixture.Descriptor(), WritePathFixture.Schema());
        var id = Guid.NewGuid();

        var result = await world.Data.ReplaceAsync(
            WritePathFixture.Entity, id, WritePathFixture.Payload("fresh"), world.Caller, cancellationToken: Ct);

        result.Created.ShouldBeTrue();
        result.Row["id"].ShouldBe(id);
    }
}
