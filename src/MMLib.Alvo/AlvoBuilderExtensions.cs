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
    /// same set. A null argument for a non-nullable parameter makes the call null without invoking the function; a
    /// present argument that does not fit its parameter (a value past <see cref="int"/>'s range for an <see cref="int"/>)
    /// fails the call without invoking it, exactly as a throw does — it never reads as null.
    /// Everything is checked here, at the call, so a mistake fails startup rather than an apply.
    /// </para>
    /// <para>
    /// <b>The function is host code inside the write's transaction.</b> It must be synchronous, fast, thread-safe and
    /// free of side effects; it runs once per evaluation with no timeout. It captures what it closes over for the host's
    /// lifetime (no DI scope).
    /// </para>
    /// <para>
    /// <b>If it throws (or an argument does not fit), the evaluation fails closed</b>, and what that looks like depends
    /// on the caller: a Data API write is rolled back and answers HTTP 500 <c>…/errors/function-failed</c> naming the
    /// function; an in-process <c>IAlvoData</c> caller (a host endpoint, the dashboard) receives an exception and nothing
    /// is written (the dashboard shows its generic fault); in an after-hook condition the after-hook is dropped and a
    /// Warning is logged. So <b>throw</b> on a present input the function cannot answer for: a <see langword="null"/>
    /// return makes the call <see langword="null"/>, a condition over <see langword="null"/> does not fire, and a
    /// <c>reject</c> gated on the function would let that input through. Keep <see langword="null"/> for an input that
    /// really has no value.
    /// </para>
    /// <para>
    /// <b>What the function throws is logged whole</b> — its message and stack trace, at Error for a write and at
    /// Warning for an after-hook condition — and never shown to the caller. So never put caller data (a field's value,
    /// an argument) in an exception message: it would land in every log sink the host ships to.
    /// </para>
    /// <para>
    /// <b>Alvo's tenant filter does not reach inside the function.</b> A function that reads stored data must take the
    /// tenant as a parameter and filter by it itself. On a tenant-scoped entity pass the row's own <c>new.tenant_id</c>,
    /// which a <c>condition</c> and a <c>mutate</c> both read; <c>@tenant.id</c> works in a <c>condition</c> only, because
    /// the <c>Mutate</c> profile refuses it.
    /// </para>
    /// <para>
    /// A changed meaning deserves a new name, so stored descriptors keep theirs. Removing or renaming a registered
    /// function makes a stored descriptor that calls it fail the apply at boot, refused as calling an unknown function.
    /// Only this host knows the function: the standalone image and the CLI refuse a descriptor that calls it as an
    /// unknown function.
    /// </para>
    /// </remarks>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="name">
    /// The CEL name: a lower-case ASCII letter, then ASCII letters, digits or <c>_</c>, at most 64 characters, and not a
    /// built-in (<c>int</c>, <c>string</c>, <c>contains</c>, …), a CEL keyword, macro or reserved word, or a standard CEL
    /// type or function name Alvo does not build yet (<c>uint</c>, <c>matches</c>, <c>duration</c>, …).
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
