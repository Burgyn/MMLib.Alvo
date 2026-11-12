using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using Npgsql;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// A real <c>40P01</c> on the write path is the port's retryable <see cref="AlvoTransientConflictException"/>, never the
/// provider's exception a request layer renders as a 500 (issue 443).
/// </summary>
/// <remarks>
/// Two transaction units each update one row and then the other's, in opposite order. The barrier is a condition — each
/// unit holds its first row lock before either asks for its second — never a timing, so the cycle forms on every run and
/// PostgreSQL's detector fails exactly one side.
/// </remarks>
public sealed class PostgreSqlDeadlockTests : IAsyncLifetime
{
    private const string Counters = "counters";

    private static readonly AlvoContext _caller = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
    };

    private readonly PostgreSqlAlvoDataFixture _fixture = new();
    private PostgreSqlAlvoDataHost? _host;

    /// <summary>Initializes a new instance of the <see cref="PostgreSqlDeadlockTests"/> class.</summary>
    public PostgreSqlDeadlockTests() =>
        Assert.SkipUnless(
            !OperatingSystem.IsWindows(),
            "PostgreSQL Testcontainers requires a Linux Docker daemon; unavailable on Windows-container runners.");

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_units_updating_two_rows_in_opposite_order_end_in_one_commit_and_one_transient_conflict()
    {
        _host = await _fixture.StartAsync(Schema, Descriptor);
        var first = await _host.Data.CreateAsync(Counters, Label("first"), _caller, cancellationToken: Ct);
        var second = await _host.Data.CreateAsync(Counters, Label("second"), _caller, cancellationToken: Ct);
        var units = _host.Services.GetRequiredService<IAlvoTransactions>();
        var firstHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var outcomes = await Task.WhenAll(
            OutcomeAsync(() => CrosswiseAsync(units, Id(first), Id(second), holding: firstHeld, before: Task.CompletedTask, then: secondHeld.Task)),
            OutcomeAsync(() => CrosswiseAsync(units, Id(second), Id(first), holding: secondHeld, before: firstHeld.Task, then: Task.CompletedTask)));

        outcomes.Count(outcome => outcome is null).ShouldBe(1);
        var failure = outcomes.OfType<Exception>().ShouldHaveSingleItem();
        var conflict = failure.ShouldBeOfType<AlvoTransientConflictException>(failure.ToString());
        conflict.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe("40P01");
    }

    /// <summary>
    /// One unit: once <paramref name="before"/> holds its own row, update <paramref name="mine"/>, say so, and once
    /// <paramref name="then"/> holds too, update <paramref name="theirs"/> — the row the other unit already holds.
    /// </summary>
    private static Task CrosswiseAsync(
        IAlvoTransactions units, Guid mine, Guid theirs, TaskCompletionSource holding, Task before, Task then) =>
        units.TransactAsync(_caller, async (unit, ct) =>
        {
            await before.WaitAsync(ct);
            await unit.UpdateAsync(Counters, mine, Label("mine"), cancellationToken: ct);
            holding.SetResult();
            await then.WaitAsync(ct);
            await unit.UpdateAsync(Counters, theirs, Label("theirs"), cancellationToken: ct);
        }, cancellationToken: Ct);

    private static async Task<Exception?> OutcomeAsync(Func<Task> write)
    {
        try
        {
            await write();
            return null;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return failure;
        }
    }

    private static Guid Id(AlvoRecord record) => (Guid)record["id"]!;

    private static Dictionary<string, object?> Label(string label) => new(StringComparer.Ordinal) { ["label"] = label };

    private static SchemaModel Schema => new([new EntitySchema
    {
        Name = Counters,
        Fields =
        [
            new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
            new FieldSchema { Name = "label", Type = SchemaField.String, Required = true },
        ],
    }]);

    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "deadlocks",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Counters] = new()
            {
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["label"] = new() { Type = DescField.String, Required = true },
                },
                Rules = new AccessRules { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" },
            },
        },
    };
}
