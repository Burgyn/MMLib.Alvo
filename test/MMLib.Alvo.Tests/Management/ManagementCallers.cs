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
    /// The evaluator is a parameter so a fact can count how many levels were actually evaluated — the only
    /// way the "no short-circuit" rule is observable, since a first-match scan from the top would answer
    /// identically on every input.
    /// </remarks>
    /// <param name="catalogs">The catalog holder the evaluator reads the levels from.</param>
    /// <param name="evaluator">The evaluator every level is judged by.</param>
    internal static ManagementAccessEvaluator Evaluator(
        IPolicyCatalogProvider catalogs, IPredicateEvaluator evaluator)
    {
        var bootstrap = Substitute.For<IAlvoBootstrapAdmin>();
        bootstrap.IsBootstrapAdmin(Arg.Any<UserId>()).Returns(call => call.Arg<UserId>() == Bootstrap);

        return new ManagementAccessEvaluator(catalogs, evaluator, bootstrap);
    }

    /// <summary>A catalog holder primed with the catalogue one apply of <paramref name="levels"/> produces.</summary>
    /// <param name="levels">The <c>access</c> block the host applied.</param>
    internal static IPolicyCatalogProvider Primed(Access levels)
    {
        var catalogs = Substitute.For<IPolicyCatalogProvider>();
        catalogs.Current.Returns(PolicyCatalogBuilderProbe.Build(levels));
        return catalogs;
    }
}
