using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Hook arithmetic over values the engine hands back, on every engine (final review M3, §0 principle 3): an integer
/// division over a stored <c>old.</c> value truncates, and a <c>math.round</c> to cents lands in a narrow decimal.
/// </summary>
/// <remarks>
/// The rest of the built-in write facts (<c>CelBuiltInWriteTests</c>) run on SQLite only: they exercise the request's own
/// values, which the HTTP binder types identically for both engines. These two read what storage gives back — an
/// <c>old.</c> integer, a decimal round-tripped through a <c>numeric(6,2)</c> on PostgreSQL and text on SQLite — where an
/// engine-specific CLR shape (an <c>int4</c>, a <c>numeric</c>) could make the two engines answer differently.
/// </remarks>
public abstract partial class DataApiEngineTests
{
    private static readonly TestApiKey _framesWriter = new("frames-writer", ["writer"], ["frames:read", "frames:write"]);

    /// <summary>
    /// <c>new.ratio / new.divisor &gt; 10</c> on create and <c>old.ratio / new.divisor &gt; 10</c> on update: 21 / 2 is
    /// 10 between integers, which is not over 10 (a decimal division would answer 10.5 and refuse); 22 / 2 and 21 / 1 are
    /// over it. The refused divisor is read back as unchanged.
    /// </summary>
    [Fact]
    public async Task An_integer_division_over_a_stored_value_truncates_on_every_engine()
    {
        await using var world = await StartBuiltInFunctionsAsync();
        using var created = await world.SendAsync(
            HttpMethod.Post, "/api/frames", _framesWriter, body: new JsonObject { ["ratio"] = 21, ["divisor"] = 2 });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        var id = (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();

        using var refusedCreate = await world.SendAsync(
            HttpMethod.Post, "/api/frames", _framesWriter, body: new JsonObject { ["ratio"] = 22, ["divisor"] = 2 });
        using var kept = await world.SendAsync(
            HttpMethod.Patch, $"/api/frames/{id}", _framesWriter, body: new JsonObject { ["divisor"] = 2 });
        using var refused = await world.SendAsync(
            HttpMethod.Patch, $"/api/frames/{id}", _framesWriter, body: new JsonObject { ["divisor"] = 1 });

        refusedCreate.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"22 / 2 is 11: {await refusedCreate.ReadTextAsync()}");
        kept.StatusCode.ShouldBe(HttpStatusCode.OK, $"21 / 2 is 10 between integers: {await kept.ReadTextAsync()}");
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.ReadTextAsync());
        (await refused.ReadTextAsync()).ShouldContain("The stored ratio is too high for this divisor.");
        using var read = await world.SendAsync(HttpMethod.Get, $"/api/frames/{id}", _framesWriter);
        (await read.ReadJsonObjectAsync())["divisor"]!.GetValue<long>().ShouldBe(2, "the refused divisor must not have landed");
    }

    /// <summary>
    /// <c>math.round(new.price * 1.2, 2)</c> into a <c>decimal(6,2)</c>: 1234.56 * 1.2 is 1481.472, stored and read back
    /// as 1481.47 on every engine. The <c>tag</c> is the same mutate's string join.
    /// </summary>
    [Fact]
    public async Task A_mutate_rounds_money_to_cents_into_a_narrow_decimal_on_every_engine()
    {
        await using var world = await StartBuiltInFunctionsAsync();

        using var created = await world.SendAsync(
            HttpMethod.Post, "/api/frames", _framesWriter, body: new JsonObject { ["code"] = " wtu 1 ", ["price"] = 1234.56m });

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        var id = (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
        using var read = await world.SendAsync(HttpMethod.Get, $"/api/frames/{id}", _framesWriter);
        var row = await read.ReadJsonObjectAsync();
        row["gross"]!.GetValue<decimal>().ShouldBe(1481.47m, "1234.56 * 1.2 = 1481.472, rounded to cents");
        row["tag"]!.GetValue<string>().ShouldBe("FR-WTU 1");
    }

    private Task<AlvoApiWorld> StartBuiltInFunctionsAsync() =>
        AlvoApiWorld.FromDescriptorAsync(
            "built-in-functions.alvo.json", [_framesWriter], new AlvoApiWorldSetup(MapAlvoProblemDetails: true), Engine);
}
