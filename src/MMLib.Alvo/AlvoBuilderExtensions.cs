using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Migrations.Internal;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Infrastructure-selection extensions on <see cref="IAlvoBuilder"/> owned by the core package.</summary>
public static class AlvoBuilderExtensions
{
    /// <summary>Selects the project descriptor as a file on disk.</summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="path">Path to the project descriptor JSON file.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IAlvoBuilder FromDescriptor(this IAlvoBuilder builder, string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        builder.Services.TryAddSingleton<IDescriptorSource>(new FileDescriptorSource(path));

        return builder;
    }

    /// <summary>Sets the prefix Alvo uses for the database objects it owns (default <c>"alvo"</c>).</summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="prefix">The schema prefix; lower snake_case, 1–16 characters.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IAlvoBuilder UseSchemaPrefix(this IAlvoBuilder builder, string prefix)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        builder.Services.Configure<AlvoOptions>(options => options.SchemaPrefix = prefix);

        return builder;
    }
}
