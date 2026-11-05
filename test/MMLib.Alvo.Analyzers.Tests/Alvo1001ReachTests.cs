namespace MMLib.Alvo.Analyzers.Tests;

/// <summary>What ALVO1001 reports inside in-transaction code: the boot ban's list, plus files — reached however it is spelled.</summary>
public sealed class Alvo1001ReachTests
{
    private const string Usings = """
        using System;
        using System.Data.Common;
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
    public async Task A_service_locator_lookup_or_a_wrapper_of_a_banned_type_is_reported()
    {
        const string source = Usings + """
            public sealed class Guard(IServiceProvider services, Lazy<HttpClient> lazy, System.Collections.Generic.IEnumerable<IAlvoData> all) : IBeforeWriteHook
            {
                public ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken)
                {
                    var client = services.GetRequiredService<HttpClient>(); // ALVO1001
                    var data = services.GetService(typeof(IAlvoData)); // ALVO1001
                    _ = lazy.IsValueCreated; // ALVO1001
                    _ = all; // ALVO1001
                    return ValueTask.CompletedTask;
                }
            }
            """;

        AnalyzerHarness.Lines(await AnalyzerHarness.Alvo1001Async(source)).ShouldBe(AnalyzerHarness.MarkedLines(source));
    }

    [Fact]
    public async Task A_data_port_a_connection_a_file_and_a_socket_are_reported()
    {
        const string source = Usings + """
            public sealed class Guard(IAlvoData data, DbConnection connection) : IBeforeWriteHook
            {
                public ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken)
                {
                    _ = data; // ALVO1001
                    _ = connection.State; // ALVO1001
                    _ = System.IO.File.Exists("/tmp/x"); // ALVO1001
                    using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp); // ALVO1001
                    return ValueTask.CompletedTask;
                }
            }
            """;

        AnalyzerHarness.Lines(await AnalyzerHarness.Alvo1001Async(source)).ShouldBe(AnalyzerHarness.MarkedLines(source));
    }

    [Fact]
    public async Task A_reference_to_a_typed_client_the_host_wrote_is_not_reported_by_itself()
    {
        const string source = Usings + """
            public interface IRatesClient { decimal Euro(); }
            public sealed class Guard(IRatesClient rates) : IBeforeWriteHook
            {
                public ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken)
                {
                    write.Set("rate", rates.Euro());
                    return ValueTask.CompletedTask;
                }
            }
            """;

        (await AnalyzerHarness.Alvo1001Async(source)).ShouldBeEmpty("a synchronous call into the host's own code is the documented blind spot");
    }

    [Fact]
    public async Task A_subclass_of_a_reached_type_and_an_implementation_of_a_reached_interface_are_reached()
    {
        const string source = Usings + """
            public interface IEmailSender { void Send(string to); }
            public sealed class RatesHttp : HttpClient { }
            public sealed class Mailer : IEmailSender { public void Send(string to) { } }
            public sealed class Guard(RatesHttp http, Mailer mail) : IBeforeWriteHook
            {
                public ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken)
                {
                    _ = http.Timeout; // ALVO1001
                    mail.Send("a@b.example"); // ALVO1001
                    return ValueTask.CompletedTask;
                }
            }
            """;

        AnalyzerHarness.Lines(await AnalyzerHarness.Alvo1001Async(source)).ShouldBe(AnalyzerHarness.MarkedLines(source));
    }

    [Fact]
    public async Task An_ef_core_context_is_reached_by_its_metadata_name()
    {
        const string source = Usings + """
            namespace Microsoft.EntityFrameworkCore { public class DbContext { } }
            public sealed class ShopContext : Microsoft.EntityFrameworkCore.DbContext { }
            public sealed class Guard(ShopContext shop) : IBeforeWriteHook
            {
                public ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken)
                {
                    _ = shop; // ALVO1001
                    return ValueTask.CompletedTask;
                }
            }
            """;

        AnalyzerHarness.Lines(await AnalyzerHarness.Alvo1001Async(source)).ShouldBe(AnalyzerHarness.MarkedLines(source));
    }

    [Fact]
    public async Task A_reached_local_is_reported_once_however_often_it_is_used()
    {
        const string source = Usings + """
            public sealed class Guard(IHttpClientFactory clients) : IBeforeWriteHook
            {
                public async ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken)
                {
                    var answer = await clients.CreateClient().GetAsync("https://rates.example", cancellationToken); // ALVO1001
                    write.Set("ok", answer.IsSuccessStatusCode);
                    write.Set("status", (int)answer.StatusCode);
                }
            }
            """;

        AnalyzerHarness.Lines(await AnalyzerHarness.Alvo1001Async(source)).Distinct().Count().ShouldBe(2,
            "the factory parameter on the marked line, and the reached local `answer` once — not at each of its uses");
    }
}
