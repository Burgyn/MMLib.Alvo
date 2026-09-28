using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>The bike-workshop example, booted as the demo boots it.</summary>
public sealed class BikeWorkshopWorld : AdminWorld
{
    /// <inheritdoc/>
    protected override string Descriptor => Descriptors.BikeWorkshop;
}

/// <summary>
/// The maintainer's own request, end to end: <c>customers.full_name = first_name + ' ' + last_name</c>, added from the
/// field editor to the bike-workshop customers who are already there, previewed, applied, and read back on the Data
/// screen — "Jana Nováková", diacritics intact (assistant-reliability design, ruling 2).
/// </summary>
/// <remarks>
/// <para>
/// <b>The rows are written before the field is staged, and that is half the case.</b> On SQLite a stored generated
/// column cannot be added to a table that holds a row, so the apply is the rebuild path; on an empty table the question
/// never arises. <c>customers</c> is also the parent every bike references, so a rebuild that let the foreign key act
/// would take the bike with it — asserted too.
/// </para>
/// <para>
/// <b>Read in the record's Calculated panel, not as a grid column.</b> The grid shows eight columns chosen by name
/// (<c>GridColumns</c>): the customer's label is already <c>first_name</c> + <c>last_name</c>, and a string declared
/// last falls past the cap. The panel is where the screen shows every computed value, formatted as the grid would.
/// </para>
/// <para>Its own world: it applies.</para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class ComputedTextScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_full_name_joined_from_first_and_last_name_is_applied_and_shown_on_the_data_screen()
    {
        var (jana, bike) = await SeedAsync();
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);

        await StageComputedAsync(session, "full_name", "first_name + ' ' + last_name");
        await session.PreviewPendingAsync();
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0);
        await session.Page.FillAsync("#apply-reason", "full_name for the reception desk");
        await session.Page.GetByRole(AriaRole.Button, new() { Name = "Apply these changes" }).ClickAsync();
        await session.Content.GetByText("Applied as revision").First.WaitForAsync();

        await session.GoAsync("/data/customers");
        await session.Page.GetByTestId("grid-row").Filter(new() { HasText = "Nováková" }).WaitForAsync();
        await session.GoAsync($"/data/customers?record={jana}");
        var calculated = session.Page.GetByTestId("record-calculated");
        await calculated.WaitForAsync();
        (await calculated.InnerTextAsync()).ShouldContain("Jana Nováková");

        using var scope = world.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<IAlvoData>();
        var system = AlvoContext.System(tenant: null);
        (await data.GetAsync("customers", jana, system, TestContext.Current.CancellationToken))
            .ShouldNotBeNull()["full_name"].ShouldBe("Jana Nováková");
        (await data.GetAsync("bikes", bike, StaffReader, TestContext.Current.CancellationToken))
            .ShouldNotBeNull("the bike survived its customer's rebuild")["customer_id"].ShouldBe(jana);

        session.AssertConsoleClean();
    }

    /// <summary>
    /// A reader the bike rules admit — they ask for <c>authenticated</c>, which the system identity (an <c>admin</c>
    /// and nothing else) does not hold.
    /// </summary>
    private static AlvoContext StaffReader { get; } = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Admin, Role.Authenticated },
    };

    internal static async Task StageComputedAsync(AdminSession session, string name, string expression)
    {
        await session.GoAsync("/schema/customers");
        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync(name);
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "computed", Exact = true }).ClickAsync();
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "string", Exact = true }).ClickAsync();
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Expression" }).FillAsync(expression);
        await session.Page.GetByTestId("field-save").ClickAsync();
        await session.Page.GetByTestId($"field-row-{name}").WaitForAsync();
    }

    private async Task<(Guid Jana, Guid Bike)> SeedAsync()
    {
        using var scope = world.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<IAlvoData>();
        var system = AlvoContext.System(tenant: null);
        var jana = await CustomerAsync(data, system, "Jana", "Nováková", "+421 905 100 200");
        await CustomerAsync(data, system, "Ján", "Kováč", "+421 905 100 300");
        var bike = await data.CreateAsync(
            "bikes",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["customer_id"] = jana,
                ["brand"] = "Kellys",
                ["model"] = "Spider 10",
                ["category"] = "mtb_hardtail",
                ["frame_number"] = "KLS0000901",
            },
            system,
            cancellationToken: TestContext.Current.CancellationToken);
        return (jana, Id(bike));
    }

    private static async Task<Guid> CustomerAsync(IAlvoData data, AlvoContext system, string first, string last, string phone) =>
        Id(await data.CreateAsync(
            "customers",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["first_name"] = first,
                ["last_name"] = last,
                ["phone"] = phone,
            },
            system,
            cancellationToken: TestContext.Current.CancellationToken));

    private static Guid Id(AlvoRecord record) => record["id"] switch
    {
        Guid id => id,
        var other => Guid.Parse(Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture)!),
    };
}

/// <summary>
/// The null rule, seen by the operator: <c>street</c> is optional, so joining it is refused at Preview with the
/// compiler's own words and the explicit fallback as the fix — the field editor adds nothing of its own.
/// </summary>
/// <remarks>Its own world: it stages a field it never discards.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class ComputedTextRefusalScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Joining_an_optional_field_is_refused_at_preview_with_the_fallback_as_the_fix()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);

        await ComputedTextScenarios.StageComputedAsync(session, "address_line", "first_name + ', ' + street");
        await session.Page.ClickAsync("[data-testid='pending-preview']");
        var refusal = session.Page.GetByTestId("error-panel");
        await refusal.WaitForAsync();

        var said = await refusal.InnerTextAsync();
        said.ShouldContain("/entities/customers/fields/address_line/computed");
        said.ShouldContain("'street'");
        said.ShouldContain("has(street) ? street : ''");

        session.AssertConsoleClean();
    }
}
