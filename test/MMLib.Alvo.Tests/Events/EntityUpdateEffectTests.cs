using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tests.Expressions;
using NSubstitute;

namespace MMLib.Alvo.Tests.Events;

/// <summary>The fail-closed half of <c>entity.update</c>'s tenancy that apply cannot reach: an event that carries no tenant.</summary>
public sealed class EntityUpdateEffectTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_tenant_scoped_target_from_an_event_without_a_tenant_is_dead_record_not_found_and_writes_nothing()
    {
        var ledger = new ScriptedLedger(CommandClaim.Granted("token"));
        var transactions = Substitute.For<IAlvoTransactions>();
        var effect = new EntityUpdateEffect(ledger, transactions, Catalogs(), new ActionValues(TestBudgets.Default()), TimeProvider.System);

        var refusal = await Should.ThrowAsync<ActionRefusedException>(() => effect.ApplyAsync(Action(), Tenantless(), Ct));

        refusal.Reason.ShouldBe(DeliveryErrors.RecordNotFound);
        transactions.ReceivedCalls().ShouldBeEmpty();
        ledger.Calls.ShouldBe(["claim", "release"]);
    }

    [Fact]
    public async Task A_key_already_committed_writes_nothing()
    {
        var ledger = new ScriptedLedger(CommandClaim.Unanswered);
        var transactions = Substitute.For<IAlvoTransactions>();
        var effect = new EntityUpdateEffect(ledger, transactions, Catalogs(), new ActionValues(TestBudgets.Default()), TimeProvider.System);

        await effect.ApplyAsync(Action(), Tenantless(), Ct);

        transactions.ReceivedCalls().ShouldBeEmpty();
        ledger.Calls.ShouldBe(["claim"]);
    }

    [Fact]
    public async Task An_authorization_refusal_from_the_unit_is_dead_write_refused_not_retried()
    {
        var ledger = new ScriptedLedger(CommandClaim.Granted("token"));
        var effect = new EntityUpdateEffect(ledger, new RefusingTransactions(), Catalogs(), new ActionValues(TestBudgets.Default()), TimeProvider.System);

        var refusal = await Should.ThrowAsync<ActionRefusedException>(() => effect.ApplyAsync(Action(), Tenanted(), Ct));

        refusal.Reason.ShouldBe(DeliveryErrors.WriteRefused);
        ledger.Calls.ShouldBe(["claim", "release"]);
    }

    private static CompiledAction Action() =>
        new(new EntityUpdateAction { Entity = "ledger", Payload = new Dictionary<string, ValueOrExpr>() }, new Dictionary<string, AlvoTemplate>(), null, "entity.update")
        {
            Target = "ledger",
        };

    private static AlvoEventContext Tenantless() => ContextAs(AlvoContext.System(null));

    private static AlvoEventContext Tenanted() => ContextAs(AlvoContext.System(TenantId.New()));

    private static AlvoEventContext ContextAs(AlvoContext caller)
    {
        var session = Substitute.For<IAlvoSession>();
        session.Context.Returns(caller);
        var @event = new AlvoEvent
        {
            Id = Guid.CreateVersion7(),
            Source = AlvoEvent.DefaultSource,
            Type = "entity.notes.created",
            Time = DateTimeOffset.UtcNow,
            Subject = "notes/1",
            PartitionKey = "notes:1",
            AuthType = AlvoEventAuthType.ApiKey,
            CorrelationId = "c",
            Data = new AlvoEventData { Record = AlvoRecord.Empty },
        };

        return new AlvoEventContext(@event, "/automation/r/actions#00000000", 1, session, PassThroughSteps.Instance);
    }

    private static IPolicyCatalogProvider Catalogs()
    {
        var descriptor = AlvoDescriptor.Parse("""
            {
              "apiVersion": "alvo.dev/v1",
              "name": "effect",
              "tenancy": { "enabled": true },
              "entities": { "ledger": { "tenancy": "scoped", "fields": { "note": { "type": "string", "maxLength": 20 } } } }
            }
            """);
        var catalogs = Substitute.For<IPolicyCatalogProvider>();
        catalogs.Current.Returns(PolicyCatalog.Build(descriptor, DescriptorToSchemaMapper.Map(descriptor), CelFixtures.Compiler));

        return catalogs;
    }

    /// <summary>Refuses every unit the way the data port refuses a caller it does not authorize.</summary>
    private sealed class RefusingTransactions : IAlvoTransactions
    {
        public Task<T> TransactAsync<T>(
            AlvoContext context, Func<IAlvoTransaction, CancellationToken, Task<T>> work, AlvoTransactionOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new AlvoAuthorizationException();
    }

    /// <summary>Answers every claim with one scripted state and records which calls were made.</summary>
    private sealed class ScriptedLedger(CommandClaim claim) : ICommandLedger
    {
        internal List<string> Calls { get; } = [];

        public Task<CommandClaim> ClaimAsync(CommandKey key, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Calls.Add("claim");
            return Task.FromResult(claim);
        }

        public Task AnswerAsync(CommandKey key, CommandAnswer answer, CancellationToken cancellationToken)
        {
            Calls.Add("answer");
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(CommandKey key, CancellationToken cancellationToken)
        {
            Calls.Add("release");
            return Task.CompletedTask;
        }

        public Task CompleteAsync(
            AlvoFrameworkEnvelope completion, CommandKey? key, CommandAnswer? answer, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> SweepAsync(DateTimeOffset claimedBefore, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
