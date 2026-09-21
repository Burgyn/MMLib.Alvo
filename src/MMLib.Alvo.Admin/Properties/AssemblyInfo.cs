using System.Runtime.CompilerServices;

// The dashboard's public surface is four types — AlvoAdmin, AlvoAdminAssets, AlvoAdminOptions,
// AlvoAdminClaims — plus the one port a host fills (IAlvoAdminCallerResolver) and the two
// extension methods that register and map it. Everything a screen is built from is internal,
// because a component is reached by URL rather than called, and publishing one would turn a
// layout decision into a contract somebody could depend on. Its own suite is therefore the only
// assembly that can see the navigation table and the management gateway. The same forgeability
// caveat as every other InternalsVisibleTo in the family applies: the assemblies are unsigned, so
// this grants access by name alone.
[assembly: InternalsVisibleTo("MMLib.Alvo.Admin.Tests")]
