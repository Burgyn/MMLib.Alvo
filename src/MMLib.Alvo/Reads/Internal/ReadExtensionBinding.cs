using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Hooks.Internal;
using MMLib.Alvo.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Reads.Internal;

/// <summary>
/// One read extension's checks, collecting its own problems: <see cref="CheckRegistration"/> needs no catalog — shape,
/// services, the dependency ban, provider, <c>"*"</c> — and <see cref="Bind"/> binds each named entity.
/// </summary>
/// <remarks>
/// The dependency ban holds for every read extension: a read may run inside a unit's transaction, and there is no read
/// stage outside one.
/// </remarks>
internal sealed class ReadExtensionBinding(ReadExtensionRegistration read)
{
    private readonly List<ExtensionProblem> _problems = [];

    internal IReadOnlyList<ExtensionProblem> Problems => _problems;

    internal static string KindOf(ReadExtensionKind kind) => kind == ReadExtensionKind.Narrowing ? "narrowing" : "enricher";

    internal ReadExtensionBinding CheckRegistration(IServiceProviderIsService services, bool relational)
    {
        CheckShape();
        ResolveServices(services);
        CheckDependencies();
        CheckProvider(relational);
        CheckEntities();

        return this;
    }

    internal BoundReadExtension? Bind(PolicyCatalog catalog)
    {
        foreach (var name in read.Entities.Where(name => name != EntityEvents.AllEntities))
        {
            BindEntity(name, catalog);
        }

        return _problems.Count == 0 ? new BoundReadExtension(read) : null;
    }

    private string Noun => KindOf(read.Kind);

    private void CheckShape()
    {
        foreach (var problem in read.Typed?.Problems ?? [])
        {
            Add("record", problem.Message, problem.Fix);
        }
    }

    private void ResolveServices(IServiceProviderIsService services)
    {
        foreach (var type in read.Invoker.Unresolvable(services))
        {
            Add("hook", $"The {Noun} needs a {type.FullName} that the host's services cannot provide.", $"Register {type.Name} in the host's services.");
        }
    }

    private void CheckDependencies()
    {
        foreach (var (name, type) in read.Invoker.Dependencies.Where(dependency => HookDependencies.IsBanned(dependency.Type)))
        {
            Add("dependencies",
                $"The {Noun} takes a {type.FullName} ('{name}'): code that runs inside a read, which may be inside a unit's transaction, may not reach the network or another data port.",
                "Read what it needs from the caller or from a cache filled outside the read; an event handler can keep that cache current.");
        }
    }

    private void CheckProvider(bool relational)
    {
        if (!relational)
        {
            Add("provider", $"Read extensions run in a relational provider's reads, and this composition has none, so the {Noun} would never run.",
                "Register UseSqlite or UsePostgreSql; the in-memory reference runs no read extension.");
        }
    }

    private void CheckEntities()
    {
        if (read.Entities.Contains(EntityEvents.AllEntities, StringComparer.Ordinal))
        {
            Add("entity", "A read extension binds named entities: '*' would narrow or shape entities added later without review.", "Name the entities.");
        }
    }

    private void BindEntity(string name, PolicyCatalog catalog)
    {
        if (catalog.Schema.Entities.FirstOrDefault(entity => string.Equals(entity.Name, name, StringComparison.Ordinal)) is not { } entity)
        {
            UnknownEntity(name, catalog);
            return;
        }

        if (read.Typed is { Problems.Count: 0 } shape)
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
        Add("entity", $"'{name}' is not an entity of this project, so the {Noun} would never run.", closest is null ? list : $"Did you mean '{closest}'? {list}");
    }

    private void Add(string slot, string message, string fix) =>
        _problems.Add(new ExtensionProblem(read.Id, Noun, $"extensions/{read.Id}/{slot}", message, fix));
}
