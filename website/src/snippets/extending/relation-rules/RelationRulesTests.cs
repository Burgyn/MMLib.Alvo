using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.TestHost;
using Shouldly;
using Xunit;

namespace Projects.Tests;

public sealed class RelationRulesTests
{
    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "projects.alvo.json");

    [Fact]
    public async Task The_snippet_admits_a_member_and_hides_the_project_from_a_stranger()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(builder => builder.FromDescriptor(Descriptor), cancellationToken: ct);
        var (clerk, member, stranger) = (AlvoTestHost.Caller("clerk"), AlvoTestHost.Caller("member"), AlvoTestHost.Caller("member"));
        var project = await alvo.Data.CreateAsync("projects", new Dictionary<string, object?> { ["name"] = "Bridge", ["archived"] = false }, clerk, cancellationToken: ct);
        await alvo.Data.CreateAsync("project_members", new Dictionary<string, object?> { ["project_id"] = project["id"], ["user_id"] = member.User.Value }, clerk, cancellationToken: ct);
        await alvo.Data.CreateAsync("tasks", new Dictionary<string, object?> { ["project_id"] = project["id"], ["title"] = "Survey" }, clerk, cancellationToken: ct);

        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "projects" }, member, ct)).Items.ShouldHaveSingleItem();
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "tasks" }, member, ct)).Items.ShouldHaveSingleItem();
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "projects" }, stranger, ct)).Items.ShouldBeEmpty();
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "tasks" }, stranger, ct)).Items.ShouldBeEmpty("a task is seen through its project");
    }

    [Fact]
    public async Task The_snippet_s_check_refuses_with_its_message()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(builder => builder.FromDescriptor(Descriptor), cancellationToken: ct);
        var product = await alvo.Data.CreateAsync("products", new Dictionary<string, object?> { ["name"] = "Lamp", ["active"] = true, ["stock"] = 2L }, AlvoTestHost.Caller("clerk"), cancellationToken: ct);

        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(() => alvo.Data.CreateAsync(
            "order_lines", new Dictionary<string, object?> { ["product_id"] = product["id"], ["quantity"] = 3L }, AlvoTestHost.Caller("member"), cancellationToken: ct));

        (refusal.Kind, refusal.Fields.Single()).ShouldBe((AlvoConstraintKind.Check, "product_id"));
        refusal.Message.ShouldBe("This product is not available in that quantity.");
    }
}
