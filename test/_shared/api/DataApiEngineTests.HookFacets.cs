using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// A before-hook's <c>mutate</c> value honours the target field's declared facets, and every engine refuses one that
/// does not with the same answer (Ruling V, #308).
/// </summary>
/// <remarks>
/// Engine-sensitive by construction, which is why it lives in this suite: before the fix SQLite (no length
/// enforcement) stored a 2,000-character value in a <c>maxLength: 40</c> field with a 201, while PostgreSQL's
/// <c>varchar(40)</c> refused the same request as an anonymous 500. The answer now is the hook's refusal on both: 403
/// <c>forbidden</c>, naming the hook, the field and the facet, never the value, and nothing stored. Measured on the
/// final patch, so a later hook may repair an earlier overrun (Ruling W); a field the descriptor flags hidden is never
/// named, nor its facet or limit (Ruling X).
/// </remarks>
public abstract partial class DataApiEngineTests
{
    private static readonly TestApiKey _hookWriter = new("hook-writer", ["writer"], ["contacts:read", "contacts:write"]);

    private const string HookSecret = "SECRET-VALUE-0123456789-0123456789-0123456789";

    public static TheoryData<string, string, string, string, string> HookFacetRefusals => new()
    {
        { "copy", HookSecret, "phone_normalized", "max-length", "beforeCreate/0" },
        { "grow", "aaaa", "phone_normalized", "max-length", "beforeCreate/1" },
        { "host", "1", "phone_normalized", "max-length", "beforeCreate/2" },
        { "enum", "SECRET-superadmin", "status", "enum-value", "beforeCreate/3" },
        { "format", "SECRET-not-an-address", "email", "format", "beforeCreate/4" },
    };

    [Theory]
    [MemberData(nameof(HookFacetRefusals))]
    public async Task A_hook_value_outside_its_fields_facets_is_refused_alike_on_every_engine(
        string mode, string text, string field, string facet, string hook)
    {
        await using var world = await StartHookFacetsAsync();

        using var refused = await world.SendAsync(
            HttpMethod.Post, "/api/contacts", _hookWriter, body: HookContact(mode, text));

        await ShouldBeHookFacetRefusalAsync(refused, field, facet, hook);
        (await world.CountRowsAsync("contacts")).ShouldBe(0, "a refused hook must not have stored the row");
    }

    [Fact]
    public async Task A_hook_decimal_outside_the_target_precision_is_refused_alike_on_every_engine()
    {
        await using var world = await StartHookFacetsAsync();
        var body = new JsonObject { ["mode"] = "decimal", ["big"] = 123456.5m };

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/contacts", _hookWriter, body: body);

        await ShouldBeHookFacetRefusalAsync(refused, "amount", "precision", "beforeCreate/5");
        (await world.CountRowsAsync("contacts")).ShouldBe(0);
    }

    [Fact]
    public async Task A_hook_value_inside_its_fields_facets_is_stored()
    {
        await using var world = await StartHookFacetsAsync();

        using var created = await world.SendAsync(
            HttpMethod.Post, "/api/contacts", _hookWriter, body: HookContact("copy", "+421 900"));

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        (await created.ReadJsonObjectAsync())["phone_normalized"]!.GetValue<string>().ShouldBe("+421 900");
    }

    [Fact]
    public async Task A_hook_value_outside_its_fields_facets_refuses_an_update_and_keeps_the_row()
    {
        await using var world = await StartHookFacetsAsync();
        using var created = await world.SendAsync(
            HttpMethod.Post, "/api/contacts", _hookWriter, body: HookContact("none", "kept"));
        var id = (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();

        using var refused = await world.SendAsync(
            HttpMethod.Patch, $"/api/contacts/{id}", _hookWriter, body: HookContact("copy", HookSecret));

        await ShouldBeHookFacetRefusalAsync(refused, "phone_normalized", "max-length", "beforeUpdate/0");
        using var read = await world.SendAsync(HttpMethod.Get, $"/api/contacts/{id}", _hookWriter);
        (await read.ReadJsonObjectAsync())["note"]!.GetValue<string>().ShouldBe("kept");
    }

    [Fact]
    public async Task A_hook_value_outside_its_fields_facets_refuses_an_upsert()
    {
        await using var world = await StartHookFacetsAsync();

        using var refused = await world.SendAsync(
            HttpMethod.Put, $"/api/contacts/{Guid.NewGuid()}", _hookWriter, body: HookContact("copy", HookSecret));

        await ShouldBeHookFacetRefusalAsync(refused, "phone_normalized", "max-length", "beforeCreate/0");
        (await world.CountRowsAsync("contacts")).ShouldBe(0);
    }

    [Fact]
    public async Task A_hook_value_outside_its_fields_facets_refuses_its_row_of_a_batch_and_stores_none()
    {
        await using var world = await StartHookFacetsAsync();
        var rows = new JsonArray(HookContact("copy", "fits"), HookContact("copy", HookSecret));

        using var refused = await world.SendAsync(
            HttpMethod.Post, "/api/contacts/batch", _hookWriter, body: new JsonObject { ["rows"] = rows });

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.ReadTextAsync());
        var text = await refused.ReadTextAsync();
        text.ShouldContain("'max-length'");
        text.ShouldNotContain("SECRET-VALUE");
        (await world.CountRowsAsync("contacts")).ShouldBe(0, "a batch is all-or-nothing");
    }

