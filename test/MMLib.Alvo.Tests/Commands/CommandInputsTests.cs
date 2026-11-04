using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Data;
using NSubstitute;
using System.Text.Json;

namespace MMLib.Alvo.Tests.Commands;

/// <summary>A command's fingerprint over its uploaded files (Ruling 52), which it can hash only at the top level (Ruling 58).</summary>
public sealed class CommandInputsTests
{
    private static readonly CommandRegistration _upload = new("upload_scan", 1, CommandAccess.Of("true"), null, "/u", null, null);

    [Fact]
    public async Task Hashing_an_upload_stops_when_the_client_hangs_up()
    {
        using var content = new MemoryStream(new byte[64 * 1024]);
        var file = new FormFile(content, 0, content.Length, "scan", "scan.pdf");
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            new CommandInputs([0]).FingerprintAsync(_upload, new DefaultHttpContext(), [file], JsonSerializerOptions.Default, aborted.Token));
    }

    [Theory]
    [InlineData(nameof(NestedForm))]
    [InlineData(nameof(NestedParameters))]
    [InlineData(nameof(NestedList))]
    [InlineData(nameof(DeeplyNested))]
    [InlineData(nameof(WholeForm))]
    public void An_upload_below_the_top_level_is_refused_naming_the_parameter(string handler)
    {
        var refusal = Should.Throw<ArgumentException>(() => CommandInputs.EnsureUploadsAreTopLevel("upload_scan", Handler(handler), Services()));

        refusal.Message.ShouldContain("'upload_scan'");
        refusal.Message.ShouldContain("'input'");
        refusal.Message.ShouldContain("top-level");
    }

    [Theory]
    [InlineData(nameof(TopLevelFile))]
    [InlineData(nameof(TopLevelFiles))]
    [InlineData(nameof(PlainBody))]
    [InlineData(nameof(PlumbingAndServices))]
    public void A_top_level_upload_and_inputs_without_one_are_accepted(string handler) =>
        Should.NotThrow(() => CommandInputs.EnsureUploadsAreTopLevel("upload_scan", Handler(handler), Services()));

    private static Delegate Handler(string name) => name switch
    {
        nameof(NestedForm) => NestedForm,
        nameof(NestedParameters) => NestedParameters,
        nameof(NestedList) => NestedList,
        nameof(DeeplyNested) => DeeplyNested,
        nameof(WholeForm) => WholeForm,
        nameof(TopLevelFile) => TopLevelFile,
        nameof(TopLevelFiles) => TopLevelFiles,
        nameof(PlainBody) => PlainBody,
        _ => PlumbingAndServices,
    };

    private static IServiceProviderIsService Services()
    {
        var services = Substitute.For<IServiceProviderIsService>();
        services.IsService(typeof(IAlvoData)).Returns(true);
        return services;
    }

    private static void NestedForm([FromForm] ScanForm input) => _ = input;

    private static void NestedParameters([AsParameters] ScanParameters input) => _ = input;

    private static void NestedList([FromForm] ScanBatch input) => _ = input;

    private static void DeeplyNested([FromForm] Envelope input) => _ = input;

    private static void WholeForm(IFormCollection input) => _ = input;

    private static void TopLevelFile(IFormFile input, string note) => _ = (input, note);

    private static void TopLevelFiles(IFormFileCollection input, [FromForm] string note) => _ = (input, note);

    private static void PlainBody(Envelope.Note input) => _ = input;

    private static void PlumbingAndServices([AsParameters] Plumbing input, HttpRequest request, IAlvoData data) => _ = (input, request, data);

    public sealed record ScanForm(string Title, IFormFile Scan);

    public sealed record ScanParameters([FromForm] IFormFileCollection Scans, [FromQuery] int Page);

    public sealed record ScanBatch(string Title, List<IFormFile> Scans);

    public sealed record Envelope(string Title, ScanForm Inner)
    {
        public sealed record Note(string Text, int Priority, DateTimeOffset At);
    }

    public sealed record Plumbing(HttpContext Http, [FromServices] IAlvoData Data, [FromQuery] int Page);
}
