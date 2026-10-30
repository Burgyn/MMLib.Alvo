using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Commands;

/// <summary>Acceptance (7): the OpenAPI document lists a command with Alvo's operationId, its body, and its refusals.</summary>
public sealed class CommandDocumentTests
{
    [Fact]
    public async Task The_document_lists_an_entity_command_with_its_operation_id_body_and_refusals()
    {
        await using var world = await DocumentedAsync();
        var document = await world.OpenApiDocumentAsync();

        var operation = document["paths"]!["/commands/bookings/{id}/confirm"]!["post"]!.AsObject();

        operation["operationId"]!.GetValue<string>().ShouldBe("commands.confirmBooking");
        operation["tags"]!.AsArray().Select(tag => tag!.GetValue<string>()).ShouldBe(["commands"]);
        var responses = operation["responses"]!.AsObject();
        responses.Select(response => response.Key).ShouldBe(["200", "401", "403", "404", "409", "412", "422", "508"], ignoreOrder: true);
        responses["404"]!["$ref"]!.GetValue<string>().ShouldBe("#/components/responses/notFound");
        operation["security"]!.AsArray().Count.ShouldBe(2, "anonymous, or the API key — as every Alvo route");
    }

    [Fact]
    public async Task An_unbound_command_documents_its_request_body_and_no_404()
    {
        await using var world = await DocumentedAsync();
        var document = await world.OpenApiDocumentAsync();

        var operation = document["paths"]!["/commands/quote"]!["post"]!.AsObject();

        operation["operationId"]!.GetValue<string>().ShouldBe("commands.quoteStay");
        operation["requestBody"]!["description"]!.GetValue<string>().ShouldBe("The input of the 'quote_stay' command.");
        operation["responses"]!.AsObject().ContainsKey("404").ShouldBeFalse();
        operation["responses"]!.AsObject().ContainsKey("400").ShouldBeTrue("a body that cannot be bound is answered before Alvo runs");
    }

    [Fact]
    public async Task A_command_is_described_and_so_is_the_row_key_it_binds_unless_the_host_said_it_first()
    {
        await using var world = await DocumentedAsync();
        var document = await world.OpenApiDocumentAsync();

        var confirm = document["paths"]!["/commands/bookings/{id}/confirm"]!["post"]!.AsObject();
        var quote = document["paths"]!["/commands/quote"]!["post"]!.AsObject();

        confirm["summary"]!.GetValue<string>().ShouldBe("Run the 'confirm_booking' command on one 'bookings' row");
        confirm["parameters"]!.AsArray().Single(parameter => parameter!["name"]!.GetValue<string>() == "id")!["description"]!
            .GetValue<string>().ShouldContain("'bookings' row");
        quote["summary"].ShouldBeNull("the host described the operation itself, so Alvo adds no summary over it");
        quote["description"]!.GetValue<string>().ShouldBe("Prices a stay.");
    }

    [Fact]
    public async Task A_command_narrows_the_shared_403_and_412_to_what_a_command_means_by_them()
    {
        await using var world = await DocumentedAsync();
        var document = await world.OpenApiDocumentAsync();

        var bound = document["paths"]!["/commands/bookings/{id}/confirm"]!["post"]!["responses"]!;
        var unbound = document["paths"]!["/commands/quote"]!["post"]!["responses"]!;

        bound["403"]!["$ref"]!.GetValue<string>().ShouldBe("#/components/responses/forbidden");
        bound["403"]!["description"]!.GetValue<string>().ShouldContain("judged against the row");
        unbound["403"]!["description"]!.GetValue<string>().ShouldNotContain("judged against the row");
        bound["412"]!["description"]!.GetValue<string>().ShouldContain("handler");
    }

    [Fact]
    public async Task A_command_reading_a_body_lists_the_frameworks_415_and_one_reading_none_does_not()
    {
        await using var world = await DocumentedAsync();
        var document = await world.OpenApiDocumentAsync();

        var unsupported = document["paths"]!["/commands/quote"]!["post"]!["responses"]!["415"].ShouldNotBeNull();
        document["paths"]!["/commands/bookings/{id}/confirm"]!["post"]!["responses"]!.AsObject().ContainsKey("415").ShouldBeFalse();
        var description = unsupported["description"]!.GetValue<string>();
        description.ShouldContain("media types", Case.Sensitive, "a command taking uploads reads form or multipart, so the advice names no one type");
        description.ShouldNotContain("JSON");
    }

    [Fact]
    public async Task A_bound_command_whose_handler_does_not_bind_the_id_still_documents_and_describes_it()
    {
        await using var world = await DocumentedAsync();
        var document = await world.OpenApiDocumentAsync();

        var parameters = document["paths"]!["/commands/bookings/{id}/archive"]!["post"]!["parameters"]!.AsArray();

        parameters.Single(parameter => parameter!["name"]!.GetValue<string>() == "id")!["description"]!
            .GetValue<string>().ShouldContain("'bookings' row");
    }

    [Fact]
    public async Task A_command_only_document_publishes_exactly_the_components_its_commands_reference()
    {
        await using var world = await DocumentedAsync();
        var document = await world.OpenApiDocumentAsync();

        var referenced = document["paths"]!.AsObject()
            .Where(path => path.Key.StartsWith("/commands", StringComparison.Ordinal))
            .SelectMany(path => path.Value!["post"]!["responses"]!.AsObject())
            .Select(response => response.Value!["$ref"]?.GetValue<string>())
            .OfType<string>()
            .Select(reference => reference["#/components/responses/".Length..])
            .ToHashSet(StringComparer.Ordinal);

        referenced.ShouldBeSubsetOf(document["components"]!["responses"]!.AsObject().Select(response => response.Key));
    }

    private static Task<AlvoApiWorld> DocumentedAsync() =>
        AlvoApiWorld.FromDescriptorAsync("command-desk.alvo.json", [CommandWorld.Staff], new AlvoApiWorldSetup(
            MapOpenApiDocument: true,
            ConfigureServices: services => services.AddSingleton(new CommandProbe()).AddSingleton<PaymentGateway>(),
            ConfigureApp: app =>
            {
                app.MapAlvoCommand("archive_booking", "/commands/bookings/{id:guid}/archive", "bookings", "true", () => TypedResults.NoContent());
                app.MapAlvoCommand("confirm_booking", "/commands/bookings/{id:guid}/confirm", "bookings", "'staff' in @user.roles", (Guid id) => TypedResults.Ok(id));
                app.MapAlvoCommand("quote_stay", "/commands/quote", "true", (StayRequest request) => TypedResults.Ok(new Quote(request.Nights)))
                    .WithDescription("Prices a stay.");
            }));
}
