using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>
/// A render fault inside an editor's or a confirm's body is caught there, drawn as a panel with a way back, and told to
/// the owner, instead of escaping past the page's boundary and ending the circuit (final review I5).
/// </summary>
public sealed class ContainedFaultTests
{
    private static readonly RenderFragment<ContainedFault.Caught> _panel
        = caught => builder => builder.AddContent(0, $"panel: {caught.Fault.Message}");

    [Fact]
    public async Task A_child_that_throws_while_rendering_is_drawn_as_the_panel_and_the_owner_is_told()
    {
        var told = new List<bool>();

        var log = new RecordingLog();

        var html = await RenderAsync(ContainedFault.Around(Throwing, _panel, told.Add), log);

        html.ShouldContain("panel: the field editor broke");
        told.ShouldBe([true]);
        log.Logged.ShouldHaveSingleItem().Message.ShouldBe("the field editor broke", "the server log still has it");
    }

    [Fact]
    public async Task A_child_that_renders_is_drawn_as_itself_and_nothing_is_told()
    {
        var told = new List<bool>();

        var html = await RenderAsync(
            ContainedFault.Around(builder => builder.AddContent(0, "the fields"), _panel, told.Add), new RecordingLog());

        html.ShouldBe("the fields");
        told.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("AlvoEditor.razor", "@ContainedFault.Around(ChildContent,")]
    [InlineData("AlvoEditor.razor", "@ContainedFault.Around(ExtraActions,")]
    [InlineData("AlvoConfirm.razor", "@ContainedFault.Around(ChildContent,")]
    public void Every_dialog_wrapper_contains_what_its_owner_hands_it(string wrapper, string containment)
        => File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo.Admin", "Components", "DesignSystem", wrapper))
            .ShouldContain(containment);

    private static void Throwing(RenderTreeBuilder builder)
    {
        builder.OpenComponent<Faulting>(0);
        builder.CloseComponent();
    }

    private static async Task<string> RenderAsync(RenderFragment fragment, RecordingLog log)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IErrorBoundaryLogger>(log);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<Host>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Host.Content)] = fragment }));
            return output.ToHtmlString();
        });
    }

    /// <summary>Draws whatever it is handed, as a page would draw an editor.</summary>
    private sealed class Host : ComponentBase
    {
        [Parameter]
        public RenderFragment? Content { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, Content);
    }

    /// <summary>What the boundary wrote to the server log.</summary>
    private sealed class RecordingLog : IErrorBoundaryLogger
    {
        public List<Exception> Logged { get; } = [];

        public ValueTask LogErrorAsync(Exception exception)
        {
            Logged.Add(exception);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A body with a render bug in it, the way a null facet in a field editor's markup would be.</summary>
    private sealed class Faulting : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
            => throw new InvalidOperationException("the field editor broke");
    }
}
