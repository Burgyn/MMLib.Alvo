using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Admin.Components.Schema;
using MudBlazor.Services;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// A diff too large to align says so where it is drawn, and draws its first lines with the rest on request (batch-B
/// re-review N4): past the cap, 6 000 changed lines read as "everything changed" and were all drawn on every render.
/// </summary>
public sealed class DescriptorDiffTests
{
    [Fact]
    public async Task A_diff_too_large_to_align_says_so_and_draws_its_first_lines()
    {
        var before = string.Concat(Enumerable.Range(0, 3000).Select(index => $"line {index}\n"));
        var html = await RenderAsync(before, before.Replace("line", "row", StringComparison.Ordinal));

        html.ShouldContain("data-testid=\"diff-unaligned\"");
        html.ShouldContain("Too large to align line by line");
        html.ShouldContain("data-testid=\"diff-show-all\"");
        CountOf(html, "<div class=\"a-diff__line").ShouldBe(400, "the rest waits for Show all");
    }

    [Fact]
    public async Task A_small_diff_is_drawn_whole_and_says_nothing_about_alignment()
    {
        var html = await RenderAsync("a\nb\nc", "a\nB\nc");

        html.ShouldNotContain("diff-unaligned");
        html.ShouldNotContain("diff-show-all");
    }

    private static int CountOf(string html, string marker)
        => (html.Length - html.Replace(marker, string.Empty, StringComparison.Ordinal).Length) / marker.Length;

    private static async Task<string> RenderAsync(string before, string after)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<DescriptorDiff>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(DescriptorDiff.Before)] = before, [nameof(DescriptorDiff.After)] = after }));
            return output.ToHtmlString();
        });
    }
}
