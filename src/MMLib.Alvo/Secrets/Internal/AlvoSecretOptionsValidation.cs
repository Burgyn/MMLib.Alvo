using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace MMLib.Alvo.Secrets.Internal;

/// <summary>
/// Refuses a key-encryption key written into configuration, at startup, by name.
/// </summary>
/// <remarks>
/// <para>
/// <b>This repository already refused the other way round, and for the harder reason.</b>
/// <c>Alvo__Admin__BootstrapPassword</c> is refused because an environment variable is readable from a
/// process listing, a crash dump and <c>docker inspect</c>; a key that decrypts <em>every</em> secret this
/// deployment holds is the same exposure with a larger blast radius, so accepting it would quietly undo the
/// reason <see cref="AlvoSecretOptions.EncryptionKeyFile"/> is a path.
/// </para>
/// <para>
/// <b>Over the raw configuration rather than over a bound property</b>, because
/// <see cref="AlvoSecretOptions"/> deliberately has no <c>EncryptionKey</c> property at all — a type cannot
/// refuse a setting it never binds, and adding the property to refuse it would be adding the footgun to
/// carry the safety catch.
/// </para>
/// </remarks>
/// <param name="configuration">The host's configuration, or <see langword="null"/> when it registered none.</param>
internal sealed class AlvoSecretOptionsValidation(IConfiguration? configuration)
    : IValidateOptions<AlvoSecretOptions>
{
    /// <summary>The key that is refused, spelled as the configuration path an operator would set.</summary>
    internal const string RefusedKey = AlvoSecretOptions.ConfigurationSection + ":EncryptionKey";

    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, AlvoSecretOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return string.IsNullOrWhiteSpace(configuration?[RefusedKey])
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(Refusal);
    }

    /// <summary>A headline an operator can act on, a blank line, and the two settings that fix it.</summary>
    private static string Refusal { get; } = string.Join(
        Environment.NewLine,
        $"Alvo cannot start: {RefusedKey} is set. Alvo never reads an encryption key from configuration, "
            + "because a value in configuration is a value in an environment dump, a process listing and a "
            + "crash report — and this one decrypts every secret this deployment holds.",
        string.Empty,
        $"  Unset:      {RefusedKey}",
        $"  And set:    {AlvoSecretOptions.ConfigurationSection}:EncryptionKeyFile=/run/secrets/alvo-secret-key");
}
