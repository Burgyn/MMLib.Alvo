using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Management;
using MMLib.Alvo.TestHost;
using Shouldly;
using System.Text.Json.Nodes;
using Xunit;

namespace FleetDesk.Tests;

public sealed class FleetDeskExtensionsTests
{
    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "fleet-desk.alvo.json");

    [Fact]
    public async Task The_catalog_lists_the_handler_and_an_apply_dropping_what_it_reads_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(builder =>
        {
            builder.FromDescriptor(Descriptor).AddFleetDeskExtensions();
            builder.Services.AddSingleton<IWorkshop, NoWorkshop>();
        }, cancellationToken: ct);
        var management = AsAdministrator(alvo);

        var handler = (await management.GetExtensionsAsync("fleet-desk", ct)).Items.ShouldHaveSingleItem();
        handler.Id.ShouldBe("book-repair");
        handler.Entities.ShouldBe(["vehicles"]);
        handler.Operations.ShouldBe(["update"]);
        handler.When.ShouldBe("changed(status) && new.status == 'in_repair'");

        var withoutStatus = JsonNode.Parse(await File.ReadAllTextAsync(Descriptor, ct))!;
        withoutStatus["entities"]!["vehicles"]!["fields"]!.AsObject().Remove("status");
        var refused = await Should.ThrowAsync<DescriptorValidationException>(() => management.ApplyDescriptorAsync(
            "fleet-desk", new ManagementApplyRequest(withoutStatus.ToJsonString(), ExpectedRevision: 1, DryRun: true), ct));
        refused.Result.Errors.ShouldContain(error => error.Path == "/entities/vehicles/fields/status");
    }

    private static IAlvoManagement AsAdministrator(AlvoTestHost alvo)
    {
        alvo.Services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = AlvoTestHost.Caller("admin"),
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "docs",
        };
        return alvo.Services.GetRequiredService<IAlvoManagement>();
    }

    private sealed class NoWorkshop : IWorkshop
    {
        public Task BookAsync(Guid vehicle, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
