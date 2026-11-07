using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// Issue 481: a host writing through the data port is measured against the field facets an HTTP body is — enum
/// <c>values</c>, <c>maxLength</c>, decimal precision — once, in the core, before the driver; so SQLite (which enforces no
/// length and no enum) and PostgreSQL (whose <c>varchar(n)</c> and <c>numeric(p, s)</c> refuse on their own) give one
/// answer. The SQLite leg is in <c>MMLib.Alvo.TestHost.Tests</c>, the PostgreSQL leg in
/// <c>MMLib.Alvo.Data.PostgreSql.Tests.Integration</c>.
/// </summary>
public abstract class PortFacetFacts
{
    internal const string Tickets = "tickets";
    internal const string FacetDesk = "facet-desk.alvo.json";

    private static readonly DateTimeOffset _now = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected FakeTimeProvider Time { get; } = new(_now);

    [Fact]
    public async Task A_create_with_a_value_outside_the_enum_is_refused_at_the_field_and_stores_nothing()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync();

        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(() =>
            alvo.Data.CreateAsync(Tickets, Ticket(priority: "urgent"), Caller, cancellationToken: Ct));

        (refused.Kind, refused.Fields.ShouldHaveSingleItem()).ShouldBe((AlvoConstraintKind.Check, "priority"));
        (await CountAsync(alvo)).ShouldBe(0);
    }

    [Fact]
    public async Task A_create_with_a_string_past_max_length_is_refused_at_the_field_and_stores_nothing()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync();

        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(() =>
            alvo.Data.CreateAsync(Tickets, Ticket(title: "eleven char"), Caller, cancellationToken: Ct));

        (refused.Kind, refused.Fields.ShouldHaveSingleItem()).ShouldBe((AlvoConstraintKind.Check, "title"));
        refused.Message.ShouldContain("10 characters");
        (await CountAsync(alvo)).ShouldBe(0);
    }

    [Fact]
    public async Task A_create_with_a_decimal_past_its_precision_is_refused_at_the_field()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync();

        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(() =>
            alvo.Data.CreateAsync(Tickets, Ticket(amount: 12345.67m), Caller, cancellationToken: Ct));

        (refused.Kind, refused.Fields.ShouldHaveSingleItem()).ShouldBe((AlvoConstraintKind.Check, "amount"));
        (await CountAsync(alvo)).ShouldBe(0);
    }

    [Fact]
    public async Task A_valid_create_and_a_valid_patch_are_stored()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync();

        var id = await CreatedAsync(alvo);
        var updated = await alvo.Data.UpdateAsync(Tickets, id, new Dictionary<string, object?> { ["priority"] = "low", ["title"] = "ten chars!" }, Caller, cancellationToken: Ct);

        (updated["priority"], updated["title"]).ShouldBe(("low", "ten chars!"));
    }

    [Fact]
    public async Task A_patch_with_a_value_outside_the_enum_is_refused_and_leaves_the_row_unchanged()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync();
        var id = await CreatedAsync(alvo);

        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(() =>
            alvo.Data.UpdateAsync(Tickets, id, new Dictionary<string, object?> { ["priority"] = "urgent" }, Caller, cancellationToken: Ct));

        (refused.Kind, refused.Fields.ShouldHaveSingleItem()).ShouldBe((AlvoConstraintKind.Check, "priority"));
        (await alvo.Data.GetAsync(Tickets, id, Caller, Ct))!["priority"].ShouldBe("high");
    }

    [Fact]
    public async Task A_patch_with_a_string_past_max_length_is_refused_and_leaves_the_row_unchanged()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync();
        var id = await CreatedAsync(alvo);

        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(() =>
            alvo.Data.UpdateAsync(Tickets, id, new Dictionary<string, object?> { ["title"] = "eleven char" }, Caller, cancellationToken: Ct));

        (refused.Kind, refused.Fields.ShouldHaveSingleItem()).ShouldBe((AlvoConstraintKind.Check, "title"));
        (await alvo.Data.GetAsync(Tickets, id, Caller, Ct))!["title"].ShouldBe("ticket");
    }

    [Fact]
    public async Task A_replace_with_a_string_past_max_length_is_refused()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync();
        var id = await CreatedAsync(alvo);

        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(() =>
            alvo.Data.ReplaceAsync(Tickets, id, Ticket(title: "eleven char"), Caller, cancellationToken: Ct));

        (refused.Kind, refused.Fields.ShouldHaveSingleItem()).ShouldBe((AlvoConstraintKind.Check, "title"));
        (await alvo.Data.GetAsync(Tickets, id, Caller, Ct))!["title"].ShouldBe("ticket");
    }

    [Fact]
    public async Task A_refusal_for_a_hidden_field_names_neither_the_field_nor_its_facet()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync();
        var ticket = Ticket();
        ticket["secret"] = "too long";

        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(() =>
            alvo.Data.CreateAsync(Tickets, ticket, Caller, cancellationToken: Ct));

        refused.Kind.ShouldBe(AlvoConstraintKind.Check);
        refused.Fields.ShouldBeEmpty();
        refused.Message.ShouldNotContain("5");
        (await CountAsync(alvo)).ShouldBe(0);
    }

    [Fact]
    public async Task A_batch_create_row_outside_the_enum_is_refused_by_its_index_with_the_facet_code()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync();

        var result = await alvo.Data.CreateManyAsync(Tickets, [Ticket(), Ticket(priority: "urgent")], Caller, cancellationToken: Ct);

        var refusal = result.Refusals.ShouldHaveSingleItem();
        (refusal.Index, refusal.Code, result.Affected).ShouldBe((1, "enum-value", 0));
        (await CountAsync(alvo)).ShouldBe(0);
    }

    [Fact]
    public async Task A_batch_patch_row_past_max_length_is_refused_by_its_index_with_the_facet_code()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync();
        var (first, second) = (await CreatedAsync(alvo), await CreatedAsync(alvo));

        var result = await alvo.Data.UpdateManyAsync(
            Tickets,
            [new AlvoRowPatch(first, new Dictionary<string, object?> { ["title"] = "fine" }), new AlvoRowPatch(second, new Dictionary<string, object?> { ["title"] = "eleven char" })],
            Caller,
            cancellationToken: Ct);

        var refusal = result.Refusals.ShouldHaveSingleItem();
        (refusal.Index, refusal.Code, result.Affected).ShouldBe((1, "max-length", 0));
        (await alvo.Data.GetAsync(Tickets, first, Caller, Ct))!["title"].ShouldBe("ticket");
    }

    [Fact]
    public async Task A_write_inside_a_transaction_unit_is_measured_too()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync();

        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(() =>
            alvo.Services.GetRequiredService<IAlvoTransactions>().TransactAsync(Caller, async (unit, ct) =>
            {
                await unit.CreateAsync(Tickets, Ticket(), ct);
                await unit.CreateAsync(Tickets, Ticket(priority: "urgent"), ct);
            }, cancellationToken: Ct));

        (refused.Kind, refused.Fields.ShouldHaveSingleItem()).ShouldBe((AlvoConstraintKind.Check, "priority"));
        (await CountAsync(alvo)).ShouldBe(0);
    }

    /// <summary>The core's own runner measures a host <c>Set</c> itself and fails the write as <c>invalid-set</c> (the documented shape).</summary>
    [Fact]
    public async Task A_host_before_write_setting_a_value_past_max_length_fails_the_write_and_stores_nothing()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync(alvo => alvo.Entity(Tickets, b => b.BeforeWrite("stretch", (BeforeWrite<AlvoRecord> write) =>
        {
            write.Set("title", "eleven char");
            return ValueTask.CompletedTask;
        })));

        (await Should.ThrowAsync<InvalidOperationException>(() => alvo.Data.CreateAsync(Tickets, Ticket(), Caller, cancellationToken: Ct)))
            .Message.ShouldContain("invalid-set");
        (await CountAsync(alvo)).ShouldBe(0);
    }

    /// <summary>
    /// <see cref="IWriteExtensionRunner"/> is public, so a host can replace it; the port measures whatever patch it returns, so host
    /// code cannot store through a runner what it cannot store through <see cref="IAlvoData"/>.
    /// </summary>
    [Fact]
    public async Task A_replaced_runner_s_create_patch_past_max_length_is_refused_at_the_field()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync(UnmeasuredRunner);

        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(() => alvo.Data.CreateAsync(Tickets, Ticket(priority: "low"), Caller, cancellationToken: Ct));

        (refused.Kind, refused.Fields.ShouldHaveSingleItem()).ShouldBe((AlvoConstraintKind.Check, "title"));
        (await CountAsync(alvo)).ShouldBe(0);
    }

    [Fact]
    public async Task A_replaced_runner_s_update_patch_past_max_length_is_refused_and_leaves_the_row_unchanged()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync(UnmeasuredRunner);
        var id = await CreatedAsync(alvo);

        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(() =>
            alvo.Data.UpdateAsync(Tickets, id, new Dictionary<string, object?> { ["priority"] = "low" }, Caller, cancellationToken: Ct));

        (refused.Kind, refused.Fields.ShouldHaveSingleItem()).ShouldBe((AlvoConstraintKind.Check, "title"));
        (await alvo.Data.GetAsync(Tickets, id, Caller, Ct))!["title"].ShouldBe("ticket");
    }

    [Fact]
    public async Task A_replaced_runner_s_batch_patch_past_max_length_is_refused_by_its_index()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync(UnmeasuredRunner);

        var result = await alvo.Data.CreateManyAsync(Tickets, [Ticket(), Ticket(priority: "low")], Caller, cancellationToken: Ct);

        var refusal = result.Refusals.ShouldHaveSingleItem();
        (refusal.Index, refusal.Code, result.Affected).ShouldBe((1, "max-length", 0));
    }

    protected abstract Task<Action<IAlvoBuilder>> DatabaseAsync();

    /// <summary>Skips the fact where the deriving engine cannot run; every fact calls it first, before any assertion can catch the skip.</summary>
    protected virtual void EnsureEngineAvailable()
    {
    }

    private static AlvoContext Caller { get; } = AlvoTestHost.Caller("staff");

    private static void UnmeasuredRunner(IAlvoBuilder alvo) =>
        alvo.Services.Replace(ServiceDescriptor.Singleton<IWriteExtensionRunner>(new UnmeasuringRunner()));

    private async Task<AlvoTestHost> StartAsync(Action<IAlvoBuilder>? configure = null)
    {
        var use = await DatabaseAsync();
        return await AlvoTestHost.StartAsync(
            alvo =>
            {
                use(alvo);
                alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", FacetDesk));
                configure?.Invoke(alvo);
            },
            Time,
            cancellationToken: Ct);
    }

    private static async Task<Guid> CreatedAsync(AlvoTestHost alvo) =>
        (Guid)(await alvo.Data.CreateAsync(Tickets, Ticket(), Caller, cancellationToken: Ct))["id"]!;

    private static async Task<int> CountAsync(AlvoTestHost alvo) =>
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = Tickets }, Caller, Ct)).Items.Count;

    /// <summary>A host's own runner that patches a write storing priority <c>low</c> with a title past its <c>maxLength</c>, measuring nothing.</summary>
    private sealed class UnmeasuringRunner : IWriteExtensionRunner
    {
        public bool Validates(string entity, AlvoWriteOperation operation) => false;

        public bool RunsInTransaction(string entity, AlvoWriteOperation operation) => operation != AlvoWriteOperation.Delete;

        public Task ValidateAsync(WriteInvocation invocation, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyDictionary<string, object?>> BeforeWriteAsync(WriteInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(invocation.New?.Values.GetValueOrDefault("priority") is "low"
                ? new Dictionary<string, object?> { ["title"] = "eleven char" }
                : new Dictionary<string, object?>());

        public Task<string?> AfterWriteAsync(WriteInvocation invocation, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private static Dictionary<string, object?> Ticket(string title = "ticket", string priority = "high", decimal amount = 10.5m) => new()
    {
        ["title"] = title,
        ["priority"] = priority,
        ["amount"] = amount,
    };
}
