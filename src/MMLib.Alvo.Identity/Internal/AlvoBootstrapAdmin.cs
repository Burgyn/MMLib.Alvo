namespace MMLib.Alvo.Identity.Internal;

/// <summary>
/// The package's <see cref="IAlvoBootstrapAdmin"/>: who, if anyone, the deployment configured as its
/// one bootstrap administrator.
/// </summary>
/// <remarks>
/// <b>A shell until the bootstrap lands.</b> It answers <see langword="false"/> for everyone, which is
/// the same default-deny the core's own <c>NoBootstrapAdmin</c> gives a host with no identity
/// subsystem at all — so installing this package cannot open a bypass before the seeding that would
/// justify one exists.
/// </remarks>
internal sealed class AlvoBootstrapAdmin : IAlvoBootstrapAdmin
{
    /// <inheritdoc/>
    public bool IsBootstrapAdmin(UserId user) => false;
}
