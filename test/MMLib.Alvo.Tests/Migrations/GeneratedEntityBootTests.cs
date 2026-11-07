using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Tests.Migrations;

/// <summary>The boot's staleness warning: logged for each generated class of the booting project that no longer fits, never a refusal.</summary>
public sealed class GeneratedEntityBootTests
{
    private const string Stale = "GeneratedEntityStale";

    [Fact]
    public async Task A_removed_field_is_reported_at_boot()
    {
        using var world = new BootServiceWorld().BootingFrom(BootServiceWorld.City);
        world.Generated = new GeneratedEntitySource([typeof(GeneratedDepot), typeof(GeneratedDiscontinuedDepot)]);

        await world.BootOrFailAsync();

        var warning = world.Log.Where(entry => entry.EventName == Stale).ShouldHaveSingleItem();
        warning.Level.ShouldBe(Microsoft.Extensions.Logging.LogLevel.Warning);
        warning.Message.ShouldContain(typeof(GeneratedDiscontinuedDepot).FullName!);
        warning.Message.ShouldContain("entity 'depots'");
        warning.Message.ShouldContain("discontinued_on");
    }

    [Fact]
    public async Task A_dashboard_first_boot_reports_it_too()
    {
        using var world = new BootServiceWorld().Applied(BootServiceWorld.City);
        world.Options.Project = BootServiceWorld.Project;
        world.Generated = new GeneratedEntitySource([typeof(GeneratedDiscontinuedDepot)]);

        await world.BootOrFailAsync();

        world.Log.Where(entry => entry.EventName == Stale).ShouldHaveSingleItem().Message.ShouldContain("discontinued_on");
    }

    [Fact]
    public async Task A_fitting_generated_class_logs_nothing()
    {
        using var world = new BootServiceWorld().BootingFrom(BootServiceWorld.City);
        world.Generated = new GeneratedEntitySource([typeof(GeneratedDepot)]);

        await world.BootOrFailAsync();

        world.Log.ShouldNotContain(entry => entry.EventName == Stale);
    }

    [Fact]
    public async Task Another_project_s_classes_are_not_checked()
    {
        using var world = new BootServiceWorld().BootingFrom(BootServiceWorld.City);
        world.Generated = new GeneratedEntitySource([typeof(GeneratedWarehouse)]);

        await world.BootOrFailAsync();

        world.Log.ShouldNotContain(entry => entry.EventName == Stale);
    }

    [Fact]
    public async Task A_check_that_fails_is_logged_and_never_refuses_the_boot()
    {
        using var world = new BootServiceWorld().BootingFrom(BootServiceWorld.City);
        world.Generated = new GeneratedEntitySource(() => throw new BadImageFormatException("broken plugin"));

        await world.BootOrFailAsync();

        world.Log.ShouldContain(entry => entry.EventName == "GeneratedEntityCheckFailed" && entry.Message.Contains("broken plugin"));
    }
}
