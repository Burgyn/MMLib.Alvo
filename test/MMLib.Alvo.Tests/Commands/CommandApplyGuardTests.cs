using Microsoft.Extensions.Logging.Abstractions;
using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Expressions;
using MMLib.Alvo.Tests.Extensions;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Commands;

/// <summary>
/// The apply guard keeps a descriptor change from unbinding a command (spec 2026-10-14 §4.2), through the binder the boot
/// uses — so a change that hides a field the access rule reads is refused at apply as it is at binding (Ruling 35).
/// </summary>
public sealed class CommandApplyGuardTests
{
    [Fact]
    public void Removing_the_entity_a_mapped_command_binds_is_refused_at_its_pointer()
    {
        var withoutTickets = new SchemaModel([.. HandlerCatalog.Schema.Entities.Where(entity => entity.Name != "tickets")]);

        var errors = Should.Throw<DescriptorValidationException>(() => Guard().Check(CatalogOf(withoutTickets))).Result.Errors;

        errors.ShouldContain(error => error.Path == "/entities/tickets" && error.Message.Contains("'resolve_ticket' (command, in this host)", StringComparison.Ordinal));
        errors.ShouldAllBe(error => error.Severity == DescriptorValidationSeverity.Error);
    }

    [Fact]
    public void Hiding_a_field_a_mapped_command_s_rule_reads_is_refused()
    {
        var errors = Should.Throw<DescriptorValidationException>(() => Guard().Check(CommandBindingTests.HidingStatus())).Result.Errors;

        errors.ShouldContain(error => error.Path.Length == 0
            && error.Severity == DescriptorValidationSeverity.Error
            && error.Message.Contains("hides from some callers", StringComparison.Ordinal));
    }

    /// <summary>A host with no handler, one command bound to <c>tickets</c> reading <c>status</c>, and a current catalog it binds.</summary>
    private static ExtensionApplyGuard Guard()
    {
        var commands = new CommandRegistry();
        commands.Map("resolve_ticket", "/commands/tickets/{id}/resolve", "tickets", CommandAccess.Of("status == 'open'"));
        var registry = new ExtensionRegistry([], commands);
        var catalogs = new PolicyCatalogProvider();
        catalogs.SetCurrent("handlers", Catalog());
        var binder = new ExtensionBinder(registry, CelFixtures.Compiler, EveryService(), NullLogger<ExtensionBinder>.Instance);

        return new ExtensionApplyGuard(registry, binder, CelFixtures.Compiler, catalogs);
    }
}
