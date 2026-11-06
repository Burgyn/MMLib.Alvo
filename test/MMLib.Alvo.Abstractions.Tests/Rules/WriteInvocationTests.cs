using MMLib.Alvo.Data;
using MMLib.Alvo.Management;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Abstractions.Tests.Rules;

/// <summary>The write-hook port's vocabulary (spec 2026-10-14 §5 row 09): stable numbers, and a default runner that runs nothing.</summary>
public sealed class WriteInvocationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_write_verbs_keep_their_numbers()
    {
        ((int)AlvoWriteOperation.Create, (int)AlvoWriteOperation.Update, (int)AlvoWriteOperation.Replace, (int)AlvoWriteOperation.Delete)
            .ShouldBe((0, 1, 2, 3));
    }

    [Fact]
    public void The_catalog_kinds_and_stages_are_appended_after_the_existing_ones()
    {
        ((int)AlvoExtensionKind.Validate, (int)AlvoExtensionKind.BeforeWrite, (int)AlvoExtensionKind.AfterWrite,
            (int)AlvoExtensionKind.BeforeDelete, (int)AlvoExtensionKind.AfterDelete).ShouldBe((2, 3, 4, 5, 6));
        ((int)AlvoExtensionStage.InTransaction).ShouldBe(3);
        ((int)AlvoExtensionKind.Narrowing, (int)AlvoExtensionStage.Read).ShouldBe((7, 4));
        System.Text.Json.JsonSerializer.Serialize(AlvoExtensionKind.Narrowing).ShouldBe("\"narrowing\"");
        ((int)AlvoExtensionKind.Enricher).ShouldBe(8);
        System.Text.Json.JsonSerializer.Serialize(AlvoExtensionKind.Enricher).ShouldBe("\"enricher\"");
        System.Text.Json.JsonSerializer.Serialize(AlvoExtensionStage.Read).ShouldBe("\"read\"");
        System.Text.Json.JsonSerializer.Serialize(AlvoExtensionKind.BeforeDelete).ShouldBe("\"beforeDelete\"");
        System.Text.Json.JsonSerializer.Serialize(AlvoExtensionStage.InTransaction).ShouldBe("\"inTransaction\"");
    }

    [Fact]
    public void An_extension_is_enabled_and_not_top_level_only_unless_its_kind_says_so()
    {
        var extension = new ManagementExtension { Id = "x", Kind = AlvoExtensionKind.EventHandler, Stage = AlvoExtensionStage.PostCommit };

        (extension.Enabled, extension.TopLevelOnly).ShouldBe((true, false));
    }

    [Fact]
    public async Task The_default_runner_runs_nothing_and_patches_nothing()
    {
        var invocation = new WriteInvocation
        {
            Entity = "bookings",
            Operation = AlvoWriteOperation.Create,
            Caller = AlvoContext.Anonymous,
            Now = DateTimeOffset.UnixEpoch,
        };

        NoWriteExtensions.Instance.Validates("bookings", AlvoWriteOperation.Create).ShouldBeFalse();
        NoWriteExtensions.Instance.RunsInTransaction("bookings", AlvoWriteOperation.Delete).ShouldBeFalse();
        await NoWriteExtensions.Instance.ValidateAsync(invocation, Ct);
        (await NoWriteExtensions.Instance.BeforeWriteAsync(invocation, Ct)).ShouldBeEmpty();
        (await NoWriteExtensions.Instance.AfterWriteAsync(invocation, Ct)).ShouldBeNull();
    }

    [Fact]
    public void The_budget_refusal_is_an_invalid_operation_naming_the_fix()
    {
        var refusal = AlvoBudgetExceededException.For("audit-trail", 50);

        refusal.ShouldBeAssignableTo<InvalidOperationException>();
        refusal.Message.ShouldContain("'audit-trail'");
        refusal.Message.ShouldContain("50");
        refusal.Message.ShouldContain("MaxOperations");
    }

    [Fact]
    public void The_rejection_table_is_reserved()
    {
        AlvoFrameworkTables.NamesFor("alvo").ShouldContain("alvo_hook_rejections");
    }
}
