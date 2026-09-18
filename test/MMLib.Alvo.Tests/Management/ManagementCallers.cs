using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Management.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tests.Rules;
using NSubstitute;

namespace MMLib.Alvo.Tests.Management;

/// <summary>The callers, the access blocks and the evaluator both management suites are written against.</summary>
/// <remarks>
/// One type rather than a private helper per suite, because two fixtures that each build "a caller holding
/// these roles" is how the evaluator facts and the filter facts would come to describe two different callers.
/// </remarks>
internal static class ManagementCallers
{
    /// <summary>Gets the bootstrap administrator every fixture here recognises.</summary>
    internal static UserId Bootstrap { get; } = UserId.New();

    /// <summary>A caller holding the named application roles, plus <c>authenticated</c>.</summary>
    /// <param name="roleNames">The application roles the caller holds.</param>
    internal static AlvoContext Caller(params string[] roleNames) => new()
    {
        User = UserId.New(),
        Roles = RoleCatalog.Create(["manager", "editor", "sales"]).Resolve([.. roleNames, "authenticated"]),
    };

    /// <summary>An <c>access</c> block declaring only the levels named.</summary>
    /// <param name="admin">The <c>admin</c> level's CEL source, or <see langword="null"/> for none.</param>
    /// <param name="developer">The <c>developer</c> level's CEL source, or <see langword="null"/> for none.</param>
    /// <param name="viewer">The <c>viewer</c> level's CEL source, or <see langword="null"/> for none.</param>
    internal static Access Levels(string? admin = null, string? developer = null, string? viewer = null) =>
        new() { Admin = admin, Developer = developer, Viewer = viewer };

    /// <summary>An evaluator over a host primed with <paramref name="levels"/>.</summary>
    /// <param name="levels">The <c>access</c> block the host applied.</param>
    internal static ManagementAccessEvaluator Evaluator(Access levels) => Evaluator(Primed(levels));

    /// <summary>An evaluator over <paramref name="catalogs"/>, recognising <see cref="Bootstrap"/> and nobody else.</summary>
    /// <param name="catalogs">The catalog holder the evaluator reads the levels from.</param>
    internal static ManagementAccessEvaluator Evaluator(IPolicyCatalogProvider catalogs) =>
        Evaluator(catalogs, new PredicateEvaluator());

    /// <summary>
    /// An evaluator over <paramref name="catalogs"/> judging every level by <paramref name="evaluator"/>.
    /// </summary>
    /// <remarks>
    /// The evaluator is a parameter so a fact can record which levels actually reached it — the only way
    /// the "no short-circuit" rule is observable, since a first-match scan from the top would answer
    /// identically on every input.
    /// </remarks>
    /// <param name="catalogs">The catalog holder the evaluator reads the levels from.</param>
    /// <param name="evaluator">The evaluator every level is judged by.</param>
    internal static ManagementAccessEvaluator Evaluator(
        IPolicyCatalogProvider catalogs, IPredicateEvaluator evaluator) =>
        Evaluator(catalogs, evaluator, Bootstrapped(user => user == Bootstrap));

    /// <summary>
    /// An evaluator over <paramref name="catalogs"/> with every collaborator supplied.
    /// </summary>
    /// <remarks>
    /// The bootstrap port is a parameter so a fact can drive a <em>misbehaving</em> implementation. It is
    /// <see langword="public"/> in Abstractions, so a host writes one, and a host that answers
    /// <see langword="true"/> for the reserved all-zero id would hand every anonymous management request
    /// full administration — a claim the port's own doc comment makes and only the gate can enforce.
    /// </remarks>
    /// <param name="catalogs">The catalog holder the evaluator reads the levels from.</param>
    /// <param name="evaluator">The evaluator every level is judged by.</param>
    /// <param name="bootstrapAdmin">The bootstrap port the gate consults.</param>
    internal static ManagementAccessEvaluator Evaluator(
        IPolicyCatalogProvider catalogs, IPredicateEvaluator evaluator, IAlvoBootstrapAdmin bootstrapAdmin) =>
        new(catalogs, evaluator, bootstrapAdmin);

    /// <summary>A bootstrap port answering <paramref name="recognises"/>.</summary>
    /// <param name="recognises">Which callers the port reports as the bootstrap administrator.</param>
    internal static IAlvoBootstrapAdmin Bootstrapped(Func<UserId, bool> recognises)
    {
        var bootstrap = Substitute.For<IAlvoBootstrapAdmin>();
        bootstrap.IsBootstrapAdmin(Arg.Any<UserId>()).Returns(call => recognises(call.Arg<UserId>()));
        return bootstrap;
    }

    /// <summary>A catalog holder primed with the catalogue one apply of <paramref name="levels"/> produces.</summary>
    /// <param name="levels">The <c>access</c> block the host applied.</param>
    internal static IPolicyCatalogProvider Primed(Access levels) =>
        Holding(PolicyCatalogBuilderProbe.Build(levels));

    /// <summary>A catalog holder primed with <paramref name="catalog"/>.</summary>
    /// <remarks>
    /// Separate from <see cref="Primed(Access)"/> so a fact that needs to name the <em>compiled</em> levels
    /// — rather than the source they were compiled from — holds the same instance the gate reads.
    /// </remarks>
    /// <param name="catalog">The catalogue one apply produced.</param>
    internal static IPolicyCatalogProvider Holding(PolicyCatalog catalog)
    {
        var catalogs = Substitute.For<IPolicyCatalogProvider>();
        catalogs.Current.Returns(catalog);
        return catalogs;
    }
}
