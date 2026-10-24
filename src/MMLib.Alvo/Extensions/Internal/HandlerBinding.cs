using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// One handler's checks, collecting its own problems: <see cref="CheckRegistration"/> needs no catalog, and
/// <see cref="Bind"/> binds each entity the handler names against one.
/// </summary>
internal sealed class HandlerBinding(HandlerRegistration handler, ICelCompiler compiler)
{
    private const string WhenFix = "Correct the condition: it is CEL over new., old., changed(field), @user and @tenant.";

    private readonly List<ExtensionProblem> _problems = [];
    private BoundCondition _fallback = BoundCondition.Always;

    internal IReadOnlyList<ExtensionProblem> Problems => _problems;

    /// <summary>The entities this handler names, when it names some and nothing else.</summary>
    private EntityEvents? Named =>
        handler.Source is EntityEvents { IsAll: false } entities && !entities.Entities.Contains(EntityEvents.AllEntities)
            ? entities
            : null;

    /// <summary>
    /// The catalog-free pass: a typed record's own shape, services, options, what the handler selects, and a fieldless
    /// <c>when</c>.
    /// </summary>
    internal HandlerBinding CheckRegistration(IServiceProviderIsService services)
    {
        CheckShape();
        ResolveServices(services);
        CheckTimeout();
        CheckChoices();
        CheckSource();

        return this;
    }

    /// <summary>The catalog pass: each named entity, its <c>when</c>, the images its verb carries and its exclusions.</summary>
    internal BoundHandler? Bind(PolicyCatalog catalog)
    {
        var conditions = Named is { } named
            ? EntityConditions(named, catalog)
            : new Dictionary<string, BoundCondition>(StringComparer.Ordinal);

        return _problems.Count == 0 ? new BoundHandler(handler, conditions, _fallback) : null;
    }

    /// <summary>What no descriptor can cure in a typed record (no constructor, a value no field holds, …); an <c>Awaiting</c> boot refuses it too.</summary>
    private void CheckShape()
    {
        foreach (var problem in handler.Typed?.Shape.Problems ?? [])
        {
            Add("record", problem.Message, problem.Fix);
        }
    }

    /// <summary>A typed record against one entity, filled from its event images; skipped when its shape is already refused.</summary>
    private void TypedMapping(EntitySchema entity)
    {
        if (handler.Typed is not { Shape: { Problems.Count: 0 } shape })
        {
            return;
        }

        foreach (var problem in ShapeMapping.Problems(shape, entity, fromEventImages: true))
        {
            Add("record", problem.Message, problem.Fix);
        }
    }

    private void ResolveServices(IServiceProviderIsService services)
    {
        foreach (var type in handler.Invoker.Unresolvable(services))
        {
            Add("handler", $"The handler needs a {type.FullName} that the host's services cannot provide.",
                $"Register {type.Name} in the host's services, or take what the handler needs from AlvoEventContext.");
        }
    }

    private void CheckTimeout()
    {
        var timeout = handler.Settings.Timeout;
        if (timeout <= TimeSpan.Zero)
        {
            Add("timeout", $"A timeout of {timeout} lets no attempt run.", "Set Timeout to a positive duration, or leave the default 30 seconds.");
        }
        else if (timeout > AlvoHandlerOptions.MaxTimeout)
        {
            Add("timeout", $"A timeout of {timeout} is longer than one attempt may hold its delivery.",
                "Set Timeout to at most 1 hour; split longer work into steps the handler records and resumes.");
        }
    }

    private void CheckChoices()
    {
        if (!Enum.IsDefined(handler.Settings.RunAs))
        {
            Add("runAs", $"{(int)handler.Settings.RunAs} is not an AlvoRunAs value.", "Set RunAs to AlvoRunAs.System or AlvoRunAs.EventActor.");
        }

        if (!Enum.IsDefined(handler.Settings.Ordering))
        {
            Add("ordering", $"{(int)handler.Settings.Ordering} is not an AlvoOrdering value.", "Set Ordering to AlvoOrdering.PerRecord or AlvoOrdering.None.");
        }
    }

    private void CheckSource()
    {
        if (handler.Source is EntityEvents { IsAll: false } entities && entities.Entities.Contains(EntityEvents.AllEntities))
        {
            Add("entity", "'*' binds every entity, so naming entities beside it says nothing.", "Register Entity(\"*\", …) on its own, or name the entities.");
        }
        else if (Named is null)
        {
            _fallback = FieldlessCondition();
        }
    }

