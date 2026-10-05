using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo;
using MMLib.Alvo.Expressions.Internal;
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

    /// <summary>
    /// Registers a host function the descriptor's CEL may call in a hook <c>condition</c> and a before-hook
    /// <c>mutate</c> value — and nowhere else: rules and computed fields are evaluated by the database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Up to four parameters of <see cref="string"/>, <see cref="long"/>, <see cref="int"/>, <see cref="decimal"/>,
    /// <see cref="bool"/>, <see cref="DateTimeOffset"/> or <see cref="Guid"/> (or a nullable one), and a result of the
    /// same set. A null argument for a non-nullable parameter makes the call null without invoking the function.
    /// Everything is checked here, at the call, so a mistake fails startup rather than an apply.
    /// </para>
    /// <para>
    /// <b>The function is host code inside the write's transaction.</b> It must be synchronous, fast, thread-safe and
    /// free of side effects; it runs once per evaluation with no timeout. If it throws, nothing is written and the caller
    /// receives <c>…/errors/function-failed</c>. It captures what it closes over for the host's lifetime (no DI scope).
    /// A changed meaning deserves a new name, so stored descriptors keep theirs. Only this host knows the function: the
    /// standalone image and the CLI refuse a descriptor that calls it as an unknown function.
    /// </para>
    /// </remarks>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="name">
    /// The CEL name: a lower-case ASCII letter, then ASCII letters, digits or <c>_</c>, at most 64 characters, and not a
    /// built-in, a CEL keyword, macro or reserved word, or a standard CEL type or function name (<c>int</c>, <c>string</c>,
    /// <c>contains</c>, <c>matches</c>, …).
    /// </param>
    /// <param name="function">The implementation, e.g. <c>(string phone) =&gt; …</c>.</param>
    /// <param name="summary">
    /// One sentence for discovery (<c>cel/functions</c>, the assistant). The name and summary are visible to every
    /// Viewer of the management API: put no secrets or internal-only wording in them.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentException">The name is invalid, reserved or taken, or the delegate cannot be called from CEL.</exception>
    public static IAlvoBuilder AddCelFunction(this IAlvoBuilder builder, string name, Delegate function, string? summary = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var registration = new CelFunctionRegistration(HostCelFunction.Create(name, function, summary));
        EnsureNotRegistered(builder.Services, name);
        builder.Services.AddSingleton(registration);

        return builder;
    }

    /// <summary>Refuses a second registration of <paramref name="name"/>; keyed descriptors are skipped (reading their instance throws).</summary>
    private static void EnsureNotRegistered(IServiceCollection services, string name)
    {
        if (services.Any(descriptor => !descriptor.IsKeyedService
            && descriptor.ImplementationInstance is CelFunctionRegistration registered && registered.Function.Name == name))
        {
            throw new ArgumentException($"A CEL function named '{name}' is already registered; register each name once.", nameof(name));
        }
    }
}
