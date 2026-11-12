using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Management;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// The dashboard's one error policy: a documented refusal is explained, a going circuit is dropped, and
/// anything else is logged and never printed.
/// </summary>
public class AdminProblemTests
{
    [Fact]
    public void A_management_refusal_keeps_the_framework_sentence_under_a_mapped_title()
    {
        var problem = AdminProblem.From(new ManagementRequestException("The descriptor has no entity called foo."))!;

        problem.IsFault.ShouldBeFalse();
        problem.Title.ShouldBe("The request was refused");
        problem.Detail.ShouldBe("The descriptor has no entity called foo.");
    }

    [Fact]
    public void A_fault_never_shows_its_message()
    {
        var problem = AdminProblem.From(new InvalidCastException("Unable to cast Secret.Connection"))!;

        problem.IsFault.ShouldBeTrue();
        problem.Title.ShouldBe(AdminProblem.FaultTitle);
        problem.Detail.ShouldBe(AdminProblem.FaultDetail);
        problem.Detail.ShouldNotContain("Secret");
        problem.Fix.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(Dropped))]
    public void A_cancelled_or_disconnected_circuit_is_dropped(Exception exception)
        => AdminProblem.From(exception).ShouldBeNull();

    public static TheoryData<Exception> Dropped() => new()
    {
        new OperationCanceledException(),
        new TaskCanceledException(),
        new JSDisconnectedException("gone"),
    };

    [Fact]
    public void A_disposed_object_is_a_fault_because_it_may_be_a_use_after_dispose()
    {
        var logger = new RecordingLogger();

        var problem = AdminProblem.From(new ObjectDisposedException("IServiceProvider"), logger)!;

        problem.IsFault.ShouldBeTrue();
        problem.Detail.ShouldBe(AdminProblem.FaultDetail);
        logger.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Error);
    }

    [Fact]
    public void Only_a_forbidden_refusal_offers_to_sign_out()
    {
        AdminProblem.From(new ManagementForbiddenException())!.OffersSignOut.ShouldBeTrue();
        AdminProblem.From(new ManagementRequestException("no"))!.OffersSignOut.ShouldBeFalse();
    }

    [Fact]
    public void An_escalation_gets_a_different_fix_on_preview_and_on_access()
    {
        var escalation = new ManagementEscalationException();

        AdminProblem.From(escalation, ProblemSite.SchemaApply)!.Fix.ShouldStartWith("This change touches the access block");
        AdminProblem.From(escalation, ProblemSite.People)!.Fix.ShouldStartWith("Another administrator can make this change");
        AdminProblem.From(escalation)!.Fix.ShouldBeNull();
    }

    [Fact]
    public void A_stale_revision_gets_the_fix_of_the_screen_that_applied()
    {
        var stale = new DescriptorConcurrencyException("shop", 3, 4);

        AdminProblem.From(stale, ProblemSite.SchemaApply)!.Fix!.ShouldContain("your edits are still here");
        AdminProblem.From(stale, ProblemSite.Rollback)!.Fix.ShouldBe(
            "Somebody applied a revision while this screen was open. Reload and try again.");
    }

    /// <summary>
    /// An apply that gave up waiting for a busy table changed nothing and is worth retrying (HX-11 Ruling 20): a refusal
    /// with a retry fix, never a fault. The dashboard names the type through the D45 grant; this suite holds no grant (one
    /// would move Abstractions' public-API baseline for a test), so it builds the exception by its name.
    /// </summary>
    [Fact]
    public void A_lock_timeout_is_a_refusal_that_says_to_retry()
    {
        var timedOut = (Exception)Activator.CreateInstance(
            typeof(ManagementRequestException).Assembly.GetType("MMLib.Alvo.Migrations.AlvoApplyLockTimeoutException", throwOnError: true)!)!;

        var applied = AdminProblem.From(timedOut, ProblemSite.SchemaApply)!;
        applied.IsFault.ShouldBeFalse();
        applied.Title.ShouldBe("A table was busy, so nothing was applied");
        applied.Detail.ShouldBe(timedOut.Message);
        applied.Fix.ShouldBe("Another transaction held a table this apply needed. Nothing changed — wait a few seconds and apply again.");
        AdminProblem.From(timedOut, ProblemSite.Rollback)!.Fix.ShouldBe(
            "Another transaction held a table this restore needed. Nothing changed — wait a few seconds and restore again.");
    }

    [Fact]
    public void A_write_failed_to_break_a_deadlock_is_a_refusal_that_says_to_save_again()
    {
        var lost = new AlvoTransientConflictException();

        var problem = AdminProblem.From(lost, ProblemSite.RecordWrite)!;

        problem.IsFault.ShouldBeFalse("a deadlock the database broke is a documented, retryable refusal, not an error");
        problem.Title.ShouldBe("Another write got in the way, so nothing was saved");
        problem.Detail.ShouldBe(lost.Message);
        problem.Fix.ShouldNotBeNull().ShouldContain("Save again");
    }

    [Fact]
    public void A_scoped_read_without_a_tenant_is_told_about_the_tenant_rather_than_the_rules()
    {
        var refused = new AlvoAuthorizationException("refused");

        AdminProblem.From(refused, ProblemSite.ScopedRecordsWithoutTenant)!.Fix.ShouldStartWith("This entity is tenant-scoped");
        AdminProblem.From(refused, ProblemSite.Records)!.Fix.ShouldStartWith("The descriptor configures no rule");
        AdminProblem.From(refused, ProblemSite.RecordWrite)!.Fix.ShouldStartWith("The write rule on this entity");
    }

    [Fact]
    public void A_unique_collision_names_the_fields()
    {
        var collision = new AlvoConstraintViolationException(AlvoConstraintKind.Unique, ["email"]);

        var problem = AdminProblem.From(collision, ProblemSite.RecordWrite)!;

        problem.Title.ShouldBe("A constraint refused the value");
        problem.Fix.ShouldBe(
            "Another record already has this value for email. The field is declared unique on the entity's Fields tab.");
    }

    [Fact]
    public void A_broken_validation_names_the_field_or_nothing()
    {
        AdminProblem.From(new AlvoConstraintViolationException(AlvoConstraintKind.Check, ["stock"]), ProblemSite.RecordWrite)!.Fix.ShouldBe(
            "The value for stock breaks the validation the descriptor declares for it. The field's validation is on the entity's Fields tab.");
        AdminProblem.From(new AlvoConstraintViolationException(AlvoConstraintKind.Check, []), ProblemSite.RecordWrite)!.Fix.ShouldBe(
            "A value breaks a validation the descriptor declares. Each field's validation is on the entity's Fields tab.");
    }

    /// <summary>
    /// A lookup check's refusal is the author's sentence about a related row, not a field's validation: the fix names the
    /// lookup, and never sends the operator to the Fields tab for a rule that is not there.
    /// </summary>
    [Fact]
    public void A_lookup_check_refusal_carries_the_author_s_message_and_names_the_lookup()
    {
        var problem = AdminProblem.From(LookupCheckRefusal(["product_id"], "This product is not available."), ProblemSite.RecordWrite)!;
        var hidden = AdminProblem.From(LookupCheckRefusal([], "This product is not available."), ProblemSite.RecordWrite)!;

        problem.Detail.ShouldBe("This product is not available.");
        problem.Fix.ShouldBe(
            "Point product_id at a record that satisfies this check, or change the values the check compares. The check is declared in the entity's checks.");
        hidden.Fix.ShouldBe(
            "Point the reference at a record that satisfies this check, or change the values the check compares. The check is declared in the entity's checks.");
    }

    /// <summary>The refusal a lookup check raises; its constructor is internal to the engine, so it is reached as the engine reaches it.</summary>
    private static AlvoConstraintViolationException LookupCheckRefusal(IReadOnlyList<string> fields, string message) =>
        (AlvoConstraintViolationException)Activator.CreateInstance(
            typeof(AlvoConstraintViolationException),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            [fields, message],
            culture: null)!;

    [Fact]
    public void An_argument_exception_is_the_data_ports_refusal_only_where_the_data_port_was_called()
    {
        var invalid = new ArgumentException("values omits a required field");

        AdminProblem.From(invalid, ProblemSite.RecordWrite)!.IsFault.ShouldBeFalse();
        AdminProblem.From(invalid)!.IsFault.ShouldBeTrue();
    }

    [Fact]
    public void A_null_argument_is_a_defect_even_where_the_data_port_was_called()
    {
        var missing = new ArgumentNullException("values");

        var problem = AdminProblem.From(missing, ProblemSite.RecordWrite)!;

        problem.IsFault.ShouldBeTrue();
        problem.Detail.ShouldNotContain("values");
    }

    [Fact]
    public void An_unsupported_operation_is_a_refusal_only_on_access()
    {
        var unsupported = new NotSupportedException("no membership writes");

        AdminProblem.From(unsupported, ProblemSite.People)!.Fix.ShouldBe(
            "This deployment's membership store does not support that operation.");
        AdminProblem.From(unsupported)!.IsFault.ShouldBeTrue();
    }

    [Fact]
    public void A_person_changed_elsewhere_is_a_refusal_that_says_to_reload()
    {
        var stale = new AlvoPreconditionFailedException("Somebody else changed this person.");

        var problem = AdminProblem.From(stale, ProblemSite.People)!;

        problem.IsFault.ShouldBeFalse("a lost race is a documented refusal, not an error the operator cannot act on");
        problem.Detail.ShouldBe("Somebody else changed this person.");
        problem.Fix.ShouldNotBeNull().ShouldContain("Reload");
    }

    [Fact]
    public void A_fault_is_logged_as_an_error_with_its_exception()
    {
        var logger = new RecordingLogger();
        var fault = new InvalidCastException("boom");

        AdminProblem.From(fault, logger);

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Exception.ShouldBeSameAs(fault);
    }

    [Fact]
    public void A_refusal_is_not_logged()
    {
        var logger = new RecordingLogger();

        AdminProblem.From(new ManagementForbiddenException(), logger);

        logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void A_dropped_exception_is_logged_only_at_debug()
    {
        var logger = new RecordingLogger();

        AdminProblem.Absorb(new OperationCanceledException(), logger);

        logger.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Debug);
    }

    private sealed record LogEntry(LogLevel Level, Exception? Exception);

    private sealed class RecordingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, exception));
    }
}
