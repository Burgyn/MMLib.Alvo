using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>A command bound to a catalog: its registration and its compiled access rule.</summary>
/// <param name="Registration">The command.</param>
/// <param name="Access">Its access rule, compiled against the catalog.</param>
internal sealed record BoundCommand(CommandRegistration Registration, CompiledExpression Access)
{
    /// <summary>Gets the command's name.</summary>
    internal string Name => Registration.Id;

    /// <summary>Gets a value indicating whether the rule reads the row, so it can be answered only per row.</summary>
    internal bool ReadsRow { get; } = EventConditionReads.FieldsReadOrCompared(Access.Root).Any();

    /// <summary>
    /// Gets the context values the rule reads, measured once — as <c>PolicyEngine</c> measures a rule's, so a caller lacking
    /// one is refused before the rule is evaluated (Ruling 33).
    /// </summary>
    internal RequiredContext Required { get; } = EventConditionReads.Required(Access);
}

/// <summary>
/// Checks one command and compiles its access rule in <see cref="CelProfile.Condition"/> (Ruling 1): against a fieldless
/// scope when it binds no entity, so the rule reads only <c>@user</c> and <c>@tenant</c>; against its entity otherwise, the
/// row as the current image. <c>old.</c> and <c>changed(…)</c> are refused: a command's row has no before-image; so is a
/// field with any <c>hidden</c> flag, which the row the caller reads would not carry (Ruling 35 — the precedent is a
/// handler condition reading an <c>events.exclude</c> field).
/// </summary>
internal sealed class CommandBinding(CommandRegistration command, ICelCompiler compiler)
{
    /// <summary>The kind a problem names.</summary>
    internal const string Kind = "command";

    private const string AccessFix = "Correct the access rule: it is CEL over @user and @tenant, and over the row's fields for a command bound to an entity.";

    private static readonly EntitySchema _noRow = new() { Name = "<command>", Fields = [] };

    private readonly List<ExtensionProblem> _problems = [];
    private CompiledExpression? _unbound;

    /// <summary>Gets every problem found so far.</summary>
    internal IReadOnlyList<ExtensionProblem> Problems => _problems;

    /// <summary>The catalog-free pass: the provider, and an unbound command's rule.</summary>
    /// <param name="relational">Whether the composition has a command ledger and transaction units.</param>
    internal CommandBinding CheckRegistration(bool relational)
    {
        if (!relational)
        {
            Add("provider",
                "Commands need a relational data provider — it keeps their idempotency keys and opens their transaction unit — and this composition registers none.",
                "Register UseSqlite(…) or UsePostgreSql(…); the in-memory reference provider runs no commands.");
        }

        if (command.Entity is null)
        {
            _unbound = Compile(_noRow);
        }

        return this;
    }

    /// <summary>Binds the command to <paramref name="catalog"/>, or answers null when it has a problem.</summary>
    /// <param name="catalog">The catalog to bind to.</param>
    internal BoundCommand? Bind(PolicyCatalog catalog)
    {
        var access = command.Entity is { } entity ? Compile(entity, catalog) : _unbound;
        return _problems.Count == 0 && access is not null ? new BoundCommand(command, access) : null;
    }

    private CompiledExpression? Compile(string entity, PolicyCatalog catalog)
    {
        var schema = catalog.Schema.Entities.FirstOrDefault(candidate => string.Equals(candidate.Name, entity, StringComparison.Ordinal));
        if (schema is not null)
        {
            return Compile(schema) is { } access ? WithoutHiddenReads(access, entity, catalog) : null;
        }

        Add("entity", $"It is bound to entity '{entity}', which the descriptor does not declare.", "Bind it to a declared entity, or declare the entity.");
        return null;
    }

    private CompiledExpression? Compile(EntitySchema scope)
    {
        var result = compiler.Compile(command.Access.Source, CelProfile.Condition, scope);
        if (result.IsSuccess)
        {
            return WithoutImages(result.Expression!);
        }

        foreach (var error in result.Errors)
        {
            Add("access", error.Message, error.FixSuggestion ?? AccessFix);
        }

        return null;
    }

    private CompiledExpression? WithoutImages(CompiledExpression access)
    {
        if (!ReadsAnImage(access.Root))
        {
            return access;
        }

        Add("access",
            "An access rule reads the row as it is now: a command has no before-image, so old. and changed(…) mean nothing here.",
            "Read the field without old. — status == 'requested' — and drop changed(…).");
        return null;
    }

    private CompiledExpression? WithoutHiddenReads(CompiledExpression access, string entity, PolicyCatalog catalog)
    {
        if (!catalog.TryGetEntity(entity, out var policy))
        {
            Add("access",
                $"'{entity}' has no policy in this catalog, so the fields it hides cannot be told apart from the ones it shows.",
                "Declare the entity in the descriptor so it carries a policy.");
            return null;
        }

        var hidden = EventConditionReads.FieldsReadOrCompared(access.Root).Where(policy.Hidden.ContainsKey).Distinct(StringComparer.Ordinal).ToList();
        foreach (var field in hidden)
        {
            Add("access",
                $"The rule reads '{field}', which '{entity}' hides from some callers: for them the rule would be decided by the field's absence.",
                $"Remove '{field}' from the access rule, or its hidden flag; if it must decide, check it in the handler after the rule admits the caller.");
        }

        return hidden.Count == 0 ? access : null;
    }

    private static bool ReadsAnImage(CelNode node) =>
        node is CelChanged or CelFieldRef { State: CelRecordState.Old } || CelTree.Children(node).Any(ReadsAnImage);

    private void Add(string slot, string message, string fix) =>
        _problems.Add(new ExtensionProblem(command.Id, Kind, $"extensions/{command.Id}/{slot}", message, fix));
}
