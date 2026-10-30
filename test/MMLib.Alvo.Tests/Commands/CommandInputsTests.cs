using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Commands.Internal;
using System.Text.Json;

namespace MMLib.Alvo.Tests.Commands;

/// <summary>A command's fingerprint over its uploaded files (Ruling 52).</summary>
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
}
