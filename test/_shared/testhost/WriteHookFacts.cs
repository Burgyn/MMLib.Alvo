using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Management;
using System.Collections.Concurrent;
using System.Data.Common;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>A scoped service whose instance tells two resolutions of one scope from two scopes.</summary>
public sealed class ScopeProbe
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// <summary>
/// Host write hooks over the real pipeline (spec 2026-10-14 §7 HX-09 acceptance (1)–(13)), on whichever engine the deriving class
/// supplies — the SQLite leg in <c>MMLib.Alvo.TestHost.Tests</c> (a temp file, so a held write lock would block), the PostgreSQL
/// leg in <c>MMLib.Alvo.Data.PostgreSql.Tests.Integration</c>.
/// </summary>
/// <remarks>Every fact that fails a write asserts no row and no outbox row, not only the exception (Review Focus 0).</remarks>
public abstract class WriteHookFacts
{
    private const string Bookings = "bookings";
    private static readonly TimeSpan _immediately = TimeSpan.FromSeconds(10);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, AlvoWriteRejectionKind, string> Stages => new()
    {
        { "validate", AlvoWriteRejectionKind.Invalid, "/title" },
        { "beforeWrite", AlvoWriteRejectionKind.Conflict, "/title" },
        { "afterWrite", AlvoWriteRejectionKind.Forbidden, "" },
    };

