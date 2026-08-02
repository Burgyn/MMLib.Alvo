namespace MMLib.Alvo;

/// <summary>
/// Supplies the <see cref="RoleCatalog"/> currently in effect — the closed set of roles a credential
/// may mint a <see cref="Role"/> from. Authentication depends on this port, never on whatever
/// happens to hold the catalog, so the source of identity roles can change without authentication
/// changing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a port rather than a member on the policy catalog.</b> In F3 the descriptor's
/// <c>auth.roles</c> is the one source for both halves of authorization — the roles a rule's literal
/// is validated against and the roles a credential may mint — and they must never disagree: a role
/// set the rules were never compiled against is exactly the inconsistency "one descriptor, one
/// catalog, one guard" exists to prevent. The tempting shortcut was to read the roles straight off
/// the compiled policy catalog, which shares that priming. It was rejected: it would make the
/// <em>policy</em> catalog the authoritative source of <em>identity</em> roles, foreclosing the
/// obvious next source (roles minted from an identity provider — OIDC group claims, an external
/// directory, #36) without either routing identity through the rule engine or reintroducing the
/// second independently-primed holder that argument rules out.
/// </para>
/// <para>
/// So the single primed source stays, and the <em>provider</em> of the policy catalog implements this
/// port: <c>IPolicyCatalogProvider</c> derives from it, one instance is registered as both, and the
/// priming machinery is shared while nothing above it depends on the policy catalog to learn a role.
/// A host that registers its own <see cref="IRoleCatalogProvider"/> takes identity roles over
/// entirely — which is what an external identity source will do — and the descriptor still governs
/// which role literals a rule may name.
/// </para>
/// </remarks>
public interface IRoleCatalogProvider
{
    /// <summary>
    /// Gets the roles currently recognised, or <see langword="null"/> when nothing has declared a set
    /// yet (no descriptor applied, no external source primed). A consumer must fail closed on
    /// <see langword="null"/> — refuse an application role rather than mint one nothing has declared.
    /// </summary>
    RoleCatalog? DeclaredRoles { get; }
}
