using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using MMLib.Alvo.Admin.Components.Schema;
using System.Text;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// The Import box's text reaches the server as a stream, never in a circuit message, so the circuit hub keeps SignalR's
/// own limit; what is over the box's ceiling is refused, at the box and again on the server (#316, Ruling U-B).
/// </summary>
public sealed class ImportLimitTests
{
    private const long SignalRDefaultReceiveLimit = 32 * 1024;

    [Fact]
    public void Adding_the_dashboard_leaves_the_circuits_receive_limit_at_signalrs_own()
        => CircuitReceiveLimit(new ServiceCollection().AddAlvoAdmin()).ShouldBe(SignalRDefaultReceiveLimit);

    [Fact]
    public void Adding_the_dashboard_leaves_every_hubs_receive_limit_at_signalrs_own()
    {
        using var provider = new ServiceCollection().AddAlvoAdmin().BuildServiceProvider();

        provider.GetRequiredService<IOptions<HubOptions>>().Value.MaximumReceiveMessageSize.ShouldBe(SignalRDefaultReceiveLimit);
    }

    [Fact]
    public void A_host_that_set_a_lower_limit_before_adding_the_dashboard_keeps_it()
    {
        var services = new ServiceCollection();
        services.AddSignalR(hub => hub.MaximumReceiveMessageSize = 16 * 1024);

        CircuitReceiveLimit(services.AddAlvoAdmin()).ShouldBe(16 * 1024);
    }

    [Fact]
    public void Every_text_within_the_character_ceiling_fits_the_stream_ceiling()
        => ((long)ImportLimit.MaxChars * ImportLimit.MaxUtf8BytesPerChar).ShouldBeLessThan(ImportLimit.MaxStreamBytes);

    [Theory]
    [InlineData("漢")]
    [InlineData("\U0001F600")]
    [InlineData("\uD800")]
    [InlineData("\"\\")]
    public void No_utf16_unit_encodes_to_more_utf8_bytes_than_the_ceiling_assumes(string text)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetByteCount(text);

