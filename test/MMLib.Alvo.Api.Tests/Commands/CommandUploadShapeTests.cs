using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MMLib.Alvo.Api.Tests.Commands;

/// <summary>
/// Ruling 58: an upload is fingerprinted by its content only as a top-level parameter (Ruling 52), so a command that binds
/// one inside a form type is refused when it is mapped — never a key that replays a different file.
/// </summary>
public sealed class CommandUploadShapeTests
{
    [Fact]
    public async Task A_command_binding_a_file_inside_a_form_type_is_refused_when_it_is_mapped()
    {
        var refusal = await Should.ThrowAsync<ArgumentException>(() => AlvoApiWorld.FromDescriptorAsync("command-desk.alvo.json", [], new AlvoApiWorldSetup(
            ConfigureApp: app => app.MapAlvoCommand("upload.nested", "/commands/nested", "true", ([FromForm] ScanForm form) => TypedResults.Ok(form.Title)))));

        refusal.ParamName.ShouldBe("handler");
        refusal.Message.ShouldContain("'upload.nested'");
    }

    public sealed record ScanForm(string Title, IFormFile Scan);
}
