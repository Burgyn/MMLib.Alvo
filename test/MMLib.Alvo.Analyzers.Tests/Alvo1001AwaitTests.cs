namespace MMLib.Alvo.Analyzers.Tests;

/// <summary>ALVO1001's await rule: in a hook only the write's transaction, a context's publish and the hook's own code may be awaited.</summary>
public sealed class Alvo1001AwaitTests
{
    private const string Usings = """
        using System;
        using System.Net.Http;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.DependencyInjection;
        using MMLib.Alvo;
        using MMLib.Alvo.Data;
        using MMLib.Alvo.Events;
        using MMLib.Alvo.Hooks;

        """;

    [Fact]
    public async Task Awaiting_the_write_s_transaction_its_typed_extensions_publish_and_own_helpers_is_not_reported()
    {
        const string source = Usings + """
            public sealed class Ticket { public Guid Id { get; init; } public string? Status { get; init; } }
            public sealed class Guard : IBeforeWriteHook<Ticket>
            {
                public async ValueTask BeforeWriteAsync(BeforeWrite<Ticket> write, CancellationToken cancellationToken)
                {
                    _ = await write.Transaction.GetAsync("tickets", write.New.Id, cancellationToken);
                    _ = await write.Transaction.AsSystem().QueryAsync<Ticket>(t => t.Status == "open", 1, cancellationToken).ConfigureAwait(false);
                    await write.PublishAsync("tickets.checked", "ticket", null, cancellationToken);
                    await CheckAsync(write, cancellationToken);
                }

                private static async Task CheckAsync(BeforeWrite<Ticket> write, CancellationToken cancellationToken) =>
                    _ = await write.Transaction.GetAsync("tickets", write.New.Id, cancellationToken);
            }
            """;

        (await AnalyzerHarness.Alvo1001Async(source)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Awaiting_a_typed_client_the_host_wrote_itself_is_reported()
    {
        const string source = Usings + """
            public interface IRatesClient { Task<decimal> EuroAsync(CancellationToken ct); }
            public sealed class Guard(IRatesClient rates) : IBeforeWriteHook
            {
                public async ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken) =>
                    write.Set("rate", await rates.EuroAsync(cancellationToken)); // ALVO1001
            }
            """;

        var reported = await AnalyzerHarness.Alvo1001Async(source);

        AnalyzerHarness.Lines(reported).ShouldBe(AnalyzerHarness.MarkedLines(source));
        reported[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldStartWith("'IRatesClient.EuroAsync' is awaited");
    }

    [Fact]
    public async Task A_helper_type_nested_in_the_hook_is_the_hook_s_own_code_and_is_analysed()
    {
        const string source = Usings + """
            public sealed class Guard : IBeforeWriteHook
            {
                public async ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken) =>
                    await Checks.RunAsync(write, cancellationToken);

                private static class Checks
                {
                    public static async Task RunAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken)
                    {
                        _ = await write.Transaction.GetAsync("tickets", Guid.Empty, cancellationToken);
                        await Task.Delay(1, cancellationToken); // ALVO1001
                    }
                }
            }
            """;

        AnalyzerHarness.Lines(await AnalyzerHarness.Alvo1001Async(source)).ShouldBe(AnalyzerHarness.MarkedLines(source));
    }

    [Fact]
    public async Task Awaiting_anything_else_in_a_hook_is_reported_once()
    {
        const string source = Usings + """
            public sealed class Slow(System.IO.Stream stream) : IAfterWriteHook
            {
                public async ValueTask AfterWriteAsync(AfterWrite<AlvoRecord> write, CancellationToken cancellationToken)
                {
                    await Task.Delay(10, cancellationToken); // ALVO1001
                    _ = await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false); // ALVO1001
                }
            }
            """;

        var reported = await AnalyzerHarness.Alvo1001Async(source);

        AnalyzerHarness.Lines(reported).ShouldBe(AnalyzerHarness.MarkedLines(source));
        reported[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldStartWith("'Task.Delay' is awaited, and only the write's transaction may be");
    }

    [Fact]
    public async Task Awaiting_an_extension_method_on_a_reached_receiver_is_reported_once_at_the_receiver()
    {
        const string source = Usings + """
            public sealed class Guard(HttpClient http) : IBeforeWriteHook
            {
                public async ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken) =>
                    write.Set("rate", await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<decimal>(http, "https://rates.example", cancellationToken)); // ALVO1001
            }
            """;

        var reported = await AnalyzerHarness.Alvo1001Async(source);

        AnalyzerHarness.Lines(reported).ShouldBe(AnalyzerHarness.MarkedLines(source), "the receiver is reported, not the await on top of it (Ruling 14)");
        reported[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldStartWith("'HttpClient' is used");
    }

    [Fact]
    public async Task Awaiting_a_local_function_declared_outside_a_hook_delegate_is_reported()
    {
        const string source = Usings + """
            public static class Rates
            {
                public static IAlvoBuilder AddRates(this IAlvoBuilder alvo, HttpClient http)
                {
                    async Task<decimal> Euro(CancellationToken ct) => decimal.Parse(await http.GetStringAsync("https://rates.example", ct));
                    return alvo.Entity("invoices", e => e.BeforeWrite("rate", async (BeforeWrite<AlvoRecord> write, CancellationToken ct) =>
                        write.Set("rate", await Euro(ct)))); // ALVO1001
                }
            }
            """;

        var reported = await AnalyzerHarness.Alvo1001Async(source);

        AnalyzerHarness.Lines(reported).ShouldBe(AnalyzerHarness.MarkedLines(source), "its body is outside the analysed lambda, so it is not the hook's own code (Ruling 25)");
        reported[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldStartWith("'Rates.Euro' is awaited");
    }

    [Fact]
    public async Task A_local_function_declared_inside_a_hook_delegate_is_its_own_code_and_is_analysed()
    {
        const string source = Usings + """
            public static class Tickets
            {
                public static IAlvoBuilder AddTickets(this IAlvoBuilder alvo) =>
                    alvo.Entity("tickets", e => e.BeforeWrite("check", async (BeforeWrite<AlvoRecord> write, CancellationToken ct) =>
                    {
                        await CheckAsync(ct);

                        async Task CheckAsync(CancellationToken token)
                        {
                            _ = await write.Transaction.GetAsync("tickets", Guid.Empty, token);
                            await Task.Delay(1, token); // ALVO1001
                        }
                    }));
            }
            """;

        AnalyzerHarness.Lines(await AnalyzerHarness.Alvo1001Async(source)).ShouldBe(AnalyzerHarness.MarkedLines(source));
    }

    [Fact]
    public async Task A_local_function_declared_inside_a_hook_type_is_its_own_code_and_is_analysed()
    {
        const string source = Usings + """
            public sealed class Guard : IBeforeWriteHook
            {
                public async ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken)
                {
                    await CheckAsync(cancellationToken);

                    async Task CheckAsync(CancellationToken token)
                    {
                        _ = await write.Transaction.GetAsync("tickets", Guid.Empty, token);
                        await Task.Delay(1, token); // ALVO1001
                    }
                }
            }
            """;

        AnalyzerHarness.Lines(await AnalyzerHarness.Alvo1001Async(source)).ShouldBe(AnalyzerHarness.MarkedLines(source));
    }
}