    [Theory]
    [MemberData(nameof(Stages))]
    public async Task A_rejection_at_each_write_stage_leaves_no_row_no_outbox_row_and_its_kind_and_pointer(string stage, AlvoWriteRejectionKind kind, string at)
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => Rejecting(b, stage)));
        var clerk = Clerk();

        var rejection = await Should.ThrowAsync<AlvoWriteRejectedException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "refused"), clerk, cancellationToken: Ct));

        (rejection.Kind, rejection.Pointer, rejection.ExtensionId).ShouldBe((kind, at, IdOf(stage)));
        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(0);
        (await WriteHookRows.CountAsync(database.Connect, "alvo_outbox")).ShouldBe(0);
    }

    [Theory]
    [InlineData("beforeDelete")]
    [InlineData("afterDelete")]
    public async Task A_rejection_at_each_delete_stage_keeps_the_row_and_emits_nothing(string stage)
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => Rejecting(b, stage)));
        var clerk = Clerk();
        var created = await alvo.Data.CreateAsync(Bookings, Booking(clerk, "kept"), clerk, cancellationToken: Ct);
        var outbox = await WriteHookRows.CountAsync(database.Connect, "alvo_outbox");

        (await Should.ThrowAsync<AlvoWriteRejectedException>(() => alvo.Data.DeleteAsync(Bookings, (Guid)created["id"]!, clerk, cancellationToken: Ct))).Kind
            .ShouldBe(AlvoWriteRejectionKind.Conflict);

        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(1);
        (await WriteHookRows.CountAsync(database.Connect, "alvo_outbox")).ShouldBe(outbox);
    }

    [Fact]
    public async Task A_set_of_an_undeclared_enum_value_is_invalid_set_and_writes_nothing()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.BeforeWrite("promote", (BeforeWrite<AlvoRecord> write) =>
        {
            write.Set("status", "superadmin");
            return ValueTask.CompletedTask;
        })));
        var clerk = Clerk();

        (await Should.ThrowAsync<InvalidOperationException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "x"), clerk, cancellationToken: Ct))).Message
            .ShouldContain("invalid-set");
        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(0);
    }

    [Fact]
    public async Task A_rejection_on_a_hidden_field_carries_no_pointer()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.BeforeWrite("score-floor", (BeforeWrite<AlvoRecord> write) =>
        {
            write.Invalid("too-low", "Below the bar.", "score");
            return ValueTask.CompletedTask;
        })));
        var clerk = Clerk();

        (await Should.ThrowAsync<AlvoWriteRejectedException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "x"), clerk, cancellationToken: Ct))).Pointer
            .ShouldBe(string.Empty);
    }

    [Fact]
    public async Task An_after_write_sees_the_id_the_defaults_and_its_child_s_rollup_reaches_the_event_and_the_row()
    {
        var database = await DatabaseAsync();
        var saved = new ConcurrentQueue<AlvoRecord>();
        var events = new ConcurrentQueue<AlvoEvent>();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b
            .AfterWrite("first-line", async (AfterWrite<AlvoRecord> write, CancellationToken ct) =>
            {
                saved.Enqueue(write.Saved);
                await write.Transaction.CreateAsync("booking_lines", new Dictionary<string, object?>
                {
                    ["booking_id"] = write.Saved["id"],
                    ["amount"] = 5L,
                    ["tenant_id"] = write.Caller.Tenant!.Value.Value,
                }, ct);
            })
            .OnCreated("seen", (AlvoCreated<AlvoRecord> _, AlvoEventContext context) => events.Enqueue(context.Event))));
        var clerk = Clerk();

        var created = await alvo.Data.CreateAsync(Bookings, Booking(clerk, "with a line"), clerk, cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);

        var row = saved.ShouldHaveSingleItem();
        (row["id"], row["status"]).ShouldBe((created["id"], "open"));
        created["lines_total"].ShouldBe(5L, "the echo carries the rollup the hook's child moved (stage 7b)");
        events.ShouldHaveSingleItem().Data.Record!["lines_total"].ShouldBe(5L);
        alvo.Hooks.AssertRan("first-line", times: 1);
    }

    [Fact]
    public async Task An_after_write_that_rejects_rolls_its_child_back_too()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.AfterWrite("undo", async (AfterWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            await write.Transaction.CreateAsync("booking_lines", new Dictionary<string, object?>
            {
                ["booking_id"] = write.Saved["id"],
                ["amount"] = 5L,
                ["tenant_id"] = write.Caller.Tenant!.Value.Value,
            }, ct);
            write.Conflict("no-lines-today", "No lines today.");
        })));
        var clerk = Clerk();

        await Should.ThrowAsync<AlvoWriteRejectedException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "undone"), clerk, cancellationToken: Ct));

        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(0);
        (await WriteHookRows.CountAsync(database.Connect, "booking_lines")).ShouldBe(0);
        alvo.Hooks.AssertRan("undo", times: 1);
    }

    [Fact]
    public async Task A_hook_that_catches_a_self_write_refusal_still_rolls_back_with_reason_self_write()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.AfterWrite("echo", async (AfterWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            try
            {
                await write.Transaction.UpdateAsync(Bookings, (Guid)write.Saved["id"]!, new Dictionary<string, object?> { ["title"] = "again" }, cancellationToken: ct);
            }
            catch (InvalidOperationException)
            {
            }
        })));
        var clerk = Clerk();

        (await Should.ThrowAsync<InvalidOperationException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "self"), clerk, cancellationToken: Ct))).Message
            .ShouldContain("self-write");
        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(0);
        (await WriteHookRows.CountAsync(database.Connect, "alvo_outbox")).ShouldBe(0);
    }

    [Fact]
    public async Task A_hook_that_catches_its_timeout_still_rolls_back_with_reason_timeout()
    {
        var database = await DatabaseAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.BeforeWrite("slow", async (BeforeWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            await write.Transaction.CreateAsync("audits", Audit(write.Caller, "before the overrun"), ct);
            clock.Advance(TimeSpan.FromMilliseconds(300));
            try
            {
                await write.Transaction.GetAsync(Bookings, Guid.NewGuid(), ct);
            }
            catch (OperationCanceledException)
            {
            }
        })), clock);
        var clerk = Clerk();

        (await Should.ThrowAsync<InvalidOperationException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "slow"), clerk, cancellationToken: Ct))).Message
            .ShouldContain("timeout");
        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(0);
        (await WriteHookRows.CountAsync(database.Connect, "audits")).ShouldBe(0);
    }

    [Fact]
    public async Task A_hook_that_catches_its_budget_refusal_still_rolls_back_with_reason_budget()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.AfterWrite("greedy", async (AfterWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            try
            {
                for (var call = 0; call < 51; call++)
                {
                    await write.Transaction.CreateAsync("audits", Audit(write.Caller, $"audit {call}"), ct);
                }
            }
            catch (AlvoBudgetExceededException)
            {
            }
        })));
        var clerk = Clerk();

        (await Should.ThrowAsync<InvalidOperationException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "greedy"), clerk, cancellationToken: Ct))).Message
            .ShouldContain("budget");
        (await WriteHookRows.CountAsync(database.Connect, "audits")).ShouldBe(0);
        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(0);
    }

    [Fact]
    public async Task A_batch_validate_hook_runs_once_per_row_with_no_transaction_open()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.Validate("probe", async (ValidateWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            await write.Data.CreateAsync("audits", Audit(write.Caller, $"checked {write.New["title"]}"), ct).WaitAsync(_immediately, ct);
            await AssertNoOpenTransactionAsync(database);
            if ((string?)write.New["title"] == "bad")
            {
                write.Invalid("bad-title", "Bad title.", "title");
            }
        })));
        var clerk = Clerk();

        var result = await alvo.Data.CreateManyAsync(Bookings, [Booking(clerk, "good"), Booking(clerk, "bad")], clerk, cancellationToken: Ct);

        result.Succeeded.ShouldBeFalse();
        result.Refusals.ShouldHaveSingleItem().ShouldSatisfyAllConditions(refusal => refusal.Index.ShouldBe(1), refusal => refusal.Code.ShouldBe("bad-title"));
        (await WriteHookRows.CountAsync(database.Connect, "audits")).ShouldBe(2, "each row's check committed on its own, outside any transaction");
        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(0);
    }

    [Fact]
    public async Task A_batch_row_s_hook_writing_a_sibling_row_is_a_self_write()
    {
        var database = await DatabaseAsync();
        var ids = new Guid[2];
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.BeforeWrite("sibling", async (BeforeWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            var sibling = ids.First(id => id != (Guid)write.New["id"]!);
            await write.Transaction.UpdateAsync(Bookings, sibling, new Dictionary<string, object?> { ["title"] = "touched" }, cancellationToken: ct);
        }, when: "changed(title)")));
        var clerk = Clerk();
        ids[0] = (Guid)(await alvo.Data.CreateAsync(Bookings, Booking(clerk, "first"), clerk, cancellationToken: Ct))["id"]!;
        ids[1] = (Guid)(await alvo.Data.CreateAsync(Bookings, Booking(clerk, "second"), clerk, cancellationToken: Ct))["id"]!;

        (await Should.ThrowAsync<InvalidOperationException>(() => alvo.Data.UpdateManyAsync(
            Bookings,
            [new AlvoRowPatch(ids[0], new Dictionary<string, object?> { ["title"] = "first, renamed" }), new AlvoRowPatch(ids[1], new Dictionary<string, object?> { ["title"] = "second, renamed" })],
            clerk,
            cancellationToken: Ct))).Message.ShouldContain("self-write");
        (await WriteHookRows.CountAsync(database.Connect, Bookings, "title IN ('first', 'second')")).ShouldBe(2);
    }

    [Fact]
    public async Task An_after_write_whose_cascade_removes_its_own_row_fails_with_row_vanished_and_emits_nothing()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity("booking_lines", lines => lines.AfterWrite("drop-parent", (AfterWrite<AlvoRecord> write, CancellationToken ct) =>
            write.Transaction.DeleteAsync(Bookings, (Guid)write.Saved["booking_id"]!, cancellationToken: ct))));
        var clerk = Clerk();
        var booking = await alvo.Data.CreateAsync(Bookings, Booking(clerk, "parent"), clerk, cancellationToken: Ct);
        var outbox = await WriteHookRows.CountAsync(database.Connect, "alvo_outbox");

        (await Should.ThrowAsync<InvalidOperationException>(() => alvo.Data.CreateAsync("booking_lines", new Dictionary<string, object?>
        {
            ["booking_id"] = booking["id"],
            ["amount"] = 1L,
            ["tenant_id"] = clerk.Tenant!.Value.Value,
        }, clerk, cancellationToken: Ct))).Message.ShouldContain("row-vanished");
        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(1);
        (await WriteHookRows.CountAsync(database.Connect, "booking_lines")).ShouldBe(0);
        (await WriteHookRows.CountAsync(database.Connect, "alvo_outbox")).ShouldBe(outbox);
    }

    [Fact]
    public async Task Validate_never_runs_on_an_update_the_payload_guard_refuses_nor_on_a_row_the_caller_cannot_reach()
    {
        var database = await DatabaseAsync();
        var runs = 0;
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.Validate("count", (ValidateWrite<AlvoRecord> _) =>
        {
            Interlocked.Increment(ref runs);
            return ValueTask.CompletedTask;
        })));
        var clerk = Clerk();
        var created = await alvo.Data.CreateAsync(Bookings, Booking(clerk, "kept"), clerk, cancellationToken: Ct);

        await Should.ThrowAsync<AlvoAuthorizationException>(() => alvo.Data.UpdateAsync(
            Bookings, (Guid)created["id"]!, new Dictionary<string, object?> { ["lines_total"] = 9L }, clerk, cancellationToken: Ct));
        await Should.ThrowAsync<AlvoRecordNotFoundException>(() => alvo.Data.UpdateAsync(
            Bookings, Guid.NewGuid(), new Dictionary<string, object?> { ["title"] = "nowhere" }, clerk, cancellationToken: Ct));

        runs.ShouldBe(1, "only the create was validated");
    }

    [Fact]
    public async Task A_validate_hook_never_runs_on_an_idempotent_replay()
    {
        var database = await DatabaseAsync();
        var runs = 0;
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.Validate("count", (ValidateWrite<AlvoRecord> _) =>
        {
            Interlocked.Increment(ref runs);
            return ValueTask.CompletedTask;
        })));
        var clerk = Clerk();
        var key = new AlvoIdempotency("book-1", "fingerprint-1");

        await alvo.Data.CreateAsync(Bookings, Booking(clerk, "once"), clerk, key, Ct);
        await alvo.Data.CreateAsync(Bookings, Booking(clerk, "once"), clerk, key, Ct);

        runs.ShouldBe(1);
    }

    [Fact]
    public async Task A_write_inside_a_unit_never_runs_validate()
    {
        var database = await DatabaseAsync();
        var runs = 0;
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.Validate("count", (ValidateWrite<AlvoRecord> _) =>
        {
            Interlocked.Increment(ref runs);
            return ValueTask.CompletedTask;
        })));
        var clerk = Clerk();

        await alvo.Services.GetRequiredService<IAlvoTransactions>().TransactAsync(clerk, (unit, ct) => unit.CreateAsync(Bookings, Booking(clerk, "inside"), ct), cancellationToken: Ct);

        runs.ShouldBe(0);
    }

    [Fact]
    public async Task A_before_write_hook_writing_its_own_entity_as_the_system_recursively_is_refused_508()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.BeforeWrite("echo", async (BeforeWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            await write.Transaction.AsSystem().CreateAsync(Bookings, Booking(write.Caller, "echo"), ct);
        })));
        var clerk = Clerk();

        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "start"), clerk, cancellationToken: Ct));

        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(0);
        (await WriteHookRows.CountAsync(database.Connect, "alvo_outbox")).ShouldBe(0);
    }

    [Fact]
    public async Task A_guard_reads_reference_data_the_caller_cannot_as_the_system_and_no_other_tenant_s()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.BeforeWrite("title-limit", async (BeforeWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            var limits = await write.Transaction.AsSystem().QueryAsync(new AlvoQuery { Entity = "limits" }, ct);
            if (limits.Items.Count == 0 || ((string)write.New["title"]!).Length > (long)limits.Items[0]["max_title"]!)
            {
                write.Invalid("title-too-long", "The title is longer than this desk allows.", "title");
            }
        })));
        var clerk = Clerk();
        var otherTenant = AlvoContext.System(new TenantId(Guid.NewGuid()));
        await alvo.Data.CreateAsync("limits", new Dictionary<string, object?> { ["max_title"] = 100L, ["tenant_id"] = otherTenant.Tenant!.Value.Value }, otherTenant, cancellationToken: Ct);

        (await Should.ThrowAsync<AlvoWriteRejectedException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "short"), clerk, cancellationToken: Ct))).Code
            .ShouldBe("title-too-long", "another tenant's limit is not this tenant's");

        var system = AlvoContext.System(clerk.Tenant);
        await alvo.Data.CreateAsync("limits", new Dictionary<string, object?> { ["max_title"] = 10L, ["tenant_id"] = clerk.Tenant!.Value.Value }, system, cancellationToken: Ct);
        await alvo.Data.CreateAsync(Bookings, Booking(clerk, "short"), clerk, cancellationToken: Ct);
        await Should.ThrowAsync<AlvoWriteRejectedException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "far longer than ten"), clerk, cancellationToken: Ct));
    }

    [Fact]
    public async Task Suppressing_host_hooks_needs_the_system_and_skips_them_with_one_audit_row()
    {
        var database = await DatabaseAsync();
        var audit = new AuditCapture();
        var runs = 0;
        await using var alvo = await StartAsync(database, alvo =>
        {
            alvo.Services.AddSingleton<ILoggerProvider>(audit);
            alvo.Entity(Bookings, b => b.BeforeWrite("count", (BeforeWrite<AlvoRecord> _) =>
            {
                Interlocked.Increment(ref runs);
                return ValueTask.CompletedTask;
            }));
        });
        var clerk = Clerk();
        var transactions = alvo.Services.GetRequiredService<IAlvoTransactions>();
        var suppress = new AlvoTransactionOptions { SuppressHostHooks = true, Reason = "nightly repair" };

        await Should.ThrowAsync<ArgumentException>(() => transactions.TransactAsync(clerk, (_, _) => Task.CompletedTask, suppress, Ct));
        var system = AlvoContext.System(clerk.Tenant);
        await transactions.TransactAsync(system, async (unit, ct) =>
        {
            await unit.CreateAsync(Bookings, Booking(clerk, "repaired one"), ct);
            await unit.CreateAsync(Bookings, Booking(clerk, "repaired two"), ct);
        }, suppress, Ct);

        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(2, "the suppressed unit ran its writes");
        runs.ShouldBe(0);
        (await WriteHookRows.CountAsync(database.Connect, "alvo_operations_log", "operation = 'suppress-host-hooks'")).ShouldBe(1);
        audit.Lines.ShouldHaveSingleItem().ShouldContain("nightly repair");
    }

    [Fact]
    public async Task A_rejection_is_logged_after_the_rollback_with_ids_and_its_code()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => Rejecting(b, "beforeWrite")));
        var clerk = Clerk();

        await Should.ThrowAsync<AlvoWriteRejectedException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "logged"), clerk, cancellationToken: Ct));

        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(0);
        (await WriteHookRows.CountAsync(database.Connect, "alvo_hook_rejections", "extension = 'before-write' AND code = 'rejected' AND kind = 'conflict' AND entity = 'bookings'"))
            .ShouldBe(1);
    }

    /// <summary>
    /// Acceptance (13), read as Ruling 57 states: "a request-scoped service" is any scope other than the unit's — here a scope
    /// the test opens as an HTTP request would; the hooks' scope is the unit's, never the caller's.
    /// </summary>
    [Fact]
    public async Task Two_hooks_of_one_unit_share_its_scope_and_a_request_scope_is_another()
    {
        var database = await DatabaseAsync();
        var seen = new ConcurrentQueue<Guid>();
        await using var alvo = await StartAsync(database, alvo =>
        {
            alvo.Services.AddScoped<ScopeProbe>();
            alvo.Entity(Bookings, b => b
                .BeforeWrite("before-probe", (BeforeWrite<AlvoRecord> _, ScopeProbe probe) => { seen.Enqueue(probe.Id); return ValueTask.CompletedTask; })
                .AfterWrite("after-probe", (AfterWrite<AlvoRecord> _, ScopeProbe probe) => { seen.Enqueue(probe.Id); return ValueTask.CompletedTask; }));
        });
        var clerk = Clerk();

        await alvo.Data.CreateAsync(Bookings, Booking(clerk, "scoped"), clerk, cancellationToken: Ct);
        await using var request = alvo.Services.CreateAsyncScope();

        seen.Count.ShouldBe(2, "both hooks ran");
        seen.Distinct().ShouldHaveSingleItem().ShouldNotBe(request.ServiceProvider.GetRequiredService<ScopeProbe>().Id);
    }

    [Fact]
    public async Task Switching_a_validate_hook_off_is_audited_and_the_next_write_skips_it()
    {
        var database = await DatabaseAsync();
        var audit = new AuditCapture();
        var runs = 0;
        await using var alvo = await StartAsync(database, alvo =>
        {
            alvo.Services.AddSingleton<ILoggerProvider>(audit);
            alvo.Entity(Bookings, b => b.Validate("vat-check", (ValidateWrite<AlvoRecord> _) =>
            {
                Interlocked.Increment(ref runs);
                return ValueTask.CompletedTask;
            }));
        });
        var clerk = Clerk();
        await alvo.Data.CreateAsync(Bookings, Booking(clerk, "checked"), clerk, cancellationToken: Ct);

        var entry = await AsAdministrator(alvo).SetEnabledAsync("hook-desk", "vat-check", enabled: false, "VIES is down", Ct);
        await alvo.Data.CreateAsync(Bookings, Booking(clerk, "unchecked"), clerk, cancellationToken: Ct);

        (runs, entry.Enabled).ShouldBe((1, false));
        (await WriteHookRows.CountAsync(database.Connect, "alvo_operations_log", "operation = 'disable-extension' AND target = 'vat-check'")).ShouldBe(1);
        audit.Lines.ShouldHaveSingleItem().ShouldContain("VIES is down");
        (await AsAdministrator(alvo).SetEnabledAsync("hook-desk", "vat-check", enabled: true, null, Ct)).Enabled.ShouldBeTrue();
        await alvo.Data.CreateAsync(Bookings, Booking(clerk, "checked again"), clerk, cancellationToken: Ct);
        runs.ShouldBe(2);
    }

    [Fact]
    public async Task A_rejected_run_counts_as_ran_for_the_recorder()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => Rejecting(b, "beforeWrite")));
        var clerk = Clerk();

        await Should.ThrowAsync<AlvoWriteRejectedException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "recorded"), clerk, cancellationToken: Ct));

        alvo.Hooks.AssertRan("before-write", times: 1);
    }

    protected abstract Task<HookDatabase> DatabaseAsync();

    /// <summary>
    /// Proves, from inside a validate hook, that no transaction of the write is open (acceptance (8), Ruling 44). SQLite: the
    /// hook's own write above would wait on the held writer lock and fail its <c>WaitAsync</c>, so nothing more is needed;
    /// PostgreSQL overrides this to find no other backend of the database <c>idle in transaction</c>.
    /// </summary>
    protected virtual Task AssertNoOpenTransactionAsync(HookDatabase database) => Task.CompletedTask;

    /// <summary>A host whose hooks run on a clock that never moves unless a fact moves it: no fact here races the 250 ms default (Ruling 40).</summary>
    private static Task<AlvoTestHost> StartAsync(HookDatabase database, Action<IAlvoBuilder> hooks, TimeProvider? time = null) =>
        AlvoTestHost.StartAsync(
            alvo =>
            {
                database.Use(alvo);
                alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", "hook-desk.alvo.json"));
                hooks(alvo);
            },
            time ?? new FakeTimeProvider(DateTimeOffset.UtcNow),
            cancellationToken: Ct);

    private static void Rejecting(MMLib.Alvo.Extensions.AlvoEntityBuilder bookings, string stage)
    {
        var id = IdOf(stage);
        _ = stage switch
        {
            "validate" => bookings.Validate(id, (ValidateWrite<AlvoRecord> write) => Done(() => write.Invalid("rejected", "Rejected.", "title"))),
            "beforeWrite" => bookings.BeforeWrite(id, (BeforeWrite<AlvoRecord> write) => Done(() => write.Conflict("rejected", "Rejected.", "title"))),
            "afterWrite" => bookings.AfterWrite(id, (AfterWrite<AlvoRecord> write) => Done(() => write.Forbidden("rejected", "Rejected."))),
            "beforeDelete" => bookings.BeforeDelete(id, (BeforeDelete<AlvoRecord> write) => Done(() => write.Conflict("rejected", "Rejected."))),
            _ => bookings.AfterDelete(id, (AfterDelete<AlvoRecord> write) => Done(() => write.Conflict("rejected", "Rejected."))),
        };
    }

    /// <summary>The id the rejecting hook of <paramref name="stage"/> registers under: an extension id is kebab-case.</summary>
    private static string IdOf(string stage) => stage switch
    {
        "beforeWrite" => "before-write",
        "afterWrite" => "after-write",
        "beforeDelete" => "before-delete",
        "afterDelete" => "after-delete",
        _ => stage,
    };

    private static ValueTask Done(Action record)
    {
        record();
        return ValueTask.CompletedTask;
    }

    private static IAlvoOperations AsAdministrator(AlvoTestHost alvo)
    {
        alvo.Services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = AlvoTestHost.Caller("manager"),
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "manager-key",
        };

        return alvo.Services.GetRequiredService<IAlvoOperations>();
    }

    private static AlvoContext Clerk() => AlvoTestHost.Caller("clerk") with { Tenant = new TenantId(Guid.Parse("22222222-0000-0000-0000-000000000002")) };

    private static Dictionary<string, object?> Booking(AlvoContext caller, string title) => new()
    {
        ["title"] = title,
        ["tenant_id"] = caller.Tenant!.Value.Value,
    };

    private static Dictionary<string, object?> Audit(AlvoContext caller, string note) => new()
    {
        ["note"] = note,
        ["tenant_id"] = caller.Tenant!.Value.Value,
    };

    protected sealed record HookDatabase(Action<IAlvoBuilder> Use, Func<DbConnection> Connect);
}