    private BoundCondition FieldlessCondition()
    {
        if (handler.When is null)
        {
            return BoundCondition.Always;
        }

        var result = compiler.Compile(handler.When, CelProfile.Condition, new EntitySchema { Name = handler.Id, Fields = [] });
        if (result.IsSuccess)
        {
            return BoundCondition.Of(result.Expression!);
        }

        result.Errors.ToList().ForEach(error => Add("when", error.Message, FieldlessFix));
        return BoundCondition.Always;
    }

    private string FieldlessFix => handler.Source is NamedEvent
        ? "An application event's payload declares no fields to check a condition against: test only @user and @tenant here, and check the payload inside the handler."
        : "A '*' registration binds every entity, including ones added later: test only @user and @tenant here, or register the entities by name.";

    private Dictionary<string, BoundCondition> EntityConditions(EntityEvents source, PolicyCatalog catalog)
    {
        var conditions = new Dictionary<string, BoundCondition>(StringComparer.Ordinal);
        foreach (var name in source.Entities)
        {
            if (EntityNamed(name, catalog) is not { } entity)
            {
                UnknownEntity(name, catalog);
            }
            else
            {
                BindEntity(entity, source.Verb, conditions);
            }
        }

        return conditions;
    }

    private void BindEntity(EntitySchema entity, HandlerVerb verb, Dictionary<string, BoundCondition> conditions)
    {
        if (Condition(entity, verb) is { } condition)
        {
            conditions[entity.Name] = condition;
        }

        TypedMapping(entity);
    }

    private static EntitySchema? EntityNamed(string name, PolicyCatalog catalog) =>
        catalog.Schema.Entities.FirstOrDefault(entity => string.Equals(entity.Name, name, StringComparison.Ordinal));

    private void UnknownEntity(string name, PolicyCatalog catalog)
    {
        var known = catalog.Schema.Entities.Select(entity => entity.Name).Order(StringComparer.Ordinal).ToList();
        var closest = NameSuggestion.Closest(name, known);
        var list = $"Entities: {string.Join(", ", known)}.";
        Add("entity", $"'{name}' is not an entity of this project, so the handler would never run.",
            closest is null ? list : $"Did you mean '{closest}'? {list}");
    }

    private BoundCondition? Condition(EntitySchema entity, HandlerVerb verb)
    {
        if (handler.When is null)
        {
            return BoundCondition.Always;
        }

        var result = compiler.Compile(handler.When, CelProfile.Condition, entity);
        if (!result.IsSuccess)
        {
            result.Errors.ToList().ForEach(error => Add("when", $"{error.Message} (entity '{entity.Name}')", error.FixSuggestion ?? WhenFix));
            return null;
        }

        var before = _problems.Count;
        ImageRefusals(result.Expression!, verb);
        ExcludedReads(result.Expression!, entity);

        return _problems.Count == before ? BoundCondition.Of(result.Expression!) : null;
    }

    private void ImageRefusals(CompiledExpression expression, HandlerVerb verb)
    {
        foreach (var refusal in AfterImagePhase.Refusals(expression.Root, Images(verb), $"On{verb}", Locator("when")))
        {
            Add("when", refusal.Message, refusal.FixSuggestion ?? WhenFix);
        }
    }

    private void ExcludedReads(CompiledExpression expression, EntitySchema entity)
    {
        foreach (var field in EventConditionReads.ExcludedFieldsReadOrCompared(expression.Root, entity))
        {
            Add("when",
                $"The condition reads '{field}', which '{entity.Name}' leaves out of its event images (events.exclude): it would be decided by the field's absence on every event.",
                $"Remove '{field}' from the condition, or from entities.{entity.Name}.events.exclude.");
        }
    }

    private void Add(string slot, string message, string fix) =>
        _problems.Add(new ExtensionProblem(handler.Id, ExtensionBindingCompiler.HandlerKind, Locator(slot), message, fix));

    private string Locator(string slot) => $"extensions/{handler.Id}/{slot}";

    private static AfterImages Images(HandlerVerb verb) => verb switch
    {
        HandlerVerb.Updated => new(Pre: true, Post: true),
        HandlerVerb.Deleted => new(Pre: true, Post: false),
        _ => new(Pre: false, Post: true),
    };
}
