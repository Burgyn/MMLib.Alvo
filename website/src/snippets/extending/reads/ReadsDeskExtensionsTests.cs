using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.TestHost;
using Shouldly;
using Xunit;

namespace ReadsDesk.Tests;

public sealed class ReadsDeskExtensionsTests
{
    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "reads-desk.alvo.json");

    [Fact]
    public async Task The_snippet_narrows_the_clerk_and_labels_every_note()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(builder => builder.FromDescriptor(Descriptor).AddReadsDesk(), cancellationToken: ct);
        var auditor = AlvoTestHost.Caller("auditor");
        await alvo.Data.CreateAsync("notes", Note("Boiler check", "north"), auditor, cancellationToken: ct);
        await alvo.Data.CreateAsync("notes", Note("Roof leak", "south"), auditor, cancellationToken: ct);

        var all = await alvo.Data.QueryAsync(new AlvoQuery { Entity = "notes" }, auditor, ct);
        var north = await alvo.Data.QueryAsync(new AlvoQuery { Entity = "notes" }, AlvoTestHost.Caller("clerk"), ct);

        all.Items.Select(row => row["display"]).ShouldBe(["Boiler check (north)", "Roof leak (south)"], ignoreOrder: true);
        north.Items.Select(row => row["display"]).ShouldBe(["Boiler check (north)"]);
    }

    [Fact]
    public async Task The_untyped_narrowing_narrows_the_clerk_alike()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(builder => builder.FromDescriptor(Descriptor).AddUntypedNarrowing(), cancellationToken: ct);
        var auditor = AlvoTestHost.Caller("auditor");
        await alvo.Data.CreateAsync("notes", Note("Boiler check", "north"), auditor, cancellationToken: ct);
        await alvo.Data.CreateAsync("notes", Note("Roof leak", "south"), auditor, cancellationToken: ct);

        var north = await alvo.Data.QueryAsync(new AlvoQuery { Entity = "notes" }, AlvoTestHost.Caller("clerk"), ct);

        north.Items.Select(row => row["title"]).ShouldBe(["Boiler check"]);
        north.Items.Single()["display"].ShouldBeNull("nothing fills a virtual field without an enricher");
    }

    private static Dictionary<string, object?> Note(string title, string region) => new() { ["title"] = title, ["region"] = region };
}
