namespace MMLib.Alvo.Analyzers.Tests;

/// <summary>Where ALVO1001 looks: in-transaction hook types (here), delegates and CEL functions (Task 6) — and not elsewhere.</summary>
public sealed class Alvo1001ScopeTests
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
    public async Task An_http_client_in_a_before_write_hook_is_reported_once_at_its_use()
    {
        const string source = Usings + """
            public sealed class RateGuard(HttpClient http) : IBeforeWriteHook
            {
                public async ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken)
                {
                    var rate = await http.GetStringAsync("https://rates.example/eur", cancellationToken); // ALVO1001
                    write.Set("rate", rate);
                }
            }
            """;

        var reported = await AnalyzerHarness.Alvo1001Async(source);

        AnalyzerHarness.Lines(reported).ShouldBe(AnalyzerHarness.MarkedLines(source));
        var message = reported[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        message.ShouldStartWith("'HttpClient' is used inside the before-write hook 'RateGuard'");
        message.ShouldContain("validate hook");
        reported[0].Severity.ShouldBe(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData("IAfterWriteHook", "ValueTask AfterWriteAsync(AfterWrite<AlvoRecord> write, CancellationToken cancellationToken)", "after-write")]
    [InlineData("IBeforeDeleteHook", "ValueTask BeforeDeleteAsync(BeforeDelete<AlvoRecord> deletion, CancellationToken cancellationToken)", "before-delete")]
    [InlineData("IAfterDeleteHook", "ValueTask AfterDeleteAsync(AfterDelete<AlvoRecord> deletion, CancellationToken cancellationToken)", "after-delete")]
    public async Task Every_in_transaction_stage_is_in_scope(string contract, string signature, string stage)
    {
        var source = Usings + $$"""
            public sealed class Audit(HttpClient http) : {{contract}}
            {
                public async {{signature}}
                {
                    await http.GetAsync("https://audit.example", cancellationToken); // ALVO1001
                }
            }
            """;

        var reported = await AnalyzerHarness.Alvo1001Async(source);

        AnalyzerHarness.Lines(reported).ShouldBe(AnalyzerHarness.MarkedLines(source));
        reported[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain($"the {stage} hook 'Audit'");
    }

    [Fact]
    public async Task A_typed_hook_its_constructor_and_its_field_initializer_are_all_in_scope()
    {
        const string source = Usings + """
            public sealed class Ticket { public Guid Id { get; init; } }
            public sealed class Guard : IBeforeWriteHook<Ticket>
            {
                private readonly HttpClient _client = new(); // ALVO1001
                public ValueTask BeforeWriteAsync(BeforeWrite<Ticket> write, CancellationToken cancellationToken) =>
                    _client.Timeout > TimeSpan.Zero ? ValueTask.CompletedTask : ValueTask.CompletedTask; // ALVO1001
            }
            """;

        AnalyzerHarness.Lines(await AnalyzerHarness.Alvo1001Async(source)).ShouldBe(AnalyzerHarness.MarkedLines(source));
    }

    [Fact]
    public async Task A_validate_hook_may_call_the_network()
    {
        const string source = Usings + """
            public sealed class VatCheck(HttpClient http) : IValidateWriteHook
            {
                public async ValueTask ValidateAsync(ValidateWrite<AlvoRecord> write, CancellationToken cancellationToken) =>
                    await http.GetAsync("https://vies.example", cancellationToken);
            }
            """;

        (await AnalyzerHarness.Alvo1001Async(source)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_event_handler_may_call_the_network()
    {
        const string source = Usings + """
            public sealed class Notify(HttpClient http) : IAlvoEventHandler<AlvoCreated<AlvoRecord>>
            {
                public Task HandleAsync(AlvoCreated<AlvoRecord> created, AlvoEventContext context, CancellationToken cancellationToken) =>
                    http.GetAsync("https://hooks.example", cancellationToken);
            }
            """;

        (await AnalyzerHarness.Alvo1001Async(source)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_type_that_is_no_hook_is_not_analysed()
    {
        const string source = Usings + """
            public sealed class Rates(HttpClient http)
            {
                public Task<string> EuroAsync(CancellationToken ct) => http.GetStringAsync("https://rates.example/eur", ct);
            }
            """;

        (await AnalyzerHarness.Alvo1001Async(source)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_compilation_that_does_not_reference_alvo_gets_no_analysis() =>
        (await AnalyzerHarness.Alvo1001WithoutAlvoAsync("""
            using System.Net.Http;
            public interface IBeforeWriteHook { }
            public sealed class Hook(HttpClient http) : IBeforeWriteHook { public object Client => http; }
            """)).ShouldBeEmpty();
}
