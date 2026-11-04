using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// One write hook's checks, collecting its own problems: <see cref="CheckRegistration"/> needs no catalog — shape, services,
/// the dependency ban, bounds, provider, a fieldless <c>when</c> — and <see cref="Bind"/> binds each named entity.
/// </summary>
internal sealed class WriteHookBinding(WriteHookRegistration hook, ICelCompiler compiler)
{
    private const string WhenFix = "Correct the condition: it is CEL over new., old., changed(field), @user and @tenant.";

    private readonly List<ExtensionProblem> _problems = [];
    private CompiledExpression? _fallback;

    internal IReadOnlyList<ExtensionProblem> Problems => _problems;

    internal static string KindOf(WriteHookStage stage) => stage switch
    {
        WriteHookStage.Validate => "validate",
        WriteHookStage.BeforeWrite => "beforeWrite",
        WriteHookStage.AfterWrite => "afterWrite",
        WriteHookStage.BeforeDelete => "beforeDelete",
        _ => "afterDelete",
    };

    /// <summary>Whether the composition runs write hooks: a unit-capable relational driver with a rejection log (Ruling 34).</summary>
    internal static bool Relational(IServiceProviderIsService services) =>
        services.IsService(typeof(IAlvoTransactions)) && services.IsService(typeof(IHookRejectionLog));

    internal WriteHookBinding CheckRegistration(IServiceProviderIsService services, bool relational)
    {
        CheckShape();
        ResolveServices(services);
        CheckDependencies();
        CheckTimeout();
        CheckBudget();
        CheckProvider(relational);
        CheckSource();

        return this;
    }

    internal BoundWriteHook? Bind(PolicyCatalog catalog)
    {
        var conditions = hook.IsEveryEntity ? new Dictionary<string, CompiledExpression?>(StringComparer.Ordinal) : EntityConditions(catalog);

        return _problems.Count == 0 ? new BoundWriteHook(hook, conditions, _fallback) : null;
    }

    private void CheckShape()
    {
        foreach (var problem in hook.Typed?.Problems ?? [])
        {
            Add("record", problem.Message, problem.Fix);
        }
    }

    private void ResolveServices(IServiceProviderIsService services)
    {
        foreach (var type in hook.Invoker.Unresolvable(services))
        {
            Add("hook", $"The hook needs a {type.FullName} that the host's services cannot provide.", $"Register {type.Name} in the host's services.");
        }
    }

    private void CheckDependencies()
    {
        if (!hook.InTransaction)
        {
            return;
        }

        foreach (var (name, type) in hook.Invoker.Dependencies.Where(dependency => HookDependencies.IsBanned(dependency.Type)))
        {
            Add("dependencies",
                $"The hook takes a {type.FullName} ('{name}'): code that runs inside the write's transaction may not reach the network or another data port.",
                "Make the call in a validate hook, before the transaction, or in an event handler, after the commit; write other rows through write.Transaction.");
        }
    }

    private void CheckTimeout()
    {
        var limit = hook.InTransaction ? AlvoHookOptions.MaxTimeout : AlvoHookOptions.MaxValidateTimeout;
        if (hook.Settings.Timeout <= TimeSpan.Zero || hook.Settings.Timeout > limit)
        {
            Add("timeout", $"A timeout of {hook.Settings.Timeout} is not greater than zero and at most {limit}.",
                hook.InTransaction
                    ? "Set Timeout to at most 30 seconds: the hook holds the write's transaction while it runs."
                    : "Set Timeout to at most 60 seconds, or leave the default 5 seconds.");
        }
    }

    private void CheckBudget()
    {
        if (hook.Settings.MaxOperations is < 1 or > AlvoHookOptions.MaxMaxOperations)
        {
            Add("maxOperations", $"MaxOperations {hook.Settings.MaxOperations} is outside 1 to {AlvoHookOptions.MaxMaxOperations}.",
                "Set MaxOperations between 1 and 1000, or leave the default 50; write many rows with one batch call.");
        }
    }

    private void CheckProvider(bool relational)
    {
        if (!relational)
        {
            Add("provider", "Write hooks run in a relational provider's transaction, and this composition has none, so the hook would never run.",
                "Register UseSqlite or UsePostgreSql; the in-memory reference runs no host write hook.");
        }
    }

    private void CheckSource()
    {
        if (!hook.IsEveryEntity && hook.Entities.Contains(EntityEvents.AllEntities))
        {
            Add("entity", "'*' binds every entity, so naming entities beside it says nothing.", "Register Entity(\"*\", …) on its own, or name the entities.");
        }
        else if (hook.IsEveryEntity && hook.When is { } when)
        {
            _fallback = Compiled(when, new EntitySchema { Name = hook.Id, Fields = [] },
                "A '*' hook binds every entity, including ones added later: test only @user and @tenant here, or register the entities by name.");
        }
    }

    private Dictionary<string, CompiledExpression?> EntityConditions(PolicyCatalog catalog)
    {
        var conditions = new Dictionary<string, CompiledExpression?>(StringComparer.Ordinal);
        foreach (var name in hook.Entities)
        {
            if (catalog.Schema.Entities.FirstOrDefault(entity => string.Equals(entity.Name, name, StringComparison.Ordinal)) is not { } entity)
            {
                UnknownEntity(name, catalog);
                continue;
            }

            conditions[name] = hook.When is { } when ? Compiled(when, entity, WhenFix) : null;
            Mapped(entity);
        }

        return conditions;
    }

    private CompiledExpression? Compiled(string when, EntitySchema entity, string fix)
    {
        var result = compiler.Compile(when, CelProfile.Condition, entity);
        if (!result.IsSuccess)
        {
            result.Errors.ToList().ForEach(error => Add("when", $"{error.Message} (entity '{entity.Name}')", error.FixSuggestion ?? fix));
            return null;
        }

        foreach (var refusal in AfterImagePhase.Refusals(result.Expression!.Root, Images(), hook.Stage.ToString(), Locator("when")))
        {
            Add("when", refusal.Message, refusal.FixSuggestion ?? fix);
        }

        return result.Expression;
    }

    private AfterImages Images() => hook.Stage is WriteHookStage.BeforeDelete or WriteHookStage.AfterDelete
        ? new(Pre: true, Post: false)
        : new(Pre: true, Post: true);

    private void Mapped(EntitySchema entity)
    {
        if (hook.Typed is { Problems.Count: 0 } shape)
        {
            foreach (var problem in ShapeMapping.Problems(shape, entity, fromEventImages: false))
            {
                Add("record", problem.Message, problem.Fix);
            }
        }
    }

    private void UnknownEntity(string name, PolicyCatalog catalog)
    {
        var known = catalog.Schema.Entities.Select(entity => entity.Name).Order(StringComparer.Ordinal).ToList();
        var closest = NameSuggestion.Closest(name, known);
        var list = $"Entities: {string.Join(", ", known)}.";
        Add("entity", $"'{name}' is not an entity of this project, so the hook would never run.", closest is null ? list : $"Did you mean '{closest}'? {list}");
    }

    private void Add(string slot, string message, string fix) =>
        _problems.Add(new ExtensionProblem(hook.Id, KindOf(hook.Stage), Locator(slot), message, fix));

    private string Locator(string slot) => $"extensions/{hook.Id}/{slot}";
}
