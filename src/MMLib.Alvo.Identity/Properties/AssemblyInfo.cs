using System.Runtime.CompilerServices;

// Everything this package ships beyond AddAlvoIdentity, AlvoIdentityOptions and AlvoIdentity is
// internal, so its own suite is the only assembly that can reach the resolver, the store and the
// bootstrap. The same forgeability caveat as every other InternalsVisibleTo in the family applies:
// the assemblies are unsigned, so this grants access by name alone.
[assembly: InternalsVisibleTo("MMLib.Alvo.Identity.Tests")]