    [Fact]
    public async Task A_later_hook_that_repairs_an_earlier_overrun_stores_the_repaired_value_on_every_engine()
    {
        await using var world = await StartHookFacetsAsync();
        var body = new JsonObject { ["mode"] = "repair", ["phone"] = "+421 900", ["note"] = HookSecret };

        using var created = await world.SendAsync(HttpMethod.Post, "/api/contacts", _hookWriter, body: body);

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        (await created.ReadJsonObjectAsync())["phone_normalized"]!.GetValue<string>().ShouldBe("+421 900");
    }

    [Fact]
    public async Task A_later_hook_that_overruns_a_value_an_earlier_one_fitted_is_named_in_the_refusal()
    {
        await using var world = await StartHookFacetsAsync();
        var body = new JsonObject { ["mode"] = "late", ["phone"] = "+421 900", ["note"] = HookSecret };

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/contacts", _hookWriter, body: body);

        await ShouldBeHookFacetRefusalAsync(refused, "phone_normalized", "max-length", "beforeCreate/9");
        (await refused.ReadTextAsync()).ShouldNotContain("beforeCreate/8");
        (await world.CountRowsAsync("contacts")).ShouldBe(0);
    }

    [Theory]
    [InlineData("hidden", "secret_code", "beforeCreate/10")]
    [InlineData("role-hidden", "role_code", "beforeCreate/11")]
    public async Task A_refusal_for_a_hidden_target_names_no_field_facet_or_limit_on_every_engine(
        string mode, string field, string hook)
    {
        await using var world = await StartHookFacetsAsync();

        using var refused = await world.SendAsync(
            HttpMethod.Post, "/api/contacts", _hookWriter, body: HookContact(mode, HookSecret));

        await ShouldBeHiddenHookRefusalAsync(refused, field, hook);
        (await world.CountRowsAsync("contacts")).ShouldBe(0);
    }

    [Fact]
    public async Task A_batch_row_refused_for_a_hidden_target_names_no_field_facet_or_limit()
    {
        await using var world = await StartHookFacetsAsync();
        var rows = new JsonArray(HookContact("copy", "fits"), HookContact("hidden", HookSecret));

        using var refused = await world.SendAsync(
            HttpMethod.Post, "/api/contacts/batch", _hookWriter, body: new JsonObject { ["rows"] = rows });

        await ShouldBeHiddenHookRefusalAsync(refused, "secret_code", "beforeCreate/10");
        (await world.CountRowsAsync("contacts")).ShouldBe(0, "a batch is all-or-nothing");
    }

    private static async Task ShouldBeHiddenHookRefusalAsync(HttpResponseMessage refused, string field, string hook)
    {
        var text = await refused.ReadTextAsync();
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, text);
        (await refused.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
        text.ShouldContain($"/entities/contacts/hooks/{hook}");
        text.ShouldNotContain(field);
        text.ShouldNotContain("max-length");
        text.ShouldNotContain("10 characters");
        text.ShouldNotContain("SECRET");
    }

    private static JsonObject HookContact(string mode, string text) => new()
    {
        ["mode"] = mode,
        ["phone"] = text.Length <= 40 ? text : "1",
        ["note"] = text,
    };

    private static async Task ShouldBeHookFacetRefusalAsync(
        HttpResponseMessage refused, string field, string facet, string hook)
    {
        var text = await refused.ReadTextAsync();
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, text);
        (await refused.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
        text.ShouldContain($"/entities/contacts/hooks/{hook}");
        text.ShouldContain($"'{field}'");
        text.ShouldContain($"'{facet}'");
        text.ShouldNotContain("SECRET");
        text.ShouldNotContain("xxxxxxxxxx");
        text.ShouldNotContain("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    }

    private Task<AlvoApiWorld> StartHookFacetsAsync() =>
        AlvoApiWorld.FromDescriptorPathAsync(
            Path.Combine(RepositoryRoot.Find(), "test", "MMLib.Alvo.Api.Tests", "descriptors", "hook-facets.alvo.json"),
            [_hookWriter],
            new AlvoApiWorldSetup(
                MapAlvoProblemDetails: true,
                ConfigureServicesAfterAlvo: services => new HookFacetsBuilder(services)
                    .AddCelFunction("pad", (string phone) => new string('x', 5000), "Answers 5,000 characters.")),
            Engine);

    /// <summary>The builder a host holds; <c>AddCelFunction</c> reads only <see cref="Services"/>.</summary>
    private sealed class HookFacetsBuilder(IServiceCollection services) : IAlvoBuilder
    {
        /// <inheritdoc/>
        public IServiceCollection Services { get; } = services;
    }
}
