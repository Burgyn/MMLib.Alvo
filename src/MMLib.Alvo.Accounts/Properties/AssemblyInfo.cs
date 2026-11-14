using System.Runtime.CompilerServices;

// Everything this package ships beyond AddAlvoAccounts, MapAlvoAccounts, the two options types and AlvoAccounts is
// internal, so its own suite is the only assembly that can reach the validation, the token endpoint and the resolver.
// The same forgeability caveat as every other InternalsVisibleTo in the family applies: the assemblies are unsigned,
// so this grants access by name alone.
[assembly: InternalsVisibleTo("MMLib.Alvo.Accounts.Tests")]
