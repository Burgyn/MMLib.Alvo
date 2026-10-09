using System.Reflection;

namespace MMLib.Alvo.DocsGen.CSharp;

internal static class ShippedAssemblies
{
    internal static IReadOnlyList<Assembly> All { get; } =
    [
        typeof(MMLib.Alvo.IAlvoBuilder).Assembly,
        typeof(MMLib.Alvo.Api.AlvoProblemTypes).Assembly,
        typeof(MMLib.Alvo.Data.EntityFrameworkCore.IAlvoSqlDialect).Assembly,
        typeof(MMLib.Alvo.Data.Sqlite.SqliteProviderOptions).Assembly,
        typeof(MMLib.Alvo.Data.PostgreSql.PostgreSqlProviderOptions).Assembly,
        typeof(MMLib.Alvo.Admin.AlvoAdmin).Assembly,
        typeof(Microsoft.Extensions.DependencyInjection.AlvoAiServiceCollectionExtensions).Assembly,
        typeof(MMLib.Alvo.Identity.AlvoIdentity).Assembly,
    ];

    internal static Assembly Host => typeof(MMLib.Alvo.Host.AlvoHost).Assembly;
}

internal static class ShippedPackages
{
    internal static IReadOnlyList<(string Package, Assembly Assembly)> All { get; } =
        [.. ShippedAssemblies.All.Select(assembly => (assembly.GetName().Name!, assembly))];
}
