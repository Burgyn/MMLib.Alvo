using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// Binds every registration of this host to one primed catalog and collects every problem at once (spec 2026-10-14
/// §4.2): unknown entities, conditions that do not compile or read what the verb's event cannot answer, ids, services.
/// </summary>
internal static class ExtensionBindingCompiler
{
    internal const string HandlerKind = "eventHandler";
    internal const string RetiredKind = "retired";

    private const string IdFix =
        "Use lower-case letters, digits and hyphens, starting with a letter, at most 63 characters — 'send-confirmation'.";

    internal static ExtensionBindingResult Compile(
        ExtensionRegistry registry, PolicyCatalog catalog, ICelCompiler compiler, IServiceProviderIsService services)
    {
        var problems = new List<ExtensionProblem>(IdProblems(registry));
        var bound = new List<BoundHandler>();
        foreach (var handler in registry.Handlers)
        {
            problems.AddRange(new HandlerBinding(handler, catalog, compiler, services).Bind(out var boundHandler));
            if (boundHandler is not null)
            {
                bound.Add(boundHandler);
            }
        }

        var refused = problems.Select(problem => problem.Id).ToHashSet(StringComparer.Ordinal);

        var bindings = new ExtensionBindings(
            bound.Where(handler => !refused.Contains(handler.Id)),
            registry.Handlers.Where(handler => refused.Contains(handler.Id)));

        return new(bindings, problems, Ties(registry));
    }

    private static IEnumerable<ExtensionProblem> IdProblems(ExtensionRegistry registry) =>
        [.. registry.All.Where(registration => !Grammatical(registration)).Select(BadId), .. Duplicates(registry), .. Collisions(registry)];

    private static bool Grammatical(ExtensionRegistration registration) =>
        registration is RetiredExtension ? ExtensionIds.IsRetirableId(registration.Id) : ExtensionIds.IsExtensionId(registration.Id);

    private static ExtensionProblem BadId(ExtensionRegistration registration) =>
        new(registration.Id, KindOf(registration), IdLocator(registration.Id), $"'{registration.Id}' is not an extension id.", IdFix);

    private static IEnumerable<ExtensionProblem> Duplicates(ExtensionRegistry registry) =>
        registry.All.GroupBy(registration => registration.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => new ExtensionProblem(
                group.Key,
                KindOf(group.First()),
                IdLocator(group.Key),
                $"'{group.Key}' is registered {group.Count()} times ({string.Join(", ", group.Select(KindOf))}).",
                "Give each registration its own id: an id is how its deliveries are found again after a restart, and a retired id may not be registered."));

    private static IEnumerable<ExtensionProblem> Collisions(ExtensionRegistry registry) =>
        registry.All.Select(registration => registration.Id).Distinct(StringComparer.Ordinal)
            .GroupBy(ExtensionIds.DerivedSegment, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => Collision([.. group], group.Key, registry));

    private static ExtensionProblem Collision(IReadOnlyList<string> ids, string segment, ExtensionRegistry registry)
    {
        var blamed = Blamed(ids, registry);

        return new(
            blamed.Id,
            KindOf(blamed),
            IdLocator(blamed.Id),
            $"'{string.Join("' and '", ids)}' derive one event-name segment, '{segment}', so the events Alvo mints for them could not be told apart.",
            "Rename one of them.");
    }

    private static ExtensionRegistration Blamed(IReadOnlyList<string> ids, ExtensionRegistry registry)
    {
        var colliding = registry.All.Where(registration => ids.Contains(registration.Id, StringComparer.Ordinal)).ToList();

        return colliding.OfType<HandlerRegistration>().FirstOrDefault() ?? colliding[0];
    }

    private static string KindOf(ExtensionRegistration registration) => registration is RetiredExtension ? RetiredKind : HandlerKind;

    private static string IdLocator(string id) => $"extensions/{id}/id";

    private static IReadOnlyList<OrderTie> Ties(ExtensionRegistry registry) =>
        [.. registry.Handlers
            .SelectMany(handler => Targets(handler.Source).Select(target => (Target: target, Handler: handler)))
            .GroupBy(entry => (entry.Target, entry.Handler.Settings.Order))
            .Where(group => group.Count() > 1 && group.Any(entry => entry.Handler.Settings.OrderStated))
            .Select(group => new OrderTie(group.Key.Target, group.Key.Order, [.. group.Select(entry => entry.Handler.Id)]))];