        bytes.ShouldBeLessThanOrEqualTo(text.Length * ImportLimit.MaxUtf8BytesPerChar);
    }

    [Fact]
    public void The_bike_workshop_example_is_far_inside_the_ceiling()
        => (File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "examples", "bike-workshop", "bike-workshop.alvo.json")).Length * 10).ShouldBeLessThan(ImportLimit.MaxChars);

    [Fact]
    public void A_refusal_from_the_box_says_the_paste_and_names_the_ceiling()
    {
        var refusal = ImportLimit.Refusal("1200000");

        refusal.ShouldContain("1,200,000 characters");
        refusal.ShouldContain("up to 1,000,000 characters");
        refusal.ShouldContain("not loaded");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12 34")]
    [InlineData("-5")]
    public void A_refusal_with_no_usable_measurement_still_names_the_ceiling(string? measured)
    {
        var refusal = ImportLimit.Refusal(measured);

        refusal.ShouldStartWith("That paste was not loaded.");
        refusal.ShouldContain("1,000,000 characters");
    }

    [Fact]
    public async Task A_stream_within_both_ceilings_is_read_as_utf8()
    {
        var read = await ImportStream.ReadAsync(StreamOver("{\"description\": \"č漢\"}"), NullLogger.Instance, TestContext.Current.CancellationToken);

        read.Refusal.ShouldBeNull();
        read.Text.ShouldBe("{\"description\": \"č漢\"}");
    }

    [Fact]
    public async Task A_stream_declared_over_the_byte_ceiling_is_refused_before_a_byte_is_read()
    {
        var stream = new FakeStream([], ImportLimit.MaxStreamBytes + 1);

        var read = await ImportStream.ReadAsync(stream, NullLogger.Instance, TestContext.Current.CancellationToken);

        read.Text.ShouldBeNull();
        read.Refusal!.ShouldContain("2,934 KB");
        read.Refusal!.ShouldContain("up to 1,000,000 characters");
        stream.OpenedWith.ShouldBeNull();
    }

    [Fact]
    public async Task A_stream_is_opened_with_the_byte_ceiling_as_the_most_it_may_carry()
    {
        var stream = StreamOver("{}");

        await ImportStream.ReadAsync(stream, NullLogger.Instance, TestContext.Current.CancellationToken);

        stream.OpenedWith.ShouldBe(ImportLimit.MaxStreamBytes);
    }

    [Fact]
    public async Task A_stream_over_the_character_ceiling_is_refused_naming_its_size()
    {
        var read = await ImportStream.ReadAsync(StreamOver(new string('x', ImportLimit.MaxChars + 1)), NullLogger.Instance, TestContext.Current.CancellationToken);

        read.Text.ShouldBeNull();
        read.Refusal!.ShouldContain("1,000,001 characters");
        read.Refusal!.ShouldContain("977 KB");
    }

    [Fact]
    public async Task A_stream_at_the_character_ceiling_is_read_whole()
    {
        var text = new string('x', ImportLimit.MaxChars);

        (await ImportStream.ReadAsync(StreamOver(text), NullLogger.Instance, TestContext.Current.CancellationToken)).Text.ShouldBe(text);
    }

    [Fact]
    public async Task A_stream_that_cannot_be_opened_is_refused_in_place_and_logged_at_warning()
    {
        var logger = new RecordingLogger();

        var read = await ImportStream.ReadAsync(new FailingStream(open: new InvalidOperationException("pipe broke")), logger, TestContext.Current.CancellationToken);

        read.Text.ShouldBeNull();
        read.Unreadable.ShouldBeTrue();
        read.Refusal.ShouldBe("That paste could not be read. Try again, or import a smaller descriptor.");
        logger.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Warning);
    }

    [Fact]
    public async Task A_stream_that_fails_part_way_is_refused_in_place_and_its_text_is_not_logged()
    {
        var logger = new RecordingLogger();

        var read = await ImportStream.ReadAsync(new FailingStream(read: new IOException("reset")), logger, TestContext.Current.CancellationToken);

        read.Unreadable.ShouldBeTrue();
        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldNotContain("secret-paste");
    }

    [Fact]
    public async Task A_read_cancelled_because_the_page_went_is_not_refused_but_cancelled()
        => await Should.ThrowAsync<OperationCanceledException>(
            () => ImportStream.ReadAsync(new FailingStream(open: new OperationCanceledException()), NullLogger.Instance, TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_read_over_a_circuit_that_has_gone_is_not_refused_but_reported_as_gone()
        => await Should.ThrowAsync<JSDisconnectedException>(
            () => ImportStream.ReadAsync(new FailingStream(open: new JSDisconnectedException("gone")), NullLogger.Instance, TestContext.Current.CancellationToken));

    [Theory]
    [InlineData("3 1", 3, true)]
    [InlineData("0 0", 0, false)]
    [InlineData("2 0", 2, false)]
    public void The_box_measure_is_read_as_alvo_js_sends_it(string measured, int lines, bool filled)
        => BoxMeasure.Parse(measured).ShouldBe(new BoxMeasure(lines, filled));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3")]
    [InlineData("3 2")]
    [InlineData("-1 1")]
    [InlineData("a 1")]
    public void A_measure_that_is_not_one_reads_as_an_empty_box(string? measured)
        => BoxMeasure.Parse(measured).ShouldBe(BoxMeasure.Empty);

    private static FakeStream StreamOver(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new FakeStream(bytes, bytes.LongLength);
    }

    private static long? CircuitReceiveLimit(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        var hub = typeof(Microsoft.AspNetCore.Components.Server.CircuitOptions).Assembly
            .GetType("Microsoft.AspNetCore.Components.Server.ComponentHub", throwOnError: true)!;
        var options = provider.GetRequiredService(typeof(IOptions<>).MakeGenericType(typeof(HubOptions<>).MakeGenericType(hub)));
        var value = (HubOptions)options.GetType().GetProperty(nameof(IOptions<object>.Value))!.GetValue(options)!;
        return value.MaximumReceiveMessageSize;
    }

    /// <summary>A browser stream of known bytes, declaring <paramref name="length"/>, that records the ceiling it was opened with.</summary>
    private sealed class FakeStream(byte[] bytes, long length) : IJSStreamReference
    {
        public long? OpenedWith { get; private set; }

        public long Length => length;

        public ValueTask<Stream> OpenReadStreamAsync(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            OpenedWith = maxAllowedSize;
            return ValueTask.FromResult<Stream>(new MemoryStream(bytes));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A browser stream whose open, or whose read after a first chunk of the paste, throws.</summary>
    private sealed class FailingStream(Exception? open = null, Exception? read = null) : IJSStreamReference
    {
        public const string Prefix = "{\"secret-paste\": ";

        public long Length => 64;

        public ValueTask<Stream> OpenReadStreamAsync(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
            => open is not null ? ValueTask.FromException<Stream>(open) : ValueTask.FromResult<Stream>(new BrokenStream(read!));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A stream that hands out <see cref="FailingStream.Prefix"/>, then throws.</summary>
    private sealed class BrokenStream(Exception failure) : MemoryStream(Encoding.UTF8.GetBytes(FailingStream.Prefix))
    {
        private bool _served;

        public override int Read(byte[] buffer, int offset, int count) => Serve(() => base.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_served)
            {
                throw failure;
            }

            _served = true;
            return base.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Serve(() => base.Read(buffer.Span)));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Serve(() => base.Read(buffer, offset, count)));

        private int Serve(Func<int> read)
        {
            if (_served)
            {
                throw failure;
            }

            _served = true;
            return read();
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class RecordingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception) + exception));
    }
}
