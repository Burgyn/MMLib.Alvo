using Microsoft.Extensions.Options;
using MMLib.Alvo.Identity.Internal;

namespace MMLib.Alvo.Identity.Tests;

/// <summary>
/// The startup refusal for a misconfigured bootstrap administrator — <c>extensibility.md</c> rule 5,
/// which every other options type in the family already satisfies.
/// </summary>
/// <remarks>
/// <b>The validation lives in the package, not in the standalone host, and that is the point.</b> A
/// host-only check leaves an <em>embedded</em> host — the distribution that calls
/// <c>AddAlvoIdentity</c> directly — with no validation at all, which is exactly the two-distribution
/// gap rule 5 exists to close.
/// </remarks>
public sealed class AlvoIdentityOptionsValidationTests : IDisposable
{
    private readonly string _passwordFile =
        Path.Combine(Path.GetTempPath(), $"alvo-admin-{Guid.NewGuid():N}.secret");

    /// <summary>Creates the mounted secret the valid cases point at.</summary>
    public AlvoIdentityOptionsValidationTests() => File.WriteAllText(_passwordFile, "Str0ng!Passw0rd");

    /// <inheritdoc/>
    public void Dispose() => File.Delete(_passwordFile);

    /// <summary>No bootstrap administrator at all is the documented default, never a misconfiguration.</summary>
    [Fact]
    public void No_bootstrap_administrator_is_configured_is_not_a_failure()
        => Validate(new AlvoIdentityOptions()).Succeeded.ShouldBeTrue();

    /// <summary>A complete, mounted pair is accepted.</summary>
    [Fact]
    public void An_address_with_a_mounted_password_file_is_accepted()
        => Validate(Options("admin@example.test", _passwordFile)).Succeeded.ShouldBeTrue();

    /// <summary>
    /// Half a credential is refused, because it reads as a bootstrap admin that will never exist.
    /// </summary>
    [Fact]
    public void An_address_without_a_password_file_is_refused()
    {
        var result = Validate(Options("admin@example.test", passwordFile: null));

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain(AlvoIdentityConfiguration.PasswordFileVariable);
    }

    /// <summary>And the reverse half, which names the address as the thing to add.</summary>
    [Fact]
    public void A_password_file_without_an_address_is_refused()
    {
        var result = Validate(Options(email: null, _passwordFile));

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain(AlvoIdentityConfiguration.EmailVariable);
    }

    /// <summary>
    /// A path with nothing at it is the single most likely way a first <c>docker run</c> goes wrong,
    /// and it must fail the start rather than the seeding.
    /// </summary>
    [Fact]
    public void A_password_file_that_is_not_mounted_is_refused()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"alvo-absent-{Guid.NewGuid():N}.secret");

        var result = Validate(Options("admin@example.test", missing));

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain(missing);
    }

    /// <summary>An address nothing could ever sign in with is refused where it was typed.</summary>
    /// <param name="email">The configured address.</param>
    [Theory]
    [InlineData("admin")]
    [InlineData("admin@")]
    [InlineData("@example.test")]
    [InlineData("Eva <eva@example.test>")]
    [InlineData("   ")]
    public void An_implausible_address_is_refused(string email)
    {
        var result = Validate(Options(email, _passwordFile));

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain(AlvoIdentityConfiguration.EmailVariable);
    }

    /// <summary>
    /// <b>Every refusal at once.</b> A container with two things wrong is one restart per fix if only
    /// the first is reported, and an operator reading a crash loop cannot tell a second failure from
    /// the same failure again.
    /// </summary>
    [Fact]
    public void Two_misconfigurations_are_reported_together_rather_than_one_restart_at_a_time()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"alvo-absent-{Guid.NewGuid():N}.secret");

        var result = Validate(Options("admin", missing));

        result.Failures.ShouldNotBeNull().Count().ShouldBe(2);
    }

    /// <summary>Runs the validation under test.</summary>
    /// <param name="options">The bound options.</param>
    /// <returns>The validation result.</returns>
    private static ValidateOptionsResult Validate(AlvoIdentityOptions options)
        => new AlvoIdentityOptionsValidation().Validate(Microsoft.Extensions.Options.Options.DefaultName, options);

    /// <summary>Builds a bound options instance.</summary>
    /// <param name="email">The configured address.</param>
    /// <param name="passwordFile">The configured secret path.</param>
    /// <returns>The options.</returns>
    private static AlvoIdentityOptions Options(string? email, string? passwordFile)
        => new() { BootstrapEmail = email, BootstrapPasswordFile = passwordFile };
}
