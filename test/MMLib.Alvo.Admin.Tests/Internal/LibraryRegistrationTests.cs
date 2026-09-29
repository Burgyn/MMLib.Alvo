using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Admin.Internal;
using MudBlazor;
using MudBlazor.Services;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// Registering the dashboard leaves the component library's process-wide options as the library ships them, so an
/// embedding host's own MudBlazor UI is not moved by it (final review I6); what the dashboard needs it says per message
/// and on its own providers.
/// </summary>
public sealed class LibraryRegistrationTests
{
    [Fact]
    public void Adding_the_dashboard_leaves_the_librarys_snackbar_options_as_shipped()
    {
        using var provider = new ServiceCollection().AddAlvoAdmin().BuildServiceProvider();
        var registered = provider.GetRequiredService<IOptions<SnackbarConfiguration>>().Value;
        var shipped = new SnackbarConfiguration();

        registered.PositionClass.ShouldBe(shipped.PositionClass);
        registered.MaxDisplayedSnackbars.ShouldBe(shipped.MaxDisplayedSnackbars);
        registered.VisibleStateDuration.ShouldBe(shipped.VisibleStateDuration);
        registered.ShowCloseIcon.ShouldBe(shipped.ShowCloseIcon);
        registered.PreventDuplicates.ShouldBe(shipped.PreventDuplicates);
    }

    [Fact]
    public void Adding_the_dashboard_leaves_the_librarys_breakpoints_as_shipped()
    {
        using var provider = new ServiceCollection().AddAlvoAdmin().BuildServiceProvider();

        provider.GetRequiredService<IOptions<ResizeOptions>>().Value.BreakpointDefinitions
            .ShouldBe(new ResizeOptions().BreakpointDefinitions);
    }

    [Fact]
    public void A_confirmation_shows_at_most_two_the_newest_and_never_a_duplicate()
    {
        using var snackbar = new SnackbarService(new FixedNavigation(), TimeProvider.System);

        snackbar.Confirm("Saved to the working copy");
        snackbar.Confirm("Saved to the working copy");
        snackbar.Confirm("Record deleted");
        snackbar.Confirm("Applied as revision 12");

        snackbar.ShownSnackbars.Select(shown => shown.Message).ShouldBe(["Record deleted", "Applied as revision 12"]);
    }

    [Fact]
    public void A_confirmation_carries_its_own_duration_close_and_duplicate_rule()
    {
        var options = new SnackbarOptions(Severity.Success, new SnackbarConfiguration
        {
            VisibleStateDuration = 60_000,
            ShowCloseIcon = false,
        });

        AdminSnackbar.Configure(options);

        options.VisibleStateDuration.ShouldBe(5000);
        options.ShowCloseIcon.ShouldBeTrue();
        options.DuplicatesBehavior.ShouldBe(SnackbarDuplicatesBehavior.Prevent);
    }

    [Fact]
    public void The_dashboard_positions_its_snackbars_by_a_class_of_its_own()
    {
        var layout = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(), "src", "MMLib.Alvo.Admin", "Components", "Shell", "AdminLayout.razor"));
        var css = File.ReadAllText(Stylesheet.AlvoCssPath);

        layout.ShouldContain("<MudSnackbarProvider Class=\"a-snackbars\" role=\"status\" aria-live=\"polite\"");
        css.ShouldContain(".a-snackbars {");
    }

    private sealed class FixedNavigation : NavigationManager
    {
        public FixedNavigation() => Initialize("http://alvo.test/", "http://alvo.test/admin");
    }
}
