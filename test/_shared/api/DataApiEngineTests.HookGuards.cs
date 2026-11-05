using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Extensions;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// HX-10 acceptance (3) and (4) on every engine: a host function past its time budget rolls the write back with 500
/// <c>function-failed</c>; a built-in refusing the caller's own data answers 422 <c>expression-failed</c> and writes nothing.
/// </summary>
/// <remarks>
/// The budget facts run on the real clock: the overrun facts give 200 ms against a 1 s sleep or a 10 s token wait, and the
/// inside-budget fact gives 2 s to a function that returns at once (Ruling 42) — a margin on both sides that a loaded CI
/// runner cannot cross. The rollback is read back through storage, which is what differs per engine.
/// </remarks>
public abstract partial class DataApiEngineTests
{
    private static readonly TestApiKey _quotesWriter = new("quotes-writer", ["writer"], ["quotes:read", "quotes:write"]);
    private static readonly TestApiKey _lotsWriter = new("lots-writer", ["writer"], ["lots:read", "lots:write"]);

    [Fact]
    public async Task A_token_aware_host_function_past_its_budget_is_function_failed_and_rolls_back_on_every_engine()
    {
        await using var world = await StartCelBudgetAsync(_tightBudget, (string currency, CancellationToken ct) =>
        {
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            ct.ThrowIfCancellationRequested();
            return "1.0";
        });

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/quotes", _quotesWriter, body: new JsonObject { ["currency"] = "EUR" });

        await ShouldBeBudgetOverrunAsync(world, refused);
    }

    [Fact]
    public async Task A_token_free_host_function_past_the_budget_is_function_failed_and_rolls_back_on_every_engine()
    {
        await using var world = await StartCelBudgetAsync(_tightBudget, (string currency) =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(1));
            return "1.0";
        });

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/quotes", _quotesWriter, body: new JsonObject { ["currency"] = "EUR" });

        await ShouldBeBudgetOverrunAsync(world, refused);
    }

    [Fact]
    public async Task A_host_function_that_catches_its_cancellation_and_returns_a_fallback_is_still_function_failed_on_every_engine()
    {
        await using var world = await StartCelBudgetAsync(_tightBudget, (string currency, CancellationToken ct) =>
        {
            try
            {
                Task.Delay(TimeSpan.FromSeconds(10), ct).Wait(ct);
            }
            catch (OperationCanceledException)
            {
                return "fallback";
            }

            return "1.0";
        });

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/quotes", _quotesWriter, body: new JsonObject { ["currency"] = "EUR" });

        await ShouldBeBudgetOverrunAsync(world, refused);
    }

    [Fact]
    public async Task A_host_function_inside_its_budget_stamps_the_row_on_every_engine()
    {
        await using var world = await StartCelBudgetAsync(TimeSpan.FromSeconds(2), (string currency, CancellationToken ct) => currency == "EUR" ? "1.0" : "0.5");

        using var created = await world.SendAsync(HttpMethod.Post, "/api/quotes", _quotesWriter, body: new JsonObject { ["currency"] = "EUR" });

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        (await created.ReadJsonObjectAsync())["rate"]!.GetValue<string>().ShouldBe("1.0");
    }

    [Fact]
    public async Task An_int_of_text_on_the_caller_s_data_is_422_expression_failed_and_writes_nothing_on_every_engine()
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync("expression-failed.alvo.json", [_lotsWriter], new AlvoApiWorldSetup(), Engine);

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/lots", _lotsWriter, body: new JsonObject { ["code"] = "L1", ["quantity_text"] = "x" });

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        var problem = await refused.ReadJsonObjectAsync();
        problem["type"]!.GetValue<string>().ShouldBe("https://alvo.dev/errors/expression-failed");
        problem["detail"]!.GetValue<string>().ShouldBe(
            "The CEL function 'int' refused this request's data: the text is not a whole number such as 42 or -7. Nothing was written.");
        (await world.CountRowsAsync("lots")).ShouldBe(0);
    }

    [Fact]
    public async Task An_update_whose_data_a_built_in_refuses_is_422_and_leaves_the_row_on_every_engine()
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync("expression-failed.alvo.json", [_lotsWriter], new AlvoApiWorldSetup(), Engine);
        using var created = await world.SendAsync(HttpMethod.Post, "/api/lots", _lotsWriter, body: new JsonObject { ["code"] = "L1", ["quantity_text"] = "12" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        var id = (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();

        using var refused = await world.SendAsync(HttpMethod.Patch, $"/api/lots/{id}", _lotsWriter, body: new JsonObject { ["quantity_text"] = "twelve" });

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        using var read = await world.SendAsync(HttpMethod.Get, $"/api/lots/{id}", _lotsWriter);
        (await read.ReadJsonObjectAsync())["quantity_text"]!.GetValue<string>().ShouldBe("12");
    }

    private static readonly TimeSpan _tightBudget = TimeSpan.FromMilliseconds(200);

    private Task<AlvoApiWorld> StartCelBudgetAsync(TimeSpan budget, Delegate rateOf) =>
        AlvoApiWorld.FromDescriptorAsync(
            "cel-budget.alvo.json",
            [_quotesWriter],
            new AlvoApiWorldSetup(
                MapAlvoProblemDetails: true,
                ConfigureAlvo: alvo => alvo.AddCelFunction("rateOf", rateOf),
                ConfigureServicesAfterAlvo: services => services.Configure<AlvoExtensionOptions>(o => o.CelFunctionBudget = budget)),
            Engine);

    private static async Task ShouldBeBudgetOverrunAsync(AlvoApiWorld world, HttpResponseMessage refused)
    {
        refused.StatusCode.ShouldBe(HttpStatusCode.InternalServerError, await refused.ReadTextAsync());
        var problem = await refused.ReadJsonObjectAsync();
        problem["type"]!.GetValue<string>().ShouldBe("https://alvo.dev/errors/function-failed");
        problem["detail"]!.GetValue<string>().ShouldBe(
            "The CEL function 'rateOf' failed: it ran past the 200 ms that host functions have in one evaluation. Nothing was written.");
        (await world.CountRowsAsync("quotes")).ShouldBe(0, "the write rolled back");
    }
}