    private static IEnumerable<string> Targets(HandlerSource source) => source switch
    {
        EntityEvents entities => entities.Entities.SelectMany(entity => Operations(entities.Verb).Select(operation => $"{entity} {operation}")),
        NamedEvent named => [named.Type],
        _ => [],
    };

    private static string[] Operations(HandlerVerb verb) => verb switch
    {
        HandlerVerb.Created => ["created"],
        HandlerVerb.Updated => ["updated"],
        HandlerVerb.Deleted => ["deleted"],
        _ => ["created", "updated"],
    };

    /// <summary>One handler's binding, collecting its own problems.</summary>
    private sealed class HandlerBinding(
        HandlerRegistration handler, PolicyCatalog catalog, ICelCompiler compiler, IServiceProviderIsService services)
    {
        private const string WhenFix = "Correct the condition: it is CEL over new., old., changed(field), @user and @tenant.";

        private readonly List<ExtensionProblem> _problems = [];

        internal List<ExtensionProblem> Bind(out BoundHandler? bound)
        {
            ResolveServices();
            CheckTimeout();
            CheckChoices();
            var named = handler.Source is EntityEvents { IsAll: false } entities ? entities : null;
            var conditions = named is null ? new Dictionary<string, BoundCondition>(StringComparer.Ordinal) : EntityConditions(named);
            var fallback = named is null ? FieldlessCondition() : BoundCondition.Always;
            bound = _problems.Count == 0 ? new BoundHandler(handler, conditions, fallback) : null;

            return _problems;
        }

        private void ResolveServices()
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

        private Dictionary<string, BoundCondition> EntityConditions(EntityEvents source)
        {
            var conditions = new Dictionary<string, BoundCondition>(StringComparer.Ordinal);
            if (source.Entities.Contains(EntityEvents.AllEntities))
            {
                Add("entity", "'*' binds every entity, so naming entities beside it says nothing.", "Register Entity(\"*\", …) on its own, or name the entities.");
                return conditions;
            }

            foreach (var name in source.Entities)
            {
                if (EntityNamed(name) is not { } entity)
                {
                    UnknownEntity(name);
                }
                else if (Condition(entity, source.Verb) is { } condition)
                {
                    conditions[name] = condition;
                }
            }

            return conditions;
        }

        private EntitySchema? EntityNamed(string name) =>
            catalog.Schema.Entities.FirstOrDefault(entity => string.Equals(entity.Name, name, StringComparison.Ordinal));

        private void UnknownEntity(string name)
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
            foreach (var refusal in AfterImagePhase.Refusals(expression.Root, Images(verb), VerbName(verb), Locator("when")))
            {
                Add("when", refusal.Message, refusal.FixSuggestion ?? WhenFix);
            }
        }

        private void ExcludedReads(CompiledExpression expression, EntitySchema entity)
        {
            var excluded = FieldsRead(expression.Root)
                .Where(field => entity.EventImageExclusions.Contains(field, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal);
            foreach (var field in excluded)
            {
                Add("when",
                    $"The condition reads '{field}', which '{entity.Name}' leaves out of its event images (events.exclude): it would be decided by the field's absence on every event.",
                    $"Remove '{field}' from the condition, or from entities.{entity.Name}.events.exclude.");
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

        private void Add(string slot, string message, string fix) =>
            _problems.Add(new ExtensionProblem(handler.Id, HandlerKind, Locator(slot), message, fix));

        private string Locator(string slot) => $"extensions/{handler.Id}/{slot}";

        private static AfterImages Images(HandlerVerb verb) => verb switch
        {
            HandlerVerb.Updated => new(Pre: true, Post: true),
            HandlerVerb.Deleted => new(Pre: true, Post: false),
            _ => new(Pre: false, Post: true),
        };

        private static string VerbName(HandlerVerb verb) => $"On{verb}";

        private static IEnumerable<string> FieldsRead(CelNode node) =>
            OwnFields(node).Concat(CelTree.Children(node).SelectMany(FieldsRead));

        private static string[] OwnFields(CelNode node) => node switch
        {
            CelFieldRef reference => [reference.FieldName],
            CelChanged changed => [changed.FieldName],
            _ => [],
        };
    }
}
