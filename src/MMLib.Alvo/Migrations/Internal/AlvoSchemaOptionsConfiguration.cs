using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace MMLib.Alvo.Migrations.Internal;

/// <summary>
/// The single owner of the <see cref="AlvoSchemaOptions.SectionName"/> configuration section: it binds the
/// values it can read and refuses the ones it cannot, so a mistyped startup mode fails the start with a
/// message naming the modes that would have worked.
/// </summary>
/// <remarks>
/// <para>
/// Binding and validating in one type is deliberate, and the reason is measured rather than stylistic.
/// Handing <see cref="AlvoSchemaOptions.Startup"/> to <c>ConfigurationBinder</c> gives away both halves of
/// the refusal: an unknown name throws out of the <em>binder</em> — before any
/// <see cref="IValidateOptions{TOptions}"/> can run — with a message that names the key and the bad value
/// but cannot name the three modes; and an out-of-range <em>number</em> is not refused at all, because
/// <c>Enum.Parse</c> accepts <c>"42"</c> and binds it to a mode that does not exist. So this type parses the
/// raw text itself, leaves an unparseable value on the safe default, and lets
/// <see cref="Validate(string?, AlvoSchemaOptions)"/> produce the whole refusal in one place.
/// </para>
/// <para>
/// <see cref="IConfiguration"/> is resolved <b>optionally</b> (the constructor takes a nullable instance
/// supplied by a factory registration, never <c>BuildServiceProvider</c>): a plain console host embedding
/// Alvo need not have registered configuration at all, and its absence simply means "no section", not a
/// failure.
/// </para>
/// </remarks>
/// <param name="configuration">The ambient configuration, or <c>null</c> when the host registered none.</param>
internal sealed class AlvoSchemaOptionsConfiguration(IConfiguration? configuration)
    : IConfigureOptions<AlvoSchemaOptions>, IValidateOptions<AlvoSchemaOptions>
{
    internal const string StartupKey = $"{AlvoSchemaOptions.SectionName}:{nameof(AlvoSchemaOptions.Startup)}";

    private const string AllowDestructiveKey =
        $"{AlvoSchemaOptions.SectionName}:{nameof(AlvoSchemaOptions.AllowDestructive)}";

    private const string StartupEnvironmentVariable = AlvoSchemaOptions.StartupEnvironmentVariable;

    /// <inheritdoc/>
    public void Configure(AlvoSchemaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (TryReadMode(out var mode))
        {
            options.Startup = mode;
        }

        if (configuration?.GetValue<bool?>(AllowDestructiveKey) is { } allowDestructive)
        {
            options.AllowDestructive = allowDestructive;
        }
    }

    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, AlvoSchemaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configured = configuration?[StartupKey];
        if (!IsAbsent(configured) && !TryParseMode(configured, out _))
        {
            return ValidateOptionsResult.Fail(Refusal(configured!));
        }

        return Enum.IsDefined(options.Startup)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(Refusal(options.Startup.ToString()));
    }

    /// <summary>
    /// A blank value reads as absent rather than as a typo: an environment variable set to nothing is a
    /// shell accident, and reading it as absent lands on <see cref="AlvoSchemaStartupMode.Verify"/>, the
    /// mode that touches nothing.
    /// </summary>
    private static bool IsAbsent(string? configured) => string.IsNullOrWhiteSpace(configured);

    private static bool TryParseMode(string? configured, out AlvoSchemaStartupMode mode)
    {
        if (Enum.TryParse(configured, ignoreCase: true, out mode) && Enum.IsDefined(mode))
        {
            return true;
        }

        mode = default;
        return false;
    }

    private bool TryReadMode(out AlvoSchemaStartupMode mode)
    {
        mode = default;
        var configured = configuration?[StartupKey];

        return !IsAbsent(configured) && TryParseMode(configured, out mode);
    }

    private static string Refusal(string configured) =>
        $"'{configured}' is not an Alvo schema startup mode. Set {StartupKey} (as an environment variable, "
        + $"{StartupEnvironmentVariable}) to one of: "
        + $"{nameof(AlvoSchemaStartupMode.Verify)} — refuse to start when the descriptor has drifted from "
        + "the schema applied to this database, and run no DDL (the default); "
        + $"{nameof(AlvoSchemaStartupMode.Apply)} — apply the drift on boot, still refusing a destructive "
        + "plan; "
        + $"{nameof(AlvoSchemaStartupMode.Skip)} — never touch the project schema.";
}
